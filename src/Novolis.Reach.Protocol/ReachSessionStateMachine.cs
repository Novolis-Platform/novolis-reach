namespace Novolis.Reach.Protocol;

/// <summary>Validates the lifecycle transitions of one Reach session.</summary>
public sealed class ReachSessionStateMachine
{
    /// <summary>Current lifecycle state.</summary>
    public ReachSessionState State { get; private set; } = ReachSessionState.New;

    /// <summary>Attempts a legal state transition.</summary>
    public bool TryTransition(ReachSessionState next)
    {
        if (!IsLegal(State, next))
            return false;

        State = next;
        return true;
    }

    /// <summary>Transitions or throws an invalid-operation exception.</summary>
    public void Transition(ReachSessionState next)
    {
        if (!TryTransition(next))
        {
            throw new InvalidOperationException(
                $"Cannot transition Reach session from {State} to {next}.");
        }
    }

    private static bool IsLegal(ReachSessionState current, ReachSessionState next) =>
        (current, next) switch
        {
            (ReachSessionState.New, ReachSessionState.HelloExchanged) => true,
            (ReachSessionState.HelloExchanged, ReachSessionState.Opening) => true,
            (ReachSessionState.Opening, ReachSessionState.Open) => true,
            (ReachSessionState.Open, ReachSessionState.Resuming) => true,
            (ReachSessionState.Resuming, ReachSessionState.Open) => true,
            (ReachSessionState.Open, ReachSessionState.Closing) => true,
            (ReachSessionState.Resuming, ReachSessionState.Closing) => true,
            (ReachSessionState.Opening, ReachSessionState.Closing) => true,
            (ReachSessionState.Closing, ReachSessionState.Closed) => true,
            (_, ReachSessionState.Failed) when current is not ReachSessionState.Closed => true,
            _ => false,
        };
}
