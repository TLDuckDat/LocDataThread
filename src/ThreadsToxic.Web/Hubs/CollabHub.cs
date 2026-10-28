using Microsoft.AspNetCore.SignalR;
using ThreadsToxic.Web.Services;

namespace ThreadsToxic.Web.Hubs;

public record FocusResult(bool Granted, string? Holder);

// Token phiên do server cấp; client gửi lại trong header X-Session-Token khi gọi API ghi.
// Name/Role lấy từ mã truy cập (cấu hình Auth:Users), không do client tự khai.
public record JoinResult(string Token, string Name, string Role);

public class CollabHub(CollabState state, AccessTokens accessTokens) : Hub
{
    public const string Room = "filter-room";

    // accessToken: mã truy cập vĩnh viễn của người dùng. Sai mã thì không vào được phòng.
    public async Task<JoinResult> Join(string accessToken)
    {
        var user = accessTokens.Resolve(accessToken)
            ?? throw new HubException("Mã truy cập không đúng.");

        state.SetUser(Context.ConnectionId, user);
        var token = state.IssueToken(Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, Room);
        await BroadcastPresence();

        // Người vừa vào cần biết bình luận nào đang bị giữ
        var leases = state.GetLeases(DateTimeOffset.UtcNow)
            .Select(l => new { itemId = l.ItemId, holder = l.Holder });
        await Clients.Caller.SendAsync("LockSnapshot", leases);

        return new JoinResult(token, user.Name, user.Role);
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
