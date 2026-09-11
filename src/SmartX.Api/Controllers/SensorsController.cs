using Microsoft.AspNetCore.Mvc;

using SmartX.Api.Dashboard;
using SmartX.Application.Sensors;
using SmartX.Contracts.Sensors;

namespace SmartX.Api.Controllers;

/// <summary>
/// Exposes HTTP operations used to register, retrieve and update Smart-X
/// sensor profiles.
///
/// Business rules and persistence are deliberately delegated to Application
/// services so this controller remains focused on HTTP behaviour.
/// </summary>
[ApiController]
[Route("api/sensors")]
public sealed class SensorsController
    : ControllerBase
{
    private readonly SensorRegistrationService _registrationService;
    private readonly SensorManagementService _managementService;
    private readonly DashboardEventCoordinator _dashboardEvents;

    public SensorsController(
        SensorRegistrationService registrationService,
        SensorManagementService managementService,
        DashboardEventCoordinator dashboardEvents)
    {
        _registrationService =
            registrationService ??
            throw new ArgumentNullException(
                nameof(registrationService));

        _managementService =
            managementService ??
            throw new ArgumentNullException(
                nameof(managementService));

        _dashboardEvents =
            dashboardEvents ??
            throw new ArgumentNullException(
                nameof(dashboardEvents));
    }

    /// <summary>
    /// Registers a new Smart-X sensor profile.
    /// </summary>
    /// <response code="201">
    /// Sensor registered successfully.
    /// </response>
    /// <response code="400">
    /// Registration data is invalid.
    /// </response>
    /// <response code="409">
    /// The physical device identifier is already registered.
    /// </response>
    [HttpPost]
    [ProducesResponseType<SensorResponse>(
        StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SensorResponse>> Register(
        [FromBody] RegisterSensorRequest request,
        CancellationToken cancellationToken)
    {
        SensorResponse sensor =
            await _registrationService.RegisterAsync(
                request,
                cancellationToken);

        await _dashboardEvents
            .NotifySensorRegisteredAsync(
                sensor.Id,
                cancellationToken);

        return CreatedAtAction(
            nameof(GetById),
            new
            {
                id =
                    sensor.Id
            },
            sensor);
    }

    /// <summary>
    /// Lists all currently registered Smart-X sensors.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SensorResponse>>(
        StatusCodes.Status200OK)]
    public async Task<ActionResult<
        IReadOnlyList<SensorResponse>>> GetAll(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SensorResponse> sensors =
            await _managementService.GetAllAsync(
                cancellationToken);

        return Ok(
            sensors);
    }

    /// <summary>
    /// Retrieves one registered sensor by its internal Smart-X ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<SensorResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SensorResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        SensorResponse sensor =
            await _managementService.GetByIdAsync(
                id,
                cancellationToken);

        return Ok(
            sensor);
    }

    /// <summary>
    /// Replaces the mutable metadata of an existing sensor profile.
    ///
    /// Physical device identity is intentionally immutable and therefore is
    /// not part of UpdateSensorRequest.
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<SensorResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SensorResponse>> Update(
        Guid id,
        [FromBody] UpdateSensorRequest request,
        CancellationToken cancellationToken)
    {
        SensorResponse sensor =
            await _managementService.UpdateAsync(
                id,
                request,
                cancellationToken);

        return Ok(
            sensor);
    }

    /// <summary>
    /// Simulates or reports a sensor operational-state transition.
    ///
    /// This endpoint is primarily used in Part 1 to demonstrate the Adaptive
    /// Anomaly Spotlight without requiring physical IoT hardware.
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    [ProducesResponseType<SensorResponse>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(
        StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SensorResponse>> ChangeStatus(
        Guid id,
        [FromBody] ChangeSensorStatusRequest request,
        CancellationToken cancellationToken)
    {
        SensorStatusChangeResult result =
            await _managementService.ChangeStatusAsync(
                id,
                request,
                cancellationToken);

        if (result.Changed)
        {
            await _dashboardEvents
                .AnalyzeSensorStatusAsync(
                    id,
                    result.PreviousStatus,
                    cancellationToken);
        }

        return Ok(
            result.Sensor);
    }
}