namespace Novolis.Reach.Transport;

/// <summary>Reassembles bounded, authenticated Reach media fragments.</summary>
public sealed class ReachDatagramReassembler
{
    private readonly TimeSpan _maximumAge;
    private readonly int _maximumFrames;
    private readonly Dictionary<long, PendingFrame> _frames = [];
    private readonly HashSet<long> _retiredSequences = [];
    private long _lastCompletedSequence = -1;
    private long _highestSeenSequence = -1;

    /// <summary>Creates a reassembler with bounded in-flight state.</summary>
    public ReachDatagramReassembler(
        int maximumFrames = 8,
        TimeSpan? maximumAge = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumFrames, 0);
        _maximumFrames = maximumFrames;
        _maximumAge = maximumAge ?? TimeSpan.FromMilliseconds(500);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            _maximumAge,
            TimeSpan.Zero);
    }

    /// <summary>Gets the number of incomplete sequences currently retained.</summary>
    public int PendingCount => _frames.Count;

    /// <summary>
    /// Adds one fragment and returns a complete payload only when all fragments
    /// have arrived before their deadline.
    /// </summary>
    public bool TryAccept(
        ReachDatagramFragment fragment,
        DateTimeOffset now,
        out byte[] payload)
    {
        payload = [];
        RemoveExpired(now);
        if (fragment.Sequence < 0
            || fragment.Sequence <= _lastCompletedSequence
            || fragment.FragmentCount == 0
            || fragment.FragmentIndex >= fragment.FragmentCount
            || fragment.Payload.Length == 0)
        {
            return false;
        }

        if (!_frames.TryGetValue(fragment.Sequence, out var frame))
        {
            if (_retiredSequences.Contains(fragment.Sequence)
                || (_highestSeenSequence >= 0
                    && fragment.Sequence < _highestSeenSequence - 64))
            {
                return false;
            }

            if (_frames.Count >= _maximumFrames)
            {
                var oldest = _frames
                    .OrderBy(static item => item.Value.CreatedAt)
                    .First()
                    .Key;
                _frames.Remove(oldest);
                _retiredSequences.Add(oldest);
            }

            frame = new PendingFrame(
                fragment.FragmentCount,
                now);
            _frames.Add(fragment.Sequence, frame);
            _highestSeenSequence = Math.Max(
                _highestSeenSequence,
                fragment.Sequence);
            PruneRetiredSequences();
        }

        if (frame.FragmentCount != fragment.FragmentCount
            || !frame.Fragments.TryAdd(
                fragment.FragmentIndex,
                fragment.Payload))
        {
            return false;
        }

        if (frame.Fragments.Count != frame.FragmentCount)
            return false;

        var totalLength = frame.Fragments.Values.Sum(
            static item => item.Length);
        payload = new byte[totalLength];
        var offset = 0;
        for (var index = 0; index < frame.FragmentCount; index++)
        {
            var part = frame.Fragments[(ushort)index];
            part.CopyTo(payload, offset);
            offset += part.Length;
        }

        _frames.Remove(fragment.Sequence);
        _retiredSequences.Add(fragment.Sequence);
        _lastCompletedSequence = Math.Max(
            _lastCompletedSequence,
            fragment.Sequence);
        PruneRetiredSequences();
        return true;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var sequence in _frames
                     .Where(item => now - item.Value.CreatedAt > _maximumAge)
                     .Select(static item => item.Key)
                     .ToArray())
        {
            _frames.Remove(sequence);
            _retiredSequences.Add(sequence);
        }
    }

    private void PruneRetiredSequences()
    {
        if (_highestSeenSequence < 0)
            return;

        foreach (var sequence in _retiredSequences
                     .Where(sequence => sequence < _highestSeenSequence - 64)
                     .ToArray())
        {
            _retiredSequences.Remove(sequence);
        }
    }

    private sealed class PendingFrame
    {
        public PendingFrame(ushort fragmentCount, DateTimeOffset createdAt)
        {
            FragmentCount = fragmentCount;
            CreatedAt = createdAt;
        }

        public ushort FragmentCount { get; }
        public DateTimeOffset CreatedAt { get; }
        public Dictionary<ushort, byte[]> Fragments { get; } = [];
    }
}
