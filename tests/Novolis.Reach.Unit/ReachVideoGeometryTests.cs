using Novolis.Reach.Client;

namespace Reach.Unit;

public sealed class ReachVideoGeometryTests
{
    [Test]
    public async Task FitKeepsUltrawideFrameCenteredInsidePortraitSurface()
    {
        var fit = ReachVideoGeometry.CalculateFit(
            surfaceWidth: 424,
            surfaceHeight: 800,
            videoWidth: 960,
            videoHeight: 400,
            zoom: 1,
            panX: 0,
            panY: 0);

        await Assert.That(fit.Scale).IsEqualTo(424d / 960d).Within(1e-9);
        await Assert.That(fit.OriginX).IsEqualTo(0).Within(1e-9);
        await Assert.That(fit.OriginY).IsEqualTo(311.6666666667).Within(1e-9);
        await Assert.That(fit.RenderedWidth).IsEqualTo(424).Within(1e-9);
        await Assert.That(fit.RenderedHeight).IsEqualTo(176.6666666667).Within(1e-9);
    }

    [Test]
    public async Task CenterPointMapsToCenterOfSelectedDisplay()
    {
        var fit = ReachVideoGeometry.CalculateFit(
            surfaceWidth: 424,
            surfaceHeight: 800,
            videoWidth: 960,
            videoHeight: 400,
            zoom: 1,
            panX: 0,
            panY: 0);

        var mapped = ReachVideoGeometry.TryMapPoint(
            fit,
            pointX: 212,
            pointY: 400,
            videoWidth: 960,
            videoHeight: 400,
            displayLeft: 100,
            displayTop: 50,
            displayWidth: 1920,
            displayHeight: 800,
            out var sourceX,
            out var sourceY);

        await Assert.That(mapped).IsTrue();
        await Assert.That(sourceX).IsEqualTo(1060).Within(1e-9);
        await Assert.That(sourceY).IsEqualTo(450).Within(1e-9);
    }

    [Test]
    public async Task PointsInLetterboxAreRejected()
    {
        var fit = ReachVideoGeometry.CalculateFit(
            surfaceWidth: 424,
            surfaceHeight: 800,
            videoWidth: 960,
            videoHeight: 400,
            zoom: 1,
            panX: 0,
            panY: 0);

        var mapped = ReachVideoGeometry.TryMapPoint(
            fit,
            pointX: 212,
            pointY: 100,
            videoWidth: 960,
            videoHeight: 400,
            displayLeft: 0,
            displayTop: 0,
            displayWidth: 1920,
            displayHeight: 800,
            out _,
            out _);

        await Assert.That(mapped).IsFalse();
    }
}
