using System.Net;
using System.Net.Http.Json;
using Npgsql;
using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public record QueueItemView(string Id, string Decision, string? DecidedBy, bool ExistsInMain, string Reason);
public record QueueCountsView(int Pending, int PendingInMain, int Promoted, int Rejected);
public record QueueListView(int Total, List<QueueItemView> Items, QueueCountsView Counts);
public record QueueActionView(int Promoted, int PromotedComments, int PromotedPosts, int Rejected, int Restored,
    int SkippedInMain, List<string> Changed);
public record ReviewerView(string Name, int Count);
public record ProgressView(int Active, int Excluded, int Reviewed, int ReviewedToday,
    List<ReviewerView> ByReviewer, List<ReviewerView> TodayByReviewer);

/// <summary>Kho phụ: đưa vào CSDL chính, loại, hoàn tác, hàng loạt; và ghi nhận người xử lý.</summary>
public class ReviewQueueTests(AppFactory factory) : IntegrationTestBase(factory)
{
    private async Task<(HttpStatusCode Status, T? Body)> PostAsync<T>(string path, string userName, object body)
    {
        var actor = await ActorAsync(userName);
        return await PostWithTokenAsync<T>(path, actor.Token, body);
    }

    private async Task<(HttpStatusCode Status, T? Body)> PostWithTokenAsync<T>(string path, string? token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (token is not null) request.Headers.Add("X-Session-Token", token);
        var response = await Http.SendAsync(request);
        var result = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>() : default;
        return (response.StatusCode, result);
    }

    private async Task<QueueListView> QueueAsync(string query = "")
    {
        var response = await Http.GetAsync("/api/review" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<QueueListView>())!;
    }

    private static async Task<string?> DecisionAsync(string id) =>
        (string?)await TestDb.QueryScalarAsync("SELECT decision FROM review_queue WHERE id = @id", ("id", id));

    [Fact]
    public async Task Promote_puts_comment_into_active_list_with_reason_as_source()
    {
        await TestDb.SeedQueueAsync("q1", "duplicate_content", "Bình luận hay đấy nhé");

        var (status, result) = await PostAsync<QueueActionView>("/api/review/promote", "Minh", new { ids = new[] { "q1" } });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, result!.PromotedComments);
        Assert.Equal("promoted", await DecisionAsync("q1"));
        Assert.False((bool)(await TestDb.QueryScalarAsync("SELECT is_excluded FROM comments WHERE id = 'q1'"))!);
        Assert.Equal("duplicate_content", await TestDb.QueryScalarAsync("SELECT source FROM comments WHERE id = 'q1'"));
        Assert.Equal("Minh", await TestDb.QueryScalarAsync("SELECT decided_by FROM review_queue WHERE id = 'q1'"));
        Assert.Equal("Minh", await TestDb.QueryScalarAsync("SELECT user_name FROM moderation_audits WHERE comment_id = 'q1' AND action = 'promote'"));
    }

    [Fact]
    public async Task Promote_skips_rows_already_in_main_and_marks_late_conflicts()
    {
        await TestDb.SeedAsync(new SeedRow("m1", "Đã có sẵn trong chính"));
        await TestDb.SeedQueueAsync("m1", "duplicate_id", "Đã có sẵn trong chính", existsInMain: true);
        await TestDb.SeedQueueAsync("late", "duplicate_id", "Vừa có trong chính");
        await TestDb.SeedAsync(new SeedRow("late", "Vừa có trong chính"));

        var (_, result) = await PostAsync<QueueActionView>("/api/review/promote", "Minh", new { ids = new[] { "m1", "late" } });

        Assert.Equal(0, result!.Promoted);
        Assert.Equal(1, result.SkippedInMain);
        Assert.Equal("pending", await DecisionAsync("m1"));
        Assert.Equal("pending", await DecisionAsync("late"));
        Assert.True((bool)(await TestDb.QueryScalarAsync("SELECT exists_in_main FROM review_queue WHERE id = 'late'"))!);
    }

    [Fact]
    public async Task Promote_and_reject_require_session_token()
    {
        await TestDb.SeedQueueAsync("q1", "meta_ai", "Meta AI trả lời ở đây");

        var (promote, _) = await PostWithTokenAsync<QueueActionView>("/api/review/promote", null, new { ids = new[] { "q1" } });
        var (reject, _) = await PostWithTokenAsync<QueueActionView>("/api/review/reject", "token-gia-mao", new { ids = new[] { "q1" } });

        Assert.Equal(HttpStatusCode.Unauthorized, promote);
        Assert.Equal(HttpStatusCode.Unauthorized, reject);
        Assert.Equal("pending", await DecisionAsync("q1"));
    }

    [Fact]
    public async Task Reject_can_be_restored_only_by_the_same_person()
    {
        await TestDb.SeedQueueAsync("r1", "non_vietnamese", "hello world again");
        await PostAsync<QueueActionView>("/api/review/reject", "Minh", new { ids = new[] { "r1" } });

        var (_, byOther) = await PostAsync<QueueActionView>("/api/review/restore", "Lan", new { ids = new[] { "r1" } });
        Assert.Equal(0, byOther!.Restored);
        Assert.Equal("rejected", await DecisionAsync("r1"));

        var (_, byMinh) = await PostAsync<QueueActionView>("/api/review/restore", "Minh", new { ids = new[] { "r1" } });
        Assert.Equal(1, byMinh!.Restored);
        Assert.Equal("pending", await DecisionAsync("r1"));
    }

    [Fact]
    public async Task List_hides_rows_already_in_main_by_default()
    {
        await TestDb.SeedQueueAsync("a", "duplicate_content", "Chưa có trong chính");
        await TestDb.SeedQueueAsync("b", "duplicate_id", "Đã có trong chính", existsInMain: true);

        var visible = await QueueAsync();
        var inMainOnly = await QueueAsync("?inMain=only");

        Assert.Equal(["a"], visible.Items.Select(i => i.Id));
        Assert.Equal(1, visible.Counts.PendingInMain);
        Assert.Equal(["b"], inMainOnly.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Bulk_applies_to_matching_rows_only_and_refuses_when_count_differs()
    {
        await TestDb.SeedQueueAsync("n1", "non_vietnamese", "tiếng Nhật ở đây");
        await TestDb.SeedQueueAsync("n2", "non_vietnamese", "tiếng Hàn ở đây");
        await TestDb.SeedQueueAsync("n3", "non_vietnamese", "tiếng Thái ở đây");
        await TestDb.SeedQueueAsync("m", "meta_ai", "Meta AI trả lời");

        var (stale, _) = await PostAsync<QueueActionView>("/api/review/bulk", "Minh",
            new { action = "reject", reason = "non_vietnamese", expectedCount = 2 });
        Assert.Equal(HttpStatusCode.Conflict, stale);
        Assert.Equal("pending", await DecisionAsync("n1"));

        var (ok, result) = await PostAsync<QueueActionView>("/api/review/bulk", "Minh",
            new { action = "reject", reason = "non_vietnamese", expectedCount = 3 });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(3, result!.Rejected);
        Assert.Equal("rejected", await DecisionAsync("n3"));
        Assert.Equal("pending", await DecisionAsync("m"));
    }

    [Fact]
    public async Task Bulk_rejects_invalid_action_and_requires_session()
    {
        await TestDb.SeedQueueAsync("x", "meta_ai", "Meta AI trả lời");

        var (badAction, _) = await PostAsync<QueueActionView>("/api/review/bulk", "Minh", new { action = "delete", expectedCount = 1 });
        var (noSession, _) = await PostWithTokenAsync<QueueActionView>("/api/review/bulk", null,
            new { action = "reject", expectedCount = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, badAction);
        Assert.Equal(HttpStatusCode.Unauthorized, noSession);
        Assert.Equal("pending", await DecisionAsync("x"));
    }

    [Fact]
    public async Task Approve_records_reviewer_and_feeds_progress()
    {
        await TestDb.SeedAsync(new SeedRow("a1", "Bạn ơi đẹp quá"), new SeedRow("a2", "Hay quá đi thôi"));

        var (status, result) = await PostAsync<object>("/api/items/approve", "Minh", new { ids = new[] { "a1" } });
        Assert.Equal(HttpStatusCode.OK, status);

        Assert.Equal("Minh", await TestDb.QueryScalarAsync("SELECT reviewed_by FROM comments WHERE id = 'a1'"));
        Assert.Null(await TestDb.QueryScalarAsync("SELECT reviewed_by FROM comments WHERE id = 'a2'"));
        Assert.Equal("approve", await TestDb.QueryScalarAsync("SELECT action FROM moderation_audits WHERE comment_id = 'a1'"));

        var progress = (await Http.GetFromJsonAsync<ProgressView>("/api/items/progress"))!;
        Assert.Equal(2, progress.Active);
        Assert.Equal(1, progress.Reviewed);
        Assert.Equal(1, progress.ReviewedToday);
        Assert.Equal([new ReviewerView("Minh", 1)], progress.ByReviewer);

        var unreviewed = await ListAsync("?review=unreviewed");
        Assert.Equal(["a2"], unreviewed.Items.Select(i => i.Id));
        var byMinh = await ListAsync("?reviewedBy=Minh");
        Assert.Equal(["a1"], byMinh.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Approve_ignores_excluded_comments()
    {
        await TestDb.SeedAsync(new SeedRow("x1", "Đã loại rồi nhé", Excluded: true));

        var (_, result) = await PostAsync<Dictionary<string, object>>("/api/items/approve", "Minh", new { ids = new[] { "x1" } });

        Assert.Equal(0, Convert.ToInt32(result!["approved"].ToString()));
        Assert.Null(await TestDb.QueryScalarAsync("SELECT reviewed_by FROM comments WHERE id = 'x1'"));
    }

    [Fact]
    public async Task Moving_a_comment_records_who_handled_it()
    {
        await TestDb.SeedAsync(new SeedRow("a1", "Bạn ơi đẹp quá"));

        var (status, _) = await MoveAsync(["a1"], toExcluded: true, userName: "Lan");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Lan", await TestDb.QueryScalarAsync("SELECT reviewed_by FROM comments WHERE id = 'a1'"));
    }

    [Fact]
    public async Task Reviewed_only_export_contains_only_handled_comments()
    {
        await TestDb.SeedAsync(new SeedRow("a1", "Bạn ơi đẹp quá"), new SeedRow("a2", "Hay quá đi thôi"));
        await PostAsync<object>("/api/items/approve", "Minh", new { ids = new[] { "a1" } });

        var all = await Http.GetAsync("/api/export/curated/csv");
        var reviewed = await Http.GetAsync("/api/export/curated/csv?reviewed=true");
        var allText = await all.Content.ReadAsStringAsync();
        var reviewedText = await reviewed.Content.ReadAsStringAsync();

        Assert.Contains("a2", allText);
        Assert.Contains("a1", reviewedText);
        Assert.DoesNotContain("a2", reviewedText);
    }

    [Fact]
    public async Task Chain_constraint_rejects_reply_without_its_parents()
    {
        // F1 không có F0: không được phép ghi vào CSDL
        await Assert.ThrowsAsync<PostgresException>(() => TestDb.ExecuteParamAsync(
            "INSERT INTO comments (id, post_id, post_url, content, reply_level, f0, f1, f2, f3) " +
            "VALUES ('bad1', 'p', 'u', 'trả lời', 1, '', 'trả lời', '', '')"));
        // F2 có F1 nhưng F1 rỗng
        await Assert.ThrowsAsync<PostgresException>(() => TestDb.ExecuteParamAsync(
            "INSERT INTO comments (id, post_id, post_url, content, reply_level, f0, f1, f2, f3) " +
            "VALUES ('bad2', 'p', 'u', 'trả lời', 2, 'gốc', '', 'trả lời', '')"));
        // F_L không phải chính bình luận
        await Assert.ThrowsAsync<PostgresException>(() => TestDb.ExecuteParamAsync(
            "INSERT INTO comments (id, post_id, post_url, content, reply_level, f0, f1, f2, f3) " +
            "VALUES ('bad3', 'p', 'u', 'trả lời', 1, 'gốc', 'khác', '', '')"));
    }

    [Fact]
    public async Task Chain_constraint_accepts_complete_thread()
    {
        // Từ migration 007, bản trả lời phải có bình luận cha thật trong cùng bài viết
        const string insert = "INSERT INTO comments (id, post_id, post_url, content, reply_level, f0, f1, f2, f3) VALUES ";
        await TestDb.ExecuteParamAsync(insert + "('ok0', 'p', 'u', 'gốc', 0, 'gốc', '', '', '')");
        await TestDb.ExecuteParamAsync(insert + "('ok1', 'p', 'u', 'cha 1', 1, 'gốc', 'cha 1', '', '')");
        await TestDb.ExecuteParamAsync(insert + "('ok2', 'p', 'u', 'cha 2', 2, 'gốc', 'cha 1', 'cha 2', '')");
        await TestDb.ExecuteParamAsync(insert +
            "('ok3', 'p', 'u', 'phản hồi cấp 3', 3, 'gốc', 'cha 1', 'cha 2', 'phản hồi cấp 3')");

        Assert.Equal("ok2", await TestDb.QueryScalarAsync("SELECT thread_parent_id FROM comments WHERE id = 'ok3'"));
        Assert.Equal("ok0", await TestDb.QueryScalarAsync("SELECT thread_root_id FROM comments WHERE id = 'ok3'"));
    }

    [Fact]
    public async Task Csv_export_neutralises_formula_like_cells()
    {
        await TestDb.SeedAsync(new SeedRow("f1", "=SUM(1,2) hay quá"));

        var response = await Http.GetAsync("/api/export/curated/csv");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains("'=SUM(1,2) hay quá", text);
    }
}
