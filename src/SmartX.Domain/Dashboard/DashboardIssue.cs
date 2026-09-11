namespace SmartX.Domain.Dashboard;

/// <summary>
/// Represents one operational condition surfaced by the Smart-X Adaptive
/// Anomaly Spotlight.
///
/// An issue is linked to one registered sensor and records both its technical
/// condition and operator lifecycle. Repeated observations update the same
/// active issue rather than flooding the dashboard with duplicates.
/// </summary>
public sealed class DashboardIssue
{
    private const int CriticalSeverityScore = 300;
    private const int WarningSeverityScore = 200;
    private const int InformationSeverityScore = 100;

    private const int MaximumOccurrenceBoost = 60;
    private const int OccurrenceBoostPerRepeat = 15;

    private const int MaximumRecencyBoost = 60;
    private const int MaximumDisconnectDurationBoost = 60;

    private const int AcknowledgedPriorityReduction = 100;

    private DashboardIssue(
        Guid id,
        Guid sensorProfileId,
        string sensorName,
        DashboardIssueType type,
        DashboardIssueSeverity severity,
        DashboardIssueTarget target,
        string title,
        string detail,
        DateTimeOffset observedAtUtc)
    {
        Id = id;
        SensorProfileId = sensorProfileId;
        SensorName = sensorName;
        Type = type;
        Severity = severity;
        Target = target;
        Title = title;
        Detail = detail;

        FirstObservedAtUtc = observedAtUtc.ToUniversalTime();
        LastObservedAtUtc = observedAtUtc.ToUniversalTime();

        State = DashboardIssueState.Active;
        OccurrenceCount = 1;
    }

    /// <summary>
    /// Unique identifier of this issue occurrence lifecycle.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Smart-X sensor affected by the issue.
    /// </summary>
    public Guid SensorProfileId { get; }

    /// <summary>
    /// Human-readable sensor name captured for dashboard display.
    /// </summary>
    public string SensorName { get; private set; }

    /// <summary>
    /// Operational category of the issue.
    /// </summary>
    public DashboardIssueType Type { get; }

    /// <summary>
    /// Current severity of the issue.
    ///
    /// Repeated observations may escalate this value but never silently
    /// downgrade an active problem.
    /// </summary>
    public DashboardIssueSeverity Severity { get; private set; }

    /// <summary>
    /// Most relevant frontend workspace for investigation.
    /// </summary>
    public DashboardIssueTarget Target { get; private set; }

    /// <summary>
    /// Concise operator-facing issue title.
    /// </summary>
    public string Title { get; private set; }

    /// <summary>
    /// Context explaining why the issue was raised.
    /// </summary>
    public string Detail { get; private set; }

    /// <summary>
    /// Operator lifecycle state.
    /// </summary>
    public DashboardIssueState State { get; private set; }

    /// <summary>
    /// First time this active issue condition was observed.
    /// </summary>
    public DateTimeOffset FirstObservedAtUtc { get; }

    /// <summary>
    /// Most recent time the condition was observed.
    /// </summary>
    public DateTimeOffset LastObservedAtUtc { get; private set; }

    /// <summary>
    /// Number of times the same active condition has been observed.
    ///
    /// This is used by prioritisation so repeated problems rise above
    /// equivalent one-off conditions.
    /// </summary>
    public int OccurrenceCount { get; private set; }

    /// <summary>
    /// Time at which an operator acknowledged the issue.
    /// </summary>
    public DateTimeOffset? AcknowledgedAtUtc { get; private set; }

    /// <summary>
    /// Time at which the underlying condition was resolved.
    /// </summary>
    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    /// <summary>
    /// Creates a validated active dashboard issue.
    /// </summary>
    public static DashboardIssue Create(
        Guid sensorProfileId,
        string? sensorName,
        DashboardIssueType type,
        DashboardIssueSeverity severity,
        DashboardIssueTarget target,
        string? title,
        string? detail,
        DateTimeOffset observedAtUtc)
    {
        ValidateSensorProfileId(sensorProfileId);
        ValidateType(type);
        ValidateSeverity(severity);
        ValidateTarget(target);
        ValidateTimestamp(observedAtUtc);

        string validatedSensorName =
            ValidateRequiredText(
                sensorName,
                "A dashboard issue sensor name is required.");

        string validatedTitle =
            ValidateRequiredText(
                title,
                "A dashboard issue title is required.");

        string validatedDetail =
            ValidateRequiredText(
                detail,
                "Dashboard issue detail is required.");

        return new DashboardIssue(
            Guid.NewGuid(),
            sensorProfileId,
            validatedSensorName,
            type,
            severity,
            target,
            validatedTitle,
            validatedDetail,
            observedAtUtc);
    }

    /// <summary>
    /// Records another observation of the same still-active condition.
    ///
    /// The latest evidence replaces the previous description and severity can
    /// escalate, but a repeated observation never silently lowers severity.
    /// An acknowledged issue remains acknowledged so repeated telemetry does
    /// not undo the operator's action.
    /// </summary>
    public void RecordOccurrence(
        string? sensorName,
        DashboardIssueSeverity severity,
        DashboardIssueTarget target,
        string? title,
        string? detail,
        DateTimeOffset observedAtUtc)
    {
        if (State == DashboardIssueState.Resolved)
        {
            throw new InvalidOperationException(
                "A resolved dashboard issue cannot record new occurrences.");
        }

        ValidateSeverity(severity);
        ValidateTarget(target);
        ValidateTimestamp(observedAtUtc);

        DateTimeOffset normalisedObservedAt =
            observedAtUtc.ToUniversalTime();

        if (normalisedObservedAt < LastObservedAtUtc)
        {
            throw new ArgumentException(
                "A dashboard issue observation cannot be older than its " +
                "latest observation.",
                nameof(observedAtUtc));
        }

        SensorName =
            ValidateRequiredText(
                sensorName,
                "A dashboard issue sensor name is required.");

        Title =
            ValidateRequiredText(
                title,
                "A dashboard issue title is required.");

        Detail =
            ValidateRequiredText(
                detail,
                "Dashboard issue detail is required.");

        Target = target;

        if (severity > Severity)
        {
            Severity = severity;
        }

        LastObservedAtUtc = normalisedObservedAt;

        checked
        {
            OccurrenceCount++;
        }
    }

    /// <summary>
    /// Marks an active issue as seen by an operator without claiming that its
    /// underlying condition has been resolved.
    /// </summary>
    public void Acknowledge(DateTimeOffset acknowledgedAtUtc)
    {
        ValidateTimestamp(acknowledgedAtUtc);

        if (State == DashboardIssueState.Resolved)
        {
            return;
        }

        DateTimeOffset normalisedAcknowledgedAt =
            acknowledgedAtUtc.ToUniversalTime();

        if (normalisedAcknowledgedAt < FirstObservedAtUtc)
        {
            throw new ArgumentException(
                "A dashboard issue cannot be acknowledged before it was " +
                "observed.",
                nameof(acknowledgedAtUtc));
        }

        State = DashboardIssueState.Acknowledged;
        AcknowledgedAtUtc = normalisedAcknowledgedAt;
    }

    /// <summary>
    /// Resolves the issue when the underlying system condition has cleared.
    /// </summary>
    public void Resolve(DateTimeOffset resolvedAtUtc)
    {
        ValidateTimestamp(resolvedAtUtc);

        if (State == DashboardIssueState.Resolved)
        {
            return;
        }

        DateTimeOffset normalisedResolvedAt =
            resolvedAtUtc.ToUniversalTime();

        if (normalisedResolvedAt < FirstObservedAtUtc)
        {
            throw new ArgumentException(
                "A dashboard issue cannot be resolved before it was observed.",
                nameof(resolvedAtUtc));
        }

        State = DashboardIssueState.Resolved;
        ResolvedAtUtc = normalisedResolvedAt;
    }

    /// <summary>
    /// Calculates the current Adaptive Anomaly Spotlight priority.
    ///
    /// Priority combines:
    /// - severity;
    /// - repeated observations;
    /// - recency for ordinary issues;
    /// - disconnect duration for active disconnects;
    /// - acknowledgement state.
    ///
    /// This produces a deterministic ranking while keeping the original
    /// severity available independently for display and filtering.
    /// </summary>
    public int CalculatePriorityScore(DateTimeOffset evaluatedAtUtc)
    {
        ValidateTimestamp(evaluatedAtUtc);

        DateTimeOffset normalisedEvaluatedAt =
            evaluatedAtUtc.ToUniversalTime();

        if (normalisedEvaluatedAt < FirstObservedAtUtc)
        {
            throw new ArgumentException(
                "Dashboard issue priority cannot be evaluated before the " +
                "issue was observed.",
                nameof(evaluatedAtUtc));
        }

        if (State == DashboardIssueState.Resolved)
        {
            return 0;
        }

        int severityScore =
            Severity switch
            {
                DashboardIssueSeverity.Critical =>
                    CriticalSeverityScore,

                DashboardIssueSeverity.Warning =>
                    WarningSeverityScore,

                DashboardIssueSeverity.Information =>
                    InformationSeverityScore,

                _ => throw new InvalidOperationException(
                    "Unsupported dashboard issue severity.")
            };

        int repeatedOccurrenceBoost =
            Math.Min(
                Math.Max(
                    OccurrenceCount - 1,
                    0) * OccurrenceBoostPerRepeat,
                MaximumOccurrenceBoost);

        int timeScore =
            Type == DashboardIssueType.SensorDisconnected
                ? CalculateDisconnectDurationBoost(
                    normalisedEvaluatedAt)
                : CalculateRecencyBoost(
                    normalisedEvaluatedAt);

        int acknowledgementAdjustment =
            State == DashboardIssueState.Acknowledged
                ? -AcknowledgedPriorityReduction
                : 0;

        return Math.Max(
            1,
            severityScore +
            repeatedOccurrenceBoost +
            timeScore +
            acknowledgementAdjustment);
    }

    private int CalculateRecencyBoost(
        DateTimeOffset evaluatedAtUtc)
    {
        double ageMinutes =
            Math.Max(
                0,
                (evaluatedAtUtc - LastObservedAtUtc)
                    .TotalMinutes);

        return Math.Max(
            0,
            MaximumRecencyBoost -
            (int)Math.Floor(ageMinutes));
    }

    private int CalculateDisconnectDurationBoost(
        DateTimeOffset evaluatedAtUtc)
    {
        double disconnectedMinutes =
            Math.Max(
                0,
                (evaluatedAtUtc - FirstObservedAtUtc)
                    .TotalMinutes);

        return Math.Min(
            MaximumDisconnectDurationBoost,
            (int)Math.Floor(disconnectedMinutes));
    }

    private static string ValidateRequiredText(
        string? value,
        string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(message);
        }

        string normalised = value.Trim();

        if (normalised.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Dashboard issue text cannot contain control characters.");
        }

        return normalised;
    }

    private static void ValidateSensorProfileId(
        Guid sensorProfileId)
    {
        if (sensorProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A dashboard issue must identify a sensor.",
                nameof(sensorProfileId));
        }
    }

    private static void ValidateType(
        DashboardIssueType type)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "Unsupported dashboard issue type.");
        }
    }

    private static void ValidateSeverity(
        DashboardIssueSeverity severity)
    {
        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(severity),
                severity,
                "Unsupported dashboard issue severity.");
        }
    }

    private static void ValidateTarget(
        DashboardIssueTarget target)
    {
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "Unsupported dashboard issue target.");
        }
    }

    private static void ValidateTimestamp(
        DateTimeOffset timestamp)
    {
        if (timestamp == default)
        {
            throw new ArgumentException(
                "A dashboard issue requires a valid timestamp.");
        }
    }
}