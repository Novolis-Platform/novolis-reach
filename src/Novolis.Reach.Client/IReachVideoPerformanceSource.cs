using Novolis.Reach.Protocol;
using Novolis.Video;

namespace Novolis.Reach.Client;

/// <summary>Optional presenter diagnostics for decoder duration.</summary>
public interface IReachVideoPerformanceSource
{
    /// <summary>Raised after one encoded frame has been decoded.</summary>
    event Action<double>? DecodeCompleted;
}
