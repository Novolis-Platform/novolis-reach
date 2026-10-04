using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Novolis.Transports;
using Novolis.Transports.Quic;
using Novolis.Transports.Tcp;

namespace Novolis.Reach.Transport;

/// <summary>Connects Reach clients through TCP or secure QUIC.</summary>
public static class ReachTransportConnector
{
    /// <summary>Connects to a parsed Reach endpoint.</summary>
    public static ValueTask<IReachTransportConnection> ConnectAsync(
        ReachTransportEndpoint endpoint,
        int mediaPort,
        CancellationToken cancellationToken = default) =>
        endpoint.IsQuic
            ? ConnectQuicAsync(endpoint, cancellationToken)
            : endpoint.IsTcp
                ? ConnectTcpAsync(endpoint, mediaPort, cancellationToken)
                : throw new ArgumentException(
                    "Reach endpoint must use tcp or quic.",
                    nameof(endpoint));

    private static async ValueTask<IReachTransportConnection> ConnectTcpAsync(
        ReachTransportEndpoint endpoint,
        int mediaPort,
        CancellationToken cancellationToken)
    {
        var control = await TcpTransportConnection.ConnectAsync(
                endpoint.Address,
                cancellationToken)
            .ConfigureAwait(false);
        var controlStream = await control.OpenBidirectionalStreamAsync(
                cancellationToken)
            .ConfigureAwait(false);
        return new ReachTransportConnection(
            control,
            controlStream,
            async token =>
            {
                var media = await TcpTransportConnection.ConnectAsync(
                        new System.Net.IPEndPoint(
                            endpoint.Address.Address,
                            mediaPort),
                        token)
                    .ConfigureAwait(false);
                var mediaStream = await media.OpenBidirectionalStreamAsync(token)
                    .ConfigureAwait(false);
                return (media, mediaStream);
            },
            openBulk: null);
    }

#pragma warning disable CA1416
    private static async ValueTask<IReachTransportConnection> ConnectQuicAsync(
        ReachTransportEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()
            && !OperatingSystem.IsLinux()
            && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "The current Android runtime does not provide MsQuic; use TCP.");
        }

        var options = new QuicTransportOptions
        {
            ClientCertificateValidationCallback =
                CreateCertificateValidator(endpoint.CertificatePin),
        };
        var connection = await QuicTransportConnection.ConnectAsync(
                endpoint.Address,
                options,
                cancellationToken)
            .ConfigureAwait(false);
        var controlStream = await connection.OpenBidirectionalStreamAsync(
                cancellationToken)
            .ConfigureAwait(false);
        return new ReachTransportConnection(
            connection,
            controlStream,
            async token =>
            {
                var mediaStream = await connection.OpenBidirectionalStreamAsync(
                        token)
                    .ConfigureAwait(false);
                return (null, mediaStream);
            },
            async token =>
            {
                var bulkStream = await connection.OpenBidirectionalStreamAsync(
                        token)
                    .ConfigureAwait(false);
                return (null, bulkStream);
            });
    }
#pragma warning restore CA1416

    private static RemoteCertificateValidationCallback
        CreateCertificateValidator(string? expectedPin) =>
        (_, certificate, _, _) =>
        {
            if (certificate is null || string.IsNullOrWhiteSpace(expectedPin))
                return false;

            var actualPin = certificate.GetCertHashString(
                System.Security.Cryptography.HashAlgorithmName.SHA256);
            return string.Equals(
                actualPin,
                expectedPin,
                StringComparison.OrdinalIgnoreCase);
        };

    private sealed class ReachTransportConnection : IReachTransportConnection
    {
        private readonly ITransportConnection _connection;
        private readonly ITransportStream _controlStream;
        private readonly Func<
            CancellationToken,
            ValueTask<(ITransportConnection? Connection, ITransportStream Stream)>>
            _openMedia;
        private readonly Func<
                CancellationToken,
                ValueTask<(ITransportConnection? Connection, ITransportStream Stream)>>?
            _openBulk;
        private readonly List<(ITransportConnection? Connection, ITransportStream Stream)>
            _childStreams = [];
        private readonly SemaphoreSlim _streamGate = new(1, 1);
        private int _disposed;

        public ReachTransportConnection(
            ITransportConnection connection,
            ITransportStream controlStream,
            Func<
                CancellationToken,
                ValueTask<(ITransportConnection? Connection, ITransportStream Stream)>>
                openMedia,
            Func<
                    CancellationToken,
                    ValueTask<(ITransportConnection? Connection, ITransportStream Stream)>>?
                openBulk)
        {
            _connection = connection;
            _controlStream = controlStream;
            _openMedia = openMedia;
            _openBulk = openBulk;
            Info = connection.Info;
        }

        public TransportConnectionInfo Info { get; }

        public Stream ControlStream => _controlStream.Stream;

        public async ValueTask<Stream> OpenMediaStreamAsync(
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            await _streamGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var media = await _openMedia(cancellationToken)
                    .ConfigureAwait(false);
                _childStreams.Add(media);
                return media.Stream.Stream;
            }
            finally
            {
                _streamGate.Release();
            }
        }

        public async ValueTask<Stream?> OpenBulkStreamAsync(
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (_openBulk is null)
                return null;

            await _streamGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var bulk = await _openBulk(cancellationToken)
                    .ConfigureAwait(false);
                _childStreams.Add(bulk);
                return bulk.Stream.Stream;
            }
            finally
            {
                _streamGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            foreach (var media in _childStreams)
            {
                await media.Stream.DisposeAsync().ConfigureAwait(false);
                if (media.Connection is not null)
                    await media.Connection.DisposeAsync().ConfigureAwait(false);
            }

            await _controlStream.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
            _streamGate.Dispose();
        }
    }
}
