using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Changes video stream quality.</summary>
public sealed record ReachVideoStreamConfiguration(
    string Codec,
    int Width,
    int Height,
    int FramesPerSecond,
    int TargetBitrate);
