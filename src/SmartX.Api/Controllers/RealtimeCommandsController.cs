using Microsoft.AspNetCore.Mvc;

using SmartX.Application.Commands;
using SmartX.Application.Realtime;
using SmartX.Contracts.Commands;
using SmartX.Domain.Commands;

namespace SmartX.Api.Controllers;

/// <summary>
/// Exposes validated simulated actuator commands and genuine Stack-backed
/// Undo over ordinary HTTP.
///
/// Application services own eligibility, serialisation, state mutation,
/// and Undo validation. HTTP remains the authoritative transport for
/// command execution and state reads.
///
/// The existing SignalR infrastructure provides best-effort invalidations
/// after accepted operations, rather than executing commands through hubs.
/// </summary>
[ApiController]
[Route("api/realtime/devices/{deviceId:guid}/commands")]
public sealed class RealtimeCommandsController : ControllerBase
{
    private readonly ActuatorCommandService _commandService;
    private readonly RealtimeSnapshotService _snapshotService;
    private readonly IRealtimeNotifier _notifier;
    private readonly ILogger<RealtimeCommandsController> _logger;

    public RealtimeCommandsController(
        ActuatorCommandService commandService,
        RealtimeSnapshotService snapshotService,
        IRealtimeNotifier notifier,
        ILogger<RealtimeCommandsController> logger)
    {
        _commandService = commandService ??
            throw new ArgumentNullException(nameof(commandService));

        _snapshotService = snapshotService ??
            throw new ArgumentNullException(nameof(snapshotService));

        _notifier = notifier ??
            throw new ArgumentNullException(nameof(notifier));

        _logger = logger ??
            throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes RelayOn or RelayOff for an eligible registered actuator.
    ///
    /// Only the exact named vocabulary is accepted. Numeric enum strings
    /// and unsupported command names cannot bypass validation.
    ///
    /// Accepted no-ops receive an audit record but do not consume Undo
    /// history. The returned state and Stack metadata reflect the actual
    /// committed transaction.
    /// </summary>
    [HttpPost]
    [ProducesResponseType<RelayCommandResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RelayCommandResponse>> Execute(
        Guid deviceId,
        [FromBody] RelayCommandRequest? request,
        CancellationToken cancellationToken)
    {
        RelayCommandType type = ParseCommandType(
            request?.CommandType);

        ActuatorCommandExecutionResult result =
            await _commandService.ExecuteAsync(
                deviceId,
                type,
                cancellationToken);

        // Even a successful no-op adds an audit record. Notify listeners
        // that command history may have changed, without falsely stating
        // that the commanded relay state necessarily changed.
        await PublishSafelyAsync(
            "ActuatorCommandRecorded",
            deviceId);

        return Ok(
            ActuatorCommandContractMapper.ToResponse(result));
    }

    /// <summary>
    /// Restores the recorded previous state of the latest successful
    /// state-changing manual command.
    ///
    /// The expected ID must match the actual top of the device's Stack.
    /// Stale requests, empty history and ineligible devices are rejected
    /// without modifying commanded state or Undo history.
    /// </summary>
    [HttpPost("undo")]
    [ProducesResponseType<RelayCommandResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RelayCommandResponse>> Undo(
        Guid deviceId,
        [FromBody] UndoRelayCommandRequest? request,
        CancellationToken cancellationToken)
    {
        if (request?.ExpectedLatestCommandId is not Guid expectedId ||
            expectedId == Guid.Empty)
        {
            throw new ActuatorCommandValidationException(
                "A valid expected latest command ID is required for Undo.");
        }

        ActuatorCommandExecutionResult result =
            await _commandService.UndoAsync(
                deviceId,
                expectedId,
                cancellationToken);

        // The original command and linked reversal have already been
        // recorded successfully by the command service and store.
        await PublishSafelyAsync(
            "ActuatorCommandUndone",
            deviceId);

        return Ok(
            ActuatorCommandContractMapper.ToResponse(result));
    }

    /// <summary>
    /// Reads authoritative commanded state and the latest 100 accepted
    /// command/reversal audit records for the selected actuator.
    ///
    /// Reading history is allowed while a registered actuator is offline.
    /// Executing a new command or Undo still requires Online status.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<ActuatorCommandHistoryResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ActuatorCommandHistoryResponse>> GetHistory(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        ActuatorCommandHistoryResponse history =
            await _snapshotService.GetActuatorHistoryAsync(
                deviceId,
                cancellationToken);

        return Ok(history);
    }

    /// <summary>
    /// Accepts the two explicit command names without allowing unknown
    /// enum values or numeric representations from external requests.
    /// Matching is case-insensitive for ordinary HTTP client usability.
    /// </summary>
    private static RelayCommandType ParseCommandType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ActuatorCommandValidationException(
                "A relay command type is required.");
        }

        string command = value.Trim();

        if (string.Equals(
            command,
            nameof(RelayCommandType.RelayOn),
            StringComparison.OrdinalIgnoreCase))
        {
            return RelayCommandType.RelayOn;
        }

        if (string.Equals(
            command,
            nameof(RelayCommandType.RelayOff),
            StringComparison.OrdinalIgnoreCase))
        {
            return RelayCommandType.RelayOff;
        }

        throw new ActuatorCommandValidationException(
            "Unsupported relay command. Use RelayOn or RelayOff.");
    }

    /// <summary>
    /// Real-time delivery is best effort and must never roll back a
    /// successfully committed command or produce a misleading HTTP error.
    ///
    /// Cancellation from the original HTTP request is intentionally not
    /// forwarded after the command has committed.
    /// </summary>
    private async Task PublishSafelyAsync(
        string reason,
        Guid targetDeviceId)
    {
        try
        {
            await _notifier.PublishAsync(
                reason,
                targetDeviceId,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Command invalidation failed for device {DeviceId} " +
                "with reason {Reason}. The committed state remains valid.",
                targetDeviceId,
                reason);
        }
    }
}