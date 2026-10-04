using Novolis.Reach.Protocol;

namespace Novolis.Reach.Client;

/// <summary>Quality tier selected for a remote video stream.</summary>
public enum ReachVideoProfileKind
{
    Lan,
    Routed,
    Constrained,
}
