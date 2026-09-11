using SmartX.Contracts.Dashboard;
using SmartX.Domain.Dashboard;

namespace SmartX.Application.Dashboard;

/// <summary>
/// Provides application operations over the shared Adaptive Anomaly
/// Spotlight issue collection.
///
/// Analysis services are responsible for discovering operational conditions.
/// This service is responsible for presenting and acknowledging those
/// conditions.
/// </summary>
public sealed class DashboardIssueService
{
    private readonly DashboardIssueCollection _issues;
    private readonly TimeProvider _timeProvider;

    public DashboardIssueService(
        DashboardIssueCollection issues,
        TimeProvider timeProvider)
    {
        _issues =
            issues ??
            throw new ArgumentNullException(
                nameof(issues));

        _timeProvider =
            timeProvider ??
            throw new ArgumentNullException(
                nameof(timeProvider));
    }

    /// <summary>
    /// Returns the current prioritized dashboard state.
    ///
    /// By default resolved history is omitted so the operator sees only
    /// actionable and acknowledged issues. Resolved history can still be
    /// requested explicitly for audit/debugging purposes.
    /// </summary>
    public DashboardSnapshotResponse GetSnapshot(
        bool includeResolved = false)
    {
        DateTimeOffset evaluatedAtUtc =
            _timeProvider
                .GetUtcNow()
                .ToUniversalTime();

        DashboardIssue[] snapshot =
            includeResolved
                ? _issues.ToArray()
                : _issues
                    .GetActiveSnapshot(
                        evaluatedAtUtc)
                    .ToArray();

        DashboardIssueResponse[] responses =
            snapshot
                /*
                 * Unresolved issues always appear before historical resolved
                 * items when includeResolved=true.
                 */
                .OrderBy(
                    issue =>
                        issue.State ==
                        DashboardIssueState.Resolved
                            ? 1
                            : 0)
                .ThenByDescending(
                    issue =>
                        issue.CalculatePriorityScore(
                            evaluatedAtUtc))
                .ThenByDescending(
                    issue =>
                        issue.LastObservedAtUtc)
                .Select(
                    issue =>
                        ToResponse(
                            issue,
                            evaluatedAtUtc))
                .ToArray();

        int activeIssueCount =
            snapshot.Count(
                issue =>
                    issue.State !=
                    DashboardIssueState.Resolved);

        int unacknowledgedIssueCount =
            snapshot.Count(
                issue =>
                    issue.State ==
                    DashboardIssueState.Active);

        return new DashboardSnapshotResponse(
            evaluatedAtUtc,
            activeIssueCount,
            unacknowledgedIssueCount,
            responses);
    }

    /// <summary>
    /// Acknowledges an existing unresolved issue.
    ///
    /// Acknowledgement intentionally does not resolve or remove the issue.
    /// The underlying condition must recover before analysis marks the issue
    /// resolved.
    /// </summary>
    public DashboardIssueResponse Acknowledge(
        Guid issueId)
    {
        if (issueId ==
            Guid.Empty)
        {
            throw new DashboardIssueNotFoundException(
                issueId);
        }

        DateTimeOffset acknowledgedAtUtc =
            _timeProvider
                .GetUtcNow()
                .ToUniversalTime();

        DashboardIssue? issue =
            _issues
                .ToArray()
                .FirstOrDefault(
                    candidate =>
                        candidate.Id ==
                        issueId);

        if (issue is null)
        {
            throw new DashboardIssueNotFoundException(
                issueId);
        }

        if (issue.State ==
            DashboardIssueState.Resolved)
        {
            throw new DashboardIssueOperationException(
                "A resolved dashboard issue cannot be acknowledged.");
        }

        /*
         * Repeating an acknowledgement is idempotent. This protects against
         * duplicate browser submissions and reconnect/retry behaviour.
         */
        if (issue.State ==
            DashboardIssueState.Active)
        {
            _issues.Acknowledge(
                issueId,
                acknowledgedAtUtc);
        }

        DashboardIssue? updatedIssue =
            _issues
                .ToArray()
                .FirstOrDefault(
                    candidate =>
                        candidate.Id ==
                        issueId);

        if (updatedIssue is null)
        {
            throw new DashboardIssueNotFoundException(
                issueId);
        }

        return ToResponse(
            updatedIssue,
            acknowledgedAtUtc);
    }

    /// <summary>
    /// Maps one Domain issue into its API-safe representation while
    /// calculating priority against the current evaluation time.
    /// </summary>
    private static DashboardIssueResponse ToResponse(
        DashboardIssue issue,
        DateTimeOffset evaluatedAtUtc)
    {
        return new DashboardIssueResponse(
            issue.Id,
            issue.SensorProfileId,
            issue.SensorName,
            issue.Type.ToString(),
            issue.Severity.ToString(),
            issue.State.ToString(),
            issue.Target.ToString(),
            issue.Title,
            issue.Detail,
            issue.OccurrenceCount,
            issue.CalculatePriorityScore(
                evaluatedAtUtc),
            issue.FirstObservedAtUtc,
            issue.LastObservedAtUtc,
            issue.AcknowledgedAtUtc,
            issue.ResolvedAtUtc);
    }
}