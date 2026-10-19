using System.Collections.Concurrent;
using System.Numerics;

using SmartX.Application.Attachments;
using SmartX.Application.Sensors;
using SmartX.Application.Telemetry;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

namespace SmartX.Application.Dashboard;

/// <summary>
/// Evaluates Smart-X operational evidence and converts meaningful conditions
/// into prioritised DashboardIssue instances.
///
/// The service coordinates four Part 1 evidence sources:
///
/// - strongly typed telemetry;
/// - sensor operational status;
/// - repeated abnormal behaviour;
/// - sensor configuration attachments.
///
/// Analysis remains in the Application layer because it coordinates multiple
/// domain aggregates and persistence abstractions. DashboardIssue and its
/// custom collection remain Domain concepts.
///
/// The service is designed to be registered as a singleton in the API layer.
/// Its mutable recovery counters therefore use thread-safe collections.
/// </summary>
public sealed class AdaptiveAnomalyAnalysisService
{
    private const int RecentTelemetryComparisonCount = 2;

    private readonly ISensorRepository _sensorRepository;
    private readonly ITelemetryRepository _telemetryRepository;
    private readonly ISensorAttachmentStore _attachmentStore;
    private readonly DashboardIssueCollection _issues;
    private readonly TimeProvider _timeProvider;
    private readonly AdaptiveAnomalyPolicy _policy;
    private readonly TelemetryAnomalyClassifier _classifier;

    /*
     * Tracks consecutive non-anomalous telemetry observations for sensors
     * with active telemetry/repeated-failure issues.
     *
     * Recovery requires more than one healthy packet so transient values do
     * not immediately clear an important issue.
     */
    private readonly ConcurrentDictionary<Guid, int>
        _stableTelemetryObservations = new();

    /*
     * Counts explicit Fault-state observations across the lifetime of the
     * running Part 1 process. This gives repeated operational failures meaning
     * even when the sensor temporarily recovers between faults.
     */
    private readonly ConcurrentDictionary<Guid, int>
        _faultObservations = new();

    public AdaptiveAnomalyAnalysisService(
        ISensorRepository sensorRepository,
        ITelemetryRepository telemetryRepository,
        ISensorAttachmentStore attachmentStore,
        DashboardIssueCollection issues,
        TimeProvider timeProvider,
        AdaptiveAnomalyPolicy policy)
    {
        _sensorRepository =
            sensorRepository ??
            throw new ArgumentNullException(
                nameof(sensorRepository));

        _telemetryRepository =
            telemetryRepository ??
            throw new ArgumentNullException(
                nameof(telemetryRepository));

        _attachmentStore =
            attachmentStore ??
            throw new ArgumentNullException(
                nameof(attachmentStore));

        _issues =
            issues ??
            throw new ArgumentNullException(
                nameof(issues));

        _timeProvider =
            timeProvider ??
            throw new ArgumentNullException(
                nameof(timeProvider));

        _policy =
            policy ??
            throw new ArgumentNullException(
                nameof(policy));

        _classifier = new TelemetryAnomalyClassifier(_policy);
    }

    /// <summary>
    /// Analyses the most recently stored telemetry for one registered sensor.
    ///
    /// Numeric sensors use the existing NumericTelemetryPacket&lt;T&gt;
    /// subtraction operator to calculate a strongly typed delta. Boolean
    /// telemetry is not treated as a numeric anomaly; however, valid Boolean
    /// packets can still contribute to confirmed recovery from a repeated
    /// operational failure.
    /// </summary>
    public async Task AnalyzeLatestTelemetryAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        SensorProfile sensor =
            await GetRequiredSensorAsync(
                sensorProfileId,
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        /*
         * Decommissioned hardware is no longer expected to produce active
         * telemetry. Do not create new anomaly issues for a retired device.
         */
        if (sensor.Status == SensorStatus.Decommissioned)
        {
            ResolveTelemetryIssues(
                sensor.Id,
                _timeProvider.GetUtcNow());

            return;
        }

        switch (sensor.DataType)
        {
            case TelemetryDataType.Float:
                await AnalyzeNumericTelemetryAsync<double>(
                    sensor,
                    cancellationToken);
                break;

            case TelemetryDataType.Integer:
                await AnalyzeNumericTelemetryAsync<int>(
                    sensor,
                    cancellationToken);
                break;

            case TelemetryDataType.Boolean:
                await AnalyzeBooleanTelemetryAsync(
                    sensor,
                    cancellationToken);
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Smart-X telemetry type " +
                    $"'{sensor.DataType}'.");
        }
    }

    /// <summary>
    /// Evaluates a completed sensor-status transition.
    ///
    /// The caller supplies the state that existed before the transition while
    /// the current state is read from the authoritative sensor repository.
    /// This allows the service to distinguish a real disconnect/recovery from
    /// an unchanged Offline profile.
    /// </summary>
    public async Task AnalyzeSensorStateAsync(
        Guid sensorProfileId,
        SensorStatus previousStatus,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(
                typeof(SensorStatus),
                previousStatus))
        {
            throw new ArgumentOutOfRangeException(
                nameof(previousStatus),
                previousStatus,
                "Unsupported previous sensor status.");
        }

        SensorProfile sensor =
            await GetRequiredSensorAsync(
                sensorProfileId,
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        SensorStatus currentStatus =
            sensor.Status;

        /*
         * Re-evaluating an unchanged state must not inflate occurrence counts.
         */
        if (currentStatus == previousStatus)
        {
            return;
        }

        DateTimeOffset now =
            _timeProvider
                .GetUtcNow()
                .ToUniversalTime();

        if (currentStatus == SensorStatus.Decommissioned)
        {
            ResolveNonActionableIssuesForDecommissionedSensor(
                sensor.Id,
                now);

            _stableTelemetryObservations.TryRemove(
                sensor.Id,
                out _);

            _faultObservations.TryRemove(
                sensor.Id,
                out _);

            _issues.AddOrUpdate(
                sensor.Id,
                sensor.Name,
                DashboardIssueType.SensorStateChange,
                DashboardIssueSeverity.Information,
                DashboardIssueTarget.Sensor,
                "Sensor decommissioned",
                "The device has been moved into its terminal " +
                "decommissioned state.",
                sensor.UpdatedAtUtc);

            return;
        }

        if (currentStatus == SensorStatus.Offline)
        {
            DateTimeOffset? lastSeen =
                await GetLastTelemetryTimestampAsync(
                    sensor,
                    cancellationToken);

            string detail =
                lastSeen is null
                    ? "The sensor transitioned offline and no telemetry " +
                      "has previously been recorded."
                    : $"The sensor transitioned offline. Its last " +
                      $"telemetry packet was captured at " +
                      $"{lastSeen.Value:O}.";

            _issues.Resolve(
                sensor.Id,
                DashboardIssueType.SensorStateChange,
                now);

            _issues.AddOrUpdate(
                sensor.Id,
                sensor.Name,
                DashboardIssueType.SensorDisconnected,
                DashboardIssueSeverity.Warning,
                DashboardIssueTarget.Sensor,
                "Sensor disconnected",
                detail,
                sensor.UpdatedAtUtc);

            return;
        }

        /*
         * Any transition away from Offline confirms that the explicit
         * disconnect condition has ended.
         */
        if (previousStatus == SensorStatus.Offline)
        {
            _issues.Resolve(
                sensor.Id,
                DashboardIssueType.SensorDisconnected,
                now);
        }

        if (currentStatus == SensorStatus.Online)
        {
            _issues.Resolve(
                sensor.Id,
                DashboardIssueType.SensorStateChange,
                now);

            return;
        }

        DashboardIssueSeverity severity =
            GetStatusSeverity(
                currentStatus);

        string title =
            GetStatusIssueTitle(
                currentStatus);

        DashboardIssue stateIssue =
            _issues.AddOrUpdate(
                sensor.Id,
                sensor.Name,
                DashboardIssueType.SensorStateChange,
                severity,
                DashboardIssueTarget.Sensor,
                title,
                $"Sensor state changed from '{previousStatus}' to " +
                $"'{currentStatus}'.",
                sensor.UpdatedAtUtc);

        /*
         * Multiple unresolved degraded state changes are themselves useful
         * evidence of instability.
         */
        if (stateIssue.OccurrenceCount >=
            _policy.RepeatedFailureThreshold)
        {
            RecordRepeatedFailure(
                sensor,
                stateIssue.OccurrenceCount,
                severity,
                now,
                "Repeated degraded sensor states");
        }

        if (currentStatus == SensorStatus.Fault)
        {
            int faultCount =
                _faultObservations.AddOrUpdate(
                    sensor.Id,
                    1,
                    static (_, currentCount) =>
                        currentCount == int.MaxValue
                            ? currentCount
                            : currentCount + 1);

            if (faultCount >=
                _policy.RepeatedFailureThreshold)
            {
                RecordRepeatedFailure(
                    sensor,
                    faultCount,
                    DashboardIssueSeverity.Critical,
                    now,
                    "Repeated sensor faults");
            }
        }
    }

    /// <summary>
    /// Checks whether a registered sensor currently has configuration evidence
    /// attached.
    ///
    /// Missing configuration creates one active issue. Re-evaluating the same
    /// missing state does not increment the occurrence count simply because a
    /// page refreshed or another attachment was inspected.
    ///
    /// Once a Configuration attachment is present, the condition resolves.
    /// </summary>
    public async Task AnalyzeConfigurationAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken = default)
    {
        SensorProfile sensor =
            await GetRequiredSensorAsync(
                sensorProfileId,
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now =
            _timeProvider
                .GetUtcNow()
                .ToUniversalTime();

        if (sensor.Status == SensorStatus.Decommissioned)
        {
            _issues.Resolve(
                sensor.Id,
                DashboardIssueType.ConfigurationIssue,
                now);

            return;
        }

        IReadOnlyList<SensorAttachmentMetadata> attachments =
            await _attachmentStore.ListAsync(
                sensor.Id,
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        bool hasConfiguration =
            attachments.Any(
                static attachment =>
                    attachment.Category ==
                    SensorAttachmentCategory.Configuration);

        if (hasConfiguration)
        {
            _issues.Resolve(
                sensor.Id,
                DashboardIssueType.ConfigurationIssue,
                now);

            return;
        }

        /*
         * Missing configuration is a state, not an event. Only create the
         * issue if the same unresolved condition is not already present.
         */
        if (HasUnresolvedIssue(
                sensor.Id,
                DashboardIssueType.ConfigurationIssue,
                now))
        {
            return;
        }

        _issues.AddOrUpdate(
            sensor.Id,
            sensor.Name,
            DashboardIssueType.ConfigurationIssue,
            DashboardIssueSeverity.Warning,
            DashboardIssueTarget.Attachments,
            "Configuration evidence missing",
            "No Configuration attachment is available for this sensor.",
            now);
    }

    /// <summary>
    /// Analyses the two most recently ingested numeric telemetry packets.
    ///
    /// The overloaded subtraction operator performs the actual typed
    /// arithmetic before the resulting delta is interpreted by the anomaly
    /// policy.
    /// </summary>
    private async Task AnalyzeNumericTelemetryAsync<T>(
        SensorProfile sensor,
        CancellationToken cancellationToken)
        where T : struct, INumber<T>
    {
        IReadOnlyList<TelemetryPacket<T>> packets =
            await _telemetryRepository.GetRecentAsync<T>(
                sensor.Id,
                RecentTelemetryComparisonCount,
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (packets.Count < RecentTelemetryComparisonCount)
        {
            return;
        }

        ApplyTelemetryObservation(sensor, _classifier.Classify(packets[^2], packets[^1]));
    }

    private async Task AnalyzeBooleanTelemetryAsync(
        SensorProfile sensor,
        CancellationToken cancellationToken)
    {
        var packets = await _telemetryRepository.GetRecentAsync<bool>(sensor.Id, 1, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (packets.Count > 0)
        {
            ApplyTelemetryObservation(sensor, _classifier.Classify<bool>(null, packets[^1]));
        }
    }

    /// <summary>
    /// Applies an already captured classification exactly once. The ingestion
    /// path uses this method so another request cannot substitute a newer pair.
    /// </summary>
    public async Task AnalyzeAcceptedTelemetryAsync(
        Guid sensorProfileId,
        TelemetryAnomalyObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var sensor = await GetRequiredSensorAsync(sensorProfileId, cancellationToken);
        if (sensor.Status == SensorStatus.Decommissioned)
        {
            ResolveTelemetryIssues(sensor.Id, _timeProvider.GetUtcNow());
            return;
        }

        ApplyTelemetryObservation(sensor, observation);
    }

    private void ApplyTelemetryObservation(SensorProfile sensor, TelemetryAnomalyObservation observation)
    {
        if (!observation.HasEvidence)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        if (!observation.IsAnomaly)
        {
            RecordStableTelemetry(sensor.Id, now);
            return;
        }

        _stableTelemetryObservations.TryRemove(sensor.Id, out _);
        var severity = observation.Severity;
        var spikeIssue = _issues.AddOrUpdate(
            sensor.Id,
            sensor.Name,
            DashboardIssueType.TelemetrySpike,
            severity,
            DashboardIssueTarget.Telemetry,
            severity == DashboardIssueSeverity.Critical
                ? "Critical telemetry spike"
                : "Telemetry spike detected",
            observation.Detail!,
            now);

        if (spikeIssue.OccurrenceCount >= _policy.RepeatedFailureThreshold)
        {
            var repeatedSeverity = severity == DashboardIssueSeverity.Critical ||
                spikeIssue.OccurrenceCount >= _policy.RepeatedFailureThreshold + 2
                    ? DashboardIssueSeverity.Critical
                    : DashboardIssueSeverity.Warning;
            RecordRepeatedFailure(sensor, spikeIssue.OccurrenceCount, repeatedSeverity,
                now, "Repeated telemetry instability");
        }
    }

    /// <summary>
    /// Tracks recovery evidence and resolves telemetry-related issues only
    /// after the configured number of consecutive healthy observations.
    /// </summary>
    private void RecordStableTelemetry(
        Guid sensorProfileId,
        DateTimeOffset observedAtUtc)
    {
        DateTimeOffset now =
            observedAtUtc.ToUniversalTime();

        bool requiresRecovery =
            HasUnresolvedIssue(
                sensorProfileId,
                DashboardIssueType.TelemetrySpike,
                now) ||
            HasUnresolvedIssue(
                sensorProfileId,
                DashboardIssueType.RepeatedFailure,
                now);

        if (!requiresRecovery)
        {
            _stableTelemetryObservations.TryRemove(
                sensorProfileId,
                out _);

            return;
        }

        int stableCount =
            _stableTelemetryObservations.AddOrUpdate(
                sensorProfileId,
                1,
                static (_, currentCount) =>
                    currentCount == int.MaxValue
                        ? currentCount
                        : currentCount + 1);

        if (stableCount <
            _policy.StableObservationsForRecovery)
        {
            return;
        }

        ResolveTelemetryIssues(
            sensorProfileId,
            now);

        _stableTelemetryObservations.TryRemove(
            sensorProfileId,
            out _);
    }

    /// <summary>
    /// Creates or updates the shared repeated-failure condition for a sensor.
    /// </summary>
    private void RecordRepeatedFailure(
        SensorProfile sensor,
        int occurrenceCount,
        DashboardIssueSeverity severity,
        DateTimeOffset observedAtUtc,
        string title)
    {
        _issues.AddOrUpdate(
            sensor.Id,
            sensor.Name,
            DashboardIssueType.RepeatedFailure,
            severity,
            DashboardIssueTarget.Telemetry,
            title,
            $"The sensor has produced {occurrenceCount} repeated " +
            "abnormal observations without confirmed stable recovery.",
            observedAtUtc);
    }

    /// <summary>
    /// Determines whether one issue type currently remains active or
    /// acknowledged for the supplied sensor.
    /// </summary>
    private bool HasUnresolvedIssue(
        Guid sensorProfileId,
        DashboardIssueType issueType,
        DateTimeOffset evaluatedAtUtc)
    {
        return _issues
            .GetActiveSnapshot(
                evaluatedAtUtc.ToUniversalTime())
            .Any(
                issue =>
                    issue.SensorProfileId ==
                    sensorProfileId &&
                    issue.Type ==
                    issueType);
    }

    /// <summary>
    /// Resolves telemetry-instability issues after healthy telemetry has been
    /// confirmed.
    /// </summary>
    private void ResolveTelemetryIssues(
        Guid sensorProfileId,
        DateTimeOffset resolvedAtUtc)
    {
        DateTimeOffset normalised =
            resolvedAtUtc.ToUniversalTime();

        _issues.Resolve(
            sensorProfileId,
            DashboardIssueType.TelemetrySpike,
            normalised);

        _issues.Resolve(
            sensorProfileId,
            DashboardIssueType.RepeatedFailure,
            normalised);
    }

    /// <summary>
    /// Clears issues that no longer require operator action once hardware is
    /// permanently decommissioned.
    /// </summary>
    private void ResolveNonActionableIssuesForDecommissionedSensor(
        Guid sensorProfileId,
        DateTimeOffset resolvedAtUtc)
    {
        _issues.Resolve(
            sensorProfileId,
            DashboardIssueType.SensorDisconnected,
            resolvedAtUtc);

        _issues.Resolve(
            sensorProfileId,
            DashboardIssueType.TelemetrySpike,
            resolvedAtUtc);

        _issues.Resolve(
            sensorProfileId,
            DashboardIssueType.RepeatedFailure,
            resolvedAtUtc);

        _issues.Resolve(
            sensorProfileId,
            DashboardIssueType.ConfigurationIssue,
            resolvedAtUtc);

        _issues.Resolve(
            sensorProfileId,
            DashboardIssueType.SensorStateChange,
            resolvedAtUtc);
    }

    /// <summary>
    /// Maps operational sensor states to dashboard severity.
    /// </summary>
    private static DashboardIssueSeverity GetStatusSeverity(
        SensorStatus status)
    {
        return status switch
        {
            SensorStatus.Warning =>
                DashboardIssueSeverity.Warning,

            SensorStatus.Fault =>
                DashboardIssueSeverity.Critical,

            SensorStatus.Maintenance =>
                DashboardIssueSeverity.Information,

            SensorStatus.Decommissioned =>
                DashboardIssueSeverity.Information,

            _ =>
                throw new InvalidOperationException(
                    $"Sensor status '{status}' does not represent a " +
                    "dashboard degradation state.")
        };
    }

    /// <summary>
    /// Returns a concise operator-facing heading for a degraded sensor state.
    /// </summary>
    private static string GetStatusIssueTitle(
        SensorStatus status)
    {
        return status switch
        {
            SensorStatus.Warning =>
                "Sensor warning state",

            SensorStatus.Fault =>
                "Sensor fault detected",

            SensorStatus.Maintenance =>
                "Sensor in maintenance",

            SensorStatus.Decommissioned =>
                "Sensor decommissioned",

            _ =>
                throw new InvalidOperationException(
                    $"Sensor status '{status}' does not represent a " +
                    "dashboard degradation state.")
        };
    }

    /// <summary>
    /// Retrieves the timestamp of the newest telemetry packet without
    /// weakening its strongly typed payload.
    /// </summary>
    private async Task<DateTimeOffset?> GetLastTelemetryTimestampAsync(
        SensorProfile sensor,
        CancellationToken cancellationToken)
    {
        return sensor.DataType switch
        {
            TelemetryDataType.Float =>
                await GetLastTelemetryTimestampAsync<double>(
                    sensor.Id,
                    cancellationToken),

            TelemetryDataType.Integer =>
                await GetLastTelemetryTimestampAsync<int>(
                    sensor.Id,
                    cancellationToken),

            TelemetryDataType.Boolean =>
                await GetLastTelemetryTimestampAsync<bool>(
                    sensor.Id,
                    cancellationToken),

            _ =>
                throw new InvalidOperationException(
                    $"Unsupported Smart-X telemetry type " +
                    $"'{sensor.DataType}'.")
        };
    }

    /// <summary>
    /// Strongly typed implementation of latest-packet timestamp retrieval.
    /// </summary>
    private async Task<DateTimeOffset?> GetLastTelemetryTimestampAsync<T>(
        Guid sensorProfileId,
        CancellationToken cancellationToken)
        where T : struct
    {
        IReadOnlyList<TelemetryPacket<T>> packets =
            await _telemetryRepository.GetRecentAsync<T>(
                sensorProfileId,
                1,
                cancellationToken);

        return packets.Count == 0
            ? null
            : packets[^1].CapturedAtUtc;
    }

    /// <summary>
    /// Resolves a sensor from the repository or raises the same application
    /// exception used by the rest of the Smart-X backend.
    /// </summary>
    private async Task<SensorProfile> GetRequiredSensorAsync(
        Guid sensorProfileId,
        CancellationToken cancellationToken)
    {
        if (sensorProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A valid sensor profile ID is required.",
                nameof(sensorProfileId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        SensorProfile? sensor =
            await _sensorRepository.GetByIdAsync(
                sensorProfileId,
                cancellationToken);

        if (sensor is null)
        {
            throw new SensorNotFoundException(
                sensorProfileId);
        }

        return sensor;
    }
}