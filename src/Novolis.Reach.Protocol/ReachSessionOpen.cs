using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Opens a client session.</summary>
public sealed record ReachSessionOpen(
    Guid SessionId,
    string RequestedDisplayId,
    bool EnableAudio = false);
