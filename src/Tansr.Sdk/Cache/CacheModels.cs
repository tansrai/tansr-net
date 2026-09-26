using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using Tansr.Sdk.Client;

namespace Tansr.Sdk.Cache;

internal enum CacheOpenKind { New, Import, Resume, Fork }

/// <summary>仅服务端返回或可信存储恢复的 opaque 引用；它不是本地授权或模型提供商密钥。</summary>
internal sealed class CacheTicket
{
    internal CacheTicket(string value) { CacheJson.Text("Ticket", value); Value = value; }
    internal string Value { get; }
    public override string ToString() => "CacheTicket(redacted)";
}

internal sealed class CacheCapabilities
{
    internal CacheCapabilities(JsonElement raw, string owner)
    { Raw = raw.Clone(); Owner = owner; }
    internal JsonElement Raw { get; }
    internal string Owner { get; }
    internal bool Available => Raw.GetProperty("features").GetArrayLength() == 1;
    internal string? OperationEpoch => Raw.GetProperty("operationEpoch").ValueKind == JsonValueKind.Null ? null
        : CacheJson.String(Raw.GetProperty("operationEpoch"), "id");
    internal int ControlBytes => Raw.GetProperty("limits").GetProperty("controlBytes").GetInt32();
}

internal sealed class CacheBinding
{
    internal CacheBinding(JsonElement raw, string owner)
    { CacheJson.Binding(raw); Raw = raw.Clone(); Owner = owner; }
    internal JsonElement Raw { get; }
    internal string Owner { get; }
    internal string Id => CacheJson.String(Raw, "bindingId");
    internal string LogicalReference => CacheJson.String(Raw, "logicalRef");
    internal string Revision => CacheJson.String(Raw, "revision");
    internal string State => CacheJson.String(Raw, "state");
    internal string GroupGeneration => CacheJson.String(Raw, "groupGeneration");
}

/// <summary>原操作的不可变事实；即使 state=active，也不表示当前授权或当前映射仍有效。</summary>
internal sealed class CacheReceipt
{
    internal CacheReceipt(JsonElement raw, string owner)
    {
        Raw = raw.Clone(); Binding = new CacheBinding(raw.GetProperty("binding"), owner);
        Ticket = raw.GetProperty("ticket").ValueKind == JsonValueKind.Null ? null : new CacheTicket(CacheJson.String(raw, "ticket"));
    }
    internal JsonElement Raw { get; }
    internal CacheBinding Binding { get; }
    internal CacheTicket? Ticket { get; }
    internal string? TicketExpiresAt => Raw.GetProperty("ticketExpiresAt").ValueKind == JsonValueKind.Null ? null : CacheJson.String(Raw, "ticketExpiresAt");
    public override string ToString() => "CacheReceipt(redacted)";
}

/// <summary>副作用原文与原身份；持久化必须走可信、受保护的本地存储，不可作为普通日志。</summary>
internal sealed class CacheOperation
{
    private readonly byte[] original;
    internal CacheOperation(string operation, JsonElement body, string owner, bool restored)
    {
        Operation = operation; Body = body.Clone(); Owner = owner; original = CacheJson.Encode(body);
        Attempted = restored;
    }
    internal string Operation { get; }
    internal JsonElement Body { get; }
    internal string Owner { get; }
    internal string OperationEpoch => CacheJson.String(Body.GetProperty("request"), "operationEpoch");
    internal string RequestId => CacheJson.String(Body.GetProperty("request"), "requestId");
    internal string? BindingId => Operation == "open" ? null : CacheJson.String(Body, "bindingId");
    internal byte[] ExportOriginalRequest() => (byte[])original.Clone();
    internal byte[]? ExportOriginalReceipt() => ReceiptCanonical is null ? null : Encoding.UTF8.GetBytes(ReceiptCanonical);
    internal SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);
    internal bool Attempted { get; set; }
    internal string? ReceiptCanonical { get; set; }
    public override string ToString() => "CacheOperation(redacted)";
}

internal sealed class CacheHttpException : TansrException
{
    internal CacheHttpException(int statusCode, string code, string retryAction, string fallback) : base(code)
    { StatusCode = statusCode; RetryAction = retryAction; Fallback = fallback; }
    internal int StatusCode { get; }
    internal string RetryAction { get; }
    internal string Fallback { get; }
}
