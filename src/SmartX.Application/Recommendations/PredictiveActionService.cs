using SmartX.Application.Commands;
using SmartX.Application.Dashboard;
using SmartX.Application.Sensors;
using SmartX.Contracts.Dashboard;
using SmartX.Domain.Commands;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Recommendations;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Recommendations;

/// <summary>
/// Derives explained, deterministic suggestions from verified P2-09 patterns
/// and CURRENT P2-06/P2-04 state; it never executes actuator commands.
///
/// The sequence is deliberately read-only:
/// 1. The bounded Dictionary pattern store prunes expired training evidence.
/// 2. The HashSet-backed dashboard snapshot supplies unresolved episodes.
/// 3. The canonical sensor repository verifies both source and target.
/// 4. The genuine command store supplies the target's current relay state.
///
/// Candidates that would be no-ops, target Offline devices, or mismatch the
/// alert's severity or numeric movement are suppressed. A suggestion is not
/// an authorization: the normal command endpoint revalidates upon execution.
/// </summary>
public sealed class PredictiveActionService
{
    private readonly IInteractionPatternStore _patterns;
    private readonly DashboardIssueService _issues;
    private readonly ISensorRepository _sensors;
    private readonly IActuatorCommandStore _commands;
    private readonly PatternLearningPolicy _policy;
    private readonly TimeProvider _clock;

    public PredictiveActionService(
        IInteractionPatternStore patterns,
        DashboardIssueService issues,
        ISensorRepository sensors,
        IActuatorCommandStore commands,
        PatternLearningPolicy policy,
        TimeProvider clock)
    {
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        _issues = issues ?? throw new ArgumentNullException(nameof(issues));
        _sensors = sensors ?? throw new ArgumentNullException(nameof(sensors));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<PredictedActionSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.GetUtcNow().ToUniversalTime();

        // Existing dashboard semantics: GET without includeResolved returns
        // only Active/Acknowledged conditions from the real HashSet index.
        // We explicitly check state as defence against future projections.
        DashboardIssueResponse[] currentIssues = _issues.GetSnapshot().Issues
            .Where(issue => issue.State is "Active" or "Acknowledged")
            .Where(issue => issue.LastObservedAtUtc <= now &&
                issue.LastObservedAtUtc >= now - _policy.MaximumCurrentConditionAge)
            .ToArray();

        // There is at most one active episode per sensor/issue type. Movement
        // is derived from the SAME relative-increase/decrease wording used
        // when the server learned the original pattern. Unknown/ambiguous
        // spike direction must never match a trained drop or rise.
        var active = new Dictionary<ConditionContextKey, DashboardIssueResponse>();
        foreach (DashboardIssueResponse issue in currentIssues)
        {
            if (!Enum.TryParse(issue.Type, out DashboardIssueType type) ||
                !Enum.IsDefined(type) ||
                !Enum.TryParse(issue.Severity, out DashboardIssueSeverity severity) ||
                !Enum.IsDefined(severity) ||
                !TryGetMovement(issue, type, out TelemetryMovementDirection movement))
            {
                continue;
            }

            active[new ConditionContextKey(issue.SensorProfileId, type, severity,
                movement)] = issue;
        }

        var suggestions = new List<PredictedAction>();
        foreach (PatternStatistics learned in _patterns.GetSnapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (learned.SupportingEpisodes < _policy.MinimumSupportingEpisodes ||
                learned.Episodes.Count < _policy.MinimumSupportingEpisodes)
            {
                continue;
            }

            InteractionPatternKey key = learned.Key;
            var conditionKey = new ConditionContextKey(key.SourceDeviceId,
                key.ConditionType, key.Severity, key.Movement);
            if (!active.TryGetValue(conditionKey, out DashboardIssueResponse? sourceIssue))
            {
                continue;
            }

            // A detached current issue must still belong to a registered
            // source. Decommissioned sensors cannot provide live context.
            SensorProfile? source = await _sensors.GetByIdAsync(
                key.SourceDeviceId, cancellationToken);
            if (source is null || source.Status == SensorStatus.Decommissioned)
            {
                continue;
            }

            // Eligibility is checked from the authoritative registered
            // profile, not from the operator's previous search selection.
            SensorProfile? target = await _sensors.GetByIdAsync(
                key.TargetActuatorId, cancellationToken);
            if (target is null || target.Id == source.Id ||
                target.Status != SensorStatus.Online ||
                target.Category != SensorCategory.Actuator ||
                target.DataType != TelemetryDataType.Boolean)
            {
                continue;
            }

            if (key.Command is not (RelayCommandType.RelayOn or RelayCommandType.RelayOff))
            {
                continue;
            }

            ActuatorStateSnapshot commanded = await _commands.GetSnapshotAsync(
                target.Id, cancellationToken);
            RelayState desired = key.Command == RelayCommandType.RelayOn
                ? RelayState.On
                : RelayState.Off;

            // An accepted no-op is not an actionable recommendation.
            if (commanded.CurrentState == desired)
            {
                continue;
            }

            // The timestamp when the MINIMUM supporting evidence would no
            // longer survive pruning is not always the oldest observation:
            // with five episodes and a threshold of three, for example,
            // only the third-newest expiry makes this pattern ineligible.
            DateTimeOffset evidenceExpiry = learned.Episodes[
                learned.Episodes.Count - _policy.MinimumSupportingEpisodes]
                .ObservedAtUtc.ToUniversalTime() + _policy.EvidenceWindow;
            DateTimeOffset conditionExpiry = sourceIssue.LastObservedAtUtc
                .ToUniversalTime() + _policy.MaximumCurrentConditionAge;
            DateTimeOffset expiresAt = evidenceExpiry < conditionExpiry
                ? evidenceExpiry
                : conditionExpiry;
            if (expiresAt < now)
            {
                continue;
            }

            int score = CalculateScore(learned.SupportingEpisodes,
                now - learned.LastObservedAtUtc.ToUniversalTime(), key.Severity);
            PredictionConfidenceBand band = score >= 85
                ? PredictionConfidenceBand.High
                : score >= 70
                    ? PredictionConfidenceBand.Moderate
                    : PredictionConfidenceBand.Emerging;

            suggestions.Add(new PredictedAction(
                sourceIssue.Id, source.Id, source.Name, key.ConditionType,
                key.Severity, key.Movement, target.Id, target.Name,
                key.Command, learned.SupportingEpisodes,
                learned.LastObservedAtUtc.ToUniversalTime(), score, band,
                expiresAt, CreateExplanation(key, source.Name, target.Name,
                    learned.SupportingEpisodes)));
        }

        // Sorting is explicit and stable. Score incorporates support,
        // recent behaviour and current severity; ties use real evidence
        // counts/recency, then immutable identifiers instead of names.
        PredictedAction[] ordered = suggestions
            .OrderByDescending(action => action.Score)
            .ThenByDescending(action => action.SupportingEpisodes)
            .ThenByDescending(action => action.LastLearnedAtUtc)
            .ThenByDescending(action => action.Severity)
            .ThenBy(action => action.SourceDeviceId)
            .ThenBy(action => action.ConditionType)
            .ThenBy(action => action.Movement)
            .ThenBy(action => action.TargetActuatorId)
            .ThenBy(action => action.Command)
            .Take(_policy.MaximumRecommendations)
            .ToArray();

        return new PredictedActionSnapshot(now, _policy.MinimumSupportingEpisodes,
            _policy.EvidenceWindow, Array.AsReadOnly(ordered));
    }

    /// <summary>
    /// Bounded score out of 100: up to 60 points for independent evidence,
    /// up to 25 for recent learning, and up to 15 for issue severity.
    /// This is a transparent ordering heuristic, NOT a measured probability.
    /// </summary>
    private static int CalculateScore(int support, TimeSpan age,
        DashboardIssueSeverity severity)
    {
        int supportPoints = Math.Min(60, support * 15);
        int recencyPoints = age <= TimeSpan.FromHours(1) ? 25
            : age <= TimeSpan.FromDays(1) ? 20
            : age <= TimeSpan.FromDays(3) ? 12
            : 4;
        int severityPoints = severity switch
        {
            DashboardIssueSeverity.Critical => 15,
            DashboardIssueSeverity.Warning => 10,
            _ => 5
        };

        return Math.Clamp(supportPoints + recencyPoints + severityPoints, 0, 100);
    }

    private static bool TryGetMovement(DashboardIssueResponse issue,
        DashboardIssueType type, out TelemetryMovementDirection movement)
    {
        movement = TelemetryMovementDirection.NotApplicable;
        if (type != DashboardIssueType.TelemetrySpike)
        {
            return true;
        }

        bool increase = issue.Detail.Contains("relative increase",
            StringComparison.OrdinalIgnoreCase);
        bool decrease = issue.Detail.Contains("relative decrease",
            StringComparison.OrdinalIgnoreCase);

        if (increase == decrease)
        {
            return false;
        }

        movement = increase
            ? TelemetryMovementDirection.Increase
            : TelemetryMovementDirection.Decrease;
        return true;
    }

    private static string CreateExplanation(InteractionPatternKey key,
        string sourceName, string targetName, int supportingEpisodes)
    {
        string condition = key.ConditionType switch
        {
            DashboardIssueType.TelemetrySpike => "telemetry spike",
            DashboardIssueType.SensorDisconnected => "sensor disconnection",
            DashboardIssueType.RepeatedFailure => "repeated failure",
            DashboardIssueType.ConfigurationIssue => "configuration issue",
            DashboardIssueType.SensorStateChange => "sensor state change",
            _ => "operational condition"
        };
        string movement = key.Movement switch
        {
            TelemetryMovementDirection.Increase => " with a relative increase",
            TelemetryMovementDirection.Decrease => " with a relative decrease",
            _ => string.Empty
        };

        return $"Current {key.Severity} {condition}{movement} on '{sourceName}'. " +
            $"In {supportingEpisodes} distinct alert episodes within the learning window, " +
            $"operators viewed the alert, associated '{targetName}' in a search, " +
            $"and successfully issued {key.Command}. " +
            "This is a heuristic suggestion, not a probability or an executed command.";
    }

    private readonly record struct ConditionContextKey(
        Guid SourceDeviceId,
        DashboardIssueType Type,
        DashboardIssueSeverity Severity,
        TelemetryMovementDirection Movement);
}