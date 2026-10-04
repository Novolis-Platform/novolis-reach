namespace Novolis.Reach.Client;

/// <summary>Describes a fitted and transformed remote video rectangle.</summary>
public readonly record struct ReachVideoFit(
    double Scale,
    double Zoom,
    double OriginX,
    double OriginY,
    double RenderedWidth,
    double RenderedHeight,
    double PanX,
    double PanY);
