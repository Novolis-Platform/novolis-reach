using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Selects a display for a client.</summary>
public sealed record ReachDisplaySelect(string DisplayId);
