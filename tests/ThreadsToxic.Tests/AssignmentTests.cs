using System.Net;
using System.Net.Http.Json;
using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public record LoadView(string Name, int Assigned, int Done, int Remaining);
public record SummaryView(int UnassignedRemaining, int LongLeaves, List<LoadView> People);
public record AssignedItemView(string Id, string? AssignedTo, int ReplyLevel);
public record AssignedListView(int Total, List<AssignedItemView> Items);

/// <summary>Chia việc theo bài viết (migration 008): cả bài về một người, cân tải, chuyển phần chưa làm.</summary>
public class AssignmentTests(AppFactory factory) : IntegrationTestBase(factory)
{
    private async Task<(HttpStatusCode Status, T? Body)> AdminPostAsync<T>(string path, object body, string user = "Minh")
    {
        var actor = await ActorAsync(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Session-Token", actor.Token);
        var response = await Http.SendAsync(request);
        var result = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>() : default;
        return (response.StatusCode, result);
    }

    private Task<(HttpStatusCode Status, SummaryView? Body)> SplitAsync(params string[] members) =>
        AdminPostAsync<SummaryView>("/api/admin/assign/split", new { members });

    private static Task<object?> AssigneeAsync(string id) =>
        TestDb.QueryScalarAsync("SELECT assigned_to FROM comments WHERE id = @id", ("id", id));

    private static async Task SeedPostAsync(string postId, int comments)
    {
        await TestDb.SeedAsync(Enumerable.Range(1, comments)
            .Select(i => new SeedRow($"{postId}_{i}", $"Bình luận {i} của {postId}", PostId: postId))
            .ToArray());
    }

    [Fact]
    public async Task Split_gives_each_post_to_one_person_and_balances_the_load()
    {
        await SeedPostAsync("p1", 5);
        await SeedPostAsync("p2", 3);
        await SeedPostAsync("p3", 2);

        var (status, summary) = await SplitAsync("Lan", "Huy");

        Assert.Equal(HttpStatusCode.OK, status);
        // Bài lớn trước: p1 (5) -> Lan, p2 (3) -> Huy, p3 (2) -> Huy (đang ít việc hơn) => 5 / 5
        Assert.Equal(5, summary!.People.Single(p => p.Name == "Lan").Remaining);
        Assert.Equal(5, summary.People.Single(p => p.Name == "Huy").Remaining);
        Assert.Equal(0, summary.UnassignedRemaining);
        foreach (var post in new[] { "p1", "p2", "p3" })
        {
            Assert.Equal(1, await TestDb.CountAsync(
                $"SELECT COUNT(DISTINCT assigned_to) FROM comments WHERE post_id = '{post}'"));
        }
    }

    [Fact]
    public async Task Whole_thread_goes_to_the_same_person()
    {
        await TestDb.SeedThreadAsync("a", postId: "pa");
        await TestDb.SeedThreadAsync("b", postId: "pb");

        await SplitAsync("Lan", "Huy");

        Assert.Equal(1, await TestDb.CountAsync("SELECT COUNT(DISTINCT assigned_to) FROM comments WHERE post_id = 'pa'"));
        Assert.NotEqual(await AssigneeAsync("a0"), await AssigneeAsync("b0"));
    }

    [Fact]
    public async Task Long_comment_without_replies_is_not_assigned_but_a_long_parent_is()
    {
        var longText = string.Concat(Enumerable.Repeat("dài quá ", 50)); // > 300 ký tự
        await TestDb.SeedAsync(
            new SeedRow("long_leaf", longText, PostId: "p1"),
            new SeedRow("long_parent", longText + "cha", PostId: "p1"),
            new SeedRow("reply", "trả lời", ReplyLevel: 1, Parent: "long_parent", PostId: "p1"));

        var (_, summary) = await SplitAsync("Lan");

        Assert.Null(await AssigneeAsync("long_leaf"));
        Assert.Equal("Lan", await AssigneeAsync("long_parent"));
        Assert.Equal("Lan", await AssigneeAsync("reply"));
        Assert.Equal(1, summary!.LongLeaves);
    }

    [Fact]
    public async Task Already_handled_comments_are_not_assigned()
    {
        await TestDb.SeedAsync(new SeedRow("done", Excluded: true), new SeedRow("todo"));

        await SplitAsync("Lan");

        Assert.Null(await AssigneeAsync("done"));
        Assert.Equal("Lan", await AssigneeAsync("todo"));
    }

    [Fact]
    public async Task New_comments_of_an_assigned_post_go_to_the_same_person()
    {
        await SeedPostAsync("p1", 3);
        await SeedPostAsync("p2", 1);
        await SplitAsync("Lan", "Huy");
        var owner = await AssigneeAsync("p1_1");

        await TestDb.SeedAsync(new SeedRow("p1_new", "bình luận mới của p1", PostId: "p1"));
        await SplitAsync("Lan", "Huy");

        Assert.Equal(owner, await AssigneeAsync("p1_new"));
    }

    [Fact]
    public async Task Split_defaults_to_all_members_and_rejects_unknown_names()
    {
        await SeedPostAsync("p1", 1);

        var (bad, _) = await SplitAsync("Lan", "NgườiLạ");
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Null(await AssigneeAsync("p1_1"));

        var (ok, _) = await SplitAsync();
        Assert.Equal(HttpStatusCode.OK, ok);
        var assignee = (string?)await AssigneeAsync("p1_1");
        Assert.Contains(assignee, AppFactory.MemberNames);   // admin không tự nhận việc khi không chỉ định
    }

    [Fact]
    public async Task Members_cannot_split_or_transfer()
    {
        await SeedPostAsync("p1", 2);

        var (split, _) = await AdminPostAsync<object>("/api/admin/assign/split", new { }, user: "Lan");
        var (transfer, _) = await AdminPostAsync<object>("/api/admin/assign/transfer", new { from = "Huy", to = "Lan" }, user: "Lan");

        Assert.Equal(HttpStatusCode.Forbidden, split);
        Assert.Equal(HttpStatusCode.Forbidden, transfer);
        Assert.Equal(0, await TestDb.CountAsync("SELECT COUNT(*) FROM comments WHERE assigned_to IS NOT NULL"));
    }

    [Fact]
    public async Task Transfer_moves_only_the_unfinished_part()
    {
        await SeedPostAsync("p1", 3);
        await SplitAsync("Lan");
        await MoveAsync(["p1_1"], toExcluded: true, "Lan");   // Lan đã làm 1

        var (status, _) = await AdminPostAsync<object>("/api/admin/assign/transfer", new { from = "Lan", to = "Huy" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Lan", await AssigneeAsync("p1_1"));
        Assert.Equal("Huy", await AssigneeAsync("p1_2"));
        Assert.Equal("Huy", await AssigneeAsync("p1_3"));
    }

    [Fact]
    public async Task My_part_lists_only_my_comments_in_thread_order()
    {
        await TestDb.SeedAsync(
            new SeedRow("r", "gốc", Score: 0.1f, PostId: "pz"),
            new SeedRow("b", "trả lời B", ReplyLevel: 1, Parent: "r", Score: 0.9f, PostId: "pz"),
            new SeedRow("a", "trả lời A", ReplyLevel: 1, Parent: "r", Score: 0.2f, PostId: "pz"),
            new SeedRow("a_child", "con của A", ReplyLevel: 2, Parent: "a", Score: 0.95f, PostId: "pz"));
        await SeedPostAsync("other", 2);
        await SplitAsync("Lan", "Huy");   // pz (4) -> Lan, other (2) -> Huy
        Assert.Equal("Lan", await AssigneeAsync("r"));

        // Http mặc định dùng mã truy cập của Lan
        var mine = await Http.GetFromJsonAsync<AssignedListView>("/api/items?assigned=me&order=thread");

        // Cha đứng trước con, nhánh A (cùng con của nó) liền nhau, không theo điểm
        Assert.Equal(["r", "a", "a_child", "b"], mine!.Items.Select(i => i.Id));
        var none = await Http.GetFromJsonAsync<AssignedListView>("/api/items?assigned=none");
        Assert.Equal(0, none!.Total);
    }

    [Fact]
    public async Task Summary_is_visible_to_everyone()
    {
        await SeedPostAsync("p1", 2);
        await SplitAsync("Lan");

        var summary = await Http.GetFromJsonAsync<SummaryView>("/api/items/assignments");

        var lan = summary!.People.Single(p => p.Name == "Lan");
        Assert.Equal((2, 0, 2), (lan.Assigned, lan.Done, lan.Remaining));
        Assert.Contains(summary.People, p => p.Name == "Thu" && p.Assigned == 0);
    }
}
