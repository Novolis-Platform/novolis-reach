using System.Net;
using System.Security.Cryptography;
using Novolis.Reach.Protocol;
using Novolis.Reach.Transport;
using Novolis.Transports;
using Novolis.Transports.Framing;
using Novolis.Transports.Udp;

namespace Novolis.Reach.Client;

/// <summary>Owns one client-side Reach control connection.</summary>
public sealed class ReachClientSession : IAsyncDisposable
{
    private readonly int? _mediaPort;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _bulkSendGate = new(1, 1);
    private IReachTransportConnection? _transport;
    private Stream? _stream;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private Stream? _mediaStream;
    private CancellationTokenSource? _mediaReceiveCancellation;
    private Task? _mediaReceiveTask;
    private ITransportDatagramChannel? _datagramChannel;
    private ReachDatagramSession? _datagramSession;
    private IPEndPoint? _datagramEndpoint;
    private CancellationTokenSource? _datagramReceiveCancellation;
    private Task? _datagramReceiveTask;
    private long _sequence;
    private string? _lastEndpoint;
    private ReachPlatform _platform;
    private string? _clientName;
    private readonly Guid _sessionId = Guid.NewGuid();
    private long _lastVideoSequence;
    private long _lastVideoFrameTicks;
    private long _lastDatagramSequence = -1;
    private int _disconnectRequested;
    private int _remoteSessionEnded;
    private int _state = (int)ReachClientConnectionState.Disconnected;
    private readonly ReachPerformanceMetrics _performance = new();
    private CancellationTokenSource? _latencyCancellation;
    private Task? _latencyTask;
    private int _phase = (int)ReachConnectionPhase.Disconnected;

    /// <summary>Creates a Reach client session.</summary>
    /// <param name="mediaPort">
    /// Optional media port override. Use zero to disable the dedicated media
    /// channel, which is useful for control-only peers.
    /// </param>
    public ReachClientSession(int? mediaPort = null)
    {
        if (mediaPort is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(mediaPort));

        _mediaPort = mediaPort;
    }

    /// <summary>Raised when the session status changes.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised when the session state changes.</summary>
    public event Action<ReachClientConnectionState>? StateChanged;

    /// <summary>Raised when the remote host ends the session.</summary>
    public event Action<string>? SessionEnded;

    /// <summary>Raised when the media channel ends independently of control.</summary>
    public event Action? MediaConnectionLost;

    /// <summary>Raised when the control channel ends unexpectedly.</summary>
    public event Action? ConnectionLost;

    /// <summary>Raised for each encoded video frame received from the host.</summary>
    public event Action<ReachVideoFrame>? VideoFrameReceived;

    /// <summary>Raised when bounded performance diagnostics change.</summary>
    public event Action<ReachPerformanceSnapshot>? PerformanceChanged;

    /// <summary>Raised when the user-facing connection phase changes.</summary>
    public event Action<ReachConnectionPhase>? PhaseChanged;

    /// <summary>Raised when the host announces its display topology.</summary>
    public event Action<ReachDisplayTopology>? DisplayTopologyReceived;

    /// <summary>Raised when the host starts or restarts its video stream.</summary>
    public event Action<ReachVideoStreamStart>? VideoStreamStarted;

    /// <summary>Raised when the host resets its video stream.</summary>
    public event Action<ReachVideoStreamReset>? VideoStreamReset;

    /// <summary>Raised when the host starts its audio stream.</summary>
    public event Action<ReachAudioStreamStart>? AudioStreamStarted;

    /// <summary>Raised for each remote audio block received from the host.</summary>
    public event Action<ReachAudioFrame>? AudioFrameReceived;

    /// <summary>Raised when the host sends clipboard content.</summary>
    public event Action<ReachClipboardContent>? ClipboardContentReceived;

    /// <summary>Raised when the host pauses or resumes sharing.</summary>
    public event Action<ReachSharingState>? SharingStateChanged;

    /// <summary>Gets negotiated capabilities after connection.</summary>
    public ReachCapabilities? NegotiatedCapabilities { get; private set; }

    /// <summary>Gets whether the control channel is connected.</summary>
    public bool IsConnected => _stream is not null;

    /// <summary>Gets whether the dedicated media channel is connected.</summary>
    public bool IsMediaConnected => _mediaStream is not null;

    /// <summary>Gets whether authenticated UDP media is connected.</summary>
    public bool IsDatagramConnected =>
        Volatile.Read(ref _datagramReceiveTask) is not null;

    /// <summary>Gets the current session state.</summary>
    public ReachClientConnectionState State =>
        (ReachClientConnectionState)Volatile.Read(ref _state);

    /// <summary>Gets the detailed user-facing connection phase.</summary>
    public ReachConnectionPhase Phase =>
        (ReachConnectionPhase)Volatile.Read(ref _phase);

    /// <summary>Gets the selected transport kind.</summary>
    public string ActiveTransport =>
        _transport?.Info.Kind.ToString() ?? "Disconnected";

    /// <summary>Gets the latest bounded performance snapshot.</summary>
    public ReachPerformanceSnapshot Performance => _performance.Snapshot();

    /// <summary>Records a frame after the UI presents it.</summary>
    public void RecordPresentedFrame(
        long sourceUtcTicks,
        double durationMilliseconds)
    {
        _performance.RecordPresented(durationMilliseconds, sourceUtcTicks);
        RaisePerformanceChanged();
    }

    /// <summary>Records a decoder duration from a platform presenter.</summary>
    public void RecordDecodedFrame(double durationMilliseconds)
    {
        _performance.RecordDecoded(durationMilliseconds);
        RaisePerformanceChanged();
    }

    /// <summary>Records a frame evicted by a platform decoder queue.</summary>
    public void RecordDroppedFrame()
    {
        _performance.RecordDropped();
        RaisePerformanceChanged();
    }

    /// <summary>Gets the last received video sequence number.</summary>
    public long LastVideoSequence => Interlocked.Read(ref _lastVideoSequence);

    /// <summary>Gets when the last encoded video frame arrived.</summary>
    public DateTimeOffset? LastVideoFrameAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastVideoFrameTicks);
            return ticks == 0
                ? null
                : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Connects and completes the Reach hello/capability exchange.</summary>
    public async Task ConnectAsync(
        string endpoint,
        ReachPlatform platform,
        string clientName,
        CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            return;

        var transportEndpoint = ReachTransportEndpoint.Parse(endpoint);
        var mediaPort = _mediaPort
            ?? (transportEndpoint.Address.Port == ReachProtocol.ControlPort
                ? ReachProtocol.MediaPort
                : checked(transportEndpoint.Address.Port + 1));
        Volatile.Write(ref _disconnectRequested, 0);
        Volatile.Write(ref _remoteSessionEnded, 0);
        SetState(ReachClientConnectionState.Connecting);
        SetPhase(ReachConnectionPhase.Connecting);
        _lastEndpoint = endpoint;
        _platform = platform;
        _clientName = clientName;
        RaiseStatus(
            $"Connecting via {transportEndpoint.Scheme} to "
            + $"{transportEndpoint.Address.Address}:{transportEndpoint.Address.Port}...");
        IReachTransportConnection transport;
        try
        {
            transport = await ReachTransportConnector.ConnectAsync(
                    transportEndpoint,
                    mediaPort,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch when (
            transportEndpoint.IsQuic
            && !cancellationToken.IsCancellationRequested)
        {
            RaiseStatus("QUIC is unavailable; trying the TCP fallback...");
            transportEndpoint = transportEndpoint with
            {
                Scheme = "tcp",
                CertificatePin = null,
            };
            try
            {
                transport = await ReachTransportConnector.ConnectAsync(
                        transportEndpoint,
                        mediaPort,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                SetState(ReachClientConnectionState.Lost);
                RaiseStatus(
                    $"Unable to connect to {transportEndpoint.Address.Address}:"
                    + $"{transportEndpoint.Address.Port}.");
                throw;
            }
        }
        catch
        {
            SetState(ReachClientConnectionState.Lost);
            RaiseStatus(
                $"Unable to connect to {transportEndpoint.Address.Address}:"
                + $"{transportEndpoint.Address.Port}.");
            throw;
        }

        _transport = transport;
        _stream = transport.ControlStream;
        SetState(ReachClientConnectionState.Connected);
        SetPhase(ReachConnectionPhase.Authenticating);
        RaiseStatus(
            $"Connected via {transport.Info.Kind} to "
            + $"{transportEndpoint.Address.Address}:{transportEndpoint.Address.Port}; "
            + "negotiating...");

        try
        {
            await SendAsync(
                ReachMessageType.ClientHello,
                new ReachClientHello(
                    ReachProtocol.AppId,
                    ReachProtocol.Version,
                    platform,
                    clientName),
                cancellationToken).ConfigureAwait(false);
            await SendAsync(
                ReachMessageType.ClientCapabilities,
                new ReachCapabilitiesMessage(GetCapabilities(platform)),
                cancellationToken).ConfigureAwait(false);

            RaiseStatus("Waiting for Reach host capabilities...");
            var hostHello = await ReadAsync<ReachHostHello>(
                    ReachMessageType.HostHello,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(hostHello.AppId, ReachProtocol.AppId, StringComparison.Ordinal)
                || !ReachProtocol.IsCompatible(hostHello.ProtocolVersion))
            {
                throw new InvalidDataException("The remote host does not speak Reach protocol 1.x.");
            }

            var hostCapabilities = await ReadAsync<ReachCapabilitiesMessage>(
                    ReachMessageType.HostCapabilities,
                    cancellationToken)
                .ConfigureAwait(false);
            var negotiatedCapabilities = ReachCapabilities.Intersect(
                hostCapabilities.Capabilities,
                GetCapabilities(platform));
            NegotiatedCapabilities = negotiatedCapabilities;
            SetPhase(ReachConnectionPhase.Starting);
            if (_lastVideoSequence == 0)
            {
                await SendAsync(
                        ReachMessageType.SessionOpen,
                        new ReachSessionOpen(
                            _sessionId,
                            string.Empty,
                            negotiatedCapabilities.Supports(ReachCapability.Audio)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await SendAsync(
                        ReachMessageType.SessionResume,
                        new ReachSessionResume(
                            _sessionId,
                            _lastVideoSequence,
                            negotiatedCapabilities.Supports(ReachCapability.Audio)),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            _receiveCancellation = new CancellationTokenSource();
            var receiveCancellation = _receiveCancellation;
            _receiveTask = Task.Run(
                () => ReceiveLoopAsync(
                    receiveCancellation,
                    receiveCancellation.Token),
                CancellationToken.None);
            var latencyCancellation = new CancellationTokenSource();
            _latencyCancellation = latencyCancellation;
            _latencyTask = Task.Run(
                () => ProbeLatencyLoopAsync(latencyCancellation.Token),
                CancellationToken.None);
            await TryConnectMediaAsync(cancellationToken)
                .ConfigureAwait(false);
            if (_mediaPort != 0 && !IsMediaConnected)
            {
                RaiseStatus(
                    $"Connected to {hostHello.HostName}, but the video channel is unavailable.");
                MediaConnectionLost?.Invoke();
            }

            // Publish the connected state before requesting the first frame.
            // The receive loop can deliver that frame before this method
            // returns, so a trailing "connected" status would overwrite the
            // more specific streaming status in the client surface.
            RaiseStatus(
                IsMediaConnected || _mediaPort == 0
                    ? $"Connected to {hostHello.HostName}; "
                      + $"video={string.Join(",", negotiatedCapabilities.OfferedVideoCodecs)}"
                    : $"Connected to {hostHello.HostName}, but the video channel is unavailable.");
            await RequestKeyFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CloseTransportAsync(announce: false).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends one typed control message.</summary>
    public async Task SendAsync<T>(
        ReachMessageType type,
        T message,
        CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("Reach is not connected.");
        await SendToStreamAsync(
                stream,
                _sendGate,
                type,
                message,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendToStreamAsync<T>(
        Stream stream,
        SemaphoreSlim gate,
        ReachMessageType type,
        T message,
        CancellationToken cancellationToken)
    {
        var payload = ReachMessageCodec.Serialize(
            type,
            Interlocked.Increment(ref _sequence),
            message);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LengthPrefixedFrameCodec.WriteAsync(stream, payload, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Disconnects the control channel.</summary>
    public async Task DisconnectAsync()
    {
        Volatile.Write(ref _disconnectRequested, 1);
        if (IsConnected)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await SendAsync(
                        ReachMessageType.SessionClose,
                        new ReachSessionClose(_sessionId, "Client disconnected."),
                        timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The peer may already have closed the connection.
            }
        }

        await CloseTransportAsync(announce: true).ConfigureAwait(false);
    }

    /// <summary>Reconnects to the last endpoint and requests session resume.</summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_lastEndpoint is null || _clientName is null)
            throw new InvalidOperationException("No previous Reach endpoint is available.");

        Volatile.Write(ref _disconnectRequested, 0);
        SetPhase(ReachConnectionPhase.Reconnecting);
        await CloseTransportAsync(
                announce: true,
                preserveVideoSequence: true)
            .ConfigureAwait(false);
        await ConnectAsync(
                _lastEndpoint,
                _platform,
                _clientName,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reopens only the reliable media stream when control survives.</summary>
    public async Task RecoverMediaAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Reach is not connected.");

        if (!IsMediaConnected && _mediaPort != 0)
            await TryConnectMediaAsync(cancellationToken).ConfigureAwait(false);
        await RequestKeyFrameAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a local file to the host in bounded chunks.</summary>
    public async Task SendFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("The file to transfer does not exist.", path);

        var hash = await ComputeHashAsync(path, cancellationToken).ConfigureAwait(false);
        var transferId = Guid.NewGuid();
        var bulkStream = _transport is { Info.Kind: TransportKind.Quic }
            ? await _transport.OpenBulkStreamAsync(cancellationToken)
                .ConfigureAwait(false)
            : null;
        var stream = bulkStream ?? _stream
            ?? throw new InvalidOperationException("Reach is not connected.");
        var gate = bulkStream is null ? _sendGate : _bulkSendGate;
        try
        {
            if (bulkStream is not null)
            {
                await SendToStreamAsync(
                        stream,
                        gate,
                        ReachMessageType.BulkHello,
                        new ReachBulkHello(
                            _sessionId,
                            ReachProtocol.AppId,
                            ReachProtocol.Version),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await SendToStreamAsync(
                    stream,
                    gate,
                    ReachMessageType.FileOffer,
                    new ReachFileOffer(
                        transferId,
                        Path.GetFileName(path),
                        fileInfo.Length,
                        Convert.ToHexString(hash)),
                    cancellationToken)
                .ConfigureAwait(false);

            await using var file = File.OpenRead(path);
            var buffer = new byte[64 * 1024];
            long offset = 0;
            while (true)
            {
                var read = await file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await SendToStreamAsync(
                        stream,
                        gate,
                        ReachMessageType.FileChunk,
                        new ReachFileChunk(
                            transferId,
                            offset,
                            buffer.AsSpan(0, read).ToArray()),
                        cancellationToken)
                    .ConfigureAwait(false);
                offset += read;
            }

            await SendToStreamAsync(
                    stream,
                    gate,
                    ReachMessageType.FileComplete,
                    new ReachFileComplete(transferId, true, null),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            bulkStream?.Dispose();
        }
    }

    /// <summary>Sends text to the host clipboard.</summary>
    public Task SendClipboardTextAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            ReachMessageType.ClipboardContent,
            new ReachClipboardContent("text", text),
            cancellationToken);

    /// <summary>Sends one pointer position in the selected display.</summary>
    public Task SendPointerMoveAsync(
        double x,
        double y,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            ReachMessageType.PointerMove,
            new ReachPointerMove(x, y),
            cancellationToken);

    /// <summary>Sends one pointer button transition.</summary>
    public Task SendPointerButtonAsync(
        string button,
        bool isDown,
        int clickCount = 1,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            ReachMessageType.PointerButton,
            new ReachPointerButton(button, isDown, clickCount),
            cancellationToken);

    /// <summary>Sends one pointer wheel delta.</summary>
    public Task SendPointerWheelAsync(
        int delta,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            ReachMessageType.PointerWheel,
            new ReachPointerWheel(delta),
            cancellationToken);

    /// <summary>Sends one virtual-key transition.</summary>
    public Task SendKeyAsync(
        ushort virtualKey,
        bool isDown,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            isDown ? ReachMessageType.KeyDown : ReachMessageType.KeyUp,
            new ReachKeyEvent(virtualKey),
            cancellationToken);

    /// <summary>Sends Unicode text input to the host.</summary>
    public Task SendTextInputAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            ReachMessageType.TextInput,
            new ReachTextInput(text),
            cancellationToken);

    /// <summary>Sends file paths as a rich clipboard file-list payload.</summary>
    public Task SendClipboardFilesAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        return SendAsync(
            ReachMessageType.ClipboardContent,
            new ReachClipboardContent("files", null, files.ToArray()),
            cancellationToken);
    }

    /// <summary>Requests a display and adaptive stream configuration.</summary>
    public Task ConfigureVideoAsync(
        int width,
        int height,
        int framesPerSecond,
        int targetBitrate,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            ReachMessageType.VideoStreamConfiguration,
            new ReachVideoStreamConfiguration(
                "H264",
                width,
                height,
                framesPerSecond,
                targetBitrate),
            cancellationToken);

    /// <summary>Selects a monitor announced by the host.</summary>
    public Task SelectDisplayAsync(
        string displayId,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            ReachMessageType.DisplaySelect,
            new ReachDisplaySelect(displayId),
            cancellationToken);

    /// <summary>Requests a fresh intra frame after a decoder reset.</summary>
    public Task RequestKeyFrameAsync(
        CancellationToken cancellationToken = default) =>
        RequestKeyFrameCoreAsync(cancellationToken);

    private async Task RequestKeyFrameCoreAsync(
        CancellationToken cancellationToken)
    {
        _performance.RecordKeyFrameRequest();
        RaisePerformanceChanged();
        await SendAsync(
                ReachMessageType.RequestKeyFrame,
                new ReachRequestKeyFrame(_lastVideoSequence),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendGate.Dispose();
        _bulkSendGate.Dispose();
    }

    private async Task<T> ReadAsync<T>(
        ReachMessageType expectedType,
        CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Reach is not connected.");
        var frame = await LengthPrefixedFrameCodec.ReadAsync(stream, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new EndOfStreamException("Reach host closed the control channel.");
        var envelope = ReachMessageCodec.Deserialize(frame.Payload);
        return ReachMessageCodec.ReadBody<T>(envelope, expectedType);
    }

    private async Task ReceiveLoopAsync(
        CancellationTokenSource owner,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var stream = _stream;
                if (stream is null)
                    return;

                var frame = await LengthPrefixedFrameCodec.ReadAsync(
                        stream,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (frame is null)
                    return;

                if (!await ProcessIncomingEnvelopeAsync(
                            ReachMessageCodec.Deserialize(frame.Payload))
                        .ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RaiseStatus($"Receive failed: {exception.Message}");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested
                && Volatile.Read(ref _disconnectRequested) == 0)
            {
                HandleUnexpectedDisconnect(owner);
            }
        }
    }

    private async Task<bool> ProcessIncomingEnvelopeAsync(
        ReachMessageEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case ReachMessageType.VideoStreamStart:
                VideoStreamStarted?.Invoke(
                    ReachMessageCodec.ReadBody<ReachVideoStreamStart>(envelope));
                break;
            case ReachMessageType.VideoStreamReset:
            {
                var reset = ReachMessageCodec.ReadBody<ReachVideoStreamReset>(envelope);
                Interlocked.Exchange(ref _lastVideoSequence, reset.Sequence);
                VideoStreamReset?.Invoke(reset);
                break;
            }
            case ReachMessageType.DisplayTopology:
                DisplayTopologyReceived?.Invoke(
                    ReachMessageCodec.ReadBody<ReachDisplayTopology>(envelope));
                break;
            case ReachMessageType.VideoFrame:
            {
                var video = ReachMessageCodec.ReadBody<ReachVideoFrame>(envelope);
                Interlocked.Exchange(ref _lastVideoSequence, video.Sequence);
                Interlocked.Exchange(
                    ref _lastVideoFrameTicks,
                    DateTimeOffset.UtcNow.UtcTicks);
                _performance.RecordReceived(video.AccessUnit.Length, video.Timestamp);
                RaisePerformanceChanged();
                SetState(ReachClientConnectionState.Streaming);
                SetPhase(ReachConnectionPhase.Streaming);
                VideoFrameReceived?.Invoke(video);
                break;
            }
            case ReachMessageType.AudioStreamStart:
                AudioStreamStarted?.Invoke(
                    ReachMessageCodec.ReadBody<ReachAudioStreamStart>(envelope));
                break;
            case ReachMessageType.AudioFrame:
                AudioFrameReceived?.Invoke(
                    ReachMessageCodec.ReadBody<ReachAudioFrame>(envelope));
                break;
            case ReachMessageType.DatagramOffer:
            {
                var offer = ReachMessageCodec.ReadBody<ReachDatagramOffer>(envelope);
                await ActivateDatagramAsync(offer).ConfigureAwait(false);
                break;
            }
            case ReachMessageType.LatencyResponse:
            {
                var response = ReachMessageCodec.ReadBody<ReachLatencyResponse>(envelope);
                var elapsedTicks = DateTime.UtcNow.Ticks - response.SentUtcTicks;
                if (elapsedTicks >= 0)
                {
                    _performance.RecordInputRoundTrip(
                        TimeSpan.FromTicks(elapsedTicks).TotalMilliseconds);
                    RaisePerformanceChanged();
                }

                break;
            }
            case ReachMessageType.SharingState:
                SharingStateChanged?.Invoke(
                    ReachMessageCodec.ReadBody<ReachSharingState>(envelope));
                break;
            case ReachMessageType.ClipboardContent:
                ClipboardContentReceived?.Invoke(
                    ReachMessageCodec.ReadBody<ReachClipboardContent>(envelope));
                break;
            case ReachMessageType.SessionClose:
            {
                var close = ReachMessageCodec.ReadBody<ReachSessionClose>(envelope);
                RaiseStatus($"The Reach host closed the session: {close.Reason}");
                Volatile.Write(ref _remoteSessionEnded, 1);
                SetPhase(ReachConnectionPhase.Ended);
                SessionEnded?.Invoke(close.Reason);
                return false;
            }
        }

        return true;
    }

    private async Task TryConnectMediaAsync(
        CancellationToken cancellationToken)
    {
        if (_mediaPort == 0 || _transport is null)
            return;

        Stream? stream = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            stream = await _transport.OpenMediaStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            await LengthPrefixedFrameCodec.WriteAsync(
                    stream,
                    ReachMessageCodec.Serialize(
                        ReachMessageType.MediaHello,
                        Interlocked.Increment(ref _sequence),
                        new ReachMediaHello(
                            _sessionId,
                            ReachProtocol.AppId,
                            ReachProtocol.Version)),
                    timeout.Token)
                .ConfigureAwait(false);

            _mediaStream = stream;
            _mediaReceiveCancellation = new CancellationTokenSource();
            var receiveCancellation = _mediaReceiveCancellation;
            _mediaReceiveTask = Task.Run(
                () => ReceiveMediaLoopAsync(
                    receiveCancellation,
                    receiveCancellation.Token),
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stream?.Dispose();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            stream?.Dispose();
        }
    }

    private async Task ActivateDatagramAsync(ReachDatagramOffer offer)
    {
        if (!IsConnected
            || offer.SessionId != _sessionId
            || offer.Port is <= 0 or > 65535
            || Volatile.Read(ref _datagramReceiveTask) is not null)
        {
            return;
        }

        UdpDatagramChannel? channel = null;
        try
        {
            var endpoint = ReachTransportEndpoint.Parse(
                _lastEndpoint
                ?? throw new InvalidOperationException(
                    "The Reach endpoint is no longer available."));
            var key = Convert.FromBase64String(offer.Key);
            var session = new ReachDatagramSession(
                offer.SessionId,
                key,
                offer.Token);
            session.Validate();
            channel = new UdpDatagramChannel(
                new IPEndPoint(IPAddress.Any, 0),
                new UdpDatagramChannelOptions
                {
                    MaximumPayloadSize = Math.Min(
                        ReachDatagramPacketCodec.MaximumPacketSize,
                        offer.MaximumPacketSize),
                    ReceiveQueueCapacity = 128,
                    ReceiveQueueFullMode =
                        System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                });
            var remoteEndpoint = new IPEndPoint(
                endpoint.Address.Address,
                offer.Port);
            using var handshakeTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(1));
            await channel.SendAsync(
                    ReachDatagramPacketCodec.EncodeHandshake(
                        session.SessionId,
                        session.Token),
                    remoteEndpoint,
                    handshakeTimeout.Token)
                .ConfigureAwait(false);

            var receiveCancellation = new CancellationTokenSource();
            if (Interlocked.CompareExchange(
                    ref _datagramChannel,
                    channel,
                    null) is not null)
            {
                receiveCancellation.Dispose();
                await channel.DisposeAsync().ConfigureAwait(false);
                return;
            }

            channel = null;
            Volatile.Write(ref _datagramSession, session);
            Volatile.Write(
                ref _datagramEndpoint,
                remoteEndpoint);
            Volatile.Write(
                ref _datagramReceiveCancellation,
                receiveCancellation);
            var receiveTask = Task.Run(
                () => ReceiveDatagramLoopAsync(
                    receiveCancellation,
                    receiveCancellation.Token),
                CancellationToken.None);
            Volatile.Write(ref _datagramReceiveTask, receiveTask);
            RaiseStatus(
                $"Authenticated UDP media is active on local port "
                + $"{((UdpDatagramChannel)_datagramChannel).LocalEndPoint.Port}.");
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            channel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            RaiseStatus($"UDP media activation failed; using the reliable fallback: {exception.Message}");
        }
    }

    private async Task ReceiveDatagramLoopAsync(
        CancellationTokenSource owner,
        CancellationToken cancellationToken)
    {
        try
        {
            var channel = Volatile.Read(ref _datagramChannel);
            var session = Volatile.Read(ref _datagramSession);
            if (channel is null || session is null)
                return;

            var reassembler = new ReachDatagramReassembler();
            while (!cancellationToken.IsCancellationRequested)
            {
                var datagram = await channel.ReceiveAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!ReachDatagramPacketCodec.TryDecodeData(
                        session,
                        datagram.Payload,
                        out var fragment)
                    || !Equals(datagram.RemoteEndpoint, _datagramEndpoint))
                {
                    continue;
                }

                if (!reassembler.TryAccept(
                        fragment,
                        datagram.ReceivedAt,
                        out var payload))
                {
                    continue;
                }

                var previous = Interlocked.Exchange(
                    ref _lastDatagramSequence,
                    fragment.Sequence);
                if (previous >= 0 && fragment.Sequence > previous + 1)
                    await RequestKeyFrameAfterDatagramGapAsync()
                        .ConfigureAwait(false);

                if (!await ProcessIncomingEnvelopeAsync(
                            ReachMessageCodec.Deserialize(payload))
                        .ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RaiseStatus($"UDP media receive failed; using the reliable fallback: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _datagramReceiveCancellation,
                        null,
                        owner),
                    owner))
            {
                _ = Interlocked.Exchange(ref _datagramReceiveTask, null);
                Interlocked.Exchange(ref _datagramChannel, null);
                Interlocked.Exchange(ref _datagramSession, null);
                Interlocked.Exchange(ref _datagramEndpoint, null);
                owner.Dispose();
                if (IsConnected)
                    RaiseStatus("UDP media ended; the reliable fallback remains available.");
            }
        }
    }

    private async Task RequestKeyFrameAfterDatagramGapAsync()
    {
        try
        {
            await RequestKeyFrameAsync().ConfigureAwait(false);
        }
        catch
        {
            // The control channel may be closing at the same time as the loss.
        }
    }

    private async Task ReceiveMediaLoopAsync(
        CancellationTokenSource owner,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var stream = _mediaStream;
                if (stream is null)
                    return;

                var frame = await LengthPrefixedFrameCodec.ReadAsync(
                        stream,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (frame is null)
                    return;

                if (!await ProcessIncomingEnvelopeAsync(
                            ReachMessageCodec.Deserialize(frame.Payload))
                        .ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RaiseStatus($"Media receive failed: {exception.Message}");
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                DetachMediaTransport(owner);
                if (IsConnected)
                    SetState(ReachClientConnectionState.Connected);
                if (IsConnected)
                    SetPhase(ReachConnectionPhase.Degraded);
                if (!IsDatagramConnected)
                {
                    RaiseStatus("The Reach media stream ended.");
                    MediaConnectionLost?.Invoke();
                }
            }
        }
    }

    private async Task ProbeLatencyLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                if (!IsConnected)
                    return;

                var probe = new ReachLatencyProbe(
                    Interlocked.Increment(ref _sequence),
                    DateTime.UtcNow.Ticks);
                try
                {
                    await SendAsync(
                            ReachMessageType.LatencyProbe,
                            probe,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception) when (
                    !cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void DetachMediaTransport(CancellationTokenSource owner)
    {
        if (!ReferenceEquals(
                Interlocked.CompareExchange(
                    ref _mediaReceiveCancellation,
                    null,
                    owner),
                owner))
        {
            return;
        }

        _ = Interlocked.Exchange(ref _mediaReceiveTask, null);
        Interlocked.Exchange(ref _mediaStream, null)?.Dispose();
        owner.Dispose();
    }

    private async Task CloseTransportAsync(
        bool announce,
        bool preserveVideoSequence = false)
    {
        var latencyCancellation = Interlocked.Exchange(
            ref _latencyCancellation,
            null);
        var latencyTask = Interlocked.Exchange(ref _latencyTask, null);
        latencyCancellation?.Cancel();
        var transport = Interlocked.Exchange(ref _transport, null);
        var stream = Interlocked.Exchange(ref _stream, null);
        var receiveCancellation = Interlocked.Exchange(ref _receiveCancellation, null);
        var receiveTask = Interlocked.Exchange(ref _receiveTask, null);
        receiveCancellation?.Cancel();
        if (stream is not null)
            stream.Dispose();
        if (receiveTask is not null)
        {
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                receiveCancellation?.IsCancellationRequested == true)
            {
            }
        }

        receiveCancellation?.Dispose();
        if (latencyTask is not null)
        {
            try
            {
                await latencyTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                latencyCancellation?.IsCancellationRequested == true)
            {
            }
        }

        latencyCancellation?.Dispose();
        var datagramCancellation = Interlocked.Exchange(
            ref _datagramReceiveCancellation,
            null);
        var datagramTask = Interlocked.Exchange(
            ref _datagramReceiveTask,
            null);
        var datagramChannel = Interlocked.Exchange(
            ref _datagramChannel,
            null);
        Interlocked.Exchange(ref _datagramSession, null);
        Interlocked.Exchange(ref _datagramEndpoint, null);
        datagramCancellation?.Cancel();
        if (datagramChannel is not null)
            await datagramChannel.DisposeAsync().ConfigureAwait(false);
        if (datagramTask is not null)
        {
            try
            {
                await datagramTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                datagramCancellation?.IsCancellationRequested == true)
            {
            }
        }

        datagramCancellation?.Dispose();
        var mediaStream = Interlocked.Exchange(ref _mediaStream, null);
        var mediaCancellation = Interlocked.Exchange(
            ref _mediaReceiveCancellation,
            null);
        var mediaTask = Interlocked.Exchange(ref _mediaReceiveTask, null);
        mediaCancellation?.Cancel();
        if (mediaStream is not null)
            mediaStream.Dispose();
        if (mediaTask is not null)
        {
            try
            {
                await mediaTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                mediaCancellation?.IsCancellationRequested == true)
            {
            }
        }

        mediaCancellation?.Dispose();
        if (transport is not null)
            await transport.DisposeAsync().ConfigureAwait(false);
        NegotiatedCapabilities = null;
        if (!preserveVideoSequence)
            Interlocked.Exchange(ref _lastVideoSequence, 0);
        Interlocked.Exchange(ref _lastVideoFrameTicks, 0);
        Interlocked.Exchange(ref _lastDatagramSequence, -1);
        Volatile.Write(ref _remoteSessionEnded, 0);
        SetState(ReachClientConnectionState.Disconnected);
        SetPhase(ReachConnectionPhase.Disconnected);
        if (announce)
            RaiseStatus("Disconnected");
    }

    private void HandleUnexpectedDisconnect(CancellationTokenSource owner)
    {
        if (!ReferenceEquals(
                Interlocked.CompareExchange(
                    ref _receiveCancellation,
                    null,
                    owner),
                owner))
        {
            return;
        }

        _ = Interlocked.Exchange(ref _receiveTask, null);
        Interlocked.Exchange(ref _stream, null)?.Dispose();
        var transport = Interlocked.Exchange(ref _transport, null);
        var datagramOwner = Interlocked.Exchange(
            ref _datagramReceiveCancellation,
            null);
        var datagramChannel = Interlocked.Exchange(
            ref _datagramChannel,
            null);
        _ = Interlocked.Exchange(ref _datagramReceiveTask, null);
        Interlocked.Exchange(ref _datagramSession, null);
        Interlocked.Exchange(ref _datagramEndpoint, null);
        datagramOwner?.Cancel();
        try
        {
            datagramChannel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // The transport has already failed; state notification is primary.
        }
        var mediaOwner = Interlocked.Exchange(
            ref _mediaReceiveCancellation,
            null);
        Interlocked.Exchange(ref _latencyCancellation, null)?.Cancel();
        _ = Interlocked.Exchange(ref _latencyTask, null);
        mediaOwner?.Cancel();
        Interlocked.Exchange(ref _mediaStream, null)?.Dispose();
        try
        {
            transport?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // The connection has already failed; state notification is primary.
        }
        NegotiatedCapabilities = null;
        Interlocked.Exchange(ref _lastVideoSequence, 0);
        Interlocked.Exchange(ref _lastVideoFrameTicks, 0);
        Interlocked.Exchange(ref _lastDatagramSequence, -1);
        owner.Dispose();
        mediaOwner?.Dispose();
        SetState(ReachClientConnectionState.Lost);
        SetPhase(
            Volatile.Read(ref _remoteSessionEnded) != 0
                ? ReachConnectionPhase.Ended
                : ReachConnectionPhase.Reconnecting);
        RaiseStatus("Reach connection lost. Use reconnect to resume.");
        ConnectionLost?.Invoke();
    }

    private void SetState(ReachClientConnectionState state)
    {
        if (Interlocked.Exchange(ref _state, (int)state) == (int)state)
            return;

        StateChanged?.Invoke(state);
    }

    private void SetPhase(ReachConnectionPhase phase)
    {
        if (Interlocked.Exchange(ref _phase, (int)phase) == (int)phase)
            return;

        PhaseChanged?.Invoke(phase);
    }

    private void RaiseStatus(string status)
    {
        StatusChanged?.Invoke(status);
    }

    private void RaisePerformanceChanged()
    {
        PerformanceChanged?.Invoke(_performance.Snapshot());
    }

    private static ReachCapabilities GetCapabilities(ReachPlatform platform) =>
        platform switch
        {
            ReachPlatform.Windows => ReachCapabilities.WindowsClient,
            ReachPlatform.Linux => ReachCapabilities.LinuxClient,
            ReachPlatform.Android => ReachCapabilities.AndroidClient,
            _ => new ReachCapabilities(ReachCapability.None),
        };

    private static async Task<byte[]> ComputeHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
        }

        return hash.GetHashAndReset();
    }
}
