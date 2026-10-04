using Novolis.Transports;

namespace Novolis.Reach.Transport;

/// <summary>Reach-facing view of a transport connection.</summary>
public interface IReachTransportConnection : IAsyncDisposable
{
    /// <summary>Gets transport identity and negotiated capabilities.</summary>
    TransportConnectionInfo Info { get; }

    /// <summary>Gets the reliable control stream.</summary>
    Stream ControlStream { get; }

    /// <summary>Opens the dedicated or multiplexed media stream.</summary>
    ValueTask<Stream> OpenMediaStreamAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a reliable stream for bulk payloads when the transport supports
    /// stream multiplexing. A null result requests control-stream fallback.
    /// </summary>
    ValueTask<Stream?> OpenBulkStreamAsync(
        CancellationToken cancellationToken = default);
}
