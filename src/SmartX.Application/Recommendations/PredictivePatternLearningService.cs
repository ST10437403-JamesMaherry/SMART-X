using SmartX.Application.Dashboard;
using SmartX.Application.Interactions;
using SmartX.Contracts.Dashboard;
using SmartX.Domain.Commands;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Interactions;
using SmartX.Domain.Recommendations;

namespace SmartX.Application.Recommendations;

/// <summary>
/// Correlates real server-validated operator evidence with an accepted
/// actuator command. This is intentionally the only production learning path.
///
/// Qualification: view a live source alert, explicitly associate that episode
/// with a searched actuator, then execute a state-changing relay command for
/// that actuator. Every step must occur in the bounded interaction interval.
/// Snapshot GETs, keystrokes, unrelated searches, no-ops and Undo cannot teach
/// patterns. The pattern store deduplicates repeated commands per episode.
/// </summary>
public sealed class PredictivePatternLearningService
{
    // The existing interaction API exposes at most 200 entries. We use the
    // newest 200, rather than widening the public read surface or retaining
    // an unbounded copy of every user interaction for prediction.
    private const int MaximumEvidenceEvents = 200;

    private readonly IOperatorInteractionStore _interactions;
    private readonly DashboardIssueService _issues;
    private readonly IInteractionPatternStore _patterns;
    private readonly PatternLearningPolicy _policy;
    private readonly TimeProvider _timeProvider;

    public PredictivePatternLearningService(
        IOperatorInteractionStore interactions,
        DashboardIssueService issues,
        IInteractionPatternStore patterns,
        PatternLearningPolicy policy,
        TimeProvider timeProvider)
    {
        _interactions = interactions ?? throw new ArgumentNullException(nameof(interactions));
        _issues = issues ?? throw new ArgumentNullException(nameof(issues));
        _patterns = patterns ?? throw new ArgumentNullException(nameof(patterns));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Called only after the existing per-device command transaction succeeds
    /// and its authoritative CommandExecuted interaction has been recorded.
    /// This never executes a command or changes its Stack/Undo semantics.
    /// </summary>
    public bool ObserveAcceptedCommand(CommandRecord command)
    {
        ArgumentNullException.ThrowIfNull(command);

        // An accepted no-op carries an audit event but not a remedy. Reversal
        // commands are recorded as CommandUndone and never teach the engine.
        if (!command.StateChanged || command.IsReversal)
        {
            return false;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow().ToUniversalTime();
        OperatorInteraction[] recent = _interactions.Read(MaximumEvidenceEvents)
            .Events.ToArray();

        OperatorInteraction? commandEvent = recent.FirstOrDefault(item =>
            item.Kind == OperatorInteractionKind.CommandExecuted &&
            item.DeviceId == command.TargetDeviceId &&
            item.CommandId == command.Id);

        if (commandEvent is null || commandEvent.OccurredAtUtc > now ||
            now - commandEvent.OccurredAtUtc > _policy.MaximumInteractionSpan)
        {
            return false;
        }

        DateTimeOffset earliest = commandEvent.OccurredAtUtc - _policy.MaximumInteractionSpan;

        // Inspect the latest *selected* submitted search, regardless of its
        // target. This prevents an intervening search for another actuator from
        // accidentally reusing an old source/target association.
        OperatorInteraction? selection = recent.FirstOrDefault(item =>
            item.Kind == OperatorInteractionKind.SearchSubmitted &&
            item.DeviceId.HasValue &&
            item.Sequence < commandEvent.Sequence &&
            item.OccurredAtUtc >= earliest &&
            item.OccurredAtUtc <= commandEvent.OccurredAtUtc);

        if (selection is null || selection.DeviceId != command.TargetDeviceId ||
            selection.AlertEpisodeId is not Guid episodeId ||
            episodeId == Guid.Empty)
        {
            return false;
        }

        // A client may request a server-validated association, but that alone
        // is not evidence of viewing the source. Require a separate earlier
        // AlertViewed record with the same exact episode ID.
        OperatorInteraction? view = recent.FirstOrDefault(item =>
            item.Kind == OperatorInteractionKind.AlertViewed &&
            item.AlertEpisodeId == episodeId &&
            item.DeviceId.HasValue &&
            item.DeviceId != command.TargetDeviceId &&
            item.Sequence < selection.Sequence &&
            item.OccurredAtUtc >= earliest &&
            item.OccurredAtUtc <= selection.OccurredAtUtc);

        if (view?.DeviceId is not Guid sourceDeviceId)
        {
            return false;
        }

        // The existing HashSet-backed condition collection is authoritative.
        // Recovery invalidates an episode even if old view/search events remain
        // in the bounded interaction history.
        DashboardIssueResponse? issue = _issues.GetSnapshot().Issues.FirstOrDefault(item =>
            item.Id == episodeId && item.SensorProfileId == sourceDeviceId);

        if (issue is null ||
            !Enum.TryParse(issue.Type, out DashboardIssueType conditionType) ||
            !Enum.IsDefined(conditionType) ||
            !Enum.TryParse(issue.Severity, out DashboardIssueSeverity severity) ||
            !Enum.IsDefined(severity))
        {
            return false;
        }

        TelemetryMovementDirection movement = TelemetryMovementDirection.NotApplicable;
        if (conditionType == DashboardIssueType.TelemetrySpike)
        {
            // P2-06 telemetry spikes use TelemetryAnomalyClassifier's exact
            // relative increase/decrease wording. An ambiguous or changed
            // detail cannot silently train a direction-agnostic pattern.
            if (issue.Detail.Contains("relative increase", StringComparison.OrdinalIgnoreCase))
                movement = TelemetryMovementDirection.Increase;
            else if (issue.Detail.Contains("relative decrease", StringComparison.OrdinalIgnoreCase))
                movement = TelemetryMovementDirection.Decrease;
            else
                return false;
        }

        var key = new InteractionPatternKey(sourceDeviceId, conditionType,
            severity, movement, command.TargetDeviceId, command.Type);

        // Timestamp and command identity come from the server-created event,
        // never a browser-supplied claim of successful execution.
        var evidence = new PatternEpisodeEvidence(episodeId, command.Id,
            commandEvent.OccurredAtUtc);
        return _patterns.Observe(new InteractionPatternObservation(key, evidence));
    }
}