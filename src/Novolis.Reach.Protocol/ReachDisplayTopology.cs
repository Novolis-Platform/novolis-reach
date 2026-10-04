using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Monitor topology announcement.</summary>
public sealed record ReachDisplayTopology(IReadOnlyList<ReachDisplay> Displays);
