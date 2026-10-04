using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>One encoded video access unit on the media channel.</summary>
public sealed record ReachVideoFrame(
    long Sequence,
    int Width,
    int Height,
    long Timestamp,
    string Codec,
    bool IsKeyFrame,
    byte[] AccessUnit);
