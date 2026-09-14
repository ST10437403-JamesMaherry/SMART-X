using SmartX.Contracts.Dashboard;

namespace SmartX.Api.Dashboard;

/// <summary>
/// Strongly typed SignalR contract implemented by connected dashboard
/// clients.
///
/// The server publishes lightweight invalidation events rather than mutable
/// issue collections. Clients then refresh their authoritative HTTP snapshot.
/// </summary>
public interface IDashboardClient
{
    Task DashboardChanged(
        DashboardChangedResponse change);
}