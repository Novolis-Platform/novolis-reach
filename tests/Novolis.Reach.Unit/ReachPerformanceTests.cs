using Novolis.Reach.Client;
using Novolis.Reach.Protocol;

namespace Reach.Unit;

public sealed class ReachPerformanceTests
{
    [Test]
    public async Task MetricsKeepBoundedFrameAndRoundTripSamples()
    {
        var metrics = new ReachPerformanceMetrics();
        var sourceTicks = DateTime.UtcNow.Ticks
            - TimeSpan.FromMilliseconds(25).Ticks;

        metrics.RecordCaptured();
        metrics.RecordEncoded(4.5);
        metrics.RecordReceived(128, sourceTicks);
        metrics.RecordDecoded(3.2);
        metrics.RecordPresented(1.1, sourceTicks);
        metrics.RecordInputRoundTrip(42);
        metrics.RecordKeyFrameRequest();

        var snapshot = metrics.Snapshot();

        await Assert.That(snapshot.CapturedFrames).IsEqualTo(1);
        await Assert.That(snapshot.EncodedFrames).IsEqualTo(1);
        await Assert.That(snapshot.ReceivedFrames).IsEqualTo(1);
        await Assert.That(snapshot.PresentedFrames).IsEqualTo(1);
        await Assert.That(snapshot.KeyFrameRequests).IsEqualTo(1);
        await Assert.That(snapshot.FrameAgeP95Milliseconds.GetValueOrDefault())
            .IsGreaterThan(0);
        await Assert.That(snapshot.InputRoundTripP95Milliseconds).IsEqualTo(42);
    }

    [Test]
    public async Task ProfileControllerDowngradesUnderPressureAndUpgradesAfterStability()
    {
        var display = new ReachDisplay(
            "display-0",
            0,
            0,
            1920,
            1080,
            96);
        var controller = new ReachVideoProfileController(
            display,
            ReachVideoProfileKind.Lan);
        var pressure = new ReachPerformanceSnapshot(
            0,
            0,
            0,
            10,
            0,
            1,
            0,
            0,
            0,
            250,
            250,
            null,
            null,
            null,
            220,
            null);

        var downgraded = controller.Observe(
            pressure,
            DateTimeOffset.UtcNow.AddSeconds(5));

        await Assert.That(downgraded).IsNotNull();
        await Assert.That(downgraded!.Kind).IsEqualTo(ReachVideoProfileKind.Routed);
        await Assert.That(downgraded.Width).IsLessThanOrEqualTo(720);

        var stable = pressure with
        {
            DroppedFrames = 1,
            FrameAgeP95Milliseconds = 20,
            InputRoundTripP95Milliseconds = 20,
        };
        var upgraded = controller.Observe(
            stable,
            DateTimeOffset.UtcNow.AddSeconds(20));

        await Assert.That(upgraded).IsNotNull();
        await Assert.That(upgraded!.Kind).IsEqualTo(ReachVideoProfileKind.Lan);
    }
}
