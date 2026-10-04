using Novolis.Reach.Protocol;

namespace Novolis.Reach.Client;

/// <summary>
/// Coordinates key-frame recovery and stale decoded-frame suppression for a
/// remote video stream.
/// </summary>
public sealed class ReachVideoStreamGate
{
    private long _generation;
    private int _requiresKeyFrame;

    /// <summary>Gets the current stream generation.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// Invalidates pending decoder work and requires the next accepted frame
    /// to be an intra frame.
    /// </summary>
    public void RequireKeyFrame()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _requiresKeyFrame, 1);
    }

    /// <summary>
    /// Accepts a frame when it can safely begin or continue the stream.
    /// </summary>
    public bool TryAccept(
        ReachVideoFrame frame,
        out long generation)
    {
        generation = Generation;
        if (Volatile.Read(ref _requiresKeyFrame) != 0
            && !frame.IsKeyFrame)
        {
            return false;
        }

        if (frame.IsKeyFrame)
            Volatile.Write(ref _requiresKeyFrame, 0);
        return true;
    }

    /// <summary>Gets whether decoded work belongs to the current stream.</summary>
    public bool IsCurrent(long generation) =>
        generation == Generation;
}
