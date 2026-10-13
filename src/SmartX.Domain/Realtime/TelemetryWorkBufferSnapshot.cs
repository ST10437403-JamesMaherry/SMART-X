namespace SmartX.Domain.Realtime;

/// <summary>
/// One consistent, detached view of pending work and admission/dispatch totals.
/// DispatchedCount does not claim that processing has succeeded.
/// </summary>
public sealed record TelemetryWorkBufferSnapshot(
    long AcceptedCount,
    long DispatchedCount,
    bool OrdinaryDispatchPaused,
    IReadOnlyList<TelemetryWorkItem> OrdinaryPending,
    IReadOnlyList<TelemetryWorkItem> CriticalPending)
{
    public int OrdinaryPendingCount => OrdinaryPending.Count;
    public int CriticalPendingCount => CriticalPending.Count;
    public int PendingCount => OrdinaryPendingCount + CriticalPendingCount;
}