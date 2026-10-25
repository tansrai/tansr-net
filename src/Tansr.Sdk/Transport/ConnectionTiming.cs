using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Tansr.Sdk.Transport;

internal enum ConnectionTimingPhase
{
    CreateBegin, ResumeBegin, RequestSerialized, IdentityBeforeToken, TokenReceived, AccessValidated,
    DiscoveryGateWait, DiscoveryGateEntered, CapabilityRequestReady, CapabilityHeaders,
    CapabilityHeadersValidated, CapabilityBody, CapabilityParsed, DiscoveryGateReleased,
    CreateRequestReady, CreateHeaders, CreateHeadersValidated, CreateBody, CreateParsed,
    Complete, Failed, Canceled
}

// 仅建会调用的可选本机观测；默认无监听者，不保存身份、地址、请求或响应内容。
internal sealed class ConnectionTiming
{
    private readonly string sample = Guid.NewGuid().ToString("N");
    private ConnectionTiming() { }
    internal static ConnectionTiming? Start(bool resume)
    {
        try
        {
            if (!TansrConnectionEventSource.Log.IsEnabled()) return null;
            var timing = new ConnectionTiming();
            timing.Mark(resume ? ConnectionTimingPhase.ResumeBegin : ConnectionTimingPhase.CreateBegin);
            return timing;
        }
        catch { return null; }
    }
    internal void Mark(ConnectionTimingPhase phase)
    {
        try
        {
            if (TansrConnectionEventSource.Log.IsEnabled())
                TansrConnectionEventSource.Log.Stage(sample, phase.ToString(), Stopwatch.GetTimestamp());
        }
        catch { /* 监听器异常不得改变原请求、异常或取消。 */ }
    }
}

[EventSource(Name = "Tansr-Sdk-ConnectionTiming")]
internal sealed class TansrConnectionEventSource : EventSource
{
    internal static readonly TansrConnectionEventSource Log = new TansrConnectionEventSource();
    private TansrConnectionEventSource() { }
    [Event(1, Level = EventLevel.Informational)]
    public void Stage(string sample, string phase, long timestamp) => WriteEvent(1, sample, phase, timestamp);
}
