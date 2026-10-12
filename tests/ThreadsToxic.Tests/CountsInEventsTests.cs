using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public class CountsInEventsTests(AppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task ItemsMoved_carries_the_bucket_counts_after_the_move()
    {
        await TestDb.SeedAsync(TestDb.Rows(5, i => new SeedRow($"t_{i:000}", Excluded: i == 5)));
        await using var lan = await HubTestClient.ConnectAsync(Factory, "Lan");

        await MoveAsync(["t_001", "t_002", "t_003"], toExcluded: true, "Minh");

        await Wait.UntilAsync(() => lan.Moved.Count == 1, "Lan phải nhận một ItemsMoved");
        var payload = lan.Moved.Single();
        Assert.Equal(new CountsResult(Active: 1, Excluded: 4), payload.Counts);
        Assert.Equal(await CountsAsync(), payload.Counts);
        Assert.False(payload.Undo);
    }

    [Fact]
    public async Task ItemsMoved_from_an_undo_carries_counts_and_is_flagged()
    {
        await TestDb.SeedAsync(TestDb.Rows(3));
        await using var lan = await HubTestClient.ConnectAsync(Factory, "Lan");
        await MoveAsync(["t_001", "t_002"], toExcluded: true, "Minh");

        await UndoAsync(["t_001", "t_002"], toExcluded: false, "Minh");

        await Wait.UntilAsync(() => lan.Moved.Count == 2, "Lan phải nhận ItemsMoved của move và undo");
        var last = lan.Moved.Last();
        Assert.True(last.Undo);
        Assert.Equal(new CountsResult(3, 0), last.Counts);
    }

    [Fact]
    public async Task List_response_includes_counts_of_both_buckets_regardless_of_filters()
    {
        await TestDb.SeedAsync(
            new SeedRow("a1", Score: 0.9f), new SeedRow("a2", Score: 0.1f), new SeedRow("a3", Score: 0.1f),
            new SeedRow("x1", Score: 0.1f, Excluded: true));

        var filtered = await ListAsync("?bucket=excluded&minScore=0.5");

        Assert.Empty(filtered.Items);                    // bộ lọc không khớp gì
        Assert.Equal(new CountsResult(3, 1), filtered.Counts);
    }

    [Fact]
    public async Task Counts_endpoint_still_works_for_older_clients()
    {
        await TestDb.SeedAsync(new SeedRow("a1"), new SeedRow("x1", Excluded: true));

        Assert.Equal(new CountsResult(1, 1), await CountsAsync());
    }
}
