using System.Net;
using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public class ListAndCountsTests(AppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Counts_reflect_both_buckets()
    {
        await TestDb.SeedAsync(
            new SeedRow("a1"), new SeedRow("a2"), new SeedRow("a3"),
            new SeedRow("x1", Excluded: true), new SeedRow("x2", Excluded: true));

        var counts = await CountsAsync();

        Assert.Equal(3, counts.Active);
        Assert.Equal(2, counts.Excluded);
    }

    [Fact]
    public async Task List_defaults_to_active_bucket_and_excluded_bucket_is_separate()
    {
        await TestDb.SeedAsync(new SeedRow("a1"), new SeedRow("x1", Excluded: true));

        var active = await ListAsync();
        var excluded = await ListAsync("?bucket=excluded");

        Assert.Equal(["a1"], active.Items.Select(i => i.Id));
        Assert.Equal(["x1"], excluded.Items.Select(i => i.Id));
        Assert.True(excluded.Items[0].IsExcluded);
        Assert.Equal("seed", excluded.Items[0].ExcludedBy);
    }

    [Fact]
    public async Task List_is_ordered_by_score_descending_then_id()
    {
        await TestDb.SeedAsync(
            new SeedRow("b", Score: 0.5f), new SeedRow("a", Score: 0.5f), new SeedRow("c", Score: 0.9f));

        var result = await ListAsync();

        Assert.Equal(["c", "a", "b"], result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task MinScore_keeps_items_at_or_above_threshold()
    {
        await TestDb.SeedAsync(
            new SeedRow("low", Score: 0.2f), new SeedRow("edge", Score: 0.5f), new SeedRow("high", Score: 0.9f));

        var result = await ListAsync("?minScore=0.5");

        Assert.Equal(["high", "edge"], result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task ReplyLevel_filters_exact_level()
    {
        await TestDb.SeedAsync(
            new SeedRow("f0", ReplyLevel: 0), new SeedRow("f1", ReplyLevel: 1, Parent: "f0"),
            new SeedRow("f2", ReplyLevel: 2, Parent: "f1"));

        var result = await ListAsync("?replyLevel=1");

        Assert.Equal(["f1"], result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Keyword_is_case_insensitive_substring_match()
    {
        await TestDb.SeedAsync(
            new SeedRow("m", Content: "Chào bạn VCL quá"), new SeedRow("n", Content: "bình thường"));

        var result = await ListAsync("?keyword=vcl");

        Assert.Equal(["m"], result.Items.Select(i => i.Id));
    }

    [Theory]
    [InlineData("50%", "giảm 50% hôm nay", "giảm 50 phần trăm")]
    [InlineData("a_b", "mã a_b", "mã axb")]
    [InlineData("\\", "đường dẫn c:\\temp", "đường dẫn c:/temp")]
    public async Task Keyword_treats_like_metacharacters_literally(string keyword, string matching, string other)
    {
        await TestDb.SeedAsync(new SeedRow("hit", Content: matching), new SeedRow("miss", Content: other));

        var result = await ListAsync("?keyword=" + Uri.EscapeDataString(keyword));

        Assert.Equal(["hit"], result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Word_filters_on_matched_words_exactly()
    {
        await TestDb.SeedAsync(
            new SeedRow("hit", Words: ["vcl", "dm"]),
            new SeedRow("near", Words: ["vclx"]),
            new SeedRow("none"));

        var result = await ListAsync("?word=vcl");

        Assert.Equal(["hit"], result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Filters_combine_with_and()
    {
        await TestDb.SeedAsync(
            new SeedRow("wrong-level", Score: 0.9f, ReplyLevel: 0, Words: ["vcl"]),
            new SeedRow("both", Score: 0.9f, ReplyLevel: 1, Words: ["vcl"], Parent: "wrong-level"),
            new SeedRow("low", Score: 0.1f, ReplyLevel: 1, Words: ["vcl"], Parent: "wrong-level"));

        var result = await ListAsync("?minScore=0.5&replyLevel=1&word=vcl");

        Assert.Equal(["both"], result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Pagination_returns_total_and_partial_last_page()
    {
        await TestDb.SeedAsync(TestDb.Rows(25));

        var page3 = await ListAsync("?pageSize=10&page=3");

        Assert.Equal(25, page3.Total);
        Assert.Equal(3, page3.Page);
        Assert.Equal(5, page3.Items.Count);
    }

    [Fact]
    public async Task PageSize_is_clamped_between_10_and_200_and_page_below_1_becomes_1()
    {
        await TestDb.SeedAsync(TestDb.Rows(205));

        var tiny = await ListAsync("?pageSize=1");
        var huge = await ListAsync("?pageSize=1000");
        var zero = await ListAsync("?page=0&pageSize=10");

        Assert.Equal(10, tiny.PageSize);
        Assert.Equal(10, tiny.Items.Count);
        Assert.Equal(200, huge.PageSize);
        Assert.Equal(200, huge.Items.Count);
        Assert.Equal(1, zero.Page);
    }

    [Fact]
    public async Task Source_filter_separates_main_data_from_review_queue()
    {
        await TestDb.SeedAsync(
            new SeedRow("main1"),
            new SeedRow("dup1", Source: "duplicate_content"),
            new SeedRow("nonvi1", Source: "non_vietnamese"),
            new SeedRow("nonvi2", Source: "non_vietnamese", Excluded: true));

        var all = await ListAsync();
        var main = await ListAsync("?source=main");
        var review = await ListAsync("?source=review");
        var nonVi = await ListAsync("?source=non_vietnamese");
        var nonViExcluded = await ListAsync("?bucket=excluded&source=non_vietnamese");

        Assert.Equal(3, all.Total);
        Assert.Equal(["main1"], main.Items.Select(i => i.Id));
        Assert.Null(main.Items[0].Source);
        Assert.Equal(["dup1", "nonvi1"], review.Items.Select(i => i.Id).Order());
        Assert.Equal(["nonvi1"], nonVi.Items.Select(i => i.Id));
        Assert.Equal("non_vietnamese", nonVi.Items[0].Source);
        Assert.Equal(["nonvi2"], nonViExcluded.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Unknown_source_returns_empty_and_counts_still_cover_everything()
    {
        await TestDb.SeedAsync(new SeedRow("main1"), new SeedRow("dup1", Source: "duplicate_content"));

        var result = await ListAsync("?source=khong_ton_tai");

        Assert.Equal(0, result.Total);
        Assert.Equal(2, result.Counts!.Active);
    }

    [Fact]
    public async Task Items_from_review_queue_can_be_moved_like_any_other()
    {
        await TestDb.SeedAsync(new SeedRow("dup1", Source: "duplicate_content"));
        var (status, moved) = await MoveAsync(["dup1"], toExcluded: true, "An");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, moved!.Moved);
        Assert.True(await TestDb.IsExcludedAsync("dup1"));
    }
}
