using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>One PCM audio block on the media channel.</summary>
public sealed record ReachAudioFrame(
    long Sequence,
    long Timestamp,
    string Codec,
    int SampleRate,
    int Channels,
    byte[] Data,
    int BitsPerSample = 16,
    bool IsFloat = false);
