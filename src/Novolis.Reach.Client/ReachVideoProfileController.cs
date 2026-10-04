using Novolis.Reach.Protocol;

namespace Novolis.Reach.Client;

/// <summary>
/// Applies bounded quality changes with cooldowns so network pressure does not
/// cause visible profile oscillation.
/// </summary>
public sealed class ReachVideoProfileController
{
    private readonly ReachDisplay _display;
    private ReachVideoProfile _current;
    private DateTimeOffset _lastChange;
    private long _lastDroppedFrames;
    private DateTimeOffset _stableSince;

    /// <summary>Creates a profile controller for one selected display.</summary>
    public ReachVideoProfileController(
        ReachDisplay display,
        ReachVideoProfileKind initialKind)
    {
        _display = display ?? throw new ArgumentNullException(nameof(display));
        _current = ReachVideoProfile.ForDisplay(display, initialKind);
        _lastChange = DateTimeOffset.UtcNow;
        _stableSince = _lastChange;
    }

    /// <summary>Gets the profile currently applied to the host.</summary>
    public ReachVideoProfile Current => _current;

    /// <summary>
    /// Returns a new profile only when pressure or sustained stability warrants
    /// a change.
    /// </summary>
    public ReachVideoProfile? Observe(
        ReachPerformanceSnapshot snapshot,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var dropped = snapshot.DroppedFrames > _lastDroppedFrames;
        _lastDroppedFrames = snapshot.DroppedFrames;
        var pressure = dropped
            || snapshot.FrameAgeP95Milliseconds is > 180
            || snapshot.InputRoundTripP95Milliseconds is > 180;
        if (pressure)
        {
            _stableSince = now;
            if (now - _lastChange < TimeSpan.FromSeconds(4))
                return null;

            var nextKind = _current.Kind switch
            {
                ReachVideoProfileKind.Lan => ReachVideoProfileKind.Routed,
                ReachVideoProfileKind.Routed => ReachVideoProfileKind.Constrained,
                _ => ReachVideoProfileKind.Constrained,
            };
            if (nextKind == _current.Kind)
                return null;

            return ChangeTo(nextKind, now);
        }

        if (snapshot.ReceivedFrames == 0)
            return null;
        if (now - _stableSince < TimeSpan.FromSeconds(10)
            || now - _lastChange < TimeSpan.FromSeconds(6))
        {
            return null;
        }

        var upgradedKind = _current.Kind switch
        {
            ReachVideoProfileKind.Constrained => ReachVideoProfileKind.Routed,
            ReachVideoProfileKind.Routed => ReachVideoProfileKind.Lan,
            _ => ReachVideoProfileKind.Lan,
        };
        if (upgradedKind == _current.Kind)
            return null;

        return ChangeTo(upgradedKind, now);
    }

    private ReachVideoProfile ChangeTo(
        ReachVideoProfileKind kind,
        DateTimeOffset now)
    {
        _current = ReachVideoProfile.ForDisplay(_display, kind);
        _lastChange = now;
        _stableSince = now;
        return _current;
    }
}
