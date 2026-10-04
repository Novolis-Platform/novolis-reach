using Novolis.Reach.Protocol;
using Novolis.Video;

namespace Novolis.Reach.Client;

/// <summary>Presenter used on platforms without a decoder in the current slice.</summary>
public sealed class NullReachVideoPresenter : IReachVideoPresenter
{
    /// <inheritdoc />
    public event Action<RawVideoFrame>? FrameDecoded
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public void Present(ReachVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
