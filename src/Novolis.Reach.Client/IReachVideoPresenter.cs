using Novolis.Reach.Protocol;
using Novolis.Video;

namespace Novolis.Reach.Client;

/// <summary>Platform-specific decoder and presenter for Reach video frames.</summary>
public interface IReachVideoPresenter : IDisposable
{
    /// <summary>Raised after an encoded frame has been decoded.</summary>
    event Action<RawVideoFrame>? FrameDecoded;

    /// <summary>Accepts one encoded frame.</summary>
    void Present(ReachVideoFrame frame);
}
