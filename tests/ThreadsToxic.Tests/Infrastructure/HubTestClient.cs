using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace ThreadsToxic.Tests.Infrastructure;

public record FocusResult(bool Granted, string? Holder);
public record MovedPayload(List<string> Ids, bool ToExcluded, string By);
public record LockInfo(string ItemId, string Holder);
public record PresenceInfo(string Name, string? FocusedItemId);

/// <summary>Một "người dùng" kết nối SignalR vào app test, ghi lại mọi sự kiện nhận được.</summary>
public sealed class HubTestClient : IAsyncDisposable
{
    public HubConnection Connection { get; }
    public string Name { get; }

    public ConcurrentQueue<(string ItemId, string? Holder)> LockChanges { get; } = new();
    public ConcurrentQueue<MovedPayload> Moved { get; } = new();
    public ConcurrentQueue<List<LockInfo>> Snapshots { get; } = new();
    public ConcurrentQueue<List<PresenceInfo>> Presence { get; } = new();

    private HubTestClient(AppFactory factory, string name)
    {
        Name = name;
        Connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/collab"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        Connection.On<string, string?>("LockChanged", (id, holder) => LockChanges.Enqueue((id, holder)));
        Connection.On<MovedPayload>("ItemsMoved", payload => Moved.Enqueue(payload));
        Connection.On<List<LockInfo>>("LockSnapshot", locks => Snapshots.Enqueue(locks));
        Connection.On<List<PresenceInfo>>("PresenceChanged", list => Presence.Enqueue(list));
    }

    public static async Task<HubTestClient> ConnectAsync(AppFactory factory, string name, bool join = true)
    {
        var client = new HubTestClient(factory, name);
        await client.Connection.StartAsync();
        if (join) await client.JoinAsync();
        return client;
    }

    public Task JoinAsync() => Connection.InvokeAsync("Join", Name);

    public Task<FocusResult> FocusAsync(string itemId) =>
        Connection.InvokeAsync<FocusResult>("Focus", itemId);

    public Task BlurAsync(string itemId) => Connection.InvokeAsync("Blur", itemId);

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

public static class Wait
{
    /// <summary>Chờ điều kiện đúng (poll 25 ms) thay vì sleep cố định.</summary>
    public static async Task UntilAsync(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail($"Hết thời gian chờ ({timeoutMs} ms): {because}");
    }
}
