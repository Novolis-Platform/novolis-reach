using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Resumes a previous client session.</summary>
public sealed record ReachSessionResume(
    Guid SessionId,
    long LastVideoSequence,
    bool EnableAudio = false);
