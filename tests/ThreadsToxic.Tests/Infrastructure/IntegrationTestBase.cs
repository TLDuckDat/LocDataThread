using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

namespace ThreadsToxic.Tests.Infrastructure;

public record ListResult(int Total, int Page, int PageSize, List<ItemView> Items);
public record ItemView(string Id, string Content, int ReplyLevel, float ToxicScore, bool IsExcluded, string? ExcludedBy);
public record CountsResult(int Active, int Excluded);
public record MoveResult(int Moved, int Skipped, List<string>? Locked = null, List<string>? MovedIds = null);
public record UndoResult(int Undone, List<string> UndoneIds, List<string> Conflicts, int Skipped, List<string> Locked);

/// <summary>
/// Mỗi test bắt đầu và kết thúc với database test trống.
/// Test không chạy song song (xem AssemblyInfo) nên không giẫm nhau.
/// </summary>
public abstract class IntegrationTestBase<TFactory> : IClassFixture<TFactory>, IAsyncLifetime
    where TFactory : AppFactory
{
    protected TFactory Factory { get; }
    protected HttpClient Http { get; }

    // Mỗi tên người dùng dùng trong MoveAsync có một kết nối SignalR thật (đã Join) để có token phiên.
    private readonly ConcurrentDictionary<string, Lazy<Task<HubTestClient>>> _actors = new();

    protected IntegrationTestBase(TFactory factory)
    {
        Factory = factory;
        Http = factory.CreateClient();
    }

    public virtual Task InitializeAsync() => TestDb.ResetAsync();

    public virtual async Task DisposeAsync()
    {
        foreach (var actor in _actors.Values)
            await (await actor.Value).DisposeAsync();

        await TestDb.ResetAsync();
        Http.Dispose();
    }

    /// <summary>Kết nối + Join một lần cho mỗi tên; dùng chung cho các request của người đó.</summary>
    protected Task<HubTestClient> ActorAsync(string userName) =>
        _actors.GetOrAdd(userName,
            name => new Lazy<Task<HubTestClient>>(() => HubTestClient.ConnectAsync(Factory, name))).Value;

    protected async Task<ListResult> ListAsync(string query = "")
    {
        var response = await Http.GetAsync("/api/items" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ListResult>())!;
    }

    protected async Task<CountsResult> CountsAsync() =>
        (await Http.GetFromJsonAsync<CountsResult>("/api/items/counts"))!;

    /// <summary>Gọi move với tư cách <paramref name="userName"/> (tự Join để lấy token phiên).</summary>
    protected async Task<(HttpStatusCode Status, MoveResult? Result)> MoveAsync(
        IEnumerable<string> ids, bool toExcluded, string userName, bool force = false)
    {
        var actor = await ActorAsync(userName);
        return await MoveWithTokenAsync(actor.Token, new { ids = ids.ToArray(), toExcluded, force });
    }

    /// <summary>Hoàn tác với tư cách <paramref name="userName"/>; toExcluded = trạng thái muốn đưa về.</summary>
    protected async Task<(HttpStatusCode Status, UndoResult? Result)> UndoAsync(
        IEnumerable<string> ids, bool toExcluded, string userName)
    {
        var actor = await ActorAsync(userName);
        return await UndoWithTokenAsync(actor.Token, new { ids = ids.ToArray(), toExcluded });
    }

    protected async Task<(HttpStatusCode Status, UndoResult? Result)> UndoWithTokenAsync(
        string? token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/items/undo")
        {
            Content = JsonContent.Create(body),
        };
        if (token is not null) request.Headers.Add("X-Session-Token", token);

        var response = await Http.SendAsync(request);
        var result = response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<UndoResult>()
            : null;
        return (response.StatusCode, result);
    }

    /// <summary>Gọi move với token và body tùy ý (dùng cho test bảo mật).</summary>
    protected async Task<(HttpStatusCode Status, MoveResult? Result)> MoveWithTokenAsync(
        string? token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/items/move")
        {
            Content = JsonContent.Create(body),
        };
        if (token is not null) request.Headers.Add("X-Session-Token", token);

        var response = await Http.SendAsync(request);
        var result = response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<MoveResult>()
            : null;
        return (response.StatusCode, result);
    }
}

public abstract class IntegrationTestBase : IntegrationTestBase<AppFactory>
{
    protected IntegrationTestBase(AppFactory factory) : base(factory) { }
}
