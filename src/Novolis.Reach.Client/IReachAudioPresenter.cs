using Novolis.Reach.Protocol;

namespace Novolis.Reach.Client;

/// <summary>Platform-specific remote audio output.</summary>
public interface IReachAudioPresenter : IDisposable
{
    /// <summary>Accepts one remote audio block.</summary>
    void Present(ReachAudioFrame frame);
}
