namespace Novolis.Reach.Protocol;

/// <summary>Immutable performance data used by Reach diagnostics and host status.</summary>
public sealed record ReachPerformanceSnapshot(
    long CapturedFrames,
    long EncodedFrames,
    long SentFrames,
    long ReceivedFrames,
    long PresentedFrames,
    long DroppedFrames,
    long KeyFrameRequests,
    long BytesSent,
    long BytesReceived,
    double? FrameAgeP50Milliseconds,
    double? FrameAgeP95Milliseconds,
    double? EncodeP95Milliseconds,
    double? DecodeP95Milliseconds,
    double? PresentP95Milliseconds,
    double? InputRoundTripP95Milliseconds,
    DateTimeOffset? LastFrameAt)
{
    /// <summary>Returns an empty snapshot for a process with no observed traffic.</summary>
    public static ReachPerformanceSnapshot Empty { get; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        null,
        null,
        null,
        null,
        null,
        null,
        null);
}
