using SmartX.Application.Commands;
using SmartX.Application.Sensors;
using SmartX.Contracts.Commands;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Telemetry;
using SmartX.Domain.Realtime;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Realtime;

/// <summary>
/// Provides validated chronological operational-history reads.
///
/// Registered-device identity remains authoritative in ISensorRepository.
/// The history store contains operational events only and never becomes
/// a competing device registry.
///
/// Results are bounded, detached, and ordered by the actual
/// SortedDictionary-backed timeline.
/// </summary>
public sealed class RealtimeHistoryService
{
    private readonly ISensorRepository _sensorRepository;
    private readonly IRealtimeHistoryStore _historyStore;

    public RealtimeHistoryService(
        ISensorRepository sensorRepository,
        IRealtimeHistoryStore historyStore)
    {
        _sensorRepository = sensorRepository ??
            throw new ArgumentNullException(nameof(sensorRepository));

        _historyStore = historyStore ??
            throw new ArgumentNullException(nameof(historyStore));
    }

    /// <summary>
    /// Returns the newest bounded chronological events for a registered
    /// device. History remains readable even when a sensor is Offline,
    /// in Maintenance, Faulted, or Decommissioned.
    ///
    /// Supported order values:
    /// - asc or ascending
    /// - desc or descending
    ///
    /// Query limit: 1 through 500, default 100.
    /// </summary>
    public async Task<RealtimeHistoryResponse> GetHistoryAsync(
        Guid targetDeviceId,
        int? limit = null,
        string? order = null,
        CancellationToken cancellationToken = default)
    {
        if (targetDeviceId == Guid.Empty)
        {
            throw new InvalidRealtimeHistoryQueryException(
                "A valid registered device ID is required.");
        }

        int requestedLimit =
            limit ?? IRealtimeHistoryStore.DefaultQueryLimit;

        if (requestedLimit < 1 ||
            requestedLimit > IRealtimeHistoryStore.MaximumQueryLimit)
        {
            throw new InvalidRealtimeHistoryQueryException(
                $"History limit must be between 1 and " +
                $"{IRealtimeHistoryStore.MaximumQueryLimit}.");
        }

        HistorySortOrder requestedOrder = ParseOrder(order);

        cancellationToken.ThrowIfCancellationRequested();

        // Registration authority is checked before consulting the
        // independent history store.
        SensorProfile? sensor =
            await _sensorRepository.GetByIdAsync(
                targetDeviceId,
                cancellationToken);

        if (sensor is null)
        {
            throw new SensorNotFoundException(targetDeviceId);
        }

        cancellationToken.ThrowIfCancellationRequested();

        RealtimeHistorySnapshot snapshot =
            _historyStore.GetSnapshot(
                sensor.Id,
                requestedLimit,
                requestedOrder);

        RealtimeHistoryEventResponse[] events =
            snapshot.Events
                .Select(ToEventResponse)
                .ToArray();

        return new RealtimeHistoryResponse(
            snapshot.TargetDeviceId,
            snapshot.TotalRecordedEvents,
            snapshot.RetainedEventCount,
            snapshot.RetentionCapacity,
            snapshot.Limit,
            snapshot.Order.ToString(),
            Array.AsReadOnly(events));
    }

    /// <summary>
    /// Parses only the documented names.
    ///
    /// Numeric enum values and unknown strings are rejected rather than
    /// being silently converted into unexpected ordering behaviour.
    /// </summary>
    private static HistorySortOrder ParseOrder(string? order)
    {
        if (order is null)
        {
            return HistorySortOrder.Ascending;
        }

        string value = order.Trim();

        if (string.Equals(
                value,
                "asc",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                value,
                "ascending",
                StringComparison.OrdinalIgnoreCase))
        {
            return HistorySortOrder.Ascending;
        }

        if (string.Equals(
                value,
                "desc",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                value,
                "descending",
                StringComparison.OrdinalIgnoreCase))
        {
            return HistorySortOrder.Descending;
        }

        throw new InvalidRealtimeHistoryQueryException(
            "History order must be asc, ascending, desc, or descending.");
    }

    /// <summary>
    /// Converts one immutable Domain event into an API-safe response.
    ///
    /// Typed telemetry values are mapped through the existing P2-03
    /// mapper, preserving native double, int, and bool payloads.
    ///
    /// Command metadata is mapped through the established P2-04 mapper,
    /// retaining original IDs and genuine Undo reversal linkage.
    /// </summary>
    private static RealtimeHistoryEventResponse ToEventResponse(
        HistoryEntry entry)
    {
        TelemetryReadingResponse? telemetry = null;
        CommandAuditResponse? command = null;

        long? enqueueSequence = null;
        long? dequeueSequence = null;
        string? processingLane = null;
        string? severity = null;
        DateTimeOffset? acceptedAtUtc = null;

        if (entry.TelemetryDispatch is TelemetryDispatch dispatch)
        {
            TelemetryWorkItem item = dispatch.WorkItem;

            telemetry = TelemetryProcessingMapper.ToReading(item);

            enqueueSequence = item.ArrivalSequence;
            dequeueSequence = dispatch.DequeueSequence;

            processingLane = item.Lane.ToString();
            severity = item.Severity.ToString();

            acceptedAtUtc = item.AcceptedAtUtc;
        }

        if (entry.Command is not null)
        {
            command =
                ActuatorCommandContractMapper.ToAuditResponse(
                    entry.Command);
        }

        return new RealtimeHistoryEventResponse(
            entry.EventId,
            entry.TargetDeviceId,
            entry.Kind.ToString(),
            entry.OccurredAtUtc,
            entry.RecordedAtUtc,
            entry.Sequence,
            telemetry,
            command,
            enqueueSequence,
            dequeueSequence,
            processingLane,
            severity,
            acceptedAtUtc);
    }
}