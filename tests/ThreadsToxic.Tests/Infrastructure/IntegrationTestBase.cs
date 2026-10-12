using System.Net;
using System.Net.Http.Json;

namespace ThreadsToxic.Tests.Infrastructure;

public record ListResult(int Total, int Page, int PageSize, List<ItemView> Items);
public record ItemView(string Id, string Content, int ReplyLevel, float ToxicScore, bool IsExcluded, string? ExcludedBy);
public record CountsResult(int Active, int Excluded);
public record MoveResult(int Moved, int Skipped);

/// <summary>
/// Mỗi test bắt đầu và kết thúc với database test trống.
/// Test không chạy song song (xem AssemblyInfo) nên không giẫm nhau.
/// </summary>
public abstract class IntegrationTestBase<TFactory> : IClassFixture<TFactory>, IAsyncLifetime
    where TFactory : AppFactory
{
    protected TFactory Factory { get; }
    protected HttpClient Http { get; }

    protected IntegrationTestBase(TFactory factory)
    {
        Factory = factory;
        Http = factory.CreateClient();
    }

    public virtual Task InitializeAsync() => TestDb.ResetAsync();

    public virtual async Task DisposeAsync()
    {
        await TestDb.ResetAsync();
        Http.Dispose();
    }

    protected async Task<ListResult> ListAsync(string query = "")
    {
        var response = await Http.GetAsync("/api/items" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ListResult>())!;
    }

    protected async Task<CountsResult> CountsAsync() =>
        (await Http.GetFromJsonAsync<CountsResult>("/api/items/counts"))!;

    protected async Task<(HttpStatusCode Status, MoveResult? Result)> MoveAsync(
        IEnumerable<string> ids, bool toExcluded, string userName)
    {
        var response = await Http.PostAsJsonAsync("/api/items/move",
            new { ids = ids.ToArray(), toExcluded, userName });
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
