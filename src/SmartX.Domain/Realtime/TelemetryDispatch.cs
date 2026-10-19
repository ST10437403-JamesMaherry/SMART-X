namespace SmartX.Domain.Realtime;

/// <summary>
/// A dispatch is distinct from successful processing. The worker must retain
/// this item while executing and expose it if processing fails.
/// </summary>
public sealed record TelemetryDispatch(
    TelemetryWorkItem WorkItem,
    long DequeueSequence);