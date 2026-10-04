using Novolis.Reach.Protocol;
using Novolis.Video;

namespace Novolis.Reach.Client;

/// <summary>Optional presenter diagnostics for dropped access units.</summary>
public interface IReachVideoDropSource
{
    /// <summary>Raised when a decoded access unit is evicted for freshness.</summary>
    event Action? FrameDropped;
}
