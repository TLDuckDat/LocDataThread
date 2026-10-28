using System.Net;
using System.Net.Http.Json;
using Npgsql;
using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public record PromoteView(int Promoted, int PromotedComments, int SkippedInMain, List<string> Changed,
    List<string>? BlockedChain);
public record ExportPreviewView(List<List<string>> Rows, int BrokenChainDropped);

/// <summary>
/// Ràng buộc chuỗi hội thoại F0..F3 (migration 007): cha bị loại thì con cũng bị loại, và mọi đường ghi
/// (API, hoàn tác, Kho phụ, nạp dữ liệu, SQL tay, xuất file) đều phải giữ được điều đó.
/// Luồng mẫu "a": a0 (F0) -> a1 (F1) -> a2 (F2) -> a3 (F3).
/// </summary>
public class ThreadChainTests(AppFactory factory) : IntegrationTestBase(factory)
{
    private static async Task<bool[]> ExcludedAsync(params string[] ids)
    {
        var result = new bool[ids.Length];
        for (var i = 0; i < ids.Length; i++) result[i] = await TestDb.IsExcludedAsync(ids[i]);
        return result;
    }

    private static Task<object?> ScalarAsync(string sql, string id) => TestDb.QueryScalarAsync(sql, ("id", id));

    // ---------- Liên kết cha-con (R1) ----------

    [Fact]
    public async Task Replies_are_linked_to_their_parent_and_root_automatically()
    {
        await TestDb.SeedThreadAsync("a");

        Assert.Null(await ScalarAsync("SELECT thread_parent_id FROM comments WHERE id = @id", "a0"));
        Assert.Equal("a0", await ScalarAsync("SELECT thread_root_id FROM comments WHERE id = @id", "a0"));
        Assert.Equal("a1", await ScalarAsync("SELECT thread_parent_id FROM comments WHERE id = @id", "a2"));
        Assert.Equal("a0", await ScalarAsync("SELECT thread_root_id FROM comments WHERE id = @id", "a3"));
    }

    [Fact]
    public async Task Reply_without_a_parent_in_the_same_post_is_rejected()
    {
        await TestDb.SeedAsync(new SeedRow("root", "gốc"));

        // Chuỗi F đúng hình dạng nhưng cha nằm ở bài viết khác: không có cha trong cùng bài
        var ex = await Assert.ThrowsAsync<PostgresException>(() => TestDb.ExecuteParamAsync("""
            INSERT INTO comments (id, post_id, post_url, content, reply_level, f0, f1, f2, f3)
            VALUES ('orphan', 'other_post', 'u', 'trả lời', 1, 'gốc', 'trả lời', '', '')
            """));
        Assert.Equal("comments_thread_parent_fk", ex.ConstraintName);
    }

    [Fact]
    public async Task Parent_with_replies_cannot_be_deleted()
    {
        await TestDb.SeedThreadAsync("a", depth: 1);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => TestDb.DeleteCommentAsync("a0"));
        Assert.Equal("comments_thread_parent_fk", ex.ConstraintName);
    }

    // ---------- Loại lan xuống con cháu (trường hợp 1, 2) ----------

    [Fact]
    public async Task Excluding_a_reply_also_excludes_every_descendant_but_not_its_ancestors()
    {
        await TestDb.SeedThreadAsync("a");

        var (status, result) = await MoveAsync(["a1"], toExcluded: true, "Minh");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, result!.Moved);
        Assert.Equal(["a2", "a3"], result.Cascaded!.Order());
        Assert.Equal(["a1", "a2", "a3"], result.MovedIds!.Order());
        Assert.Equal(new[] { false, true, true, true }, await ExcludedAsync("a0", "a1", "a2", "a3"));

        var audits = await TestDb.AuditsAsync();
        Assert.Contains(("a1", "Minh", "exclude"), audits);
        Assert.Contains(("a2", "Minh", "exclude_cascade"), audits);
        Assert.Contains(("a3", "Minh", "exclude_cascade"), audits);
        Assert.Equal("a1", await ScalarAsync(
            "SELECT cause_id FROM moderation_audits WHERE comment_id = @id AND action = 'exclude_cascade'", "a3"));
    }

    [Fact]
    public async Task Excluding_the_root_also_excludes_a_reply_someone_already_kept()
    {
        await TestDb.SeedThreadAsync("a");
        var lan = await ActorAsync("Lan");
        using (var approve = new HttpRequestMessage(HttpMethod.Post, "/api/items/approve")
               { Content = JsonContent.Create(new { ids = new[] { "a3" } }) })
        {
            approve.Headers.Add("X-Session-Token", lan.Token);
            Assert.Equal(HttpStatusCode.OK, (await Http.SendAsync(approve)).StatusCode);
        }

        var (_, result) = await MoveAsync(["a0"], toExcluded: true, "Minh");

        Assert.Equal(["a1", "a2", "a3"], result!.Cascaded!.Order());
        Assert.True(await TestDb.IsExcludedAsync("a3"));
        // Bị loại theo không ghi đè người đã xử lý bình luận đó
        Assert.Equal("Lan", await ScalarAsync("SELECT reviewed_by FROM comments WHERE id = @id", "a3"));
    }

    [Fact]
    public async Task Other_threads_and_siblings_are_not_touched()
    {
        await TestDb.SeedThreadAsync("a");
        await TestDb.SeedAsync(new SeedRow("a1b", "anh em của a1", ReplyLevel: 1, Parent: "a0"));
        await TestDb.SeedThreadAsync("b", postId: "post_other");

        await MoveAsync(["a1"], toExcluded: true, "Minh");

        Assert.False(await TestDb.IsExcludedAsync("a1b"));
        Assert.Equal(new[] { false, false, false, false }, await ExcludedAsync("b0", "b1", "b2", "b3"));
    }

    [Fact]
    public async Task Cascade_is_broadcast_so_other_screens_drop_the_whole_branch()
    {
        await TestDb.SeedThreadAsync("a");
        var lan = await ActorAsync("Lan");

        await MoveAsync(["a1"], toExcluded: true, "Minh");

        await Wait.UntilAsync(() => lan.Moved.Any(m => m.Ids.Contains("a3")), "Lan nhận ItemsMoved có cả nhánh");
        var payload = lan.Moved.Single(m => m.Ids.Contains("a3"));
        Assert.True(payload.ToExcluded);
        Assert.Equal(["a1", "a2", "a3"], payload.Ids.Order());
    }

    // ---------- Khóa: không loại nửa vời (trường hợp 6) ----------

    [Fact]
    public async Task Branch_with_a_reply_viewed_by_someone_else_is_skipped_as_a_whole()
    {
        await TestDb.SeedThreadAsync("a");
        var lan = await ActorAsync("Lan");
        Assert.True((await lan.FocusAsync("a3")).Granted);

        var (_, result) = await MoveAsync(["a1"], toExcluded: true, "Minh");

        Assert.Equal(0, result!.Moved);
        Assert.Equal(["a1"], result.Locked);
        Assert.Equal(new[] { false, false, false }, await ExcludedAsync("a1", "a2", "a3"));

        var (_, forced) = await MoveAsync(["a1"], toExcluded: true, "Minh", force: true);

        Assert.Equal(1, forced!.Moved);
        Assert.Equal(new[] { true, true, true }, await ExcludedAsync("a1", "a2", "a3"));
        Assert.Contains(("a1", "Minh", "force_exclude"), await TestDb.AuditsAsync());
    }

    // ---------- Khôi phục (trường hợp 3) ----------

    [Fact]
    public async Task Restoring_a_reply_under_an_excluded_parent_is_blocked()
    {
        await TestDb.SeedThreadAsync("a");
        await MoveAsync(["a1"], toExcluded: true, "Minh");

        var (status, result) = await MoveAsync(["a3"], toExcluded: false, "Minh");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, result!.Moved);
        Assert.Equal(["a3"], result.Blocked);
        Assert.Equal(["a1", "a2"], result.BlockingAncestors!.Order());
        Assert.True(await TestDb.IsExcludedAsync("a3"));
    }

    [Fact]
    public async Task Restore_with_ancestors_brings_back_the_whole_chain_above()
    {
        await TestDb.SeedThreadAsync("a");
        await MoveAsync(["a1"], toExcluded: true, "Minh");

        var (_, result) = await MoveAsync(["a3"], toExcluded: false, "Lan", withAncestors: true);

        Assert.Equal(1, result!.Moved);
        Assert.Equal(["a1", "a2"], result.RestoredAncestors!.Order());
        Assert.Equal(new[] { false, false, false, false }, await ExcludedAsync("a0", "a1", "a2", "a3"));
        Assert.Equal("a3", await ScalarAsync(
            "SELECT cause_id FROM moderation_audits WHERE comment_id = @id ORDER BY id DESC LIMIT 1", "a1"));
    }

    [Fact]
    public async Task Restoring_a_parent_leaves_its_excluded_children_excluded()
    {
        await TestDb.SeedThreadAsync("a");
        await MoveAsync(["a1"], toExcluded: true, "Minh");

        var (_, result) = await MoveAsync(["a1"], toExcluded: false, "Minh");

        Assert.Equal(1, result!.Moved);
        Assert.Equal(new[] { false, true, true }, await ExcludedAsync("a1", "a2", "a3"));
    }

    [Fact]
    public async Task Restoring_parent_and_child_together_is_allowed()
    {
        await TestDb.SeedThreadAsync("a");
        await MoveAsync(["a1"], toExcluded: true, "Minh");

        var (_, result) = await MoveAsync(["a1", "a2", "a3"], toExcluded: false, "Minh");

        Assert.Equal(3, result!.Moved);
        Assert.Empty(result.Blocked!);
        Assert.Equal(new[] { false, false, false }, await ExcludedAsync("a1", "a2", "a3"));
    }

    // ---------- Hoàn tác (trường hợp 4, 5) ----------

    [Fact]
    public async Task Undoing_an_exclusion_restores_exactly_the_replies_it_dragged_along()
    {
        await TestDb.SeedThreadAsync("a");
        await MoveAsync(["a3"], toExcluded: true, "Minh");   // a3 bị loại riêng từ trước
        await MoveAsync(["a1"], toExcluded: true, "Minh");   // kéo theo a2 (a3 đã ở Đã loại)

        // Client cũ chỉ gửi id đã chọn: server tự tìm phần đã kéo theo
        var (status, undo) = await UndoAsync(["a1"], toExcluded: false, "Minh");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, undo!.Undone);
        Assert.Equal(["a2"], undo.Cascaded!);
        Assert.Equal(new[] { false, false, true }, await ExcludedAsync("a1", "a2", "a3"));
    }

    [Fact]
    public async Task Undoing_a_restore_with_ancestors_excludes_the_chain_again()
    {
        await TestDb.SeedThreadAsync("a");
        await MoveAsync(["a1"], toExcluded: true, "Minh");
        var (_, restored) = await MoveAsync(["a3"], toExcluded: false, "Minh", withAncestors: true);

        var (_, undo) = await UndoAsync(restored!.MovedIds!, toExcluded: true, "Minh");

        Assert.Equal(3, undo!.UndoneIds.Count);
        Assert.Equal(new[] { false, true, true, true }, await ExcludedAsync("a0", "a1", "a2", "a3"));
    }

    [Fact]
    public async Task Undo_does_not_put_a_reply_back_under_a_parent_excluded_by_someone_else()
    {
        await TestDb.SeedThreadAsync("a");
        var (_, minh) = await MoveAsync(["a2"], toExcluded: true, "Minh"); // kéo theo a3
        await MoveAsync(["a1"], toExcluded: true, "Lan");                  // sau đó Lan loại cha a1

        var (_, undo) = await UndoAsync(minh!.MovedIds!, toExcluded: false, "Minh");

        Assert.Equal(0, undo!.Undone);
        Assert.Contains("a2", undo.Conflicts);
        Assert.Equal(new[] { true, true, true }, await ExcludedAsync("a1", "a2", "a3"));
    }

    // ---------- DB tự chặn khi ghi tay (R2) ----------

    [Fact]
    public async Task Database_rejects_restoring_a_reply_under_an_excluded_parent_by_hand()
    {
        await TestDb.SeedThreadAsync("a");
        await MoveAsync(["a1"], toExcluded: true, "Minh");

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            TestDb.ExecuteAsync("UPDATE comments SET is_excluded = FALSE WHERE id = 'a3'"));
        Assert.Equal("comments_exclusion_chain", ex.ConstraintName);
        Assert.True(await TestDb.IsExcludedAsync("a3"));
    }

    [Fact]
    public async Task Database_rejects_excluding_a_parent_by_hand_while_replies_stay_active()
    {
        await TestDb.SeedThreadAsync("a", depth: 1);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            TestDb.ExecuteAsync("UPDATE comments SET is_excluded = TRUE WHERE id = 'a0'"));
        Assert.Equal("comments_exclusion_chain", ex.ConstraintName);
        Assert.False(await TestDb.IsExcludedAsync("a0"));
    }

    [Fact]
    public async Task Database_accepts_excluding_parent_and_children_in_one_statement()
    {
        await TestDb.SeedThreadAsync("a");

        await TestDb.ExecuteAsync("UPDATE comments SET is_excluded = TRUE WHERE id IN ('a1', 'a2', 'a3')");

        Assert.Equal(new[] { true, true, true }, await ExcludedAsync("a1", "a2", "a3"));
    }

    // ---------- Nạp dữ liệu mới (trường hợp 8, 9) ----------

    [Fact]
    public async Task New_reply_under_an_excluded_parent_arrives_already_excluded()
    {
        await TestDb.SeedThreadAsync("a", depth: 1);
        await MoveAsync(["a1"], toExcluded: true, "Minh");

        await TestDb.SeedAsync(new SeedRow("late", "trả lời đến sau", ReplyLevel: 2, Parent: "a1"));

        Assert.True(await TestDb.IsExcludedAsync("late"));
        Assert.Contains(("late", "system", "exclude_cascade"), await TestDb.AuditsAsync());
    }

    // ---------- Kho phụ (trường hợp 10) ----------

    private static Task SeedQueueReplyAsync(string id, string postId, string f0, string content) =>
        TestDb.ExecuteParamAsync("""
            INSERT INTO review_queue (id, record_type, reason, record_id, post_id, post_url, content,
                f0, f1, f2, f3, reply_level, is_reply, toxic_score, review_status, review_status_vi, analysis_data,
                occurrences, first_seen, last_seen, crawler_ids)
            VALUES (@id, 'comment', 'duplicate_content', @id, @post, 'https://example.test/q', @content,
                @f0, @content, '', '', 1, TRUE, 0.5, 'clean', 'Trong sạch', '{}'::jsonb,
                1, '2026-10-01', '2026-10-01', ARRAY[@id]::text[])
            """, ("id", id), ("post", postId), ("f0", f0), ("content", content));

    private async Task<PromoteView> PromoteAsync(params string[] ids)
    {
        var actor = await ActorAsync("Minh");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/review/promote")
        {
            Content = JsonContent.Create(new { ids }),
        };
        request.Headers.Add("X-Session-Token", actor.Token);
        var response = await Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PromoteView>())!;
    }

    [Fact]
    public async Task Promoting_a_reply_links_it_to_its_parent_in_main()
    {
        await TestDb.SeedAsync(new SeedRow("m0", "gốc trong CSDL chính", PostId: "pq"));
        await SeedQueueReplyAsync("q1", "pq", "gốc trong CSDL chính", "trả lời từ kho phụ");

        var result = await PromoteAsync("q1");

        Assert.Equal(1, result.PromotedComments);
        Assert.Equal("m0", await ScalarAsync("SELECT thread_parent_id FROM comments WHERE id = @id", "q1"));
    }

    [Fact]
    public async Task Promoting_a_reply_whose_parent_is_missing_or_excluded_is_blocked()
    {
        await TestDb.SeedAsync(new SeedRow("m0", "gốc đã loại", PostId: "pq", Excluded: true));
        await SeedQueueReplyAsync("q_excluded_parent", "pq", "gốc đã loại", "trả lời 1");
        await SeedQueueReplyAsync("q_no_parent", "pq", "gốc không tồn tại", "trả lời 2");

        var result = await PromoteAsync("q_excluded_parent", "q_no_parent");

        Assert.Equal(0, result.Promoted);
        Assert.Equal(0, result.SkippedInMain);
        Assert.Equal(["q_excluded_parent", "q_no_parent"], result.BlockedChain!.Order());
        Assert.Equal(0, await TestDb.CountAsync("SELECT COUNT(*) FROM comments WHERE id LIKE 'q\\_%'"));
        Assert.Equal(2, await TestDb.CountAsync(
            "SELECT COUNT(*) FROM review_queue WHERE decision = 'pending' AND NOT exists_in_main"));
    }

    [Fact]
    public async Task Promoting_parent_and_reply_together_works_in_one_batch()
    {
        await TestDb.SeedQueueAsync("qr0", "duplicate_content", "gốc từ kho phụ");
        await SeedQueueReplyAsync("qr1", "post_q", "gốc từ kho phụ", "trả lời của gốc kho phụ");

        var result = await PromoteAsync("qr1", "qr0");

        Assert.Equal(2, result.PromotedComments);
        Assert.Equal("qr0", await ScalarAsync("SELECT thread_parent_id FROM comments WHERE id = @id", "qr1"));
    }

    // ---------- Xuất file (trường hợp 7, 11) ----------

    [Fact]
    public async Task Export_drops_replies_whose_context_is_blank_after_cleaning()
    {
        // Ngữ cảnh F0 không phải tiếng Việt: khi làm sạch thành rỗng -> dòng F1 bị đứt chuỗi
        await TestDb.SeedAsync(
            new SeedRow("jp0", "こんにちは皆さん、元気ですか"),
            new SeedRow("jp1", "Bạn nói gì vậy, mình không hiểu gì cả", ReplyLevel: 1, Parent: "jp0"),
            new SeedRow("vn0", "Hôm nay trời đẹp quá các bạn ơi"),
            new SeedRow("vn1", "Đúng rồi, đi chơi thôi nào", ReplyLevel: 1, Parent: "vn0"));

        var csv = await (await Http.GetAsync("/api/export/curated/csv")).Content.ReadAsStringAsync();
        var preview = await Http.GetFromJsonAsync<ExportPreviewView>("/api/export/curated");

        Assert.DoesNotContain("jp1", csv);
        Assert.Contains("vn1", csv);
        Assert.Equal(1, preview!.BrokenChainDropped);
        var response = await Http.GetAsync("/api/export/curated/csv");
        Assert.Equal("1", response.Headers.GetValues("X-Broken-Chain-Dropped").Single());
    }

    [Fact]
    public async Task Excluded_parent_means_no_reply_of_it_is_exported()
    {
        await TestDb.SeedAsync(
            new SeedRow("p0", "Hôm nay trời đẹp quá các bạn ơi"),
            new SeedRow("p1", "Đúng rồi, đi chơi thôi nào", ReplyLevel: 1, Parent: "p0"));
        await MoveAsync(["p0"], toExcluded: true, "Minh");

        var csv = await (await Http.GetAsync("/api/export/curated/csv")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("p0", csv);
        Assert.DoesNotContain("p1", csv);
    }

    [Fact]
    public async Task Reviewed_only_export_needs_the_whole_chain_above_to_be_reviewed()
    {
        await TestDb.SeedAsync(
            new SeedRow("r0", "Hôm nay trời đẹp quá các bạn ơi"),
            new SeedRow("r1", "Đúng rồi, đi chơi thôi nào", ReplyLevel: 1, Parent: "r0"));
        var minh = await ActorAsync("Minh");

        async Task ApproveAsync(string id)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/items/approve")
            {
                Content = JsonContent.Create(new { ids = new[] { id } }),
            };
            request.Headers.Add("X-Session-Token", minh.Token);
            Assert.Equal(HttpStatusCode.OK, (await Http.SendAsync(request)).StatusCode);
        }

        await ApproveAsync("r1");
        var onlyChild = await (await Http.GetAsync("/api/export/curated/csv?reviewed=true")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("r1", onlyChild);

        await ApproveAsync("r0");
        var both = await (await Http.GetAsync("/api/export/curated/csv?reviewed=true")).Content.ReadAsStringAsync();
        Assert.Contains("r0", both);
        Assert.Contains("r1", both);
    }

    // ---------- Kiểm tra toàn vẹn ----------

    [Fact]
    public async Task Integrity_check_script_reports_zero_violations_after_mixed_operations()
    {
        await TestDb.SeedThreadAsync("a");
        await TestDb.SeedThreadAsync("b", postId: "post_other");
        await MoveAsync(["a1"], toExcluded: true, "Minh");
        await MoveAsync(["b3"], toExcluded: true, "Lan");
        await MoveAsync(["a3"], toExcluded: false, "Lan", withAncestors: true);
        await UndoAsync(["b3"], toExcluded: false, "Lan");

        var script = await File.ReadAllTextAsync(FindRepoFile("db", "checks", "chain_integrity.sql"));
        var query = string.Join('\n', script.Split('\n').Where(l => !l.TrimStart().StartsWith("--"))).Trim().TrimEnd(';');
        Assert.Equal(0, await TestDb.CountAsync($"SELECT COALESCE(SUM(violations), 0) FROM ({query}) checks"));
    }

    private static string FindRepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }
}
