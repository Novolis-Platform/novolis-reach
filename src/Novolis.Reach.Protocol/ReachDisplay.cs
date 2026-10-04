using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>One display in a topology announcement.</summary>
public sealed record ReachDisplay(
    string Id,
    int Left,
    int Top,
    int Width,
    int Height,
    uint Dpi);
