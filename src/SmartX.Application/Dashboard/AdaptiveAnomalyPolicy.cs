namespace SmartX.Application.Dashboard;

/// <summary>
/// Defines the thresholds used by the Smart-X Adaptive Anomaly Spotlight.
///
/// Part 1 sensor profiles do not yet contain domain-specific minimum and
/// maximum telemetry ranges. The current policy therefore evaluates abrupt
/// changes relative to the immediately preceding telemetry baseline.
///
/// Keeping these values in one immutable policy object avoids scattering
/// unexplained thresholds throughout the analysis service and allows later
/// PoE stages to replace or configure the policy without changing the
/// underlying anomaly workflow.
/// </summary>
public sealed record AdaptiveAnomalyPolicy
{
    /// <summary>
    /// Default relative change at which an abrupt telemetry movement becomes
    /// noteworthy.
    ///
    /// A value of 0.20 represents a twenty-percent symmetric relative change.
    /// </summary>
    public const double DefaultWarningRelativeChange = 0.20;

    /// <summary>
    /// Default relative change at which an abrupt telemetry movement becomes
    /// critical.
    /// </summary>
    public const double DefaultCriticalRelativeChange = 0.50;

    /// <summary>
    /// Maximum elapsed time between two readings for their difference to be
    /// considered a sudden spike rather than ordinary long-term movement.
    /// </summary>
    public static readonly TimeSpan DefaultMaximumSpikeInterval =
        TimeSpan.FromMinutes(5);

    /// <summary>
    /// Number of consecutive non-anomalous telemetry comparisons required
    /// before an active spike is considered recovered.
    ///
    /// Requiring confirmation prevents one normal-looking packet from
    /// instantly hiding a genuine operational problem.
    /// </summary>
    public const int DefaultStableObservationsForRecovery = 2;

    /// <summary>
    /// Number of repeated abnormal observations required before Smart-X raises
    /// an additional repeated-failure condition.
    /// </summary>
    public const int DefaultRepeatedFailureThreshold = 3;

    public AdaptiveAnomalyPolicy(
        double warningRelativeChange =
            DefaultWarningRelativeChange,
        double criticalRelativeChange =
            DefaultCriticalRelativeChange,
        TimeSpan? maximumSpikeInterval = null,
        int stableObservationsForRecovery =
            DefaultStableObservationsForRecovery,
        int repeatedFailureThreshold =
            DefaultRepeatedFailureThreshold)
    {
        if (!double.IsFinite(warningRelativeChange) ||
            warningRelativeChange <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(warningRelativeChange),
                warningRelativeChange,
                "The warning relative-change threshold must be a " +
                "positive finite value.");
        }

        if (!double.IsFinite(criticalRelativeChange) ||
            criticalRelativeChange <= warningRelativeChange)
        {
            throw new ArgumentOutOfRangeException(
                nameof(criticalRelativeChange),
                criticalRelativeChange,
                "The critical relative-change threshold must be greater " +
                "than the warning threshold.");
        }

        var resolvedMaximumSpikeInterval =
            maximumSpikeInterval ??
            DefaultMaximumSpikeInterval;

        if (resolvedMaximumSpikeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSpikeInterval),
                resolvedMaximumSpikeInterval,
                "The maximum spike interval must be greater than zero.");
        }

        if (stableObservationsForRecovery <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stableObservationsForRecovery),
                stableObservationsForRecovery,
                "At least one stable telemetry observation is required.");
        }

        if (repeatedFailureThreshold < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repeatedFailureThreshold),
                repeatedFailureThreshold,
                "A repeated failure must require at least two " +
                "observations.");
        }

        WarningRelativeChange =
            warningRelativeChange;

        CriticalRelativeChange =
            criticalRelativeChange;

        MaximumSpikeInterval =
            resolvedMaximumSpikeInterval;

        StableObservationsForRecovery =
            stableObservationsForRecovery;

        RepeatedFailureThreshold =
            repeatedFailureThreshold;
    }

    /// <summary>
    /// Relative change required for a warning-level telemetry spike.
    /// </summary>
    public double WarningRelativeChange { get; }

    /// <summary>
    /// Relative change required for a critical telemetry spike.
    /// </summary>
    public double CriticalRelativeChange { get; }

    /// <summary>
    /// Maximum interval over which a change is considered sudden.
    /// </summary>
    public TimeSpan MaximumSpikeInterval { get; }

    /// <summary>
    /// Consecutive stable observations required to confirm recovery.
    /// </summary>
    public int StableObservationsForRecovery { get; }

    /// <summary>
    /// Repeated abnormal observations required for a repeated-failure issue.
    /// </summary>
    public int RepeatedFailureThreshold { get; }
}