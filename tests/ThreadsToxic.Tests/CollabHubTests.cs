using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public class CollabHubTests(AppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Join_broadcasts_presence_to_everyone_in_the_room()
    {
        await using var minh = await HubTestClient.ConnectAsync(Factory, "Minh");
        await using var lan = await HubTestClient.ConnectAsync(Factory, "Lan");

        await Wait.UntilAsync(
            () => minh.Presence.Any(p => p.Select(u => u.Name).Order().SequenceEqual(["Lan", "Minh"])),
            "Minh phải thấy cả Lan và Minh trong presence");
    }

    [Fact]
    public async Task Join_rejects_an_empty_name()
    {
        await using var client = await HubTestClient.ConnectAsync(Factory, "   ", join: false);

        await Assert.ThrowsAnyAsync<Exception>(() => client.JoinAsync());
    }

    [Fact]
    public async Task Focus_before_joining_is_denied()
    {
        await using var client = await HubTestClient.ConnectAsync(Factory, "Ghost", join: false);

        var result = await client.FocusAsync("t_001");

        Assert.False(result.Granted);
        Assert.Null(result.Holder);
    }

    [Fact]
    public async Task Second_user_is_denied_while_the_first_holds_the_item()
    {
        await using var minh = await HubTestClient.ConnectAsync(Factory, "Minh");
        await using var lan = await HubTestClient.ConnectAsync(Factory, "Lan");

        var first = await minh.FocusAsync("t_001");
        var second = await lan.FocusAsync("t_001");

        Assert.True(first.Granted);
        Assert.False(second.Granted);
        Assert.Equal("Minh", second.Holder);
    }

    [Fact]
    public async Task Holder_can_refocus_the_same_item()
    {
        await using var minh = await HubTestClient.ConnectAsync(Factory, "Minh");

        await minh.FocusAsync("t_001");
        var again = await minh.FocusAsync("t_001");

        Assert.True(again.Granted);
    }

    [Fact]
    public async Task Focus_is_broadcast_as_LockChanged_to_other_users()
    {
        await using var minh = await HubTestClient.ConnectAsync(Factory, "Minh");
        await using var huy = await HubTestClient.ConnectAsync(Factory, "Huy");

        await minh.FocusAsync("t_001");

        await Wait.UntilAsync(() => huy.LockChanges.Contains(("t_001", "Minh")),
            "Huy phải nhận LockChanged(t_001, Minh)");
    }

    [Fact]
    public async Task Blur_releases_the_lock_for_others()
    {
        await using var minh = await HubTestClient.ConnectAsync(Factory, "Minh");
        await using var lan = await HubTestClient.ConnectAsync(Factory, "Lan");
        await minh.FocusAsync("t_001");

        await minh.BlurAsync("t_001");

        await Wait.UntilAsync(() => lan.LockChanges.Contains(("t_001", null)),
            "Lan phải nhận LockChanged(t_001, null)");
        Assert.True((await lan.FocusAsync("t_001")).Granted);
    }

    [Fact]
    public async Task Late_joiner_receives_a_snapshot_of_current_locks()
    {
        await using var minh = await HubTestClient.ConnectAsync(Factory, "Minh");
        await minh.FocusAsync("t_001");

        await using var late = await HubTestClient.ConnectAsync(Factory, "Late");

        await Wait.UntilAsync(
            () => late.Snapshots.Any(s => s.Any(l => l.ItemId == "t_001" && l.Holder == "Minh")),
            "người vào sau phải nhận LockSnapshot có khóa của Minh");
    }

    [Fact]
    public async Task Disconnect_releases_the_users_locks()
    {
        await using var lan = await HubTestClient.ConnectAsync(Factory, "Lan");
        var minh = await HubTestClient.ConnectAsync(Factory, "Minh");
        await minh.FocusAsync("t_001");

        await minh.DisposeAsync();

        await Wait.UntilAsync(() => lan.LockChanges.Contains(("t_001", null)),
            "khóa của Minh phải được giải phóng khi ngắt kết nối");
        await Wait.UntilAsync(
            () => lan.Presence.Any(p => p.Select(u => u.Name).SequenceEqual(["Lan"])),
            "Minh phải biến mất khỏi presence");
    }

    [Fact]
    public async Task Moving_an_item_releases_its_lock_and_broadcasts_ItemsMoved()
    {
        await TestDb.SeedAsync(new SeedRow("t_001"));
        await using var minh = await HubTestClient.ConnectAsync(Factory, "Minh");
        await using var lan = await HubTestClient.ConnectAsync(Factory, "Lan");
        await minh.FocusAsync("t_001");

        await MoveAsync(["t_001"], toExcluded: true, "Huy");

        await Wait.UntilAsync(() => lan.LockChanges.Contains(("t_001", null)),
            "khóa phải được giải phóng sau khi bình luận bị chuyển");
        await Wait.UntilAsync(
            () => lan.Moved.Any(m => m.Ids.SequenceEqual(["t_001"]) && m.ToExcluded && m.By == "Huy"),
            "Lan phải nhận ItemsMoved");
        Assert.True((await lan.FocusAsync("t_001")).Granted);
    }

    [Fact]
    public async Task No_ItemsMoved_is_sent_when_nothing_changed()
    {
        await TestDb.SeedAsync(new SeedRow("t_001", Excluded: true));
        await using var lan = await HubTestClient.ConnectAsync(Factory, "Lan");

        var (_, result) = await MoveAsync(["t_001"], toExcluded: true, "Huy");
        await Task.Delay(200);

        Assert.Equal(0, result!.Moved);
        Assert.Empty(lan.Moved);
    }

    [Fact]
    public async Task Move_currently_ignores_locks_held_by_other_users()
    {
        await TestDb.SeedAsync(new SeedRow("t_001"));
        await using var minh = await HubTestClient.ConnectAsync(Factory, "Minh");
        await minh.FocusAsync("t_001");

        var (_, result) = await MoveAsync(["t_001"], toExcluded: true, "Huy");

        Assert.Equal(1, result!.Moved);
    }
}

public class LeaseExpiryTests(ShortLeaseFactory factory) : IntegrationTestBase<ShortLeaseFactory>(factory)
{
    [Fact]
    public async Task Expired_lease_is_released_by_the_sweeper_and_can_be_taken_over()
    {
        await using var minh = await HubTestClient.ConnectAsync(Factory, "Minh");
        await using var lan = await HubTestClient.ConnectAsync(Factory, "Lan");
        Assert.True((await minh.FocusAsync("t_001")).Granted);
        Assert.False((await lan.FocusAsync("t_001")).Granted);

        await Wait.UntilAsync(() => lan.LockChanges.Contains(("t_001", null)),
            "sweeper phải báo LockChanged(t_001, null) sau khi lease 1 giây hết hạn", timeoutMs: 6000);

        Assert.True((await lan.FocusAsync("t_001")).Granted);
    }
}
