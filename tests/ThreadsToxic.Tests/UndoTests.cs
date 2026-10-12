using System.Net;
using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public class UndoTests(AppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Move_reports_the_exact_ids_that_changed()
    {
        await TestDb.SeedAsync(new SeedRow("act"), new SeedRow("exc", Excluded: true));

        var (_, result) = await MoveAsync(["act", "exc", "missing"], toExcluded: true, "Minh");

        Assert.Equal(["act"], result!.MovedIds);
    }

    [Fact]
    public async Task Exclude_then_undo_restores_state_counts_and_keeps_both_audit_rows()
    {
        await TestDb.SeedAsync(TestDb.Rows(4));
        var before = await CountsAsync();
        var (_, moved) = await MoveAsync(["t_001", "t_002"], toExcluded: true, "Minh");

        var (status, undone) = await UndoAsync(moved!.MovedIds!, toExcluded: false, "Minh");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, undone!.Undone);
        Assert.Empty(undone.Conflicts);
        Assert.Equal(before, await CountsAsync());
        Assert.False(await TestDb.IsExcludedAsync("t_001"));
        var audits = await TestDb.AuditsAsync();
        Assert.Equal(4, audits.Count);
        Assert.Equal(2, audits.Count(a => a.Action == "exclude" && a.User == "Minh"));
        Assert.Equal(2, audits.Count(a => a.Action == "restore" && a.User == "Minh"));
    }

    [Fact]
    public async Task Restore_then_undo_puts_the_item_back_in_the_excluded_bucket()
    {
        await TestDb.SeedAsync(new SeedRow("t_001", Excluded: true));
        await MoveAsync(["t_001"], toExcluded: false, "Minh");

        var (_, undone) = await UndoAsync(["t_001"], toExcluded: true, "Minh");

        Assert.Equal(1, undone!.Undone);
        Assert.True(await TestDb.IsExcludedAsync("t_001"));
    }

    [Fact]
    public async Task Undo_skips_items_another_user_acted_on_afterwards_and_reports_them()
    {
        await TestDb.SeedAsync(new SeedRow("mine"), new SeedRow("taken"));
        var (_, moved) = await MoveAsync(["mine", "taken"], toExcluded: true, "Minh");
        // Lan khôi phục rồi loại lại "taken": audit gần nhất của nó giờ là của Lan.
        await MoveAsync(["taken"], toExcluded: false, "Lan");
        await MoveAsync(["taken"], toExcluded: true, "Lan");

        var (_, undone) = await UndoAsync(moved!.MovedIds!, toExcluded: false, "Minh");

        Assert.Equal(["mine"], undone!.UndoneIds);
        Assert.Equal(["taken"], undone.Conflicts);
        Assert.False(await TestDb.IsExcludedAsync("mine"));
        Assert.True(await TestDb.IsExcludedAsync("taken"));   // thao tác của Lan được giữ nguyên
    }

    [Fact]
    public async Task Undo_is_refused_for_items_that_user_never_touched()
    {
        await TestDb.SeedAsync(new SeedRow("t_001"));
        await MoveAsync(["t_001"], toExcluded: true, "Lan");

        var (_, undone) = await UndoAsync(["t_001"], toExcluded: false, "Minh");

        Assert.Equal(0, undone!.Undone);
        Assert.Equal(["t_001"], undone.Conflicts);
        Assert.True(await TestDb.IsExcludedAsync("t_001"));
    }

    [Fact]
    public async Task Undo_twice_changes_nothing_the_second_time()
    {
        await TestDb.SeedAsync(new SeedRow("t_001"));
        await MoveAsync(["t_001"], toExcluded: true, "Minh");
        await UndoAsync(["t_001"], toExcluded: false, "Minh");

        var (_, again) = await UndoAsync(["t_001"], toExcluded: false, "Minh");

        Assert.Equal(0, again!.Undone);
        Assert.Equal(1, again.Skipped);   // audit gần nhất vẫn là của Minh nhưng đã ở đúng trạng thái
        Assert.Equal(2, (await TestDb.AuditsAsync()).Count);
    }

    [Fact]
    public async Task Undo_respects_locks_held_by_other_users()
    {
        await TestDb.SeedAsync(new SeedRow("t_001"));
        await MoveAsync(["t_001"], toExcluded: true, "Minh");
        var lan = await ActorAsync("Lan");
        await lan.FocusAsync("t_001");

        var (_, undone) = await UndoAsync(["t_001"], toExcluded: false, "Minh");

        Assert.Equal(0, undone!.Undone);
        Assert.Equal(["t_001"], undone.Locked);
        Assert.True(await TestDb.IsExcludedAsync("t_001"));
    }

    [Fact]
    public async Task Undo_without_a_session_token_is_unauthorized()
    {
        await TestDb.SeedAsync(new SeedRow("t_001"));
        await MoveAsync(["t_001"], toExcluded: true, "Minh");

        var (status, _) = await UndoWithTokenAsync(null, new { ids = new[] { "t_001" }, toExcluded = false });

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.True(await TestDb.IsExcludedAsync("t_001"));
    }

    [Fact]
    public async Task Undo_validates_its_batch()
    {
        var minh = await ActorAsync("Minh");

        var empty = await UndoWithTokenAsync(minh.Token, new { ids = Array.Empty<string>(), toExcluded = false });
        var huge = await UndoWithTokenAsync(minh.Token,
            new { ids = Enumerable.Range(0, 501).Select(i => $"id_{i}").ToArray(), toExcluded = false });

        Assert.Equal(HttpStatusCode.BadRequest, empty.Status);
        Assert.Equal(HttpStatusCode.BadRequest, huge.Status);
    }

    [Fact]
    public async Task Undo_broadcasts_ItemsMoved_to_the_room()
    {
        await TestDb.SeedAsync(new SeedRow("t_001"));
        await using var lan = await HubTestClient.ConnectAsync(Factory, "Lan");
        await MoveAsync(["t_001"], toExcluded: true, "Minh");

        await UndoAsync(["t_001"], toExcluded: false, "Minh");

        await Wait.UntilAsync(
            () => lan.Moved.Any(m => m.Ids.SequenceEqual(["t_001"]) && !m.ToExcluded && m.By == "Minh"),
            "Lan phải nhận ItemsMoved của lần hoàn tác");
    }

    [Fact]
    public async Task Concurrent_undo_and_move_by_another_user_never_overrides_the_other_user()
    {
        await TestDb.SeedAsync(TestDb.Rows(20));
        var ids = Enumerable.Range(1, 20).Select(i => $"t_{i:000}").ToArray();
        await MoveAsync(ids, toExcluded: true, "Minh");

        // Lan khôi phục toàn bộ trong khi Minh cố hoàn tác cùng lúc.
        var lanTask = MoveAsync(ids, toExcluded: false, "Lan");
        var minhTask = UndoAsync(ids, toExcluded: false, "Minh");
        await Task.WhenAll(lanTask, minhTask);

        // Dù thứ tự thế nào, mỗi dòng cuối cùng đều ở trạng thái "đang lọc" và có nhật ký nhất quán.
        Assert.Equal(new CountsResult(20, 0), await CountsAsync());
        var audits = await TestDb.AuditsAsync();
        Assert.Equal(20, audits.Count(a => a.Action == "exclude"));
        Assert.Equal(20, audits.Count(a => a.Action == "restore"));   // mỗi dòng đúng một lần khôi phục
    }
}
