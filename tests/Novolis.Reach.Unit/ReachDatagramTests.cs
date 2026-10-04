using System.Security.Cryptography;
using Novolis.Reach.Transport;

namespace Reach.Unit;

public sealed class ReachDatagramTests
{
    [Test]
    public async Task HandshakeRoundTripsSessionAndToken()
    {
        var sessionId = Guid.NewGuid();
        var packet = ReachDatagramPacketCodec.EncodeHandshake(
            sessionId,
            "reach-test-token");

        var decoded = ReachDatagramPacketCodec.TryDecodeHandshake(
            packet,
            out var decodedSessionId,
            out var token);

        await Assert.That(decoded).IsTrue();
        await Assert.That(decodedSessionId).IsEqualTo(sessionId);
        await Assert.That(token).IsEqualTo("reach-test-token");
    }

    [Test]
    public async Task AuthenticatedPayloadReassemblesOutOfOrder()
    {
        var session = CreateSession();
        var payload = RandomNumberGenerator.GetBytes(4_000);
        var packets = ReachDatagramPacketCodec.EncodeData(
            session,
            sequence: 9,
            payload,
            maximumPacketSize: 300);
        var reassembler = new ReachDatagramReassembler();
        byte[]? result = null;

        foreach (var packet in packets.Reverse())
        {
            var decoded = ReachDatagramPacketCodec.TryDecodeData(
                session,
                packet,
                out var fragment);
            await Assert.That(decoded).IsTrue();
            if (reassembler.TryAccept(
                    fragment,
                    DateTimeOffset.UtcNow,
                    out var completed))
            {
                result = completed;
            }
        }

        await Assert.That(result).IsNotNull();
        await Assert.That(result!).IsEquivalentTo(payload);
        await Assert.That(packets.All(
            static packet => packet.Length <= 300)).IsTrue();
    }

    [Test]
    public async Task TamperedPayloadIsRejected()
    {
        var session = CreateSession();
        var packet = ReachDatagramPacketCodec.EncodeData(
            session,
            sequence: 1,
            "hello"u8)[0];
        packet[^1] ^= 0x7f;

        var decoded = ReachDatagramPacketCodec.TryDecodeData(
            session,
            packet,
            out _);

        await Assert.That(decoded).IsFalse();
    }

    [Test]
    public async Task IncompleteSequenceExpiresAndDoesNotBlockLaterFrame()
    {
        var session = CreateSession();
        var reassembler = new ReachDatagramReassembler(
            maximumAge: TimeSpan.FromMilliseconds(10));
        var first = ReachDatagramPacketCodec.EncodeData(
            session,
            sequence: 1,
            RandomNumberGenerator.GetBytes(600),
            maximumPacketSize: 300);
        var secondPayload = "next frame"u8.ToArray();
        var second = ReachDatagramPacketCodec.EncodeData(
            session,
            sequence: 2,
            secondPayload,
            maximumPacketSize: 300);
        var now = DateTimeOffset.UtcNow;

        ReachDatagramPacketCodec.TryDecodeData(
            session,
            first[0],
            out var firstFragment);
        reassembler.TryAccept(firstFragment, now, out _);
        ReachDatagramPacketCodec.TryDecodeData(
            session,
            second[0],
            out var secondFragment);

        var completed = reassembler.TryAccept(
            secondFragment,
            now.AddMilliseconds(20),
            out var result);

        await Assert.That(completed).IsTrue();
        await Assert.That(result).IsEquivalentTo(secondPayload);
        await Assert.That(reassembler.PendingCount).IsEqualTo(0);

        await Assert.That(
                reassembler.TryAccept(
                    firstFragment,
                    now.AddMilliseconds(30),
                    out _))
            .IsFalse();
    }

    private static ReachDatagramSession CreateSession() =>
        new(
            Guid.NewGuid(),
            RandomNumberGenerator.GetBytes(32),
            "token");
}
