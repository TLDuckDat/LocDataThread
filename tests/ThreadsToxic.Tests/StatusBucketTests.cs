using System.Net;
using System.Net.Http.Json;
using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public record StatusCountsView(int Active, int Excluded, int Todo, int Kept);
public record UnreviewView(int Reset, List<string> ResetIds, int Skipped);

/// <summary>Ba mục rõ ràng: Chưa lọc (todo), Đã giữ (kept), Đã bỏ (excluded); và đưa bình luận về Chưa lọc.</summary>
public class StatusBucketTests(AppFactory factory) : IntegrationTestBase(factory)
{
    private async Task<(HttpStatusCode Status, T? Body)> PostAsync<T>(string path, string userName, object body)
    {
        var actor = await ActorAsync(userName);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Session-Token", actor.Token);
        var response = await Http.SendAsync(request);
        var result = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>() : default;
        return (response.StatusCode, result);
    }

    private async Task<StatusCountsView> StatusCountsAsync() =>
        (await Http.GetFromJsonAsync<StatusCountsView>("/api/items/counts"))!;

    [Fact]
    public async Task Each_comment_is_in_exactly_one_of_todo_kept_excluded()
    {
        await TestDb.SeedAsync(new SeedRow("t1"), new SeedRow("k1"), new SeedRow("k2"), new SeedRow("x1", Excluded: true));
        await PostAsync<object>("/api/items/approve", "Lan", new { ids = new[] { "k1", "k2" } });

        Assert.Equal(new StatusCountsView(Active: 3, Excluded: 1, Todo: 1, Kept: 2), await StatusCountsAsync());
        Assert.Equal(["t1"], (await ListAsync("?bucket=todo")).Items.Select(i => i.Id));
        Assert.Equal(["k1", "k2"], (await ListAsync("?bucket=kept")).Items.Select(i => i.Id).Order());
        Assert.Equal(["x1"], (await ListAsync("?bucket=excluded")).Items.Select(i => i.Id));
        // "active" (mặc định) vẫn là todo + kept cho client cũ
        Assert.Equal(3, (await ListAsync()).Total);
    }

    [Fact]
    public async Task Excluding_a_kept_comment_moves_it_to_excluded()
    {
        await TestDb.SeedAsync(new SeedRow("a"));
        await PostAsync<object>("/api/items/approve", "Lan", new { ids = new[] { "a" } });

        await MoveAsync(["a"], toExcluded: true, "Huy");

        Assert.Equal(new StatusCountsView(Active: 0, Excluded: 1, Todo: 0, Kept: 0), await StatusCountsAsync());
    }

    [Fact]
    public async Task Undoing_an_exclude_returns_each_comment_to_its_previous_status()
    {
        // t: chưa lọc; k: Huy đã giữ; child: trả lời của t (bị bỏ theo cha), Thu đã giữ
        await TestDb.SeedAsync(new SeedRow("t"), new SeedRow("k"), new SeedRow("child", ReplyLevel: 1, Parent: "t"));
        await PostAsync<object>("/api/items/approve", "Huy", new { ids = new[] { "k" } });
        await PostAsync<object>("/api/items/approve", "Thu", new { ids = new[] { "child" } });

        var (_, moved) = await MoveAsync(["t", "k"], toExcluded: true, "Lan");
        Assert.Equal(["child"], moved!.Cascaded);
        await UndoAsync(moved.MovedIds!, toExcluded: false, "Lan");

        Assert.Equal(["t"], (await ListAsync("?bucket=todo")).Items.Select(i => i.Id));
        Assert.Equal(["child", "k"], (await ListAsync("?bucket=kept")).Items.Select(i => i.Id).Order());
        Assert.Equal("Huy", await TestDb.QueryScalarAsync("SELECT reviewed_by FROM comments WHERE id = 'k'"));
        Assert.Equal("Thu", await TestDb.QueryScalarAsync("SELECT reviewed_by FROM comments WHERE id = 'child'"));
    }

    [Fact]
    public async Task Manual_restore_moves_comment_to_kept()
    {
        await TestDb.SeedAsync(new SeedRow("a"));
        await MoveAsync(["a"], toExcluded: true, "Lan");

        await MoveAsync(["a"], toExcluded: false, "Huy");

        Assert.Equal(["a"], (await ListAsync("?bucket=kept")).Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Unreview_returns_own_kept_comment_to_todo_and_is_audited()
    {
        await TestDb.SeedAsync(new SeedRow("a"));
        await PostAsync<object>("/api/items/approve", "Lan", new { ids = new[] { "a" } });

        var (status, result) = await PostAsync<UnreviewView>("/api/items/unreview", "Lan", new { ids = new[] { "a" } });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["a"], result!.ResetIds);
        Assert.Equal(["a"], (await ListAsync("?bucket=todo")).Items.Select(i => i.Id));
        Assert.Equal("unreview", await TestDb.QueryScalarAsync(
            "SELECT action FROM moderation_audits WHERE comment_id = 'a' ORDER BY id DESC LIMIT 1"));
    }

    [Fact]
    public async Task Member_cannot_unreview_someone_elses_work_but_admin_can()
    {
        await TestDb.SeedAsync(new SeedRow("a"));
        await PostAsync<object>("/api/items/approve", "Lan", new { ids = new[] { "a" } });

        var (_, byHuy) = await PostAsync<UnreviewView>("/api/items/unreview", "Huy", new { ids = new[] { "a" } });
        Assert.Equal(0, byHuy!.Reset);
        Assert.Equal(1, byHuy.Skipped);
        Assert.Equal(1, (await StatusCountsAsync()).Kept);

        var (_, byAdmin) = await PostAsync<UnreviewView>("/api/items/unreview", AppFactory.AdminName, new { ids = new[] { "a" } });
        Assert.Equal(1, byAdmin!.Reset);
        Assert.Equal(1, (await StatusCountsAsync()).Todo);
    }

    [Fact]
    public async Task Unreview_ignores_excluded_comments()
    {
        await TestDb.SeedAsync(new SeedRow("a"));
        await MoveAsync(["a"], toExcluded: true, "Lan");

        var (_, result) = await PostAsync<UnreviewView>("/api/items/unreview", "Lan", new { ids = new[] { "a" } });

        Assert.Equal(0, result!.Reset);
        Assert.Equal(1, (await StatusCountsAsync()).Excluded);
    }
}
