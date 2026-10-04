using System.Text.Json;

namespace Novolis.Reach.Protocol;

/// <summary>Changes audio stream configuration.</summary>
public sealed record ReachAudioStreamConfiguration(string Codec, int SampleRate, int Channels);
