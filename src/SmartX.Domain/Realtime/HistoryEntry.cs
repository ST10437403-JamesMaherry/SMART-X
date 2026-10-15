using SmartX.Domain.Commands;

namespace SmartX.Domain.Realtime;

/// <summary>
/// Operational events recorded after successful telemetry processing or a
/// successful manual command/Undo transaction. A history entry is not a
/// replacement for the Part 1 ingestion-order telemetry repository.
/// </summary>
public enum HistoryEventKind
{
    TelemetryProcessed = 1,
    RelayCommand = 2,
    RelayUndo = 3
}

/// <summary>
/// Immutable, source-linked operational event. The stored telemetry dispatch
/// retains its original strongly typed packet, including the arrival and
/// dequeue sequences; command events retain the original command record.
///
/// OccurredAtUtc determines chronological placement (telemetry capture time or
/// command execution time). RecordedAtUtc is when the successful processing
/// or command event was recorded. These times may legitimately differ.
///
/// Sequence is assigned once by the operational history store and breaks
/// ties between events sharing the same UTC occurrence timestamp.
/// </summary>
public sealed record HistoryEntry
{
    private HistoryEntry(
        string eventId,
        Guid targetDeviceId,
        HistoryEventKind kind,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset recordedAtUtc,
        TelemetryDispatch? telemetryDispatch,
        CommandRecord? command)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("An event identity is required.", nameof(eventId));
        }

        if (targetDeviceId == Guid.Empty)
        {
            throw new ArgumentException("A target device ID is required.", nameof(targetDeviceId));
        }

        if (occurredAtUtc == default || recordedAtUtc == default)
        {
            throw new ArgumentException("Valid occurrence and recording times are required.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if ((kind == HistoryEventKind.TelemetryProcessed) != (telemetryDispatch is not null) ||
            (kind != HistoryEventKind.TelemetryProcessed) != (command is not null))
        {
            throw new ArgumentException("The event kind must match exactly one source payload.");
        }

        EventId = eventId;
        TargetDeviceId = targetDeviceId;
        Kind = kind;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
        RecordedAtUtc = recordedAtUtc.ToUniversalTime();
        TelemetryDispatch = telemetryDispatch;
        Command = command;
    }

    /// <summary>
    /// Stable source identity, unique within one device. Telemetry uses its
    /// global acceptance sequence and commands use their immutable GUID.
    /// </summary>
    public string EventId { get; }

    public Guid TargetDeviceId { get; }

    public HistoryEventKind Kind { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public DateTimeOffset RecordedAtUtc { get; }

    /// <summary>One-based insertion sequence assigned by the history store.</summary>
    public long Sequence { get; private init; }

    public TelemetryDispatch? TelemetryDispatch { get; }

    public CommandRecord? Command { get; }

    /// <summary>
    /// Records a successfully processed packet from the exact accepted work.
    /// ArrivalSequence is stable even when critical dispatch changes order.
    /// </summary>
    public static HistoryEntry FromProcessedTelemetry(
        TelemetryDispatch dispatch,
        DateTimeOffset processedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        TelemetryWorkItem item = dispatch.WorkItem ??
            throw new ArgumentException("A telemetry dispatch must contain work.", nameof(dispatch));

        if (dispatch.DequeueSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dispatch), "A valid dequeue sequence is required.");
        }

        return new HistoryEntry(
            $"telemetry:{item.ArrivalSequence:D20}",
            item.SensorProfileId,
            HistoryEventKind.TelemetryProcessed,
            item.CapturedAtUtc,
            processedAtUtc,
            dispatch,
            null);
    }

    /// <summary>
    /// Records an accepted manual command or successful linked reversal.
    /// Both use the original command ID as their stable event identity.
    /// </summary>
    public static HistoryEntry FromCommand(CommandRecord command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return new HistoryEntry(
            $"command:{command.Id:D}",
            command.TargetDeviceId,
            command.IsReversal ? HistoryEventKind.RelayUndo : HistoryEventKind.RelayCommand,
            command.ExecutedAtUtc,
            command.ExecutedAtUtc,
            null,
            command);
    }

    /// <summary>
    /// Creates the immutable stored version of an event. Callers cannot
    /// provide their own ordering number or resequence an existing event.
    /// </summary>
    public HistoryEntry WithSequence(long sequence)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (Sequence != 0)
        {
            throw new InvalidOperationException("A stored history event cannot be resequenced.");
        }

        return this with { Sequence = sequence };
    }

    /// <summary>
    /// Detects conflicting reuse of a retained source-event identity. The
    /// recording time is excluded because a repeated callback may run later;
    /// the immutable source and original occurrence must still be the same.
    /// </summary>
    public bool MatchesSource(HistoryEntry other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (EventId != other.EventId || TargetDeviceId != other.TargetDeviceId ||
            Kind != other.Kind || OccurredAtUtc != other.OccurredAtUtc)
        {
            return false;
        }

        if (Command is not null || other.Command is not null)
        {
            return Command == other.Command;
        }

        if (TelemetryDispatch is not { } first || other.TelemetryDispatch is not { } second)
        {
            return false;
        }

        TelemetryWorkItem a = first.WorkItem;
        TelemetryWorkItem b = second.WorkItem;

        if (first.DequeueSequence != second.DequeueSequence ||
            a.ArrivalSequence != b.ArrivalSequence ||
            a.SourceDeviceIdentifier != b.SourceDeviceIdentifier ||
            a.DataType != b.DataType || a.Severity != b.Severity ||
            a.AcceptedAtUtc != b.AcceptedAtUtc)
        {
            return false;
        }

        return (a, b) switch
        {
            (TelemetryWorkItem<double> x, TelemetryWorkItem<double> y) =>
                x.Packet.Payload.Equals(y.Packet.Payload),
            (TelemetryWorkItem<int> x, TelemetryWorkItem<int> y) =>
                x.Packet.Payload == y.Packet.Payload,
            (TelemetryWorkItem<bool> x, TelemetryWorkItem<bool> y) =>
                x.Packet.Payload == y.Packet.Payload,
            _ => false
        };
    }
}