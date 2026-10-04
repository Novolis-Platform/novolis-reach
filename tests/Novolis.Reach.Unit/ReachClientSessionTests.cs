using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Linq;
using Novolis.Reach.Client;
using Novolis.Reach.Protocol;
using Novolis.Transports.Framing;
using ClientSessionState = Novolis.Reach.Client.ReachClientConnectionState;

namespace Reach.Unit;

public sealed class ReachClientSessionTests
{
    [Test]
    public async Task ClientSessionCompletesLocalEmulatorHandshakeAndReceivesInput()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var inputReceived = new TaskCompletionSource<ReachPointerMove>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var videoReceived = new TaskCompletionSource<ReachVideoFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new ConcurrentQueue<string>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var emulator = RunEmulatorAsync(
            listener,
            inputReceived,
            cancellation.Token);

        await using var session = new ReachClientSession(mediaPort: 0);
        session.StatusChanged += statuses.Enqueue;
        session.VideoFrameReceived += frame => videoReceived.TrySetResult(frame);
        await session.ConnectAsync(
                $"tcp://127.0.0.1:{endpoint.Port}",
                ReachPlatform.Windows,
                "Reach.Unit",
                cancellation.Token)
            .ConfigureAwait(false);

        var video = await videoReceived.Task.WaitAsync(cancellation.Token);
        await Assert.That(video.Codec).IsEqualTo("H264");
        await Assert.That(video.IsKeyFrame).IsTrue();
        await Assert.That(video.AccessUnit.SequenceEqual(new byte[] { 1, 2, 3 })).IsTrue();
        await Assert.That(session.State).IsEqualTo(ClientSessionState.Streaming);
        await Assert.That(statuses.Any(status =>
            status.Contains(
                "Connected to Reach.Unit Emulator",
                StringComparison.Ordinal))).IsTrue();

        await session.SendPointerMoveAsync(42, 24, cancellation.Token);
        var input = await inputReceived.Task.WaitAsync(cancellation.Token);
        await Assert.That(input.X).IsEqualTo(42);
        await Assert.That(input.Y).IsEqualTo(24);

        await session.DisconnectAsync().ConfigureAwait(false);
        await emulator.WaitAsync(cancellation.Token).ConfigureAwait(false);
    }

    [Test]
    public async Task ClientSessionReportsControlLossAndClearsConnectionState()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var connectionLost = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var emulator = RunEmulatorThenCloseAsync(
            listener,
            sendSessionClose: false,
            cancellation.Token);

        await using var session = new ReachClientSession(mediaPort: 0);
        session.ConnectionLost += () => connectionLost.TrySetResult(true);
        await session.ConnectAsync(
                $"tcp://127.0.0.1:{endpoint.Port}",
                ReachPlatform.Android,
                "Reach.Unit",
                cancellation.Token)
            .ConfigureAwait(false);

        await connectionLost.Task.WaitAsync(cancellation.Token);
        await Assert.That(session.IsConnected).IsFalse();
        await Assert.That(session.State).IsEqualTo(ClientSessionState.Lost);
        await emulator.WaitAsync(cancellation.Token).ConfigureAwait(false);
    }

    [Test]
    public async Task ClientSessionReportsRemoteSessionClose()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var sessionEnded = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionLost = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var emulator = RunEmulatorThenCloseAsync(
            listener,
            sendSessionClose: true,
            cancellation.Token);

        await using var session = new ReachClientSession(mediaPort: 0);
        session.SessionEnded += reason => sessionEnded.TrySetResult(reason);
        session.ConnectionLost += () => connectionLost.TrySetResult(true);
        await session.ConnectAsync(
                $"tcp://127.0.0.1:{endpoint.Port}",
                ReachPlatform.Android,
                "Reach.Unit",
                cancellation.Token)
            .ConfigureAwait(false);

        var reason = await sessionEnded.Task.WaitAsync(cancellation.Token);
        await connectionLost.Task.WaitAsync(cancellation.Token);
        await Assert.That(reason).IsEqualTo("Test host closed the session.");
        await Assert.That(session.State).IsEqualTo(ClientSessionState.Lost);
        await emulator.WaitAsync(cancellation.Token).ConfigureAwait(false);
    }

    [Test]
    public async Task ClientSessionReportsMediaChannelLoss()
    {
        using var controlListener = new TcpListener(IPAddress.Loopback, 0);
        controlListener.Start();
        var controlPort = ((IPEndPoint)controlListener.LocalEndpoint).Port;
        using var mediaListener = new TcpListener(IPAddress.Loopback, 0);
        mediaListener.Start();
        var mediaPort = ((IPEndPoint)mediaListener.LocalEndpoint).Port;
        var mediaLost = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var emulator = RunMediaEmulatorAsync(
            controlListener,
            mediaListener,
            cancellation.Token);

        await using var session = new ReachClientSession(mediaPort);
        session.MediaConnectionLost += () => mediaLost.TrySetResult(true);
        await session.ConnectAsync(
                $"tcp://127.0.0.1:{controlPort}",
                ReachPlatform.Android,
                "Reach.Unit",
                cancellation.Token)
            .ConfigureAwait(false);

        await mediaLost.Task.WaitAsync(cancellation.Token);
        await Assert.That(session.IsConnected).IsTrue();
        await Assert.That(session.IsMediaConnected).IsFalse();
        await Assert.That(session.State).IsEqualTo(ClientSessionState.Connected);
        await session.DisconnectAsync().ConfigureAwait(false);
        cancellation.Cancel();
        await emulator.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task ClientSessionReconnectsAfterControlLoss()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var connectionLost = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var emulator = RunReconnectEmulatorAsync(
            listener,
            reconnected,
            cancellation.Token);

        await using var session = new ReachClientSession(mediaPort: 0);
        session.ConnectionLost += () => connectionLost.TrySetResult(true);
        await session.ConnectAsync(
                $"tcp://127.0.0.1:{endpoint.Port}",
                ReachPlatform.Android,
                "Reach.Unit",
                cancellation.Token)
            .ConfigureAwait(false);

        await connectionLost.Task.WaitAsync(cancellation.Token);
        await session.ReconnectAsync(cancellation.Token);
        await reconnected.Task.WaitAsync(cancellation.Token);
        await Assert.That(session.IsConnected).IsTrue();
        await Assert.That(session.State).IsEqualTo(ClientSessionState.Connected);

        await session.DisconnectAsync().ConfigureAwait(false);
        cancellation.Cancel();
        await emulator.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task RunEmulatorAsync(
        TcpListener listener,
        TaskCompletionSource<ReachPointerMove> inputReceived,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken)
                .ConfigureAwait(false);
            using var stream = client.GetStream();

            var hello = await ReadBodyAsync<ReachClientHello>(
                    stream,
                    cancellationToken)
                .ConfigureAwait(false);
            var capabilities = await ReadBodyAsync<ReachCapabilitiesMessage>(
                    stream,
                    cancellationToken)
                .ConfigureAwait(false);
            await SendAsync(
                    stream,
                    ReachMessageType.HostHello,
                    new ReachHostHello(
                        ReachProtocol.AppId,
                        ReachProtocol.Version,
                        "Reach.Unit Emulator",
                        ["tcp://127.0.0.1:19800"]),
                    cancellationToken)
                .ConfigureAwait(false);
            await SendAsync(
                    stream,
                    ReachMessageType.HostCapabilities,
                    new ReachCapabilitiesMessage(
                        ReachCapabilities.Intersect(
                            ReachCapabilities.WindowsHost,
                            capabilities.Capabilities)),
                    cancellationToken)
                .ConfigureAwait(false);

            var open = await ReadEnvelopeAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            if (open?.Type is not ReachMessageType.SessionOpen
                and not ReachMessageType.SessionResume)
            {
                throw new InvalidDataException("The client did not open a session.");
            }

            await SendAsync(
                    stream,
                    ReachMessageType.VideoStreamStart,
                    new ReachVideoStreamStart("H264", 2, 2, 1),
                    cancellationToken)
                .ConfigureAwait(false);
            await SendAsync(
                    stream,
                    ReachMessageType.VideoFrame,
                    new ReachVideoFrame(
                        1,
                        2,
                        2,
                        DateTime.UtcNow.Ticks,
                        "H264",
                        true,
                        [1, 2, 3]),
                    cancellationToken)
                .ConfigureAwait(false);

            while (true)
            {
                var envelope = await ReadEnvelopeAsync(stream, cancellationToken)
                    .ConfigureAwait(false);
                if (envelope is null)
                    return;
                if (envelope.Type == ReachMessageType.PointerMove)
                {
                    inputReceived.TrySetResult(
                        ReachMessageCodec.ReadBody<ReachPointerMove>(envelope));
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RunEmulatorThenCloseAsync(
        TcpListener listener,
        bool sendSessionClose,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken)
                .ConfigureAwait(false);
            using var stream = client.GetStream();
            await CompleteHandshakeAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            if (sendSessionClose)
            {
                await SendAsync(
                        stream,
                        ReachMessageType.SessionClose,
                        new ReachSessionClose(
                            Guid.Empty,
                            "Test host closed the session."),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RunMediaEmulatorAsync(
        TcpListener controlListener,
        TcpListener mediaListener,
        CancellationToken cancellationToken)
    {
        try
        {
            using var controlClient = await controlListener.AcceptTcpClientAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            using var controlStream = controlClient.GetStream();
            await CompleteHandshakeAsync(controlStream, cancellationToken)
                .ConfigureAwait(false);

            using (var mediaClient = await mediaListener.AcceptTcpClientAsync(
                       cancellationToken).ConfigureAwait(false))
            using (var mediaStream = mediaClient.GetStream())
            {
                var mediaHello = await ReadEnvelopeAsync(
                        mediaStream,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (mediaHello?.Type is not ReachMessageType.MediaHello)
                    throw new InvalidDataException("Media hello was not received.");

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            controlListener.Stop();
            mediaListener.Stop();
        }
    }

    private static async Task RunReconnectEmulatorAsync(
        TcpListener listener,
        TaskCompletionSource<bool> reconnected,
        CancellationToken cancellationToken)
    {
        try
        {
            using (var firstClient = await listener.AcceptTcpClientAsync(
                       cancellationToken).ConfigureAwait(false))
            {
                using var firstStream = firstClient.GetStream();
                await CompleteHandshakeAsync(firstStream, cancellationToken)
                    .ConfigureAwait(false);
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            using var secondClient = await listener.AcceptTcpClientAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            using var secondStream = secondClient.GetStream();
            await CompleteHandshakeAsync(secondStream, cancellationToken)
                .ConfigureAwait(false);
            reconnected.TrySetResult(true);
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task CompleteHandshakeAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        await ReadBodyAsync<ReachClientHello>(stream, cancellationToken)
            .ConfigureAwait(false);
        var capabilities = await ReadBodyAsync<ReachCapabilitiesMessage>(
                stream,
                cancellationToken)
            .ConfigureAwait(false);
        await SendAsync(
                stream,
                ReachMessageType.HostHello,
                new ReachHostHello(
                    ReachProtocol.AppId,
                    ReachProtocol.Version,
                    "Reach.Unit Emulator",
                    ["tcp://127.0.0.1:19800"]),
                cancellationToken)
            .ConfigureAwait(false);
        await SendAsync(
                stream,
                ReachMessageType.HostCapabilities,
                new ReachCapabilitiesMessage(
                    ReachCapabilities.Intersect(
                        ReachCapabilities.WindowsHost,
                        capabilities.Capabilities)),
                cancellationToken)
            .ConfigureAwait(false);

        var open = await ReadEnvelopeAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        if (open?.Type is not ReachMessageType.SessionOpen
            and not ReachMessageType.SessionResume)
        {
            throw new InvalidDataException("The client did not open a session.");
        }
    }

    private static async Task<T> ReadBodyAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var envelope = await ReadEnvelopeAsync(stream, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new EndOfStreamException();
        return ReachMessageCodec.ReadBody<T>(envelope);
    }

    private static async Task<ReachMessageEnvelope?> ReadEnvelopeAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var frame = await LengthPrefixedFrameCodec.ReadAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return frame is null ? null : ReachMessageCodec.Deserialize(frame.Payload);
    }

    private static ValueTask SendAsync<T>(
        Stream stream,
        ReachMessageType type,
        T message,
        CancellationToken cancellationToken) =>
        LengthPrefixedFrameCodec.WriteAsync(
            stream,
            ReachMessageCodec.Serialize(type, DateTime.UtcNow.Ticks, message),
            cancellationToken);
}
