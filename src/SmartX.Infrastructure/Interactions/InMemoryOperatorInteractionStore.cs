using SmartX.Application.Interactions;
using SmartX.Domain.Interactions;

namespace SmartX.Infrastructure.Interactions;

/// <summary>
/// Bounded FIFO ring of interactions. The lock makes sequence assignment,
/// eviction and reads atomic. Only explicitly recorded operations enter it.
/// </summary>
public sealed class InMemoryOperatorInteractionStore : IOperatorInteractionStore
{
    public const int DefaultCapacity = 2_000;
    private readonly object _gate = new();
    private readonly Queue<OperatorInteraction> _events = new();
    private readonly TimeProvider _clock;
    private readonly int _capacity;
    private long _sequence;

    public InMemoryOperatorInteractionStore(TimeProvider clock, int capacity = DefaultCapacity)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public OperatorInteraction Append(OperatorInteractionKind kind, Guid? deviceId = null,
        Guid? alertEpisodeId = null, Guid? commandId = null, string? searchText = null,
        string? canonicalIdentifier = null, string? category = null, string? status = null,
        bool? hasActiveCondition = null, string? conditionType = null, int? resultCount = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        lock (_gate)
        {
            var item = new OperatorInteraction(++_sequence, _clock.GetUtcNow().ToUniversalTime(),
                kind, deviceId, alertEpisodeId, commandId, searchText, canonicalIdentifier,
                category, status, hasActiveCondition, conditionType, resultCount);
            if (_events.Count == _capacity) _events.Dequeue();
            _events.Enqueue(item);
            return item;
        }
    }

    public OperatorInteractionSnapshot Read(int limit = 50)
    {
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate)
        {
            // Return the most recent events, with newest first for operator inspection.
            return new OperatorInteractionSnapshot(_sequence, _events.Count, _capacity,
                Array.AsReadOnly(_events.Reverse().Take(limit).ToArray()));
        }
    }
}