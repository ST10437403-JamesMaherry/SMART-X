using SmartX.Application.Dashboard;
using SmartX.Contracts.Dashboard;
using SmartX.Contracts.Realtime;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Realtime;

/// <summary>
/// Read-only device discovery over existing, authoritative Smart-X sources.
///
/// - Exact physical-identifier lookup uses P2-02's canonical Dictionary path.
/// - Text, category and status filtering scan the existing ordered snapshot.
/// - Active conditions come from P2-06's HashSet-backed dashboard snapshot.
/// - Neither searching nor HTTP refreshes create operator-interaction events.
///
/// This service owns no device registry, search index or alert collection.
/// </summary>
public sealed class RealtimeDeviceSearchService
{
    private readonly RealtimeSnapshotService _snapshots;
    private readonly DashboardIssueService _issues;
    private readonly TimeProvider _timeProvider;

    public RealtimeDeviceSearchService(
        RealtimeSnapshotService snapshots,
        DashboardIssueService issues,
        TimeProvider timeProvider)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _issues = issues ?? throw new ArgumentNullException(nameof(issues));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<RealtimeDeviceSearchResponse> SearchAsync(
        RealtimeDeviceSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        // Reject invalid combinations before performing any repository reads.
        ValidatedRealtimeDeviceSearchQuery criteria = query.Validate();

        IReadOnlyList<RealtimeDeviceResponse> candidates;

        if (criteria.Identifier is not null)
        {
            // This branch calls the original single-device Dictionary lookup.
            // Do not replace it with GetDevicesAsync().Where(...).
            try
            {
                RealtimeDeviceResponse device =
                    await _snapshots.GetByDeviceIdentifierAsync(
                        criteria.Identifier.Value,
                        criteria.Identifier.Kind.ToString(),
                        cancellationToken);

                candidates = Array.AsReadOnly(new[] { device });
            }
            catch (RealtimeDeviceNotFoundException)
            {
                // Search semantics: a valid identifier with no matches is an
                // empty result page. The legacy GET /devices route still 404s.
                candidates = Array.Empty<RealtimeDeviceResponse>();
            }
        }
        else
        {
            // P2-02 already returns deterministic registration order.
            // No persistent inverted index or search engine is introduced.
            candidates = await _snapshots.GetDevicesAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // GetSnapshot(false) reads only unresolved P2-06 HashSet membership,
        // including acknowledged episodes; it does not create fresh issues.
        DashboardSnapshotResponse activeSnapshot = _issues.GetSnapshot();
        ILookup<Guid, DashboardIssueResponse> issuesBySensor =
            activeSnapshot.Issues.ToLookup(issue => issue.SensorProfileId);

        var matches = new List<RealtimeDeviceSearchItemResponse>();

        foreach (RealtimeDeviceResponse device in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!MatchesBasicFilters(device, criteria))
            {
                continue;
            }

            DashboardIssueResponse[] activeIssues =
                issuesBySensor[device.Id].ToArray();

            bool hasActive = activeIssues.Length > 0;

            if (criteria.HasActiveCondition is bool requiredActive &&
                hasActive != requiredActive)
            {
                continue;
            }

            if (criteria.ConditionType is not null &&
                !activeIssues.Any(issue => issue.Type == criteria.ConditionType.ToString()))
            {
                continue;
            }

            // DashboardSnapshotResponse is already priority ordered; using
            // its first issue preserves the existing research-backed ranking.
            DashboardIssueResponse? topIssue = activeIssues.FirstOrDefault();

            matches.Add(new RealtimeDeviceSearchItemResponse(
                device,
                activeIssues.Length,
                topIssue?.Id,
                topIssue?.Type));
        }

        int total = matches.Count;

        RealtimeDeviceSearchItemResponse[] page = matches
            .Skip(criteria.Offset)
            .Take(criteria.Limit)
            .ToArray();

        return new RealtimeDeviceSearchResponse(
            _timeProvider.GetUtcNow().ToUniversalTime(),
            total,
            criteria.Limit,
            criteria.Offset,
            // Avoid integer overflow when offset is large.
            criteria.Offset < total - page.Length,
            new RealtimeDeviceSearchAppliedFiltersResponse(
                criteria.Identifier?.Value,
                criteria.Identifier?.Kind.ToString(),
                criteria.Text,
                criteria.Category?.ToString(),
                criteria.Status?.ToString(),
                criteria.HasActiveCondition,
                criteria.ConditionType?.ToString()),
            Array.AsReadOnly(page));
    }

    private static bool MatchesBasicFilters(
        RealtimeDeviceResponse device,
        ValidatedRealtimeDeviceSearchQuery criteria)
    {
        if (criteria.Category is SensorCategory category &&
            !device.Category.Equals(category.ToString(), StringComparison.Ordinal))
        {
            return false;
        }

        if (criteria.Status is SensorStatus status &&
            !device.Status.Equals(status.ToString(), StringComparison.Ordinal))
        {
            return false;
        }

        if (criteria.Text is not string text)
        {
            return true;
        }

        // Case-insensitive text discovery; no artificial fuzzy-matching
        // algorithm. An explicit identifier filter remains exact and distinct.
        return device.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               device.DeviceIdentifier.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               device.DeploymentLocation.Facility.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               device.DeploymentLocation.Zone.Contains(text, StringComparison.OrdinalIgnoreCase) ||
               device.DeploymentLocation.NodeId.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}