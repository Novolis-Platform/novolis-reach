using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Requests a stream size and quality.</summary>
public sealed record ReachDisplayResize(int Width, int Height, int FramesPerSecond);
