using Microsoft.AspNetCore.SignalR;

namespace SmartX.Api.Realtime;

/// <summary>Invalidations only; state and simulation controls use HTTP.</summary>
public sealed class RealtimeHub : Hub
{
}