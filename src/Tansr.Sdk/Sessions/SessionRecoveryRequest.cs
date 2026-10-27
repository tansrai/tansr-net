namespace Tansr.Sdk.Sessions;

/// <summary>Bounded presentation recovery; never requests an archive export or replays an input.</summary>
public sealed class SessionRecoveryRequest
{
    public long AfterSequence { get; set; } = -1;
    public long? ExpectedFirstSequence { get; set; }
    public long? MinimumTerminalSequence { get; set; }
    public int MaximumMessages { get; set; } = 128;
    public int MaximumBytes { get; set; } = 262144;

    internal SessionRecoveryRequest Copy()
    {
        const long maximum = 9007199254740991L;
        if (AfterSequence < -1 || AfterSequence > maximum || ExpectedFirstSequence < 0 || ExpectedFirstSequence > maximum ||
            MinimumTerminalSequence < 0 || MinimumTerminalSequence > maximum ||
            MinimumTerminalSequence.HasValue && !ExpectedFirstSequence.HasValue ||
            MaximumMessages < 1 || MaximumMessages > 256 || MaximumBytes < 1024 || MaximumBytes > 2097152)
            throw new ArgumentOutOfRangeException(nameof(SessionRecoveryRequest));
        return new SessionRecoveryRequest
        {
            AfterSequence = AfterSequence, ExpectedFirstSequence = ExpectedFirstSequence,
            MinimumTerminalSequence = MinimumTerminalSequence, MaximumMessages = MaximumMessages, MaximumBytes = MaximumBytes
        };
    }
}
