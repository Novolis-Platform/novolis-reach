using Novolis.Reach.Protocol;

namespace Reach.Unit;

public sealed class ReachProtocolTests
{
    [Test]
    public async Task CompatibleVersionsShareMajorLine()
    {
        await Assert.That(ReachProtocol.IsCompatible("1.9")).IsTrue();
        await Assert.That(ReachProtocol.IsCompatible("2.0")).IsFalse();
        await Assert.That(ReachProtocol.IsCompatible(null)).IsFalse();
    }

    [Test]
    public async Task CapabilityIntersectionPreservesCommonCodecs()
    {
        var host = new ReachCapabilities(
            ReachCapability.H264
            | ReachCapability.Mouse
            | ReachCapability.Audio,
            MaximumDisplays: 4,
            VideoCodecs: ["H264", "AV1"],
            AudioCodecs: ["PCM"]);
        var client = new ReachCapabilities(
            ReachCapability.H264
            | ReachCapability.Mouse,
            MaximumDisplays: 2,
            VideoCodecs: ["H264"],
            AudioCodecs: ["Opus"]);

        var negotiated = ReachCapabilities.Intersect(host, client);

        await Assert.That(negotiated.Supports(ReachCapability.H264)).IsTrue();
        await Assert.That(negotiated.Supports(ReachCapability.Audio)).IsFalse();
        await Assert.That(negotiated.MaximumDisplays).IsEqualTo(2);
        await Assert.That(negotiated.OfferedVideoCodecs).Contains("H264");
        await Assert.That(negotiated.OfferedAudioCodecs).IsEmpty();
    }

    [Test]
    public async Task MessageCodecRoundTripsTypedBody()
    {
        var bytes = ReachMessageCodec.Serialize(
            ReachMessageType.ClientHello,
            sequence: 4,
            new ReachClientHello(
                ReachProtocol.AppId,
                ReachProtocol.Version,
                ReachPlatform.Android,
                "phone"));

        var envelope = ReachMessageCodec.Deserialize(bytes);
        var hello = ReachMessageCodec.ReadBody<ReachClientHello>(envelope);

        await Assert.That(envelope.Type).IsEqualTo(ReachMessageType.ClientHello);
        await Assert.That(envelope.Sequence).IsEqualTo(4);
        await Assert.That(hello.Platform).IsEqualTo(ReachPlatform.Android);
        await Assert.That(hello.ClientName).IsEqualTo("phone");
    }

    [Test]
    public async Task BulkStreamHelloRoundTripsTypedBody()
    {
        var sessionId = Guid.NewGuid();
        var bytes = ReachMessageCodec.Serialize(
            ReachMessageType.BulkHello,
            sequence: 7,
            new ReachBulkHello(
                sessionId,
                ReachProtocol.AppId,
                ReachProtocol.Version));

        var envelope = ReachMessageCodec.Deserialize(bytes);
        var hello = ReachMessageCodec.ReadBody<ReachBulkHello>(envelope);

        await Assert.That(envelope.Type).IsEqualTo(ReachMessageType.BulkHello);
        await Assert.That(hello.SessionId).IsEqualTo(sessionId);
        await Assert.That(hello.AppId).IsEqualTo(ReachProtocol.AppId);
    }

    [Test]
    public async Task SessionStateMachineAcceptsOpenAndResume()
    {
        var machine = new ReachSessionStateMachine();

        await Assert.That(machine.TryTransition(ReachSessionState.HelloExchanged)).IsTrue();
        await Assert.That(machine.TryTransition(ReachSessionState.Opening)).IsTrue();
        await Assert.That(machine.TryTransition(ReachSessionState.Open)).IsTrue();
        await Assert.That(machine.TryTransition(ReachSessionState.Resuming)).IsTrue();
        await Assert.That(machine.TryTransition(ReachSessionState.Open)).IsTrue();
        await Assert.That(machine.State).IsEqualTo(ReachSessionState.Open);
    }

    [Test]
    public async Task SessionStateMachineRejectsOpeningFromNew()
    {
        var machine = new ReachSessionStateMachine();

        await Assert.That(machine.TryTransition(ReachSessionState.Opening)).IsFalse();
        await Assert.That(machine.State).IsEqualTo(ReachSessionState.New);
    }
}
