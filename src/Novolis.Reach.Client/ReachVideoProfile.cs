using Novolis.Reach.Protocol;

namespace Novolis.Reach.Client;

/// <summary>Negotiated dimensions and pacing for one video stream.</summary>
public sealed record ReachVideoProfile(
    ReachVideoProfileKind Kind,
    int Width,
    int Height,
    int FramesPerSecond,
    int TargetBitrate)
{
    /// <summary>Creates a conservative profile for an announced display.</summary>
    public static ReachVideoProfile ForDisplay(
        ReachDisplay display,
        ReachVideoProfileKind kind)
    {
        ArgumentNullException.ThrowIfNull(display);
        var maximumWidth = kind switch
        {
            ReachVideoProfileKind.Lan => 960,
            ReachVideoProfileKind.Routed => 720,
            _ => 480,
        };
        var scale = Math.Min(1d, maximumWidth / (double)display.Width);
        var width = Align(display.Width * scale);
        var height = Align(display.Height * scale);
        var framesPerSecond = kind switch
        {
            ReachVideoProfileKind.Lan => 24,
            ReachVideoProfileKind.Routed => 18,
            _ => 10,
        };
        var bitrate = kind switch
        {
            ReachVideoProfileKind.Lan => 4_000_000,
            ReachVideoProfileKind.Routed => 2_500_000,
            _ => 1_500_000,
        };
        return new ReachVideoProfile(
            kind,
            width,
            height,
            framesPerSecond,
            bitrate);
    }

    private static int Align(double value) =>
        Math.Max(16, (int)Math.Round(value / 16d) * 16);
}
