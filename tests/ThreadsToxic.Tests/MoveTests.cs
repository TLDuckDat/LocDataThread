using System.Net;
using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public class MoveTests(AppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Exclude_then_restore_round_trips_state_and_counts()
    {
        await TestDb.SeedAsync(TestDb.Rows(5));
        string[] ids = ["t_001", "t_002"];

        var (status, excluded) = await MoveAsync(ids, toExcluded: true, "Minh");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, excluded!.Moved);
        Assert.Equal(0, excluded.Skipped);
        Assert.True(await TestDb.IsExcludedAsync("t_001"));
        Assert.Equal(new CountsResult(3, 2), await CountsAsync());

        var (_, restored) = await MoveAsync(ids, toExcluded: false, "Minh");

        Assert.Equal(2, restored!.Moved);
        Assert.False(await TestDb.IsExcludedAsync("t_001"));
        Assert.Equal(new CountsResult(5, 0), await CountsAsync());
    }

    [Fact]
    public async Task Excluding_records_who_excluded_and_restoring_clears_it()
    {
        await TestDb.SeedAsync(new SeedRow("a"));

        await MoveAsync(["a"], toExcluded: true, "Lan");
        var excluded = (await ListAsync("?bucket=excluded")).Items.Single();
        Assert.Equal("Lan", excluded.ExcludedBy);

        await MoveAsync(["a"], toExcluded: false, "Lan");
        var active = (await ListAsync()).Items.Single();
        Assert.Null(active.ExcludedBy);
    }

    [Fact]
    public async Task Repeating_the_same_move_changes_nothing()
    {
        await TestDb.SeedAsync(TestDb.Rows(3));
        string[] ids = ["t_001", "t_002", "t_003"];

        await MoveAsync(ids, true, "Minh");
        var (_, again) = await MoveAsync(ids, true, "Minh");

        Assert.Equal(0, again!.Moved);
        Assert.Equal(3, again.Skipped);
        Assert.Equal(3, (await TestDb.AuditsAsync()).Count);
    }

    [Fact]
    public async Task Mixed_batch_moves_only_rows_in_the_other_bucket()
    {
        await TestDb.SeedAsync(new SeedRow("act"), new SeedRow("exc", Excluded: true));

        var (_, result) = await MoveAsync(["act", "exc", "missing"], toExcluded: true, "Huy");

        Assert.Equal(1, result!.Moved);
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public async Task Each_change_writes_exactly_one_audit_row()
    {
        await TestDb.SeedAsync(TestDb.Rows(3));

        await MoveAsync(["t_001", "t_002"], true, "Minh");
        await MoveAsync(["t_001"], false, "Lan");

        var audits = await TestDb.AuditsAsync();
        Assert.Equal(3, audits.Count);
        Assert.Contains(("t_001", "Minh", "exclude"), audits);
        Assert.Contains(("t_002", "Minh", "exclude"), audits);
        Assert.Contains(("t_001", "Lan", "restore"), audits);
    }

    [Fact]
    public async Task Four_parallel_requests_on_the_same_batch_change_each_row_once()
    {
        await TestDb.SeedAsync(TestDb.Rows(20));
        var ids = Enumerable.Range(1, 20).Select(i => $"t_{i:000}").ToArray();

        var results = await Task.WhenAll(
            new[] { "Minh", "Lan", "Huy", "Thu" }.Select(user => MoveAsync(ids, true, user)));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        Assert.Equal(20, results.Sum(r => r.Result!.Moved));
        Assert.Equal(20, (await TestDb.AuditsAsync()).Count);
        Assert.Equal(new CountsResult(0, 20), await CountsAsync());
    }

    [Fact]
    public async Task Request_without_ids_is_rejected()
    {
        var (status, _) = await MoveAsync([], true, "Minh");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Batch_over_500_ids_is_rejected()
    {
        var ids = Enumerable.Range(0, 501).Select(i => $"id_{i}");

        var (status, _) = await MoveAsync(ids, true, "Minh");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }
}
