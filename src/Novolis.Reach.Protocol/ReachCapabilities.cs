namespace Novolis.Reach.Protocol;

/// <summary>Capabilities offered by one side of a Reach session.</summary>
public sealed record ReachCapabilities(
    ReachCapability Features,
    int MaximumDisplays = 1,
    int MaximumWidth = 3840,
    int MaximumHeight = 2160,
    string[]? VideoCodecs = null,
    string[]? AudioCodecs = null)
{
    /// <summary>Gets codecs offered for video.</summary>
    public IReadOnlyList<string> OfferedVideoCodecs =>
        VideoCodecs ?? Array.Empty<string>();

    /// <summary>Gets codecs offered for audio.</summary>
    public IReadOnlyList<string> OfferedAudioCodecs =>
        AudioCodecs ?? Array.Empty<string>();

    /// <summary>Returns whether a capability is offered.</summary>
    public bool Supports(ReachCapability capability) =>
        (Features & capability) == capability;

    /// <summary>Intersects host and client capabilities.</summary>
    public static ReachCapabilities Intersect(
        ReachCapabilities host,
        ReachCapabilities client)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(client);

        var videoCodecs = host.OfferedVideoCodecs
            .Intersect(client.OfferedVideoCodecs, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var audioCodecs = host.OfferedAudioCodecs
            .Intersect(client.OfferedAudioCodecs, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var features = host.Features & client.Features;
        return new ReachCapabilities(
            features,
            Math.Min(host.MaximumDisplays, client.MaximumDisplays),
            Math.Min(host.MaximumWidth, client.MaximumWidth),
            Math.Min(host.MaximumHeight, client.MaximumHeight),
            videoCodecs,
            audioCodecs);
    }

    /// <summary>Baseline capabilities for a Windows host.</summary>
    public static ReachCapabilities WindowsHost { get; } = new(
        ReachCapability.H264
        | ReachCapability.Mouse
        | ReachCapability.Keyboard
        | ReachCapability.ClipboardText
        | ReachCapability.SingleDisplay
        | ReachCapability.MultiMonitor
        | ReachCapability.Audio
        | ReachCapability.AdaptiveQuality,
        MaximumDisplays: 8,
        VideoCodecs: ["H264"],
        AudioCodecs: ["PCM", "PCM_FLOAT"]);

    /// <summary>Baseline capabilities for a Windows client.</summary>
    public static ReachCapabilities WindowsClient { get; } = new(
        ReachCapability.H264
        | ReachCapability.Mouse
        | ReachCapability.Keyboard
        | ReachCapability.ClipboardText
        | ReachCapability.MultiMonitor
        | ReachCapability.Audio
        | ReachCapability.AdaptiveQuality,
        MaximumDisplays: 8,
        VideoCodecs: ["H264"],
        AudioCodecs: ["PCM", "PCM_FLOAT"]);

    /// <summary>Baseline capabilities for a Linux client.</summary>
    public static ReachCapabilities LinuxClient { get; } = new(
        ReachCapability.H264
        | ReachCapability.Mouse
        | ReachCapability.Keyboard
        | ReachCapability.ClipboardText
        | ReachCapability.MultiMonitor
        | ReachCapability.AdaptiveQuality,
        MaximumDisplays: 8,
        VideoCodecs: ["H264"]);

    /// <summary>Baseline capabilities for an Android client.</summary>
    public static ReachCapabilities AndroidClient { get; } = new(
        ReachCapability.H264
        | ReachCapability.Touch
        | ReachCapability.ClipboardText
        | ReachCapability.SingleDisplay
        | ReachCapability.AdaptiveQuality,
        VideoCodecs: ["H264"]);
}
