using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Starts an audio stream.</summary>
public sealed record ReachAudioStreamStart(
    string Codec,
    int SampleRate,
    int Channels,
    int BitsPerSample = 16,
    bool IsFloat = false);
