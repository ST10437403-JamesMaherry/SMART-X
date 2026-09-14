using SmartX.Contracts.Sensors;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Sensors;

/// <summary>
/// Describes the result of a sensor operational-state evaluation.
///
/// Retaining both the previous and current strongly typed statuses allows
/// downstream dashboard analysis to determine whether a meaningful state
/// transition actually occurred.
/// </summary>
public sealed record SensorStatusChangeResult(
    SensorStatus PreviousStatus,
    SensorStatus CurrentStatus,
    bool Changed,
    SensorResponse Sensor);