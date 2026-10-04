using Novolis.Reach.Protocol;
using Novolis.Video;

namespace Novolis.Reach.Client;

/// <summary>Optional presenter hook for a host video-stream reset.</summary>
public interface IReachVideoStreamResetter
{
    /// <summary>Discards decoder state before the next key frame.</summary>
    void ResetStream();
}
