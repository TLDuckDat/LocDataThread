using System.Net;
using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public class LockEnforcementTests(AppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Move_skips_items_locked_by_another_user_and_reports_them()
    {
        await TestDb.SeedAsync(new SeedRow("t_001"));
        var minh = await ActorAsync("Minh");
        await minh.FocusAsync("t_001");

        var (status, result) = await MoveAsync(["t_001"], toExcluded: true, "Huy");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, result!.Moved);
        Assert.Equal(["t_001"], result.Locked);
        Assert.False(await TestDb.IsExcludedAsync("t_001"));
        Assert.Empty(await TestDb.AuditsAsync());
    }

    [Fact]
    public async Task Holder_can_move_their_own_locked_item()
    {
        await TestDb.SeedAsync(new SeedRow("t_001"));
        var minh = await ActorAsync("Minh");
        await minh.FocusAsync("t_001");

        var (_, result) = await MoveAsync(["t_001"], toExcluded: true, "Minh");

        Assert.Equal(1, result!.Moved);
        Assert.Empty(result.Locked!);
    }

    [Fact]
    public async Task Mixed_batch_moves_free_items_and_reports_locked_ones()
    {
        await TestDb.SeedAsync(new SeedRow("held"), new SeedRow("free"));
        var minh = await ActorAsync("Minh");
        await minh.FocusAsync("held");

        var (_, result) = await MoveAsync(["held", "free"], toExcluded: true, "Huy");

        Assert.Equal(1, result!.Moved);
        Assert.Equal(["held"], result.Locked);
        Assert.False(await TestDb.IsExcludedAsync("held"));
        Assert.True(await TestDb.IsExcludedAsync("free"));
    }

    [Fact]
    public async Task Restore_respects_locks_too()
    {
        await TestDb.SeedAsync(new SeedRow("t_001", Excluded: true));
        var minh = await ActorAsync("Minh");
        await minh.FocusAsync("t_001");

        var (_, result) = await MoveAsync(["t_001"], toExcluded: false, "Huy");

        Assert.Equal(0, result!.Moved);
        Assert.Equal(["t_001"], result.Locked);
        Assert.True(await TestDb.IsExcludedAsync("t_001"));
    }

    [Fact]
    public async Task Force_overrides_the_lock_and_marks_the_audit_row()
    {
        await TestDb.SeedAsync(new SeedRow("held"), new SeedRow("free"));
        var minh = await ActorAsync("Minh");
        await minh.FocusAsync("held");

        var (_, result) = await MoveAsync(["held", "free"], toExcluded: true, "Huy", force: true);

        Assert.Equal(2, result!.Moved);
        Assert.Empty(result.Locked!);
        var audits = await TestDb.AuditsAsync();
        Assert.Contains(("held", "Huy", "force_exclude"), audits);
        Assert.Contains(("free", "Huy", "exclude"), audits);   // chỉ dòng thực sự bị vượt khóa mới đánh dấu force
    }

    [Fact]
    public async Task Force_restore_is_marked_in_the_audit()
    {
        await TestDb.SeedAsync(new SeedRow("t_001", Excluded: true));
        var minh = await ActorAsync("Minh");
        await minh.FocusAsync("t_001");

        await MoveAsync(["t_001"], toExcluded: false, "Huy", force: true);

        Assert.Contains(("t_001", "Huy", "force_restore"), await TestDb.AuditsAsync());
    }

    [Fact]
    public async Task Locked_list_is_present_and_empty_when_nothing_is_locked()
    {
        await TestDb.SeedAsync(new SeedRow("t_001"));

        var (_, result) = await MoveAsync(["t_001"], toExcluded: true, "Huy");

        Assert.NotNull(result!.Locked);
        Assert.Empty(result.Locked);
    }

    [Fact]
    public async Task Force_does_not_release_the_other_users_lock_until_the_item_actually_moves()
    {
        // Sau khi bị vượt khóa, bình luận đã đổi nên lease được giải phóng cho cả phòng.
        await TestDb.SeedAsync(new SeedRow("t_001"));
        var minh = await ActorAsync("Minh");
        await using var lan = await HubTestClient.ConnectAsync(Factory, "Lan");
        await minh.FocusAsync("t_001");

        await MoveAsync(["t_001"], toExcluded: true, "Huy", force: true);

        await Wait.UntilAsync(() => lan.LockChanges.Contains(("t_001", null)),
            "lease bị giải phóng sau khi bình luận bị chuyển cưỡng chế");
    }
}

public class LockExpiryEnforcementTests(ShortLeaseFactory factory) : IntegrationTestBase<ShortLeaseFactory>(factory)
{
    [Fact]
    public async Task Expired_lease_no_longer_blocks_a_move()
    {
        await TestDb.SeedAsync(new SeedRow("t_001"));
        var minh = await ActorAsync("Minh");
        await minh.FocusAsync("t_001");

        var (_, blocked) = await MoveAsync(["t_001"], toExcluded: true, "Huy");
        Assert.Equal(["t_001"], blocked!.Locked);

        // Lease 1 giây: chờ tới khi hết hạn thì thao tác phải được chấp nhận.
        MoveResult? last = null;
        for (var i = 0; i < 100 && last?.Moved != 1; i++)
        {
            await Task.Delay(50);
            last = (await MoveAsync(["t_001"], toExcluded: true, "Huy")).Result;
        }

        Assert.Equal(1, last!.Moved);
    }
}
