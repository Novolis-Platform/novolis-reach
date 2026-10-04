using Novolis.Reach.Client;

namespace Reach.Unit;

public sealed class ReachVideoFrameOrientationTests
{
    [Test]
    public async Task NormalizationMovesBottomMarkerToTop()
    {
        const int width = 2;
        const int height = 3;
        var pixels = new byte[width * height * 4];
        FillRow(pixels, width, 0, 0x11);
        FillRow(pixels, width, 1, 0x22);
        FillRow(pixels, width, 2, 0xEE);

        ReachVideoFrameOrientation.NormalizeTopToBottom(
            pixels,
            width,
            height);

        await Assert.That(RowMarker(pixels, width, 0)).IsEqualTo((byte)0xEE);
        await Assert.That(RowMarker(pixels, width, 1)).IsEqualTo((byte)0x22);
        await Assert.That(RowMarker(pixels, width, 2)).IsEqualTo((byte)0x11);
    }

    [Test]
    public async Task NormalizationPreservesPointMappingConvention()
    {
        var fit = ReachVideoGeometry.CalculateFit(
            surfaceWidth: 100,
            surfaceHeight: 100,
            videoWidth: 10,
            videoHeight: 10,
            zoom: 1,
            panX: 0,
            panY: 0);

        var mapped = ReachVideoGeometry.TryMapPoint(
            fit,
            pointX: 50,
            pointY: 0,
            videoWidth: 10,
            videoHeight: 10,
            displayLeft: 0,
            displayTop: 0,
            displayWidth: 10,
            displayHeight: 10,
            out _,
            out var sourceY);

        await Assert.That(mapped).IsTrue();
        await Assert.That(sourceY).IsEqualTo(0);
    }

    private static void FillRow(
        byte[] pixels,
        int width,
        int row,
        byte marker)
    {
        for (var x = 0; x < width; x++)
        {
            var offset = (row * width + x) * 4;
            pixels[offset] = marker;
            pixels[offset + 1] = marker;
            pixels[offset + 2] = marker;
            pixels[offset + 3] = byte.MaxValue;
        }
    }

    private static byte RowMarker(
        byte[] pixels,
        int width,
        int row) =>
        pixels[row * width * 4];
}
