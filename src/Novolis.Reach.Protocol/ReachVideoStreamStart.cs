using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Starts a video stream.</summary>
public sealed record ReachVideoStreamStart(string Codec, int Width, int Height, int FramesPerSecond);
