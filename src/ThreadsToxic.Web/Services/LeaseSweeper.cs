using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using ThreadsToxic.Web.Hubs;

namespace ThreadsToxic.Web.Services;

// Mở khóa các bình luận có lease đã hết hạn (người xem quên/mất mạng) và báo cho cả phòng.
public class LeaseSweeper(CollabState state, IHubContext<CollabHub> hub, IOptions<CollabOptions> options)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Sweep);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var itemId in state.ExpireLeases(DateTimeOffset.UtcNow))
                await hub.Clients.Group(CollabHub.Room).SendAsync("LockChanged", itemId, null, stoppingToken);
        }
    }
}
