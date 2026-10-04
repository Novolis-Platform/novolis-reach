using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Resets a video stream decoder.</summary>
public sealed record ReachVideoStreamReset(long Sequence);
