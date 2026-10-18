namespace MqttProbe.Core.Models.Sparkplug;

internal sealed class SpbSequenceTracker(TimeProvider timeProvider, int timeoutMilliseconds)
{
    private readonly List<byte> _missing = [];
    private int _timeoutMilliseconds = Math.Clamp(timeoutMilliseconds, 0, 60000);
    private byte? _last;
    private DateTime? _startedAt;
    private DateTime? _deadline;
    private bool _expired;

    public SpbSequenceDiagnostics Snapshot { get; private set; } = new();

    public void UpdateTimeout(int timeoutMilliseconds)
    {
        _timeoutMilliseconds = Math.Clamp(timeoutMilliseconds, 0, 60000);
        if (_startedAt is { } startedAt && _missing.Count > 0)
            _deadline = startedAt.AddMilliseconds(_timeoutMilliseconds);
    }

    public void Reset(ulong? baseline)
    {
        _missing.Clear();
        _startedAt = null;
        _deadline = null;
        _expired = false;
        if (baseline is { } value && value <= byte.MaxValue)
        {
            _last = (byte)value;
            Snapshot = new SpbSequenceDiagnostics
            {
                State = SpbSequenceState.InOrder,
                ExpectedSequence = unchecked((byte)(_last.Value + 1)),
                ReceivedSequence = baseline
            };
            return;
        }

        _last = null;
        Snapshot = new SpbSequenceDiagnostics
        {
            State = baseline is null ? SpbSequenceState.Unknown : SpbSequenceState.Invalid,
            ReceivedSequence = baseline
        };
    }

    public bool Observe(ulong? received)
    {
        if (received is not { } value || value > byte.MaxValue)
        {
            Snapshot = new SpbSequenceDiagnostics { State = SpbSequenceState.Invalid, ReceivedSequence = received };
            return false;
        }

        var sequence = (byte)value;
        var missingIndex = _missing.IndexOf(sequence);
        if (missingIndex >= 0)
            return ResolveMissingSequence(sequence, value, missingIndex);

        if (_last is not { } last)
        {
            _last = sequence;
            Snapshot = new SpbSequenceDiagnostics
            {
                State = SpbSequenceState.InOrder,
                ExpectedSequence = unchecked((byte)(sequence + 1)),
                ReceivedSequence = value
            };
            return true;
        }

        var delta = unchecked((byte)(sequence - last));
        if (delta == 0)
            return false;
        if (delta == 128)
        {
            Snapshot = new SpbSequenceDiagnostics
            {
                State = SpbSequenceState.Invalid,
                ExpectedSequence = unchecked((byte)(last + 1)),
                ReceivedSequence = value
            };
            return false;
        }

        if (delta < 128)
        {
            ApplyForwardSequence(last, sequence, delta, value);
            return true;
        }

        return false;
    }

    private bool ResolveMissingSequence(byte sequence, ulong received, int missingIndex)
    {
        _missing.RemoveAt(missingIndex);
        if (_last is { } previous)
        {
            var forwardDelta = unchecked((byte)(sequence - previous));
            if (forwardDelta is > 0 and < 128)
            {
                ApplyForwardSequence(previous, sequence, forwardDelta, received);
                return true;
            }
        }
        if (_missing.Count == 0)
        {
            _startedAt = null;
            _deadline = null;
        }
        var nextState = SpbSequenceState.InOrder;
        if (_expired)
            nextState = SpbSequenceState.GapDetected;
        else if (_missing.Count > 0)
            nextState = SpbSequenceState.Reordering;
        UpdateSnapshot(nextState, received);
        return true;
    }

    private void ApplyForwardSequence(byte last, byte sequence, byte delta, ulong received)
    {
        for (var step = 1; step < delta; step++)
        {
            var missingSequence = unchecked((byte)(last + step));
            if (!_missing.Contains(missingSequence))
                _missing.Add(missingSequence);
        }
        _last = sequence;
        if (_missing.Count > 0)
        {
            _startedAt ??= timeProvider.GetUtcNow().UtcDateTime;
            _deadline ??= _startedAt.Value.AddMilliseconds(_timeoutMilliseconds);
        }
        else
        {
            _startedAt = null;
            _deadline = null;
        }
        var nextState = SpbSequenceState.InOrder;
        if (_expired)
            nextState = SpbSequenceState.GapDetected;
        else if (_missing.Count > 0)
            nextState = SpbSequenceState.Reordering;
        UpdateSnapshot(nextState, received);
    }

    public bool CheckTimeout()
    {
        if (Snapshot.State != SpbSequenceState.Reordering || _deadline is not { } deadline
            || timeProvider.GetUtcNow().UtcDateTime < deadline)
            return false;

        _expired = true;
        UpdateSnapshot(SpbSequenceState.GapDetected, Snapshot.ReceivedSequence ?? 0);
        return true;
    }

    private void UpdateSnapshot(SpbSequenceState state, ulong received)
    {
        byte? expected = _missing.Count > 0 ? _missing[0] : null;
        if (expected is null && _last is { } last)
            expected = unchecked((byte)(last + 1));

        Snapshot = new SpbSequenceDiagnostics
        {
            State = state,
            ExpectedSequence = expected,
            ReceivedSequence = received,
            MissingSequences = _missing.ToArray(),
            ReorderStartedAtUtc = _startedAt,
            ReorderDeadlineUtc = _deadline
        };
    }
}
