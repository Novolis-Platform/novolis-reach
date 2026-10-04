using Novolis.Reach.Protocol;

namespace Novolis.Reach.Client;

/// <summary>No-op audio output for clients without an audio backend.</summary>
public sealed class NullReachAudioPresenter : IReachAudioPresenter
{
    /// <inheritdoc />
    public void Present(ReachAudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
