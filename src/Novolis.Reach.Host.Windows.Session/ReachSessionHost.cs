using System.Diagnostics;
using System.Threading.Channels;
using System.Drawing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Novolis.Reach.Protocol;
using Novolis.Transports.Framing;
using Novolis.Transports.LocalIpc;
using Novolis.Video;
using Novolis.Video.Capture.Windows;
using Novolis.Video.Codecs.H264;
using Novolis.Windows.Audio;
using Novolis.Windows.Clipboard;
using Novolis.Windows.Display;
using Novolis.Windows.Input;

namespace Novolis.Reach.Host.Windows.Session;

/// <summary>
/// Runs in the interactive user session and owns capture and input access.
/// </summary>
public sealed class ReachSessionHost : BackgroundService
{
    private const string Endpoint = "Novolis.Reach.Host.Windows";
    private readonly ILogger<ReachSessionHost> _log;
    private readonly WindowsInputController _input;
    private readonly WindowsClipboardService _clipboard;
    private readonly WindowsDisplayTopology _display;
    private readonly WindowsLoopbackAudioCapture _audioCapture;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ReachPerformanceMetrics _performance = new();
    private ILocalIpcConnection? _connection;
    private WindowsDesktopCaptureSource? _capture;
    private WindowsH264Encoder? _encoder;
    private Channel<RawVideoFrame>? _frames;
    private Task? _encodeTask;
    private bool _audioStarted;
    private bool _audioEnabled;
    private bool _videoMetadataSent;
    private string _selectedDisplayId = string.Empty;
    private int _framesPerSecond = 30;
    private int _targetWidth;
    private int _targetHeight;
    private int _targetBitrate = 8_000_000;
    private int _streamWidth;
    private int _streamHeight;
    private long _sequence;
    private Channel<LocalIpcFrame>? _videoQueue;
    private Task? _videoSenderTask;

    /// <summary>Creates the interactive-session helper.</summary>
    public ReachSessionHost(
        ILogger<ReachSessionHost> log,
        WindowsInputController input,
        WindowsClipboardService clipboard,
        WindowsDisplayTopology display,
        WindowsLoopbackAudioCapture audioCapture)
    {
        _log = log;
        _input = input;
        _clipboard = clipboard;
        _display = display;
        _audioCapture = audioCapture;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var listener = LocalIpcTransport.CreateListener(
            new LocalIpcEndpoint(Endpoint));
        _log.LogInformation("Reach interactive session helper is listening.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await using var connection = await listener.AcceptAsync(stoppingToken)
                .ConfigureAwait(false);
            _connection = connection;
            var videoQueue = Channel.CreateBounded<LocalIpcFrame>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                });
            _videoQueue = videoQueue;
            using var videoSenderCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _videoSenderTask = VideoSendLoopAsync(
                connection,
                videoQueue.Reader,
                videoSenderCancellation.Token);
            try
            {
                await foreach (var frame in connection.ReadAllAsync(stoppingToken))
                {
                    if (!string.Equals(frame.Kind, "control", StringComparison.Ordinal))
                        continue;
                    await HandleControlAsync(frame, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _log.LogWarning(exception, "Reach service connection ended.");
            }
            finally
            {
                videoQueue.Writer.TryComplete();
                videoSenderCancellation.Cancel();
                if (_videoSenderTask is not null)
                {
                    try
                    {
                        await _videoSenderTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        videoSenderCancellation.IsCancellationRequested)
                    {
                    }
                }

                _videoSenderTask = null;
                _videoQueue = null;
                await StopCaptureAsync().ConfigureAwait(false);
                _connection = null;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopCaptureAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleControlAsync(
        LocalIpcFrame frame,
        CancellationToken cancellationToken)
    {
        var envelope = ReachMessageCodec.Deserialize(frame.Payload);
        switch (envelope.Type)
        {
            case ReachMessageType.SessionOpen:
            {
                var open = ReachMessageCodec.ReadBody<ReachSessionOpen>(envelope);
                _selectedDisplayId = string.IsNullOrWhiteSpace(open.RequestedDisplayId)
                    ? "display-0"
                    : open.RequestedDisplayId;
                _audioEnabled = open.EnableAudio;
                if (_capture is null)
                {
                    await StartCaptureAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendSessionMetadataAsync(cancellationToken).ConfigureAwait(false);
                }

                break;
            }
            case ReachMessageType.SessionResume:
            {
                var resume = ReachMessageCodec.ReadBody<ReachSessionResume>(envelope);
                _audioEnabled = resume.EnableAudio;
                if (_capture is null)
                {
                    await StartCaptureAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendSessionMetadataAsync(cancellationToken).ConfigureAwait(false);
                }

                break;
            }
            case ReachMessageType.SessionClose:
                await StopCaptureAsync().ConfigureAwait(false);
                break;
            case ReachMessageType.PointerMove:
            {
                var move = ReachMessageCodec.ReadBody<ReachPointerMove>(envelope);
                _input.MovePointer(
                    (int)global::System.Math.Round(move.X),
                    (int)global::System.Math.Round(move.Y));
                break;
            }
            case ReachMessageType.PointerButton:
            {
                var button = ReachMessageCodec.ReadBody<ReachPointerButton>(envelope);
                if (Enum.TryParse<WindowsInputController.WindowsPointerButton>(
                        button.Button,
                        ignoreCase: true,
                        out var parsedButton))
                {
                    _input.Button(parsedButton, button.IsDown);
                }

                break;
            }
            case ReachMessageType.PointerWheel:
                _input.Scroll(ReachMessageCodec.ReadBody<ReachPointerWheel>(envelope).Delta);
                break;
            case ReachMessageType.KeyDown:
                _input.Key(
                    ReachMessageCodec.ReadBody<ReachKeyEvent>(envelope).VirtualKey,
                    release: false);
                break;
            case ReachMessageType.KeyUp:
                _input.Key(
                    ReachMessageCodec.ReadBody<ReachKeyEvent>(envelope).VirtualKey,
                    release: true);
                break;
            case ReachMessageType.TextInput:
                _input.Text(ReachMessageCodec.ReadBody<ReachTextInput>(envelope).Text);
                break;
            case ReachMessageType.ClipboardContent:
            {
                var clipboard = ReachMessageCodec.ReadBody<ReachClipboardContent>(envelope);
                if (string.Equals(clipboard.Format, "text", StringComparison.OrdinalIgnoreCase)
                    && clipboard.Text is not null)
                {
                    _clipboard.WriteText(clipboard.Text);
                }
                else if (string.Equals(
                             clipboard.Format,
                             "files",
                             StringComparison.OrdinalIgnoreCase)
                         && clipboard.Files is not null)
                {
                    _clipboard.WriteFileDropList(clipboard.Files);
                }

                break;
            }
            case ReachMessageType.ClipboardChanged:
                await SendClipboardAsync(cancellationToken).ConfigureAwait(false);
                break;
            case ReachMessageType.VideoStreamConfiguration:
            {
                var configuration = ReachMessageCodec.ReadBody<ReachVideoStreamConfiguration>(envelope);
                if (!string.Equals(
                        configuration.Codec,
                        "H264",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Reach host does not support {configuration.Codec} video.");
                }

                await RestartCaptureAsync(
                        global::System.Math.Clamp(configuration.Width, 0, 3840),
                        global::System.Math.Clamp(configuration.Height, 0, 2160),
                        global::System.Math.Clamp(configuration.FramesPerSecond, 5, 60),
                        global::System.Math.Clamp(
                            configuration.TargetBitrate,
                            250_000,
                            50_000_000),
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            case ReachMessageType.DisplayResize:
            {
                var resize = ReachMessageCodec.ReadBody<ReachDisplayResize>(envelope);
                await RestartCaptureAsync(
                        global::System.Math.Clamp(resize.Width, 0, 3840),
                        global::System.Math.Clamp(resize.Height, 0, 2160),
                        global::System.Math.Clamp(resize.FramesPerSecond, 5, 60),
                        _targetBitrate,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            case ReachMessageType.DisplaySelect:
            {
                _selectedDisplayId = ReachMessageCodec
                    .ReadBody<ReachDisplaySelect>(envelope)
                    .DisplayId;
                if (_capture is not null)
                {
                    await RestartCaptureAsync(
                            _targetWidth,
                            _targetHeight,
                            _framesPerSecond,
                            _targetBitrate,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                break;
            }
            case ReachMessageType.RequestKeyFrame:
                await RestartCaptureAsync(
                        _targetWidth,
                        _targetHeight,
                        _framesPerSecond,
                        _targetBitrate,
                        cancellationToken,
                        notifyReset: false)
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task StartCaptureAsync(CancellationToken cancellationToken)
    {
        if (_capture is not null)
        {
            await SendSessionMetadataAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var connection = _connection
            ?? throw new InvalidOperationException("No service connection is available.");
        _frames = Channel.CreateBounded<RawVideoFrame>(
            new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
        _videoMetadataSent = false;
        _streamWidth = 0;
        _streamHeight = 0;
        _capture = CreateCaptureSource();
        _capture.FrameCaptured += OnFrameCaptured;
        await _capture.StartAsync(cancellationToken).ConfigureAwait(false);
        _encodeTask = EncodeLoopAsync(connection, _frames.Reader, cancellationToken);

        StartAudio();
        await SendAudioMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
        await SendTopologyAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task RestartCaptureAsync(
        int width,
        int height,
        int framesPerSecond,
        int targetBitrate,
        CancellationToken cancellationToken,
        bool notifyReset = true)
    {
        _targetWidth = width;
        _targetHeight = height;
        _framesPerSecond = framesPerSecond;
        _targetBitrate = targetBitrate;
        await StopCaptureAsync().ConfigureAwait(false);
        var connection = _connection;
        if (connection is not null)
        {
            if (notifyReset)
            {
                await SendAsync(
                        connection,
                        ReachMessageType.VideoStreamReset,
                        new ReachVideoStreamReset(Interlocked.Read(ref _sequence)),
                        "control",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await StartCaptureAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private WindowsDesktopCaptureSource CreateCaptureSource() =>
        new(
            _framesPerSecond,
            captureAllMonitors: GetSelectedMonitor() is null,
            captureBounds: GetSelectedMonitor() is { } monitor
                ? new Rectangle(
                    monitor.Left,
                    monitor.Top,
                    monitor.Width,
                    monitor.Height)
                : null,
            targetWidth: _targetWidth,
            targetHeight: _targetHeight);

    private WindowsMonitorInfo? GetSelectedMonitor()
    {
        if (!int.TryParse(
                _selectedDisplayId.StartsWith("display-", StringComparison.Ordinal)
                    ? _selectedDisplayId["display-".Length..]
                    : string.Empty,
                out var index))
        {
            return null;
        }

        return _display.GetMonitors().ElementAtOrDefault(index);
    }

    private void StartAudio()
    {
        if (!_audioEnabled || _audioStarted)
            return;

        try
        {
            _audioCapture.DataAvailable += OnAudioDataAvailable;
            _audioCapture.Start();
            _audioStarted = true;
        }
        catch (Exception exception)
        {
            _log.LogWarning(exception, "Loopback audio is unavailable; continuing without audio.");
            _audioCapture.DataAvailable -= OnAudioDataAvailable;
            _audioStarted = false;
        }
    }

    private async Task SendSessionMetadataAsync(CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null)
            return;

        if (_streamWidth > 0 && _streamHeight > 0)
        {
            await SendAsync(
                    connection,
                    ReachMessageType.VideoStreamStart,
                    new ReachVideoStreamStart(
                        "H264",
                        _streamWidth,
                        _streamHeight,
                        _framesPerSecond),
                    "control",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        StartAudio();
        await SendAudioMetadataAsync(connection, cancellationToken).ConfigureAwait(false);
        await SendTopologyAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAudioMetadataAsync(
        ILocalIpcConnection connection,
        CancellationToken cancellationToken)
    {
        if (!_audioStarted || _audioCapture.Format is not { } audioFormat)
            return;

        await SendAsync(
                connection,
                ReachMessageType.AudioStreamStart,
                new ReachAudioStreamStart(
                    audioFormat.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat
                        ? "PCM_FLOAT"
                        : "PCM",
                    audioFormat.SampleRate,
                    audioFormat.Channels,
                    audioFormat.BitsPerSample,
                    audioFormat.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat),
                "control",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EncodeLoopAsync(
        ILocalIpcConnection connection,
        ChannelReader<RawVideoFrame> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var captured in reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                var encodeStart = Stopwatch.GetTimestamp();
                var frame = ResizeFrame(captured);
                _encoder ??= new WindowsH264Encoder(
                    frame.Width,
                    frame.Height,
                    _framesPerSecond,
                    _targetBitrate);
                if (!_encoder.TryEncode(frame, out var encoded)
                    || encoded is null)
                {
                    continue;
                }

                _performance.RecordEncoded(
                    Stopwatch.GetElapsedTime(encodeStart).TotalMilliseconds);
                _streamWidth = encoded.Width;
                _streamHeight = encoded.Height;
                if (!_videoMetadataSent)
                {
                    _videoMetadataSent = true;
                    await SendAsync(
                            connection,
                            ReachMessageType.VideoStreamStart,
                            new ReachVideoStreamStart(
                                encoded.Codec,
                                encoded.Width,
                                encoded.Height,
                                _framesPerSecond),
                            "control",
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                var packet = new ReachVideoFrame(
                    Interlocked.Increment(ref _sequence),
                    encoded.Width,
                    encoded.Height,
                    encoded.Timestamp,
                    encoded.Codec,
                    encoded.IsKeyFrame,
                    encoded.AccessUnit);
                var payload = ReachMessageCodec.Serialize(
                    ReachMessageType.VideoFrame,
                    Interlocked.Increment(ref _sequence),
                    packet);
                QueueVideoFrame(
                    new LocalIpcFrame(
                        Interlocked.Increment(ref _sequence),
                        "media",
                        ReachMessageType.VideoFrame.ToString(),
                        payload));
            }
            catch (Exception exception)
            {
                _log.LogWarning(exception, "Reach frame encoding failed.");
            }
        }
    }

    private async Task VideoSendLoopAsync(
        ILocalIpcConnection connection,
        ChannelReader<LocalIpcFrame> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await connection.SendAsync(frame, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or ObjectDisposedException
                        or InvalidOperationException)
                {
                    _log.LogDebug(
                        exception,
                        "Reach video IPC sender stopped.");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void QueueVideoFrame(LocalIpcFrame frame)
    {
        var queue = Volatile.Read(ref _videoQueue);
        if (queue is null)
        {
            _performance.RecordDropped();
            return;
        }

        if (queue.Writer.TryWrite(frame))
            return;

        if (queue.Reader.TryRead(out _))
            _performance.RecordDropped();
        if (!queue.Writer.TryWrite(frame))
            _performance.RecordDropped();
    }

    private RawVideoFrame ResizeFrame(RawVideoFrame frame)
    {
        if (_targetWidth <= 0
            || _targetHeight <= 0
            || (_targetWidth == frame.Width && _targetHeight == frame.Height))
        {
            return frame;
        }

        var width = global::System.Math.Clamp(_targetWidth, 1, 3840);
        var height = global::System.Math.Clamp(_targetHeight, 1, 2160);
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var sourceY = y * frame.Height / height;
            for (var x = 0; x < width; x++)
            {
                var sourceX = x * frame.Width / width;
                var source = sourceY * frame.Stride + sourceX * 4;
                var destination = (y * width + x) * 4;
                frame.Pixels.AsSpan(source, 4).CopyTo(pixels.AsSpan(destination, 4));
            }
        }

        return new RawVideoFrame(
            width,
            height,
            width * 4,
            frame.Format,
            pixels,
            frame.Timestamp);
    }

    private async Task SendTopologyAsync(
        ILocalIpcConnection connection,
        CancellationToken cancellationToken)
    {
        var displays = _display.GetMonitors()
            .Select((monitor, index) => new ReachDisplay(
                $"display-{index}",
                monitor.Left,
                monitor.Top,
                monitor.Width,
                monitor.Height,
                monitor.Dpi))
            .ToArray();
        await SendAsync(
                connection,
                ReachMessageType.DisplayTopology,
                new ReachDisplayTopology(displays),
                "control",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendClipboardAsync(CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null)
            return;

        var files = _clipboard.ReadFileDropList();
        await SendAsync(
                connection,
                ReachMessageType.ClipboardContent,
                new ReachClipboardContent(
                    files.Count == 0 ? "text" : "files",
                    _clipboard.ReadText(),
                    files.ToArray()),
                "control",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendAsync<T>(
        ILocalIpcConnection connection,
        ReachMessageType type,
        T message,
        string kind,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = ReachMessageCodec.Serialize(
                type,
                Interlocked.Increment(ref _sequence),
                message);
            await connection.SendAsync(
                    new LocalIpcFrame(
                        Interlocked.Increment(ref _sequence),
                        kind,
                        type.ToString(),
                        payload),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void OnFrameCaptured(RawVideoFrame frame)
    {
        _performance.RecordCaptured();
        _frames?.Writer.TryWrite(frame);
    }

    private void OnAudioDataAvailable(object? sender, NAudio.Wave.WaveInEventArgs args)
    {
        var connection = _connection;
        var format = _audioCapture.Format;
        if (connection is null || format is null || args.BytesRecorded == 0)
            return;

        var data = args.Buffer.AsSpan(0, args.BytesRecorded).ToArray();
        _ = SendAsync(
                connection,
                ReachMessageType.AudioFrame,
                new ReachAudioFrame(
                    Interlocked.Increment(ref _sequence),
                    DateTime.UtcNow.Ticks,
                    format.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat
                        ? "PCM_FLOAT"
                        : "PCM",
                    format.SampleRate,
                    format.Channels,
                    data,
                    format.BitsPerSample,
                    format.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat),
                "media",
                CancellationToken.None)
            .ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                        _log.LogDebug(task.Exception, "Audio frame send failed.");
                },
                TaskScheduler.Default);
    }

    private async Task StopCaptureAsync()
    {
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is not null)
        {
            capture.FrameCaptured -= OnFrameCaptured;
            await capture.StopAsync().ConfigureAwait(false);
            await capture.DisposeAsync().ConfigureAwait(false);
        }

        if (_audioStarted)
        {
            _audioCapture.DataAvailable -= OnAudioDataAvailable;
            _audioStarted = false;
            await _audioCapture.DisposeAsync().ConfigureAwait(false);
        }

        _frames?.Writer.TryComplete();
        _frames = null;
        var encodeTask = Interlocked.Exchange(ref _encodeTask, null);
        if (encodeTask is not null)
        {
            try
            {
                await encodeTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _encoder?.Dispose();
        _encoder = null;
        _videoMetadataSent = false;
        _streamWidth = 0;
        _streamHeight = 0;
    }
}
