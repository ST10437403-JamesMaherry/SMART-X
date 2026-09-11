namespace SmartX.Contracts.Dashboard;

/// <summary>
/// Lightweight SignalR invalidation event.
///
/// The event intentionally does not contain the complete dashboard state.
/// Connected clients receive the notification and then retrieve the
/// authoritative snapshot through the normal HTTP API.
/// </summary>
public sealed record DashboardChangedResponse(
    string Reason,
    Guid? SensorProfileId,
    DateTimeOffset OccurredAtUtc);