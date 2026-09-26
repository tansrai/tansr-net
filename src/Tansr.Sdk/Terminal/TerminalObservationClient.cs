using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Tansr.Sdk.Client;
using Tansr.Sdk.Protocol;

namespace Tansr.Sdk.Terminal;

public enum TerminalResourceState { Active, Accepted, Draining, Completed, Failed, Unknown }

/// <summary>An original Serve resource observation. Close acceptance and execution completion
/// do not imply that persistent writes, tools or shared host resources have finished.</summary>
public sealed class TerminalResourceObservation
{
    internal TerminalResourceObservation(JsonElement value)
    {
        Raw = value.Clone(); EpochStartSequence = long.Parse(value.GetProperty("epochStartSeq").GetString()!, CultureInfo.InvariantCulture);
        State = value.GetProperty("state").GetString() switch
        {
            "active" => TerminalResourceState.Active,
            "accepted" => TerminalResourceState.Accepted,
            "draining" => TerminalResourceState.Draining,
            "completed" => TerminalResourceState.Completed,
            "failed" => TerminalResourceState.Failed,
            _ => TerminalResourceState.Unknown
        };
        CloseRequested = value.GetProperty("closeRequested").GetBoolean(); ExecutionEnded = value.GetProperty("executionEnded").GetBoolean();
        ErrorCode = value.GetProperty("errorCode").GetString();
    }
    public JsonElement Raw { get; }
    public long EpochStartSequence { get; }
    public TerminalResourceState State { get; }
    public bool CloseRequested { get; }
    public bool ExecutionEnded { get; }
    public string? ErrorCode { get; }
    public bool Completed => State == TerminalResourceState.Completed;
}

public sealed class TerminalResourceSettlementException : Exception
{
    internal TerminalResourceSettlementException(TerminalResourceObservation observation) : base(observation.ErrorCode ?? "resource_settlement_unknown") { Observation = observation; }
    public TerminalResourceObservation Observation { get; }
}

public sealed class TerminalOutputCorrelation
{
    internal TerminalOutputCorrelation(JsonElement value) { Raw = value.Clone(); }
    public JsonElement Raw { get; }
    public string ToolCallId => Raw.GetProperty("toolCallId").GetString()!;
    public string OperationId => Operation.GetProperty("operationId").GetString()!;
    public string RequestDigest => Operation.GetProperty("requestDigest").GetString()!;
    public string OutputAuthority => Raw.GetProperty("outputAuthority").GetString()!;
    public JsonElement Operation => Raw.GetProperty("operation").Clone();
}

public sealed class TerminalOutputCorrelations
{
    internal TerminalOutputCorrelations(JsonElement value)
    { Raw = value.Clone(); Correlations = Array.AsReadOnly(value.GetProperty("correlations").EnumerateArray().Select(item => new TerminalOutputCorrelation(item)).ToArray()); }
    public JsonElement Raw { get; }
    public IReadOnlyList<TerminalOutputCorrelation> Correlations { get; }
    public bool Truncated => Raw.GetProperty("truncated").GetBoolean();
}

/// <summary>Explicit preview consumer of the independent, read-only terminal-observation-v1
/// contract. It never closes, wakes, rebinds or repeats execution and never infers completion from HTTP 202.</summary>
public sealed class TerminalObservationClient : IDisposable
{
    public const string Protocol = TerminalObservationContract.Protocol;
    public const string SchemaSha256 = TerminalObservationContract.SchemaSha256;
    private readonly TansrClient client;
    private readonly bool ownsClient;
    private readonly JsonElement owner;
    private readonly CancellationTokenSource stop = new();
    private int disposed;
    public TerminalObservationClient(TansrClientOptions options, bool enablePreview = false, HttpClient? injected = null)
    {
        if (!enablePreview) throw new TansrProtocolException("unsupported_capability");
        if (options is null) throw new ArgumentNullException(nameof(options));
        client = new TansrClient(options, injected); ownsClient = true;
        try { owner = client.ReadTerminalScope(); }
        catch { client.Dispose(); throw; }
    }
    /// <summary>Preserves the caller's authenticated transport, local-host ownership checks
    /// and lifetime. Disposing this observation facade does not dispose the supplied client.</summary>
    public TerminalObservationClient(TansrClient client, bool enablePreview = false)
    {
        if (!enablePreview) throw new TansrProtocolException("unsupported_capability");
        this.client = client ?? throw new ArgumentNullException(nameof(client)); owner = client.ReadTerminalScope();
    }
    private JsonElement Scope()
    {
        var value = client.ReadTerminalScope(); TerminalControlClient.CheckOwner(owner, value); return value;
    }
    private JsonElement Reference(string sessionId, SessionContract contract)
    {
        if (contract != SessionContract.Sdk1 && contract != SessionContract.Sdk2OffloadV1) throw new ArgumentOutOfRangeException(nameof(contract));
        var value = TerminalJson.Object(w => { w.WriteString("sessionContract", contract == SessionContract.Sdk1 ? "sdk1" : "sdk2-offload-v1"); w.WriteString("sessionId", sessionId); });
        TerminalObservationContract.Validate("SessionReference", value);
        if (value.GetProperty("sessionContract").GetString() != client.TerminalSessionContract) throw new TansrProtocolException("binding_conflict");
        if (sessionId == "." || sessionId == "..") throw new TansrProtocolException("invalid_request");
        return value;
    }
    private async Task<JsonElement> ReadAsync(JsonElement session, string action, string responseName, string? toolCallId, CancellationToken ct)
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(TerminalObservationClient));
        if (toolCallId != null) TerminalObservationContract.Validate("LegacyId", TerminalJson.Object(w => w.WriteString("id", toolCallId)).GetProperty("id"));
        var path = "/v3/terminal-observation/sessions/" + Uri.EscapeDataString(session.GetProperty("sessionId").GetString()!) + "/" + action +
            "?contract=" + Protocol + "&sessionContract=" + session.GetProperty("sessionContract").GetString() + (toolCallId is null ? "" : "&toolCallId=" + Uri.EscapeDataString(toolCallId));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        var result = await client.ReadTerminalObservationAsync(path, responseName, Scope(), deadline.Token).ConfigureAwait(false);
        if (!TerminalJson.Equal(session, result.GetProperty("session"))) throw new TansrProtocolException("integrity_mismatch");
        return result;
    }
    public async Task<TerminalResourceObservation> ReadResourcesAsync(string sessionId, SessionContract sessionContract = SessionContract.Sdk1, CancellationToken cancellationToken = default)
    {
        var value = await ReadAsync(Reference(sessionId, sessionContract), "resources", "ResourcesResponse", null, cancellationToken).ConfigureAwait(false);
        TerminalObservationContract.ValidateResource(value); return new TerminalResourceObservation(value);
    }
    public async Task<TerminalOutputCorrelations> ReadOutputCorrelationsAsync(string sessionId, SessionContract sessionContract = SessionContract.Sdk1, string? toolCallId = null, CancellationToken cancellationToken = default)
    {
        var session = Reference(sessionId, sessionContract);
        var value = await ReadAsync(session, "output-correlations", "CorrelationsResponse", toolCallId, cancellationToken).ConfigureAwait(false);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.GetProperty("correlations").EnumerateArray())
            if (!TerminalJson.Equal(session, item.GetProperty("session")) || toolCallId != null && item.GetProperty("toolCallId").GetString() != toolCallId ||
                !identities.Add(item.GetProperty("operation").GetProperty("operationId").GetString()!)) throw new TansrProtocolException("integrity_mismatch");
        return new TerminalOutputCorrelations(value);
    }
    /// <summary>Only polls this original session/epoch; failed or unknown resource states throw
    /// with their observation. Deadline/cancellation ends this wait, never the remote work.</summary>
    public async Task<TerminalResourceObservation> WaitForResourcesAsync(string sessionId, TimeSpan timeout,
        SessionContract sessionContract = SessionContract.Sdk1, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1) || interval < TimeSpan.FromMilliseconds(10) || interval > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var originalScope = Scope(); long? epoch = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token); deadline.CancelAfter(timeout);
        try
        {
            for (; ; )
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!TerminalJson.Equal(originalScope, Scope())) throw new TansrProtocolException("context_changed");
                var observed = await ReadResourcesAsync(sessionId, sessionContract, deadline.Token).ConfigureAwait(false);
                if (!TerminalJson.Equal(originalScope, Scope())) throw new TansrProtocolException("context_changed");
                if (epoch.HasValue && epoch.Value != observed.EpochStartSequence) throw new TansrProtocolException("stale_generation");
                epoch = observed.EpochStartSequence;
                if (observed.Completed) return observed;
                if (observed.State == TerminalResourceState.Failed || observed.State == TerminalResourceState.Unknown) throw new TerminalResourceSettlementException(observed);
                await Task.Delay(interval, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !stop.IsCancellationRequested)
        { throw new TimeoutException("Resource observation deadline expired; remote cleanup is still unconfirmed."); }
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) { stop.Cancel(); if (ownsClient) client.Dispose(); } }
}
