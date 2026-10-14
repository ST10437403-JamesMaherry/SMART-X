using SmartX.Application.Commands;
using SmartX.Application.Sensors;
using SmartX.Contracts.Commands;
using SmartX.Contracts.Realtime;
using SmartX.Domain.Commands;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Realtime;

/// <summary>
/// Projects the authoritative registered sensor repository into
/// immutable Part 2 HTTP responses.
///
/// Device identity and metadata continue to come exclusively from
/// ISensorRepository. Processing diagnostics come from the existing
/// telemetry processing store.
///
/// Simulated relay state comes from the separate command store.
/// No independently maintained device registry is introduced.
/// </summary>
public sealed class RealtimeSnapshotService
{
    private readonly ISensorRepository _sensorRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ITelemetryProcessingStore _processing;
    private readonly IActuatorCommandStore _commandStore;

    public RealtimeSnapshotService(
        ISensorRepository sensorRepository,
        TimeProvider timeProvider,
        ITelemetryProcessingStore processing,
        IActuatorCommandStore commandStore)
    {
        _sensorRepository = sensorRepository ??
            throw new ArgumentNullException(nameof(sensorRepository));

        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));

        _processing = processing ??
            throw new ArgumentNullException(nameof(processing));

        _commandStore = commandStore ??
            throw new ArgumentNullException(nameof(commandStore));
    }

    /// <summary>
    /// Returns detached values in deterministic Part 1 registry order.
    ///
    /// The registered-sensor collection is read from the original
    /// repository. Command metadata is added only for devices whose
    /// registered category and telemetry type support relay commands.
    /// </summary>
    public async Task<IReadOnlyList<RealtimeDeviceResponse>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<SensorProfile> sensors =
            await _sensorRepository.GetAllAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var devices = new List<RealtimeDeviceResponse>(sensors.Count);

        foreach (SensorProfile sensor in sensors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            devices.Add(
                await ToResponseAsync(sensor, cancellationToken));
        }

        return Array.AsReadOnly(devices.ToArray());
    }

    /// <summary>
    /// Reuses the existing identifier parser and canonical Dictionary
    /// lookup. Exact lookup does not scan the registered-device list.
    /// </summary>
    public async Task<RealtimeDeviceResponse> GetByDeviceIdentifierAsync(
        string? deviceIdentifier,
        string? identifierKind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DeviceIdentifier canonical =
            SensorContractMapper.MapDeviceIdentifier(
                deviceIdentifier,
                identifierKind);

        SensorProfile? sensor =
            await _sensorRepository.GetByDeviceIdentifierAsync(
                canonical,
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (sensor is null)
        {
            throw new RealtimeDeviceNotFoundException(canonical);
        }

        return await ToResponseAsync(sensor, cancellationToken);
    }

    /// <summary>
    /// Returns the current device and processing state without changing
    /// P2-03 telemetry queue accounting or dispatch diagnostics.
    /// </summary>
    public async Task<RealtimeSnapshotResponse> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RealtimeDeviceResponse> devices =
            await GetDevicesAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        return new RealtimeSnapshotResponse(
            _timeProvider.GetUtcNow().ToUniversalTime(),
            devices.Count,
            devices,
            TelemetryProcessingMapper.ToResponse(
                _processing.GetSnapshot()));
    }

    /// <summary>
    /// Returns the actual commanded relay state and bounded command
    /// audit for a registered Actuator/Boolean device.
    ///
    /// The sensor repository is checked before reading command state.
    /// Operational status does not prevent an operator from inspecting
    /// command history, even though a non-Online device cannot execute
    /// new commands or Undo.
    /// </summary>
    public async Task<ActuatorCommandHistoryResponse> GetActuatorHistoryAsync(
        Guid targetDeviceId,
        CancellationToken cancellationToken = default)
    {
        if (targetDeviceId == Guid.Empty)
        {
            throw new ActuatorCommandValidationException(
                "A valid target device ID is required.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        SensorProfile? sensor =
            await _sensorRepository.GetByIdAsync(
                targetDeviceId,
                cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (sensor is null)
        {
            throw new SensorNotFoundException(targetDeviceId);
        }

        if (!SupportsRelayCommands(sensor))
        {
            throw new ActuatorCommandConflictException(
                "Command history is available only for registered " +
                "Actuator devices with Boolean telemetry.");
        }

        ActuatorStateSnapshot snapshot =
            await _commandStore.GetSnapshotAsync(
                targetDeviceId,
                cancellationToken);

        return ActuatorCommandContractMapper.ToHistoryResponse(snapshot);
    }

    /// <summary>
    /// Copies every existing public profile field and attaches the
    /// latest processed observation as before.
    ///
    /// Only the optional commanded actuator projection is new.
    /// A sensor's latest reported reading never overwrites a manual
    /// command's actual simulated relay state.
    /// </summary>
    private async Task<RealtimeDeviceResponse> ToResponseAsync(
        SensorProfile sensor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var profile = SensorContractMapper.ToResponse(sensor);
        var processed = _processing.GetDeviceState(sensor.Id);

        ActuatorCommandStateResponse? actuatorState = null;

        if (SupportsRelayCommands(sensor))
        {
            ActuatorStateSnapshot commandSnapshot =
                await _commandStore.GetSnapshotAsync(
                    sensor.Id,
                    cancellationToken);

            actuatorState =
                ActuatorCommandContractMapper.ToStateResponse(
                    commandSnapshot);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new RealtimeDeviceResponse(
            profile.Id,
            profile.Name,
            profile.DeviceIdentifier,
            profile.IdentifierKind,
            profile.DeploymentLocation,
            profile.Category,
            profile.Status,
            profile.TelemetryDataType,
            profile.CreatedAtUtc,
            profile.UpdatedAtUtc,
            processed is null
                ? null
                : TelemetryProcessingMapper.ToReading(
                    processed.LatestObservation),
            processed?.LastProcessedAtUtc,
            actuatorState);
    }

    /// <summary>
    /// The registered profile, not the stored commanded state, decides
    /// whether a device is capable of supporting Boolean relay control.
    ///
    /// Online eligibility is checked separately by the command service
    /// when a state-changing operation actually executes.
    /// </summary>
    private static bool SupportsRelayCommands(SensorProfile sensor)
    {
        return sensor.Category == SensorCategory.Actuator &&
               sensor.DataType == TelemetryDataType.Boolean;
    }
}