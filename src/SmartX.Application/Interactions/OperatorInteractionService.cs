using SmartX.Application.Dashboard;
using SmartX.Application.Realtime;
using SmartX.Contracts.Dashboard;
using SmartX.Contracts.Realtime;
using SmartX.Domain.Interactions;

namespace SmartX.Application.Interactions;

/// <summary>
/// Validates intentional browser actions against authoritative registry/alert
/// state. No GET request, keystroke, or client-claimed command success is logged.
/// </summary>
public sealed class OperatorInteractionService
{
    private readonly IOperatorInteractionStore _store;
    private readonly RealtimeDeviceSearchService _search;
    private readonly RealtimeSnapshotService _devices;
    private readonly DashboardIssueService _issues;

    public OperatorInteractionService(IOperatorInteractionStore store,
        RealtimeDeviceSearchService search, RealtimeSnapshotService devices,
        DashboardIssueService issues)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _issues = issues ?? throw new ArgumentNullException(nameof(issues));
    }

    public async Task<OperatorInteraction> RecordSearchAsync(RecordDeviceSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        OperatorInteractionKind kind = request.Kind switch
        {
            "SearchSubmitted" => OperatorInteractionKind.SearchSubmitted,
            "FiltersChanged" => OperatorInteractionKind.FiltersChanged,
            _ => throw new InvalidOperatorInteractionException(
                "Search interaction kind must be SearchSubmitted or FiltersChanged.")
        };

        // Re-run the validated read-only query on the server; never trust a
        // client-reported result count or normalized identifier.
        var query = new RealtimeDeviceSearchQuery(request.DeviceIdentifier,
            request.IdentifierKind, request.Text, request.Category, request.Status,
            request.HasActiveCondition, request.ConditionType, request.Limit, request.Offset);
        RealtimeDeviceSearchResponse result = await _search.SearchAsync(query, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.SelectedDeviceId.HasValue &&
            !result.Devices.Any(x => x.Device.Id == request.SelectedDeviceId.Value))
        {
            throw new InvalidOperatorInteractionException(
                "Selected device must belong to the submitted result page.");
        }

        if (request.SourceAlertEpisodeId.HasValue)
        {
            if (!request.SelectedDeviceId.HasValue)
                throw new InvalidOperatorInteractionException(
                    "A searched actuator must be selected to associate the source alert.");
            ValidateActiveAlert(request.SourceAlertEpisodeId.Value, null);
        }

        var filters = result.AppliedFilters;
        return _store.Append(kind, request.SelectedDeviceId, request.SourceAlertEpisodeId,
            searchText: filters.Text, canonicalIdentifier: filters.DeviceIdentifier,
            category: filters.Category, status: filters.Status,
            hasActiveCondition: filters.HasActiveCondition,
            conditionType: filters.ConditionType, resultCount: result.TotalMatches);
    }

    public async Task<OperatorInteraction> RecordViewAsync(RecordOperatorViewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DeviceId is not Guid deviceId || deviceId == Guid.Empty)
            throw new InvalidOperatorInteractionException("A valid deviceId is required.");

        // This is a read for validation, not evidence of a view by itself.
        var devices = await _devices.GetDevicesAsync(cancellationToken);
        if (!devices.Any(x => x.Id == deviceId))
            throw new InvalidOperatorInteractionException("The viewed device is not registered.");

        OperatorInteractionKind kind = OperatorInteractionKind.DeviceViewed;
        if (request.AlertEpisodeId.HasValue)
        {
            ValidateActiveAlert(request.AlertEpisodeId.Value, deviceId);
            kind = OperatorInteractionKind.AlertViewed;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return _store.Append(kind, deviceId, request.AlertEpisodeId);
    }

    private void ValidateActiveAlert(Guid issueId, Guid? deviceId)
    {
        DashboardIssueResponse? match = _issues.GetSnapshot().Issues.FirstOrDefault(
            issue => issue.Id == issueId &&
                (!deviceId.HasValue || issue.SensorProfileId == deviceId.Value));
        if (match is null)
            throw new InvalidOperatorInteractionException(
                "The alert episode is not currently active for this device.");
    }

    public OperatorInteractionHistoryResponse GetHistory(int limit = 50)
    {
        if (limit is < 1 or > 200)
            throw new InvalidOperatorInteractionException("limit must be between 1 and 200.");
        OperatorInteractionSnapshot snapshot = _store.Read(limit);
        return new OperatorInteractionHistoryResponse(snapshot.TotalRecorded,
            snapshot.RetainedCount, snapshot.Capacity,
            snapshot.Events.Select(ToResponse).ToArray());
    }

    public static OperatorInteractionResponse ToResponse(OperatorInteraction item) =>
        new(item.Sequence, item.OccurredAtUtc, item.Kind.ToString(), item.DeviceId,
            item.AlertEpisodeId, item.CommandId, item.SearchText, item.CanonicalIdentifier,
            item.Category, item.Status, item.HasActiveCondition, item.ConditionType, item.ResultCount);
}