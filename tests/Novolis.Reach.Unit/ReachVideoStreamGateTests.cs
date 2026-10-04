using Novolis.Reach.Client;
using Novolis.Reach.Protocol;

namespace Reach.Unit;

public sealed class ReachVideoStreamGateTests
{
    [Test]
    public async Task ResetRejectsDeltaFramesUntilAnIntraFrameArrives()
    {
        var gate = new ReachVideoStreamGate();
        gate.RequireKeyFrame();

        var delta = CreateFrame(sequence: 1, isKeyFrame: false);
        var key = CreateFrame(sequence: 2, isKeyFrame: true);

        await Assert.That(gate.TryAccept(delta, out _)).IsFalse();
        await Assert.That(gate.TryAccept(key, out var generation)).IsTrue();
        await Assert.That(gate.IsCurrent(generation)).IsTrue();
        await Assert.That(gate.TryAccept(
            CreateFrame(sequence: 3, isKeyFrame: false),
            out _)).IsTrue();
    }

    [Test]
    public async Task NewResetMakesDecodedWorkFromThePreviousStreamStale()
    {
        var gate = new ReachVideoStreamGate();
        var frame = CreateFrame(sequence: 1, isKeyFrame: true);

        await Assert.That(gate.TryAccept(frame, out var previousGeneration)).IsTrue();
        gate.RequireKeyFrame();

        await Assert.That(gate.IsCurrent(previousGeneration)).IsFalse();
        await Assert.That(gate.TryAccept(
            CreateFrame(sequence: 2, isKeyFrame: false),
            out _)).IsFalse();
    }

    private static ReachVideoFrame CreateFrame(long sequence, bool isKeyFrame) =>
        new(
            sequence,
            16,
            16,
            sequence,
            "H264",
            isKeyFrame,
            [1, 2, 3]);
}
