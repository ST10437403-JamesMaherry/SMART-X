using System.Collections;

namespace SmartX.Domain.Dashboard;

/// <summary>
/// Purpose-built collection for Adaptive Anomaly Spotlight issues.
///
/// The collection uses:
///
/// - List&lt;DashboardIssue&gt; for ordered issue history;
/// - Dictionary&lt;string, DashboardIssue&gt; for efficient lookup of currently
///   active issue conditions.
///
/// Repeated observations therefore update an existing active issue rather
/// than forcing a linear scan or flooding the dashboard with duplicates.
///
/// The collection is synchronised because telemetry, status changes and other
/// API operations may report conditions concurrently.
/// </summary>
public sealed class DashboardIssueCollection
    : IReadOnlyList<DashboardIssue>
{
    private const int DefaultMaximumHistory = 250;

    private readonly object _syncRoot = new();

    private readonly List<DashboardIssue> _issues = [];

    private readonly Dictionary<string, DashboardIssue>
        _activeIssueIndex =
            new(StringComparer.Ordinal);

    private readonly int _maximumHistory;

    public DashboardIssueCollection(
        int maximumHistory = DefaultMaximumHistory)
    {
        if (maximumHistory <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumHistory),
                maximumHistory,
                "Dashboard issue history capacity must be greater than zero.");
        }

        _maximumHistory = maximumHistory;
    }

    /// <summary>
    /// Number of retained active and historical issues.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_syncRoot)
            {
                return _issues.Count;
            }
        }
    }

    /// <summary>
    /// Provides read-only access to retained issue history.
    /// </summary>
    public DashboardIssue this[int index]
    {
        get
        {
            lock (_syncRoot)
            {
                return _issues[index];
            }
        }
    }

    /// <summary>
    /// Adds a new condition or updates the existing unresolved issue for the
    /// same sensor and issue type.
    ///
    /// This is the core deduplication behaviour used to turn repeated faults
    /// into an occurrence count instead of dozens of identical dashboard
    /// rows.
    /// </summary>
    public DashboardIssue AddOrUpdate(
        Guid sensorProfileId,
        string sensorName,
        DashboardIssueType type,
        DashboardIssueSeverity severity,
        DashboardIssueTarget target,
        string title,
        string detail,
        DateTimeOffset observedAtUtc)
    {
        string issueKey =
            CreateIssueKey(
                sensorProfileId,
                type);

        lock (_syncRoot)
        {
            if (_activeIssueIndex.TryGetValue(
                    issueKey,
                    out DashboardIssue? existingIssue))
            {
                existingIssue.RecordOccurrence(
                    sensorName,
                    severity,
                    target,
                    title,
                    detail,
                    observedAtUtc);

                return existingIssue;
            }

            DashboardIssue issue =
                DashboardIssue.Create(
                    sensorProfileId,
                    sensorName,
                    type,
                    severity,
                    target,
                    title,
                    detail,
                    observedAtUtc);

            _issues.Add(issue);
            _activeIssueIndex.Add(
                issueKey,
                issue);

            TrimResolvedHistory();

            return issue;
        }
    }

    /// <summary>
    /// Acknowledges one retained issue.
    ///
    /// Returns false when the supplied ID is not present.
    /// </summary>
    public bool Acknowledge(
        Guid issueId,
        DateTimeOffset acknowledgedAtUtc)
    {
        lock (_syncRoot)
        {
            DashboardIssue? issue =
                _issues.FirstOrDefault(
                    candidate =>
                        candidate.Id == issueId);

            if (issue is null)
            {
                return false;
            }

            issue.Acknowledge(
                acknowledgedAtUtc);

            return true;
        }
    }

    /// <summary>
    /// Resolves the current issue of a specific type for a sensor.
    ///
    /// Removing it from the active index allows a future recurrence to create
    /// a fresh issue lifecycle while retaining the resolved event in history.
    /// </summary>
    public bool Resolve(
        Guid sensorProfileId,
        DashboardIssueType type,
        DateTimeOffset resolvedAtUtc)
    {
        string issueKey =
            CreateIssueKey(
                sensorProfileId,
                type);

        lock (_syncRoot)
        {
            if (!_activeIssueIndex.TryGetValue(
                    issueKey,
                    out DashboardIssue? issue))
            {
                return false;
            }

            issue.Resolve(
                resolvedAtUtc);

            _activeIssueIndex.Remove(
                issueKey);

            TrimResolvedHistory();

            return true;
        }
    }

    /// <summary>
    /// Returns issues ordered for the Adaptive Anomaly Spotlight.
    ///
    /// Active and acknowledged issues are ranked by dynamic priority score.
    /// Resolved issues follow in reverse chronological order so recent
    /// recovery remains available without competing for operator attention.
    /// </summary>
    public IReadOnlyList<DashboardIssue> GetPrioritizedSnapshot(
        DateTimeOffset evaluatedAtUtc)
    {
        lock (_syncRoot)
        {
            DashboardIssue[] unresolved =
                _issues
                    .Where(
                        issue =>
                            issue.State !=
                            DashboardIssueState.Resolved)
                    .OrderByDescending(
                        issue =>
                            issue.CalculatePriorityScore(
                                evaluatedAtUtc))
                    .ThenByDescending(
                        issue =>
                            issue.LastObservedAtUtc)
                    .ThenBy(
                        issue =>
                            issue.Id)
                    .ToArray();

            DashboardIssue[] resolved =
                _issues
                    .Where(
                        issue =>
                            issue.State ==
                            DashboardIssueState.Resolved)
                    .OrderByDescending(
                        issue =>
                            issue.ResolvedAtUtc)
                    .ThenByDescending(
                        issue =>
                            issue.LastObservedAtUtc)
                    .ToArray();

            return [
                .. unresolved,
                .. resolved
            ];
        }
    }

    /// <summary>
    /// Returns only issues that still represent an unresolved condition.
    /// </summary>
    public IReadOnlyList<DashboardIssue> GetActiveSnapshot(
        DateTimeOffset evaluatedAtUtc)
    {
        lock (_syncRoot)
        {
            return _issues
                .Where(
                    issue =>
                        issue.State !=
                        DashboardIssueState.Resolved)
                .OrderByDescending(
                    issue =>
                        issue.CalculatePriorityScore(
                            evaluatedAtUtc))
                .ThenByDescending(
                    issue =>
                        issue.LastObservedAtUtc)
                .ThenBy(
                    issue =>
                        issue.Id)
                .ToArray();
        }
    }

    /// <summary>
    /// Enumeration operates on a snapshot so callers never hold the internal
    /// collection lock while processing issue data.
    /// </summary>
    public IEnumerator<DashboardIssue> GetEnumerator()
    {
        DashboardIssue[] snapshot;

        lock (_syncRoot)
        {
            snapshot = [.. _issues];
        }

        return ((IEnumerable<DashboardIssue>)snapshot)
            .GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    /// Keeps memory usage bounded by deleting the oldest resolved issues when
    /// the configured history limit is exceeded.
    ///
    /// Unresolved operational issues are never discarded merely to satisfy
    /// the history limit.
    /// </summary>
    private void TrimResolvedHistory()
    {
        while (_issues.Count > _maximumHistory)
        {
            DashboardIssue? oldestResolvedIssue =
                _issues
                    .Where(
                        issue =>
                            issue.State ==
                            DashboardIssueState.Resolved)
                    .OrderBy(
                        issue =>
                            issue.ResolvedAtUtc)
                    .ThenBy(
                        issue =>
                            issue.FirstObservedAtUtc)
                    .FirstOrDefault();

            if (oldestResolvedIssue is null)
            {
                return;
            }

            _issues.Remove(
                oldestResolvedIssue);
        }
    }

    private static string CreateIssueKey(
        Guid sensorProfileId,
        DashboardIssueType type)
    {
        if (sensorProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "A dashboard issue must identify a sensor.",
                nameof(sensorProfileId));
        }

        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type),
                type,
                "Unsupported dashboard issue type.");
        }

        return $"{sensorProfileId:N}:{type}";
    }
}