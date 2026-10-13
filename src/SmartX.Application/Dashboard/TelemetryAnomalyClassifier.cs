using System.Numerics;

using SmartX.Domain.Dashboard;
using SmartX.Domain.Telemetry;

namespace SmartX.Application.Dashboard;

/// <summary>
/// Shared, side-effect-free Part 1 spike policy. Classifying a packet never
/// changes issue occurrence counts or recovery counters.
/// </summary>
public sealed class TelemetryAnomalyClassifier
{
    private readonly AdaptiveAnomalyPolicy _policy;

    public TelemetryAnomalyClassifier(AdaptiveAnomalyPolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public TelemetryAnomalyObservation Classify<T>(
        TelemetryPacket<T>? previous,
        TelemetryPacket<T> current)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(current);

        // Boolean packets are valid recovery evidence without arithmetic.
        if (current is TelemetryPacket<bool>)
        {
            return TelemetryAnomalyObservation.Stable;
        }

        if (previous is null || current.CapturedAtUtc < previous.CapturedAtUtc)
        {
            return TelemetryAnomalyObservation.None;
        }

        return (previous, current) switch
        {
            (TelemetryPacket<double> p, TelemetryPacket<double> c) =>
                ClassifyNumeric(p, c),
            // Widen only the comparison so int.MinValue/int.MaxValue cannot
            // overflow subtraction or Abs. The accepted packet stays int.
            (TelemetryPacket<int> p, TelemetryPacket<int> c) =>
                ClassifyNumeric(
                    new NumericTelemetryPacket<long>(p.SourceDeviceIdentifier, p.Payload, p.CapturedAtUtc),
                    new NumericTelemetryPacket<long>(c.SourceDeviceIdentifier, c.Payload, c.CapturedAtUtc)),
            _ => throw new ArgumentException("Unsupported telemetry payload type.", nameof(current))
        };
    }

    private TelemetryAnomalyObservation ClassifyNumeric<T>(
        TelemetryPacket<T> previousPacket,
        TelemetryPacket<T> currentPacket)
        where T : struct, INumber<T>
    {
        var previous = new NumericTelemetryPacket<T>(
            previousPacket.SourceDeviceIdentifier, previousPacket.Payload, previousPacket.CapturedAtUtc);
        var current = new NumericTelemetryPacket<T>(
            currentPacket.SourceDeviceIdentifier, currentPacket.Payload, currentPacket.CapturedAtUtc);

        // Preserve the custom numeric subtraction operator's application role.
        TelemetryDelta<T> delta = current - previous;
        var previousValue = double.CreateChecked(delta.PreviousValue);
        var currentValue = double.CreateChecked(delta.CurrentValue);
        var scale = Math.Max(Math.Abs(previousValue), Math.Abs(currentValue));
        var magnitude = Math.Abs(double.CreateChecked(delta.Magnitude));
        var relativeChange = scale == 0 ? 0 : double.IsFinite(magnitude)
            ? magnitude / scale
            : Math.Abs(currentValue / scale - previousValue / scale);

        if (delta.Elapsed > _policy.MaximumSpikeInterval ||
            relativeChange < _policy.WarningRelativeChange)
        {
            return TelemetryAnomalyObservation.Stable;
        }

        var severity = relativeChange >= _policy.CriticalRelativeChange
            ? DashboardIssueSeverity.Critical
            : DashboardIssueSeverity.Warning;
        var movement = delta.IsIncrease ? "increase" : delta.IsDecrease ? "decrease" : "change";
        var detail = $"Telemetry changed from {delta.PreviousValue} to " +
            $"{delta.CurrentValue}, a {relativeChange:P0} relative {movement}, " +
            $"over {delta.Elapsed.TotalSeconds:0.##} seconds.";

        return new TelemetryAnomalyObservation(true, true, severity, detail);
    }
}