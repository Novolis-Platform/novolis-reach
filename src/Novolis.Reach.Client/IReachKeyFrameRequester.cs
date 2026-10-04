using Novolis.Reach.Protocol;
using Novolis.Video;

namespace Novolis.Reach.Client;

/// <summary>Optional decoder recovery notification for presenters that lose codec state.</summary>
public interface IReachKeyFrameRequester
{
    /// <summary>Raised when the presenter needs a fresh key frame.</summary>
    event Action? KeyFrameRequested;
}
