namespace GovernanceCouncil.Web.Hubs;

using Microsoft.AspNetCore.SignalR;

/// <summary>
/// SignalR hub for streaming live deliberation progress to connected clients.
/// Agents publish their output in real-time as the deliberation progresses; clients join a
/// per-deliberation group to receive those updates.
/// </summary>
public sealed class DeliberationHub : Hub
{
    /// <summary>
    /// Joins a deliberation room to receive live updates for a specific deliberation.
    /// </summary>
    public async Task JoinDeliberation(string deliberationId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, deliberationId);
    }

    /// <summary>
    /// Leaves a deliberation room.
    /// </summary>
    public async Task LeaveDeliberation(string deliberationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, deliberationId);
    }
}
