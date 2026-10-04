namespace Novolis.Reach.Client;

/// <summary>Calculates fit, zoom, pan, and source-display coordinates.</summary>
public static class ReachVideoGeometry
{
    /// <summary>Calculates a centered fit for a remote video frame.</summary>
    public static ReachVideoFit CalculateFit(
        double surfaceWidth,
        double surfaceHeight,
        int videoWidth,
        int videoHeight,
        double zoom,
        double panX,
        double panY)
    {
        if (surfaceWidth <= 0
            || surfaceHeight <= 0
            || videoWidth <= 0
            || videoHeight <= 0)
        {
            return default;
        }

        var scale = Math.Min(
            surfaceWidth / videoWidth,
            surfaceHeight / videoHeight);
        var clampedZoom = Math.Clamp(zoom, 1, 4);
        var baseWidth = videoWidth * scale;
        var baseHeight = videoHeight * scale;
        var renderedWidth = baseWidth * clampedZoom;
        var renderedHeight = baseHeight * clampedZoom;
        var maxPanX = Math.Max(0, (renderedWidth - surfaceWidth) / 2);
        var maxPanY = Math.Max(0, (renderedHeight - surfaceHeight) / 2);
        var clampedPanX = Math.Clamp(panX, -maxPanX, maxPanX);
        var clampedPanY = Math.Clamp(panY, -maxPanY, maxPanY);
        var centerX = surfaceWidth / 2;
        var centerY = surfaceHeight / 2;
        var originX = centerX
            + ((surfaceWidth - baseWidth) / 2 - centerX) * clampedZoom
            + clampedPanX;
        var originY = centerY
            + ((surfaceHeight - baseHeight) / 2 - centerY) * clampedZoom
            + clampedPanY;

        return new ReachVideoFit(
            scale,
            clampedZoom,
            originX,
            originY,
            renderedWidth,
            renderedHeight,
            clampedPanX,
            clampedPanY);
    }

    /// <summary>
    /// Maps a point in the fitted surface to the selected source display.
    /// </summary>
    public static bool TryMapPoint(
        ReachVideoFit fit,
        double pointX,
        double pointY,
        int videoWidth,
        int videoHeight,
        int displayLeft,
        int displayTop,
        int displayWidth,
        int displayHeight,
        out double sourceX,
        out double sourceY)
    {
        sourceX = 0;
        sourceY = 0;
        if (fit.Scale <= 0
            || videoWidth <= 0
            || videoHeight <= 0
            || pointX < fit.OriginX
            || pointY < fit.OriginY
            || pointX >= fit.OriginX + fit.RenderedWidth
            || pointY >= fit.OriginY + fit.RenderedHeight)
        {
            return false;
        }

        var transformedScale = fit.Scale * fit.Zoom;
        sourceX = displayLeft + Math.Clamp(
            (pointX - fit.OriginX) / transformedScale,
            0,
            videoWidth - 1)
            * (displayWidth > 0
                ? (double)displayWidth / videoWidth
                : 1);
        sourceY = displayTop + Math.Clamp(
            (pointY - fit.OriginY) / transformedScale,
            0,
            videoHeight - 1)
            * (displayHeight > 0
                ? (double)displayHeight / videoHeight
                : 1);
        return true;
    }
}
