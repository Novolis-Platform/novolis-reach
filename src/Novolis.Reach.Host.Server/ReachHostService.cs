using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Novolis.Reach.Protocol;
using Novolis.Reach.Transport;
using Novolis.Transports;
using Novolis.Transports.Discovery;
using Novolis.Transports.Framing;
using Novolis.Transports.LocalIpc;
using Novolis.Transports.Quic;
using Novolis.Transports.Tcp;
using Novolis.Transports.Tailscale;
using Novolis.Transports.Udp;
using Novolis.Windows.Sessions;

namespace Novolis.Reach.Host.Server;

/// <summary>
/// Headless Reach host service. It accepts clients on Tailscale and bridges
/// session commands to the interactive-session helper over local IPC.
/// </summary>
public sealed class ReachHostService : BackgroundService
{
    private const string OperatorEndpoint = "Novolis.Reach.Host.Windows.Service";
    private const string SessionEndpoint = "Novolis.Reach.Host.Windows";
    private readonly ILogger<ReachHostService> _log;
    private readonly WindowsSessionManager _sessions;
    private readonly ConcurrentDictionary<long, ClientConnection> _clients = new();
    private readonly ConcurrentDictionary<Guid, FileTransferState> _fileTransfers = new();
    private readonly ConcurrentBag<IAsyncDisposable> _listeners = new();
    private readonly ConcurrentBag<IAsyncDisposable> _mediaListeners = new();
    private readonly object _messagesGate = new();
    private readonly List<string> _messages = [];
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ReachPerformanceMetrics _performance = new();
    private ILocalIpcConnection? _sessionConnection;
    private ITransportDatagramChannel? _datagramChannel;
    private long _clientSequence;
    private long _localSequence;
    private string[] _endpoints = [];
    private bool _sharingPaused;
    private int _hostingStopped;
    private DateTimeOffset _nextSessionHelperLaunchAttempt =
        DateTimeOffset.MinValue;

    /// <summary>Creates the host service.</summary>
    public ReachHostService(
        ILogger<ReachHostService> log,
        WindowsSessionManager sessions)
    {
        _log = log;
        _sessions = sessions;
    }

    /// <summary>Gets the current operator status.</summary>
    public ReachHostStatus GetStatus()
    {
        _sessions.TryGetActiveSession(out var session);
        var sessionConnection = Volatile.Read(ref _sessionConnection);
        lock (_messagesGate)
        {
            return new ReachHostStatus(
                _endpoints.Length == 0
                    ? "Waiting for LAN or Tailscale"
                    : Volatile.Read(ref _hostingStopped) != 0
                        ? "Stopped by operator"
                    : sessionConnection is null
                        ? "Waiting for interactive session"
                        : "Running",
                _endpoints,
                _clients.Count,
                _sharingPaused,
                session?.DomainName is { Length: > 0 } domain
                    ? $"{domain}\\{session.UserName}"
                    : session?.UserName,
                _messages.TakeLast(40).ToArray(),
                _performance.Snapshot());
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            _lifetime.Token);
        var cancellationToken = linked.Token;
        Log("Reach host service starting.");

        var addresses = new[] { IPAddress.Loopback }
            .Concat(GetReachableIPv4Addresses())
            .Distinct()
            .ToArray();
        X509Certificate2? quicCertificate = null;
        if (QuicTransportListener.IsSupported)
        {
            try
            {
                quicCertificate = CreateQuicCertificate();
            }
            catch (Exception exception)
            {
                Log($"QUIC certificate initialization failed: {exception.Message}");
            }
        }

        var quicPin = quicCertificate?.GetCertHashString(
            HashAlgorithmName.SHA256);
        _endpoints = addresses
            .SelectMany(address => quicPin is { Length: > 0 }
                ? new[]
                {
                    $"quic://{address}:{ReachProtocol.ControlPort}?pin={quicPin}",
                    $"tcp://{address}:{ReachProtocol.ControlPort}",
                }
                : new[]
                {
                    $"tcp://{address}:{ReachProtocol.ControlPort}",
                })
            .ToArray();
        if (addresses.Length == 0)
            Log("No private IPv4 adapter is available; remote listening is paused.");

        ITransportDatagramChannel? datagramChannel = null;
        try
        {
            datagramChannel = new UdpDatagramChannel(
                new IPEndPoint(IPAddress.Any, ReachProtocol.MediaPort));
            _datagramChannel = datagramChannel;
            Log(
                $"Listening for authenticated Reach UDP media on "
                + $"{datagramChannel.LocalEndPoint}.");
        }
        catch (SocketException exception)
        {
            Log(
                $"Could not listen for Reach UDP media on port "
                + $"{ReachProtocol.MediaPort}: {exception.Message}");
        }

        var tasks = new List<Task>
        {
            RunOperatorIpcAsync(cancellationToken),
            RunSessionBridgeAsync(cancellationToken),
            RunDiscoveryAsync(cancellationToken),
        };
        if (datagramChannel is not null)
            tasks.Add(RunDatagramMediaListenerAsync(datagramChannel, cancellationToken));
        foreach (var address in addresses)
        {
            tasks.Add(RunRemoteListenerAsync(address, cancellationToken));
            tasks.Add(RunMediaListenerAsync(address, cancellationToken));
            if (quicCertificate is not null)
            {
                tasks.Add(RunQuicListenerAsync(
                    address,
                    quicCertificate,
                    cancellationToken));
            }
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var listener in _listeners)
                await listener.DisposeAsync().ConfigureAwait(false);
            foreach (var listener in _mediaListeners)
                await listener.DisposeAsync().ConfigureAwait(false);
            if (datagramChannel is not null)
                await datagramChannel.DisposeAsync().ConfigureAwait(false);
            _datagramChannel = null;
            foreach (var client in _clients.Values)
                await client.DisposeAsync().ConfigureAwait(false);
            quicCertificate?.Dispose();
            var sessionConnection = Interlocked.Exchange(ref _sessionConnection, null);
            if (sessionConnection is not null)
                await sessionConnection.DisposeAsync().ConfigureAwait(false);
            Log("Reach host service stopped.");
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetime.Cancel();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunRemoteListenerAsync(
        IPAddress address,
        CancellationToken cancellationToken)
    {
        TcpTransportListener listener;
        try
        {
            listener = new TcpTransportListener(
                new IPEndPoint(address, ReachProtocol.ControlPort));
        }
        catch (SocketException exception)
        {
            Log($"Could not listen for Reach clients on {address}:{ReachProtocol.ControlPort}: {exception.Message}");
            return;
        }

        _listeners.Add(listener);
        Log($"Listening for Reach clients on {address}:{ReachProtocol.ControlPort}.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var transport = await listener.AcceptConnectionAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
                var id = Interlocked.Increment(ref _clientSequence);
                var connection = await ClientConnection.CreateAsync(
                        id,
                        transport,
                        cancellationToken)
                    .ConfigureAwait(false);
                _clients[id] = connection;
                _ = HandleRemoteClientAsync(connection, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException exception)
        {
            Log($"Reach listener on {address} stopped: {exception.Message}");
        }
        finally
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunQuicListenerAsync(
        IPAddress address,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        QuicTransportListener listener;
        try
        {
            listener = await QuicTransportListener.ListenAsync(
                    new IPEndPoint(address, ReachProtocol.ControlPort),
                    new QuicTransportOptions
                    {
                        ServerCertificate = certificate,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Log(
                $"Could not listen for Reach QUIC clients on "
                + $"{address}:{ReachProtocol.ControlPort}: {exception.Message}");
            return;
        }

        _listeners.Add(listener);
        Log(
            $"Listening for Reach QUIC clients on "
            + $"{address}:{ReachProtocol.ControlPort}.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ITransportConnection transport;
                try
                {
                    transport = await listener.AcceptConnectionAsync(
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    var id = Interlocked.Increment(ref _clientSequence);
                    var connection = await ClientConnection.CreateAsync(
                            id,
                            transport,
                            cancellationToken)
                        .ConfigureAwait(false);
                    _clients[id] = connection;
                    _ = HandleRemoteClientAsync(connection, cancellationToken);
                }
                catch
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log($"Reach QUIC listener on {address} stopped: {exception.Message}");
        }
        finally
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunMediaListenerAsync(
        IPAddress address,
        CancellationToken cancellationToken)
    {
        TcpTransportListener listener;
        try
        {
            listener = new TcpTransportListener(
                new IPEndPoint(address, ReachProtocol.MediaPort));
        }
        catch (SocketException exception)
        {
            Log($"Could not listen for Reach media on {address}:{ReachProtocol.MediaPort}: {exception.Message}");
            return;
        }

        _mediaListeners.Add(listener);
        Log($"Listening for Reach media on {address}:{ReachProtocol.MediaPort}.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var transport = await listener.AcceptConnectionAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
                _ = HandleMediaClientAsync(transport, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException exception)
        {
            Log($"Reach media listener on {address} stopped: {exception.Message}");
        }
        finally
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunDatagramMediaListenerAsync(
        ITransportDatagramChannel channel,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var datagram = await channel.ReceiveAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!ReachDatagramPacketCodec.TryDecodeHandshake(
                        datagram.Payload,
                        out var sessionId,
                        out var token))
                {
                    continue;
                }

                var connection = _clients.Values.FirstOrDefault(candidate =>
                    candidate.IsReady
                    && candidate.DatagramSession?.SessionId == sessionId
                    && string.Equals(
                        candidate.DatagramSession.Token,
                        token,
                        StringComparison.Ordinal)
                    && IsSameRemoteAddress(
                        candidate.RemoteEndPoint,
                        datagram.RemoteEndpoint));
                if (connection is null)
                    continue;

                connection.AttachDatagram(channel, datagram.RemoteEndpoint);
                Log(
                    $"Authenticated UDP media for client {connection.Id} from "
                    + $"{datagram.RemoteEndpoint}.");
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log($"Reach UDP media listener stopped: {exception.Message}");
        }
    }

    private static bool IsSameRemoteAddress(
        EndPoint expected,
        EndPoint actual) =>
        expected is IPEndPoint expectedIp
        && actual is IPEndPoint actualIp
        && expectedIp.Address.Equals(actualIp.Address);

    private async Task HandleQuicMediaStreamAsync(
        ClientConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var transportStream = await connection.Transport
                        .AcceptInboundStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                _ = HandleQuicInboundStreamAsync(
                    connection,
                    transportStream,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log($"Reach QUIC stream accept loop ended: {exception.Message}");
        }
    }

    private async Task HandleQuicInboundStreamAsync(
        ClientConnection connection,
        ITransportStream transportStream,
        CancellationToken cancellationToken)
    {
        var attached = false;
        try
        {
            var envelope = await ReadEnvelopeAsync(
                    transportStream.Stream,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new EndOfStreamException(
                    "Reach QUIC stream closed during hello.");
            switch (envelope.Type)
            {
                case ReachMessageType.MediaHello:
                {
                    var hello = ReachMessageCodec.ReadBody<ReachMediaHello>(envelope);
                    ValidateQuicStreamHello(
                        hello.AppId,
                        hello.ProtocolVersion,
                        hello.SessionId,
                        connection.SessionId,
                        "media");
                    connection.AttachMediaStream(connection.Transport, transportStream);
                    attached = true;
                    Log($"QUIC media stream attached to client {connection.Id}.");
                    await transportStream.Stream.CopyToAsync(
                            Stream.Null,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
                case ReachMessageType.BulkHello:
                {
                    var hello = ReachMessageCodec.ReadBody<ReachBulkHello>(envelope);
                    ValidateQuicStreamHello(
                        hello.AppId,
                        hello.ProtocolVersion,
                        hello.SessionId,
                        connection.SessionId,
                        "bulk");
                    Log($"QUIC bulk stream attached to client {connection.Id}.");
                    await HandleQuicBulkStreamAsync(
                            connection,
                            transportStream.Stream,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
                default:
                    throw new InvalidDataException(
                        "Reach QUIC stream did not send MediaHello or BulkHello.");
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log($"Reach QUIC stream ended: {exception.Message}");
        }
        finally
        {
            if (attached)
                connection.DetachMediaStream(transportStream.Stream);
            else
                await transportStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleQuicBulkStreamAsync(
        ClientConnection connection,
        Stream stream,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await ReadEnvelopeAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (envelope is null)
                return;

            switch (envelope.Type)
            {
                case ReachMessageType.FileOffer:
                    await HandleFileOfferAsync(
                            connection,
                            ReachMessageCodec.ReadBody<ReachFileOffer>(envelope),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case ReachMessageType.FileChunk:
                    await HandleFileChunkAsync(
                            ReachMessageCodec.ReadBody<ReachFileChunk>(envelope),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case ReachMessageType.FileComplete:
                    await HandleFileCompleteAsync(
                            connection,
                            ReachMessageCodec.ReadBody<ReachFileComplete>(envelope),
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException(
                        "Reach QUIC bulk stream carried an unsupported message.");
            }
        }
    }

    private static void ValidateQuicStreamHello(
        string appId,
        string protocolVersion,
        Guid sessionId,
        Guid expectedSessionId,
        string streamName)
    {
        if (!string.Equals(appId, ReachProtocol.AppId, StringComparison.Ordinal)
            || !ReachProtocol.IsCompatible(protocolVersion)
            || sessionId != expectedSessionId)
        {
            throw new InvalidDataException(
                $"Incompatible Reach QUIC {streamName} hello.");
        }
    }

    private async Task HandleMediaClientAsync(
        ITransportConnection transport,
        CancellationToken cancellationToken)
    {
        ITransportStream? transportStream = null;
        ClientConnection? connection = null;
        var attached = false;
        try
        {
            transportStream = await transport.AcceptInboundStreamAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            var envelope = await ReadEnvelopeAsync(
                    transportStream.Stream,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new EndOfStreamException("Reach media client closed during hello.");
            if (envelope.Type != ReachMessageType.MediaHello)
                throw new InvalidDataException("Reach media connection did not send MediaHello.");

            var hello = ReachMessageCodec.ReadBody<ReachMediaHello>(envelope);
            if (!string.Equals(hello.AppId, ReachProtocol.AppId, StringComparison.Ordinal)
                || !ReachProtocol.IsCompatible(hello.ProtocolVersion))
            {
                throw new InvalidDataException("Incompatible Reach media hello.");
            }

            var remoteAddress =
                (transport.Info.RemoteEndPoint as IPEndPoint)?.Address;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                connection = _clients.Values.FirstOrDefault(candidate =>
                    candidate.IsReady
                    && candidate.SessionId == hello.SessionId
                    && (remoteAddress is null
                        || (candidate.RemoteEndPoint as IPEndPoint)?.Address.Equals(remoteAddress) == true));
                if (connection is not null)
                    break;

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (connection is null)
                throw new InvalidDataException("Reach media hello has no active control session.");

            connection.AttachMediaStream(transport, transportStream);
            attached = true;
            Log($"Media channel attached to client {connection.Id}.");
            await transportStream.Stream.CopyToAsync(
                    Stream.Null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log($"Reach media connection ended: {exception.Message}");
        }
        finally
        {
            if (connection is not null && transportStream is not null && attached)
                connection.DetachMediaStream(transportStream.Stream);
            else
            {
                if (transportStream is not null)
                    await transportStream.DisposeAsync().ConfigureAwait(false);
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HandleRemoteClientAsync(
        ClientConnection connection,
        CancellationToken cancellationToken)
    {
        Log($"Client {connection.Id} connected from {connection.RemoteEndPoint}.");
        try
        {
            var hello = await ReadMessageAsync<ReachClientHello>(
                    ReachMessageType.ClientHello,
                    connection.Stream,
                    cancellationToken)
                .ConfigureAwait(false);
            Log($"Client {connection.Id} sent {hello.Platform} hello.");
            if (!string.Equals(hello.AppId, ReachProtocol.AppId, StringComparison.Ordinal)
                || !ReachProtocol.IsCompatible(hello.ProtocolVersion))
            {
                throw new InvalidDataException("Incompatible Reach client hello.");
            }
            if (Volatile.Read(ref _hostingStopped) != 0)
                throw new InvalidOperationException(
                    "Reach hosting has been stopped by the operator.");

            var clientCapabilities = await ReadMessageAsync<ReachCapabilitiesMessage>(
                    ReachMessageType.ClientCapabilities,
                    connection.Stream,
                    cancellationToken).ConfigureAwait(false);
            Log($"Client {connection.Id} capabilities received.");
            var negotiated = ReachCapabilities.Intersect(
                ReachCapabilities.WindowsHost,
                clientCapabilities.Capabilities);
            connection.Capabilities = negotiated;
            await connection.SendAsync(
                    ReachMessageType.HostHello,
                    new ReachHostHello(
                        ReachProtocol.AppId,
                        ReachProtocol.Version,
                        Environment.MachineName,
                        _endpoints),
                    cancellationToken)
                .ConfigureAwait(false);
            await connection.SendAsync(
                    ReachMessageType.HostCapabilities,
                    new ReachCapabilitiesMessage(negotiated),
                    cancellationToken).ConfigureAwait(false);
            connection.IsReady = true;
            Log($"Client {connection.Id} handshake complete.");
            if (connection.Transport.Info.Kind == TransportKind.Quic)
            {
                _ = HandleQuicMediaStreamAsync(
                    connection,
                    cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await ReadEnvelopeAsync(
                        connection.Stream,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (envelope is null)
                    return;

                if (envelope.Type == ReachMessageType.LatencyProbe)
                {
                    var probe = ReachMessageCodec.ReadBody<ReachLatencyProbe>(envelope);
                    await connection.SendAsync(
                            ReachMessageType.LatencyResponse,
                            new ReachLatencyResponse(
                                probe.Id,
                                probe.SentUtcTicks),
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (envelope.Type == ReachMessageType.SessionClose)
                {
                    var close = ReachMessageCodec.ReadBody<ReachSessionClose>(envelope);
                    connection.SessionId = close.SessionId;
                    if (!_clients.Values.Any(client =>
                            client.IsReady && client.Id != connection.Id))
                    {
                        connection.SessionCloseForwarded = true;
                        await SendSessionCommandAsync(
                                ReachMessageType.SessionClose,
                                close,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    return;
                }

                if (envelope.Type == ReachMessageType.SessionOpen)
                {
                    var open = ReachMessageCodec.ReadBody<ReachSessionOpen>(envelope);
                    if (!await WaitForSessionConnectionAsync(cancellationToken)
                            .ConfigureAwait(false))
                    {
                        await SendSessionEndedAsync(
                                connection,
                                "The interactive Reach host is unavailable.",
                                cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    connection.SessionId = open.SessionId;
                    connection.RequestedDisplayId = open.RequestedDisplayId;
                    connection.EnableAudio = open.EnableAudio
                        && connection.Capabilities?.Supports(ReachCapability.Audio) == true;
                    await OfferDatagramMediaAsync(
                            connection,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await SendSessionCommandAsync(
                            ReachMessageType.SessionOpen,
                            open with
                            {
                                EnableAudio = connection.EnableAudio,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    await SendSharingStateAsync(
                            connection,
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (envelope.Type == ReachMessageType.SessionResume)
                {
                    var resume = ReachMessageCodec.ReadBody<ReachSessionResume>(envelope);
                    if (!await WaitForSessionConnectionAsync(cancellationToken)
                            .ConfigureAwait(false))
                    {
                        await SendSessionEndedAsync(
                                connection,
                                "The interactive Reach host is unavailable.",
                                cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    connection.SessionId = resume.SessionId;
                    connection.LastVideoSequence = resume.LastVideoSequence;
                    connection.EnableAudio = resume.EnableAudio
                        && connection.Capabilities?.Supports(ReachCapability.Audio) == true;
                    await OfferDatagramMediaAsync(
                            connection,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await SendSessionCommandAsync(
                            ReachMessageType.SessionResume,
                            resume with
                            {
                                EnableAudio = connection.EnableAudio,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    await SendSharingStateAsync(
                            connection,
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (envelope.Type == ReachMessageType.FileOffer)
                {
                    await HandleFileOfferAsync(
                            connection,
                            ReachMessageCodec.ReadBody<ReachFileOffer>(envelope),
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (envelope.Type == ReachMessageType.FileChunk)
                {
                    await HandleFileChunkAsync(
                            ReachMessageCodec.ReadBody<ReachFileChunk>(envelope),
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (envelope.Type == ReachMessageType.FileComplete)
                {
                    await HandleFileCompleteAsync(
                            connection,
                            ReachMessageCodec.ReadBody<ReachFileComplete>(envelope),
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (envelope.Type is ReachMessageType.PointerMove
                    or ReachMessageType.PointerButton
                    or ReachMessageType.PointerWheel
                    or ReachMessageType.KeyDown
                    or ReachMessageType.KeyUp
                    or ReachMessageType.TextInput
                    or ReachMessageType.ClipboardChanged
                    or ReachMessageType.ClipboardContent
                    or ReachMessageType.DisplaySelect
                    or ReachMessageType.DisplayResize
                    or ReachMessageType.VideoStreamConfiguration
                    or ReachMessageType.RequestKeyFrame
                    or ReachMessageType.AudioStreamConfiguration)
                {
                    await ForwardToSessionAsync(envelope, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log($"Client {connection.Id} ended: {exception.Message}");
        }
        finally
        {
            _clients.TryRemove(connection.Id, out _);
            if (connection.IsReady
                && !connection.SessionCloseForwarded
                && !_clients.Values.Any(static client => client.IsReady))
            {
                try
                {
                    await SendSessionCommandAsync(
                            ReachMessageType.SessionClose,
                            new ReachSessionClose(
                                connection.SessionId,
                                "Client connection ended."),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }
            foreach (var transfer in _fileTransfers.Values.Where(
                         transfer => transfer.ClientId == connection.Id))
            {
                if (_fileTransfers.TryRemove(transfer.TransferId, out var removed))
                    await removed.DisposeAsync().ConfigureAwait(false);
            }
            await connection.DisposeAsync().ConfigureAwait(false);
            Log($"Client {connection.Id} disconnected.");
        }
    }

    private async Task RunOperatorIpcAsync(CancellationToken cancellationToken)
    {
        await using var listener = LocalIpcTransport.CreateListener(
            new LocalIpcEndpoint(OperatorEndpoint));
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var connection = await listener.AcceptAsync(cancellationToken)
                .ConfigureAwait(false);
            await foreach (var frame in connection.ReadAllAsync(cancellationToken))
            {
                if (!string.Equals(frame.Kind, "operator", StringComparison.Ordinal))
                    continue;

                var request = ReachMessageCodec.ReadBody<ReachHostControlRequest>(
                    ReachMessageCodec.Deserialize(frame.Payload));
                var response = HandleOperatorRequest(request);
                await connection.SendAsync(
                    new LocalIpcFrame(
                        Interlocked.Increment(ref _localSequence),
                        "operator",
                        "response",
                        ReachMessageCodec.Serialize(
                            ReachMessageType.HostStatus,
                            frame.Sequence,
                            response)),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private ReachHostControlResponse HandleOperatorRequest(ReachHostControlRequest request)
    {
        switch (request.Command)
        {
            case ReachHostCommand.SetSharingPaused:
                _sharingPaused = request.Enabled ?? false;
                Log(_sharingPaused ? "Sharing paused by operator." : "Sharing resumed by operator.");
                _ = BroadcastSharingStateAsync(_sharingPaused);
                return new ReachHostControlResponse(true, "Sharing state changed.", GetStatus());
            case ReachHostCommand.ReconnectHelper:
                _ = ReconnectInteractiveHelperAsync();
                return new ReachHostControlResponse(
                    true,
                    "Interactive helper reconnect requested.",
                    GetStatus());
            case ReachHostCommand.StopHosting:
                Interlocked.Exchange(ref _hostingStopped, 1);
                _sharingPaused = true;
                _ = StopHostingAsync();
                return new ReachHostControlResponse(
                    true,
                    "Reach hosting stopped until the service restarts.",
                    GetStatus());
            case ReachHostCommand.GetLogs:
            case ReachHostCommand.GetStatus:
                return new ReachHostControlResponse(true, "Host status.", GetStatus());
            default:
                return new ReachHostControlResponse(false, "Unknown host command.", GetStatus());
        }
    }

    private async Task ReconnectInteractiveHelperAsync()
    {
        var connection = Interlocked.Exchange(ref _sessionConnection, null);
        if (connection is not null)
            await connection.DisposeAsync().ConfigureAwait(false);
        Log("Interactive helper reconnect requested by operator.");
    }

    private async Task StopHostingAsync()
    {
        try
        {
            await NotifySessionEndedAsync(
                    "Reach hosting was stopped by the operator.",
                    _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task BroadcastSharingStateAsync(bool paused)
    {
        try
        {
            var sends = _clients.Values
                .Where(static client => client.IsReady)
                .Select(client => client.SendAsync(
                    ReachMessageType.SharingState,
                    new ReachSharingState(
                        paused,
                        paused
                            ? "Sharing is paused by the host operator."
                            : "Sharing is active."),
                    _lifetime.Token).AsTask());
            await Task.WhenAll(sends).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private ValueTask SendSharingStateAsync(
        ClientConnection connection,
        CancellationToken cancellationToken) =>
        connection.SendAsync(
            ReachMessageType.SharingState,
            new ReachSharingState(
                _sharingPaused,
                _sharingPaused
                    ? "Sharing is paused by the host operator."
                    : "Sharing is active."),
            cancellationToken);

    private async Task RunSessionBridgeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ILocalIpcConnection? connection = null;
            try
            {
                connection = await TryConnectSessionHelperAsync(
                        cancellationToken,
                        TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
                if (connection is null)
                {
                    TryStartSessionHelper();
                    connection = await TryConnectSessionHelperAsync(
                            cancellationToken,
                            TimeSpan.FromSeconds(5))
                        .ConfigureAwait(false);
                }

                if (connection is null)
                {
                    Log("Interactive session helper is not ready yet.");
                    await Task.Delay(
                            TimeSpan.FromSeconds(3),
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                Interlocked.Exchange(ref _sessionConnection, connection);
                Log("Interactive session helper connected.");
                var pendingClient = _clients.Values
                    .Where(static client => client.IsReady)
                    .OrderBy(static client => client.Id)
                    .FirstOrDefault();
                if (pendingClient is not null)
                {
                    await SendSessionCommandAsync(
                            ReachMessageType.SessionOpen,
                            new ReachSessionOpen(
                                pendingClient.SessionId,
                                pendingClient.RequestedDisplayId,
                                pendingClient.EnableAudio),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await foreach (var frame in connection.ReadAllAsync(cancellationToken))
                {
                    if (!Enum.TryParse<ReachMessageType>(
                            frame.Name,
                            ignoreCase: true,
                            out var messageType))
                    {
                        continue;
                    }

                    if (!IsHostToClientFrame(frame.Kind, messageType))
                        continue;
                    if (_sharingPaused && frame.Kind == "media")
                        continue;

                    if (messageType == ReachMessageType.VideoFrame)
                    {
                        var video = ReachMessageCodec.ReadBody<ReachVideoFrame>(
                            ReachMessageCodec.Deserialize(frame.Payload));
                        _performance.RecordReceived(
                            video.AccessUnit.Length,
                            video.Timestamp);
                    }

                    await BroadcastPayloadAsync(
                            messageType,
                            frame.Payload,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Log($"Interactive session helper unavailable: {exception.Message}");
            }
            finally
            {
                var activeConnection = Interlocked.CompareExchange(
                    ref _sessionConnection,
                    null,
                    connection);
                if (ReferenceEquals(activeConnection, connection)
                    && connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }

                if (connection is not null
                    && !cancellationToken.IsCancellationRequested)
                {
                    await NotifySessionEndedAsync(
                            "The interactive Reach host connection ended.",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<ILocalIpcConnection?> TryConnectSessionHelperAsync(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            var client = LocalIpcTransport.CreateClient();
            return await client.ConnectAsync(
                    new LocalIpcEndpoint(SessionEndpoint),
                    timeoutCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static bool IsHostToClientFrame(
        string kind,
        ReachMessageType type) =>
        kind switch
        {
            "control" => type is ReachMessageType.VideoStreamStart
                or ReachMessageType.VideoStreamReset
                or ReachMessageType.DisplayTopology
                or ReachMessageType.AudioStreamStart
                or ReachMessageType.ClipboardContent,
            "media" => type is ReachMessageType.VideoFrame
                or ReachMessageType.AudioFrame,
            _ => false,
        };

    private void TryStartSessionHelper()
    {
        if (Volatile.Read(ref _hostingStopped) != 0)
            return;

        var now = DateTimeOffset.UtcNow;
        if (now < _nextSessionHelperLaunchAttempt)
            return;

        _nextSessionHelperLaunchAttempt = now.AddSeconds(5);
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "Novolis.Reach.Host.Windows.exe");
        if (!File.Exists(executable))
        {
            Log("Reach host executable is not beside the service; waiting for an independently started host.");
            return;
        }

        if (_sessions.IsProcessRunningInActiveSession(executable))
        {
            Log("Interactive Reach host is already running; waiting for its IPC endpoint.");
            return;
        }

        if (_sessions.TryStartInActiveSession(
                executable,
                string.Empty,
                out var errorCode))
        {
            Log("Started the interactive Reach host.");
        }
        else
        {
            Log($"Could not start the interactive Reach host (Win32 {errorCode}).");
        }
    }

    private async Task<bool> WaitForSessionConnectionAsync(
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (Volatile.Read(ref _sessionConnection) is not null)
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        return Volatile.Read(ref _sessionConnection) is not null;
    }

    private async Task OfferDatagramMediaAsync(
        ClientConnection connection,
        CancellationToken cancellationToken)
    {
        var channel = Volatile.Read(ref _datagramChannel);
        if (connection.Transport.Info.Kind != TransportKind.Quic
            || channel is null
            || channel.LocalEndPoint is not IPEndPoint localEndpoint)
        {
            connection.ClearDatagram();
            return;
        }

        var session = new ReachDatagramSession(
            connection.SessionId,
            RandomNumberGenerator.GetBytes(32),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
        connection.SetDatagramSession(session);
        await connection.SendAsync(
                ReachMessageType.DatagramOffer,
                new ReachDatagramOffer(
                    session.SessionId,
                    localEndpoint.Port,
                    session.Token,
                    Convert.ToBase64String(session.Key)),
                cancellationToken)
            .ConfigureAwait(false);
        Log($"Offered authenticated UDP media to client {connection.Id}.");
    }

    private async Task SendSessionEndedAsync(
        ClientConnection connection,
        string reason,
        CancellationToken cancellationToken)
    {
        connection.SessionCloseForwarded = true;
        await connection.SendAsync(
                ReachMessageType.SessionClose,
                new ReachSessionClose(connection.SessionId, reason),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task NotifySessionEndedAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        var sends = _clients.Values
            .Where(static client => client.IsReady)
            .Select(client => NotifySessionEndedAsync(
                client,
                reason,
                cancellationToken));
        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    private async Task NotifySessionEndedAsync(
        ClientConnection connection,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendSessionEndedAsync(
                    connection,
                    reason,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or ObjectDisposedException
                or SocketException
                or InvalidOperationException)
        {
            Log($"Could not notify client {connection.Id} that the session ended: {exception.Message}");
        }
    }

    private async Task ForwardToSessionAsync(
        ReachMessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var connection = Volatile.Read(ref _sessionConnection)
            ?? throw new InvalidOperationException(
                "The interactive Reach host is not connected.");

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.SendAsync(
                new LocalIpcFrame(
                    Interlocked.Increment(ref _localSequence),
                    "control",
                    envelope.Type.ToString(),
                    ReachMessageCodec.Serialize(
                        envelope.Type,
                        envelope.Sequence,
                        envelope.Body)),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task SendSessionCommandAsync<T>(
        ReachMessageType type,
        T message,
        CancellationToken cancellationToken)
    {
        var connection = _sessionConnection;
        if (connection is null)
            return;

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.SendAsync(
                new LocalIpcFrame(
                    Interlocked.Increment(ref _localSequence),
                    "control",
                    type.ToString(),
                    ReachMessageCodec.Serialize(
                        type,
                        Interlocked.Increment(ref _localSequence),
                        message)),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task BroadcastPayloadAsync(
        ReachMessageType messageType,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var sends = _clients.Values
            .Where(client =>
                client.IsReady
                && (messageType is not ReachMessageType.AudioStreamStart
                    and not ReachMessageType.AudioFrame
                    || client.Capabilities?.Supports(ReachCapability.Audio) == true))
            .Select(client => BroadcastPayloadToClientAsync(
                client,
                payload,
                messageType is ReachMessageType.VideoFrame
                    or ReachMessageType.AudioFrame,
                messageType is ReachMessageType.VideoFrame,
                cancellationToken));
        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    private async Task BroadcastPayloadToClientAsync(
        ClientConnection connection,
        byte[] payload,
        bool media,
        bool latestFrame,
        CancellationToken cancellationToken)
    {
        try
        {
            var dropped = await connection.SendPayloadForChannelAsync(
                    payload,
                    media,
                    latestFrame,
                    cancellationToken)
                .ConfigureAwait(false);
            if (media)
                _performance.RecordSent(payload.Length);
            if (dropped)
                _performance.RecordDropped();
        }
        catch (Exception exception) when (
            exception is IOException
                or ObjectDisposedException
                or SocketException
                or InvalidOperationException)
        {
            Log($"Client {connection.Id} dropped during broadcast: {exception.Message}");
        }
    }

    private async Task HandleFileOfferAsync(
        ClientConnection connection,
        ReachFileOffer offer,
        CancellationToken cancellationToken)
    {
        const long maximumLength = 2L * 1024 * 1024 * 1024;
        if (offer.Length < 0 || offer.Length > maximumLength)
            throw new InvalidDataException("Reach file offer exceeds the host limit.");

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Novolis",
            "Reach",
            "Incoming");
        Directory.CreateDirectory(directory);
        var safeName = Path.GetFileName(offer.Name);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new InvalidDataException("Reach file offer has no file name.");

        var path = Path.Combine(directory, $"{offer.TransferId:N}-{safeName}");
        var transfer = new FileTransferState(
            connection.Id,
            offer.TransferId,
            path,
            offer.Length,
            offer.Hash,
            new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan));
        if (!_fileTransfers.TryAdd(offer.TransferId, transfer))
        {
            await transfer.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Reach transfer id was already active.");
        }

        Log($"Receiving {safeName} ({offer.Length} bytes) from client {connection.Id}.");
        await connection.SendAsync(
                ReachMessageType.FileOffer,
                offer,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleFileChunkAsync(
        ReachFileChunk chunk,
        CancellationToken cancellationToken)
    {
        if (!_fileTransfers.TryGetValue(chunk.TransferId, out var transfer))
            throw new InvalidDataException("Reach file chunk has no active offer.");
        if (chunk.Offset != transfer.BytesWritten)
            throw new InvalidDataException("Reach file chunk offset is not sequential.");
        if (chunk.Data.Length > 1024 * 1024
            || transfer.BytesWritten + chunk.Data.Length > transfer.ExpectedLength)
        {
            throw new InvalidDataException("Reach file chunk exceeds its offer.");
        }

        await transfer.Stream.WriteAsync(chunk.Data, cancellationToken).ConfigureAwait(false);
        transfer.BytesWritten += chunk.Data.Length;
    }

    private async Task HandleFileCompleteAsync(
        ClientConnection connection,
        ReachFileComplete complete,
        CancellationToken cancellationToken)
    {
        if (!_fileTransfers.TryRemove(complete.TransferId, out var transfer))
            throw new InvalidDataException("Reach file completion has no active offer.");

        var success = complete.Succeeded
            && transfer.BytesWritten == transfer.ExpectedLength;
        await transfer.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await transfer.DisposeAsync().ConfigureAwait(false);
        if (success)
        {
            await using var receivedFile = File.OpenRead(transfer.Path);
            var actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(receivedFile, cancellationToken)
                    .ConfigureAwait(false));
            success = string.Equals(
                actualHash,
                transfer.ExpectedHash,
                StringComparison.OrdinalIgnoreCase);
        }

        Log(success
            ? $"Received file {transfer.Path}."
            : $"Rejected incomplete or invalid file transfer {complete.TransferId}.");
        await connection.SendAsync(
                ReachMessageType.FileComplete,
                complete with
                {
                    Succeeded = success,
                    Error = success ? null : "Host validation failed.",
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunDiscoveryAsync(CancellationToken cancellationToken)
    {
        await using var responder = new DiscoveryResponder(
            new IPEndPoint(IPAddress.Any, ReachProtocol.DiscoveryPort),
            ReachProtocol.DiscoveryProbe,
            new DiscoveryBeacon(
                ReachProtocol.AppId,
                ReachProtocol.Version,
                Environment.MachineName,
                _endpoints));
        await responder.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    private static X509Certificate2 CreateQuicCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=Novolis Reach"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        var password = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));
        var pfx = generated.Export(X509ContentType.Pfx, password);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pfx,
                password,
                X509KeyStorageFlags.MachineKeySet
                    | X509KeyStorageFlags.PersistKeySet,
                null);
        }
        catch (CryptographicException)
        {
            return X509CertificateLoader.LoadPkcs12(
                pfx,
                password,
                X509KeyStorageFlags.UserKeySet
                    | X509KeyStorageFlags.PersistKeySet,
                null);
        }
    }

    private static IReadOnlyList<IPAddress> GetReachableIPv4Addresses()
    {
        var addresses = new List<IPAddress>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (var address in networkInterface
                         .GetIPProperties()
                         .UnicastAddresses
                         .Select(static item => item.Address)
                         .Where(static item =>
                             item.AddressFamily == AddressFamily.InterNetwork
                             && !IPAddress.IsLoopback(item))
                         .Where(IsPrivateOrTailscaleIPv4))
            {
                if (!addresses.Contains(address))
                    addresses.Add(address);
            }
        }

        return addresses;
    }

    private static bool IsPrivateOrTailscaleIPv4(IPAddress address)
    {
        if (TailscaleAddressEnumerator.IsTailscaleIPv4(address))
            return true;

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static async Task<T> ReadMessageAsync<T>(
        ReachMessageType expectedType,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var envelope = await ReadEnvelopeAsync(stream, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new EndOfStreamException("Reach client closed the control stream.");
        return ReachMessageCodec.ReadBody<T>(envelope, expectedType);
    }

    private static async Task<ReachMessageEnvelope?> ReadEnvelopeAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var frame = await LengthPrefixedFrameCodec.ReadAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return frame is null ? null : ReachMessageCodec.Deserialize(frame.Payload);
    }

    private void Log(string message)
    {
        _log.LogInformation("{Message}", message);
        lock (_messagesGate)
            _messages.Add($"{DateTimeOffset.Now:HH:mm:ss} {message}");
    }

    private sealed class ClientConnection : IAsyncDisposable
    {
        private readonly ITransportConnection _transport;
        private readonly ITransportStream _controlTransportStream;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly SemaphoreSlim _mediaSendGate = new(1, 1);
        private readonly object _mediaStateGate = new();
        private readonly Channel<byte[]> _latestMedia =
            Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                });
        private readonly CancellationTokenSource _mediaLifetime = new();
        private readonly Task _mediaSendTask;
        private ITransportStream? _mediaTransportStream;
        private ITransportConnection? _mediaTransport;
        private Stream? _mediaStream;
        private ITransportDatagramChannel? _datagramChannel;
        private IPEndPoint? _datagramEndpoint;
        private ReachDatagramSession? _datagramSession;
        private long _datagramSequence;
        private int _disposeStarted;

        private ClientConnection(
            long id,
            ITransportConnection transport,
            ITransportStream controlTransportStream)
        {
            Id = id;
            _transport = transport;
            _controlTransportStream = controlTransportStream;
            _mediaSendTask = Task.Run(
                () => MediaSendLoopAsync(_mediaLifetime.Token));
        }

        public static async ValueTask<ClientConnection> CreateAsync(
            long id,
            ITransportConnection transport,
            CancellationToken cancellationToken)
        {
            var stream = await transport.AcceptInboundStreamAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            return new ClientConnection(id, transport, stream);
        }

        public long Id { get; }
        public ITransportConnection Transport => _transport;
        public Stream Stream => _controlTransportStream.Stream;
        public EndPoint RemoteEndPoint => _transport.Info.RemoteEndPoint;
        public bool IsReady { get; set; }
        public ReachCapabilities? Capabilities { get; set; }
        public Guid SessionId { get; set; }
        public string RequestedDisplayId { get; set; } = string.Empty;
        public long LastVideoSequence { get; set; }
        public bool EnableAudio { get; set; }
        public bool SessionCloseForwarded { get; set; }
        public bool HasMediaChannel => Volatile.Read(ref _mediaStream) is not null;
        public ReachDatagramSession? DatagramSession =>
            Volatile.Read(ref _datagramSession);

        public void SetDatagramSession(ReachDatagramSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            session.Validate();
            ClearDatagram();
            Volatile.Write(ref _datagramSession, session);
        }

        public void AttachDatagram(
            ITransportDatagramChannel channel,
            EndPoint remoteEndpoint)
        {
            ArgumentNullException.ThrowIfNull(channel);
            if (remoteEndpoint is not IPEndPoint ipEndpoint)
                return;

            Volatile.Write(ref _datagramChannel, channel);
            Volatile.Write(
                ref _datagramEndpoint,
                new IPEndPoint(ipEndpoint.Address, ipEndpoint.Port));
        }

        public void ClearDatagram()
        {
            Volatile.Write(ref _datagramEndpoint, null);
            Volatile.Write(ref _datagramChannel, null);
            Volatile.Write(ref _datagramSession, null);
        }

        public void AttachMediaStream(
            ITransportConnection owner,
            ITransportStream stream)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(stream);
            ITransportStream? previousStream;
            ITransportConnection? previousTransport;
            lock (_mediaStateGate)
            {
                previousStream = _mediaTransportStream;
                previousTransport = _mediaTransport;
                _mediaTransportStream = stream;
                _mediaTransport = ReferenceEquals(owner, _transport)
                    ? null
                    : owner;
                _mediaStream = stream.Stream;
            }

            previousStream?.Stream.Dispose();
            DisposeSynchronously(previousTransport);
        }

        public void DetachMediaStream(Stream stream)
        {
            ITransportStream? transportStream = null;
            ITransportConnection? transport = null;
            lock (_mediaStateGate)
            {
                if (!ReferenceEquals(_mediaStream, stream))
                    return;

                _mediaStream = null;
                transportStream = _mediaTransportStream;
                transport = _mediaTransport;
                _mediaTransportStream = null;
                _mediaTransport = null;
            }

            transportStream?.Stream.Dispose();
            DisposeSynchronously(transport);
        }

        public async ValueTask SendAsync<T>(
            ReachMessageType type,
            T message,
            CancellationToken cancellationToken)
        {
            await SendPayloadAsync(
                ReachMessageCodec.Serialize(
                    type,
                    DateTime.UtcNow.Ticks,
                    message),
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask SendPayloadAsync(
            byte[] payload,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await LengthPrefixedFrameCodec.WriteAsync(
                        Stream,
                        payload,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        public async ValueTask<bool> SendPayloadForChannelAsync(
            byte[] payload,
            bool media,
            bool latestFrame,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var datagramSession = Volatile.Read(ref _datagramSession);
            var datagramChannel = Volatile.Read(ref _datagramChannel);
            var datagramEndpoint = Volatile.Read(ref _datagramEndpoint);
            if (media
                && datagramSession is not null
                && datagramChannel is not null
                && datagramEndpoint is not null)
            {
                try
                {
                    var sequence = Interlocked.Increment(ref _datagramSequence);
                    foreach (var packet in ReachDatagramPacketCodec.EncodeData(
                                 datagramSession,
                                 sequence,
                                 payload,
                                 datagramChannel.MaximumPayloadSize))
                    {
                        await datagramChannel.SendAsync(
                                packet,
                                datagramEndpoint,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return false;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    ClearDatagram();
                }
            }

            var mediaStream = Volatile.Read(ref _mediaStream);
            if (!media || mediaStream is null)
            {
                await SendPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
                return false;
            }

            if (latestFrame)
            {
                if (_latestMedia.Writer.TryWrite(payload))
                    return false;

                var dropped = _latestMedia.Reader.TryRead(out _);
                if (!_latestMedia.Writer.TryWrite(payload))
                    dropped = true;
                return dropped;
            }

            await _mediaSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await LengthPrefixedFrameCodec.WriteAsync(
                        mediaStream,
                        payload,
                        cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                DetachMediaStream(mediaStream);
                await SendPayloadAsync(payload, cancellationToken).ConfigureAwait(false);
                return false;
            }
            finally
            {
                _mediaSendGate.Release();
            }
        }

        private async Task MediaSendLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var payload in _latestMedia.Reader.ReadAllAsync(
                                   cancellationToken))
                {
                    var mediaStream = Volatile.Read(ref _mediaStream);
                    if (mediaStream is null)
                        continue;

                    await _mediaSendGate.WaitAsync(cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        await LengthPrefixedFrameCodec.WriteAsync(
                                mediaStream,
                                payload,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception) when (
                        !cancellationToken.IsCancellationRequested)
                    {
                        DetachMediaStream(mediaStream);
                    }
                    finally
                    {
                        _mediaSendGate.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                return;

            _latestMedia.Writer.TryComplete();
            _mediaLifetime.Cancel();
            ITransportStream? mediaTransportStream;
            ITransportConnection? mediaTransport;
            lock (_mediaStateGate)
            {
                mediaTransportStream = _mediaTransportStream;
                mediaTransport = _mediaTransport;
                _mediaTransportStream = null;
                _mediaTransport = null;
                _mediaStream = null;
            }
            ClearDatagram();

            if (mediaTransportStream is not null)
                await mediaTransportStream.DisposeAsync().ConfigureAwait(false);
            if (mediaTransport is not null)
                await mediaTransport.DisposeAsync().ConfigureAwait(false);
            await _controlTransportStream.DisposeAsync().ConfigureAwait(false);
            await _transport.DisposeAsync().ConfigureAwait(false);
            try
            {
                await _mediaSendTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_mediaLifetime.IsCancellationRequested)
            {
            }
            _mediaLifetime.Dispose();
            // The gates are intentionally left undisposed. A broadcast may
            // already be waiting on one while the client is being removed;
            // disposing it here races that waiter and tears down the service.
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposeStarted) != 0)
                throw new ObjectDisposedException(nameof(ClientConnection));
        }

        private static void DisposeSynchronously(IAsyncDisposable? disposable)
        {
            if (disposable is null)
                return;

            try
            {
                disposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // The associated connection is already being detached.
            }
        }
    }

    private sealed class FileTransferState : IAsyncDisposable
    {
        public FileTransferState(
            long clientId,
            Guid transferId,
            string path,
            long expectedLength,
            string expectedHash,
            FileStream stream)
        {
            ClientId = clientId;
            TransferId = transferId;
            Path = path;
            ExpectedLength = expectedLength;
            ExpectedHash = expectedHash;
            Stream = stream;
        }

        public long ClientId { get; }
        public Guid TransferId { get; }
        public string Path { get; }
        public long ExpectedLength { get; }
        public string ExpectedHash { get; }
        public FileStream Stream { get; }
        public long BytesWritten { get; set; }

        public ValueTask DisposeAsync() => Stream.DisposeAsync();
    }
}
