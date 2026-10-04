using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Truthful host sharing state for the remote client surface.</summary>
public sealed record ReachSharingState(bool IsPaused, string Reason);
