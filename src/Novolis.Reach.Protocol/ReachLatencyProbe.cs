using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Measures control-channel round-trip latency.</summary>
public sealed record ReachLatencyProbe(long Id, long SentUtcTicks);
