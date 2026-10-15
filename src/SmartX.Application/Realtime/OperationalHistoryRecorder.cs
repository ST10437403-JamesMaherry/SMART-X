using System.Diagnostics;

using SmartX.Domain.Commands;
using SmartX.Domain.Realtime;

namespace SmartX.Application.Realtime;

/// <summary>
/// Records completed Smart-X operations in the shared chronological
/// history store.
///
/// This class does not process telemetry, execute commands, or modify
/// operational sensor state. It receives successful outcomes from the
/// existing authoritative application paths.
///
/// Duplicate source callbacks are handled by IRealtimeHistoryStore.
/// </summary>
public sealed class OperationalHistoryRecorder
{
    private readonly IRealtimeHistoryStore _historyStore;

    public OperationalHistoryRecorder(
        IRealtimeHistoryStore historyStore)
    {
        _historyStore = historyStore ??
            throw new ArgumentNullException(nameof(historyStore));
    }

    /// <summary>
    /// Records the exact telemetry dispatch after successful completion.
    ///
    /// Uses the original packet capture timestamp for chronology and
    /// the supplied processing completion time for recorded metadata.
    ///
    /// A duplicate dispatch cannot produce a second retained event
    /// with the same source identity.
    /// </summary>
    public bool RecordProcessedTelemetry(
        TelemetryDispatch dispatch,
        DateTimeOffset processedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        return TryRecord(
            () => HistoryEntry.FromProcessedTelemetry(
                dispatch,
                processedAtUtc),
            "telemetry",
            dispatch.WorkItem.SensorProfileId);
    }

    /// <summary>
    /// Records a successfully accepted manual command or genuine Undo.
    ///
    /// No-ops are recorded because they are successful operator actions,
    /// but they never create additional undoable Stack entries.
    ///
    /// Reversals preserve their original ReversesCommandId linkage.
    /// </summary>
    public bool RecordAcceptedCommand(CommandRecord command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return TryRecord(
            () => HistoryEntry.FromCommand(command),
            "command",
            command.TargetDeviceId);
    }

    /// <summary>
    /// History is a secondary, process-local projection.
    ///
    /// A rare projection failure must not report an already-committed
    /// command as unsuccessful or change a completed telemetry dispatch
    /// into failed outstanding work.
    ///
    /// Unexpected failures are reported to the server's diagnostic trace.
    /// False also represents an ordinary, idempotently suppressed duplicate.
    /// </summary>
    private bool TryRecord(
        Func<HistoryEntry> createEvent,
        string source,
        Guid targetDeviceId)
    {
        try
        {
            HistoryEntry entry = createEvent();

            return _historyStore.TryAppend(entry);
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "Smart-X operational history projection failed. " +
                "Source: {0}; Device: {1}; Error: {2}",
                source,
                targetDeviceId,
                exception);

            return false;
        }
    }
}