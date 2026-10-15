using System.Collections;

namespace SmartX.Domain.Dashboard;

/// <summary>
/// Purpose-built collection for Adaptive Anomaly Spotlight issues.
///
/// The collection uses:
///
/// - HashSet&lt;string&gt; for authoritative, unique active-condition membership;
/// - Dictionary&lt;string, DashboardIssue&gt; for active issue details;
/// - List&lt;DashboardIssue&gt; for retained activation and resolution history.
///
/// One stable sensor-ID-plus-condition-type key is used in both indexes.
/// Membership decides first activation, repeated occurrence, active snapshots,
/// and removal on recovery. An acknowledged issue remains an active condition.
///
/// All changes to membership, issue details, and retained issue history
/// occur under one lock, preventing duplicate activation under concurrency.
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

    // Authoritative active-condition membership, not a presentation-only copy.
    // The dictionary retains the matching issue's mutable details.
    private readonly HashSet<string> _activeConditions =
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
    /// Number of distinct unresolved sensor-plus-condition keys.
    /// Acknowledgement does not reduce this count; recovery does.
    /// </summary>
    public int ActiveConditionCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _activeConditions.Count;
            }
        }
    }

    /// <summary>
    /// Constant-average-time active membership check. The stable key is
    /// validated consistently with AddOrUpdate and Resolve.
    /// </summary>
    public bool ContainsActiveCondition(
        Guid sensorProfileId,
        DashboardIssueType type)
    {
        string key = CreateIssueKey(sensorProfileId, type);

        lock (_syncRoot)
        {
            return _activeConditions.Contains(key);
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
    /// Repeated reports increment occurrence counts and permit severity
    /// escalation without creating a second activation episode.
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
            // HashSet membership governs whether this is a repeat or the
            // first activation of a distinct unresolved condition.
            if (_activeConditions.Contains(issueKey))
            {
                if (!_activeIssueIndex.TryGetValue(
                        issueKey,
                        out DashboardIssue? existingIssue))
                {
                    throw new InvalidOperationException(
                        "Active condition membership and issue details are inconsistent.");
                }

                existingIssue.RecordOccurrence(
                    sensorName,
                    severity,
                    target,
                    title,
                    detail,
                    observedAtUtc);

                return existingIssue;
            }

            if (_activeIssueIndex.ContainsKey(issueKey))
            {
                throw new InvalidOperationException(
                    "An inactive condition still has active issue details.");
            }

            // Validate the complete new issue before reserving membership.
            // Invalid reports cannot leave a phantom active condition.
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

            // The shared lock makes the three changes atomic to callers.
            // Roll back both indexes if adding the new history row fails.
            _activeIssueIndex.Add(issueKey, issue);

            try
            {
                if (!_activeConditions.Add(issueKey))
                {
                    throw new InvalidOperationException(
                        "An active condition was inserted concurrently.");
                }

                _issues.Add(issue);
            }
            catch
            {
                _activeConditions.Remove(issueKey);
                _activeIssueIndex.Remove(issueKey);
                throw;
            }

            TrimResolvedHistory();

            return issue;
        }
    }

    /// <summary>
    /// Acknowledges one retained issue.
    ///
    /// Acknowledgement is an operator action, not condition recovery.
    /// The issue remains in the HashSet until explicitly resolved.
    ///
    /// Returns false when the supplied issue ID is not retained.
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
    /// Recovery removes the stable key from both active indexes.
    /// A later recurrence therefore creates a new activation episode,
    /// while the original resolved issue may remain in bounded history.
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
            if (!_activeConditions.Contains(issueKey))
            {
                return false;
            }

            if (!_activeIssueIndex.TryGetValue(
                    issueKey,
                    out DashboardIssue? issue))
            {
                throw new InvalidOperationException(
                    "Active condition membership and issue details are inconsistent.");
            }

            // Resolve first. Invalid timestamps must never remove active
            // membership or lose the original unresolved issue.
            issue.Resolve(resolvedAtUtc);

            // Remove membership and active details inside the same lock.
            _activeConditions.Remove(issueKey);
            _activeIssueIndex.Remove(issueKey);

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
    ///
    /// Active issue selection is driven by the actual HashSet rather than
    /// scanning historical rows to infer membership.
    /// </summary>
    public IReadOnlyList<DashboardIssue> GetPrioritizedSnapshot(
        DateTimeOffset evaluatedAtUtc)
    {
        lock (_syncRoot)
        {
            DashboardIssue[] unresolved =
                _activeConditions
                    .Select(key => _activeIssueIndex[key])
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
    /// Returns only issues that still represent unresolved conditions.
    ///
    /// Reads the authoritative HashSet and resolves each member to the
    /// corresponding dictionary-backed issue details.
    ///
    /// Acknowledged issues remain present until their condition recovers.
    /// </summary>
    public IReadOnlyList<DashboardIssue> GetActiveSnapshot(
        DateTimeOffset evaluatedAtUtc)
    {
        lock (_syncRoot)
        {
            return _activeConditions
                .Select(key => _activeIssueIndex[key])
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
    /// collection lock while processing issue history.
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
    /// Keeps memory usage bounded by deleting the oldest resolved issues
    /// when the configured history limit is exceeded.
    ///
    /// Unresolved operational issues are never discarded merely to satisfy
    /// the history limit.
    ///
    /// All calls occur while the collection lock is held.
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

    /// <summary>
    /// Constructs the stable membership identity for one device condition.
    ///
    /// Guid:N gives an unambiguous canonical 32-character sensor identifier.
    /// The validated enum name separates distinct operational conditions.
    /// Ordinal comparison is used by both the HashSet and dictionary.
    /// </summary>
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