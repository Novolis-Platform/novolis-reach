using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Replies to a control-channel latency probe.</summary>
public sealed record ReachLatencyResponse(long Id, long SentUtcTicks);
