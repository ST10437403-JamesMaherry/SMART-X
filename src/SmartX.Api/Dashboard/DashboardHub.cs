using Microsoft.AspNetCore.SignalR;

namespace SmartX.Api.Dashboard;

/// <summary>
/// SignalR endpoint used to push Adaptive Anomaly Spotlight change
/// notifications to connected React clients.
///
/// Part 1 clients do not invoke commands through this hub. All state-changing
/// operations continue to use the normal HTTP API, keeping one authoritative
/// command path.
/// </summary>
public sealed class DashboardHub
    : Hub<IDashboardClient>
{
}