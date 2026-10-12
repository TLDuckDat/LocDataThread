using Microsoft.AspNetCore.SignalR;
using ThreadsToxic.Web.Services;

namespace ThreadsToxic.Web.Hubs;

public record FocusResult(bool Granted, string? Holder);

public class CollabHub(CollabState state) : Hub
{
    public const string Room = "filter-room";

    public async Task Join(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length is 0 or > 40)
            throw new HubException("Tên phải từ 1 đến 40 ký tự.");

        state.SetUser(Context.ConnectionId, name);
        await Groups.AddToGroupAsync(Context.ConnectionId, Room);
        await BroadcastPresence();

        // Người vừa vào cần biết bình luận nào đang bị giữ
        var leases = state.GetLeases(DateTimeOffset.UtcNow)
            .Select(l => new { itemId = l.ItemId, holder = l.Holder });
        await Clients.Caller.SendAsync("LockSnapshot", leases);
    }

    // Giữ bình luận đang xem (lease 45 giây). Người khác sẽ thấy ổ khóa kèm tên.
    public async Task<FocusResult> Focus(string itemId)
    {
        var (granted, holder) = state.TryLock(itemId, Context.ConnectionId, DateTimeOffset.UtcNow);
        if (granted)
        {
            await Clients.Group(Room).SendAsync("LockChanged", itemId, holder);
            await BroadcastPresence();
        }
        return new FocusResult(granted, holder);
    }

    public async Task Blur(string itemId)
    {
        if (state.Unlock(itemId, Context.ConnectionId))
        {
            await Clients.Group(Room).SendAsync("LockChanged", itemId, null);
            await BroadcastPresence();
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var itemId in state.RemoveConnection(Context.ConnectionId))
            await Clients.Group(Room).SendAsync("LockChanged", itemId, null);

        await BroadcastPresence();
        await base.OnDisconnectedAsync(exception);
    }

    private Task BroadcastPresence() =>
        Clients.Group(Room).SendAsync("PresenceChanged", state.GetPresence());
}
