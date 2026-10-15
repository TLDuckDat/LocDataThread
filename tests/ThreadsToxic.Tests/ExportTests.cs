using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using ThreadsToxic.Tests.Infrastructure;
using ThreadsToxic.Web.Endpoints;

namespace ThreadsToxic.Tests;

public record PreviewResult(List<string> Columns, List<List<string>> Rows, int PreviewLimit);

public class ExportTests(AppFactory factory) : IntegrationTestBase(factory)
{
    private const string Header =
        "Mã ID,Bài viết,f0,f1,f2,f3,Nội dung,Điểm đánh giá,Chủ đề bài viết," +
        "Tự đánh giá của bạn (Xấu luôn / Chưa rõ / Trong sạch),Từ lóng mới bổ sung (nếu có)";

    // Dữ liệu giống bên crawler: mọi bình luận có review_status, user_review rỗng, user_keywords "[]"
    private static async Task SeedCrawlerRowsAsync(params (string Id, string Content, float Score, string Status, string StatusVi, string PostId, int Rowid)[] rows)
    {
        await TestDb.SeedAsync(rows.Select(r => new SeedRow(r.Id, r.Content, Score: r.Score)).ToArray());
        foreach (var r in rows)
        {
            await TestDb.ExecuteAsync($"""
                UPDATE comments SET post_id = '{r.PostId}', review_status = '{r.Status}', review_status_vi = '{r.StatusVi}',
                    severity = 'Trong sạch', user_review = '', user_review_vi = '', user_keywords = '[]',
                    f0 = content, source_rowid = {r.Rowid}, scraped_at = '2026-10-01T00:00:{r.Rowid:00}'
                WHERE id = '{r.Id}'
                """);
        }
    }

    private async Task<PreviewResult> PreviewAsync(string query = "")
    {
        var response = await Http.GetAsync("/api/export/curated" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PreviewResult>())!;
    }

    private async Task<(string Text, string? FileName)> CsvAsync(string query = "")
    {
        var response = await Http.GetAsync("/api/export/curated/csv" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);                    // UTF-8 BOM như utf-8-sig
        return (Encoding.UTF8.GetString(bytes[3..]), response.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task Only_active_vietnamese_comments_are_exported()
    {
        await SeedCrawlerRowsAsync(
            ("keep", "Bạn ơi đẹp quá", 0f, "clean", "Trong sạch", "p1", 1),
            ("gone", "Bình luận này đã bị loại", 0f, "clean", "Trong sạch", "p1", 2),
            ("english", "hello world", 0f, "clean", "Trong sạch", "p1", 3));
        await TestDb.ExecuteAsync("UPDATE comments SET is_excluded = TRUE WHERE id = 'gone'");

        var preview = await PreviewAsync();

        Assert.Equal(11, preview.Columns.Count);
        Assert.Equal(["keep"], preview.Rows.Select(r => r[0]));
    }

    [Fact]
    public async Task Csv_matches_crawler_format_and_missing_post_topic_is_nan()
    {
        await SeedCrawlerRowsAsync(
            ("c1", "Bài này hay, \"thật\" đấy", 0.7f, "bad", "Xấu luôn (Rõ ràng)", "p1", 1),
            ("c2", "Không có bài viết gốc nhé", 0f, "clean", "Trong sạch", "p_missing", 2));
        await TestDb.ExecuteAsync("""
            INSERT INTO posts (id, url, content, categories)
            VALUES ('p1', 'https://example.test/p1', 'Hôm nay trời đẹp quá', '["Chửi thề / Tục tĩu"]')
            """);

        var (text, fileName) = await CsvAsync();

        Assert.Matches(@"^threads_danh_gia_4_cot_all_\d{8}_\d{6}\.csv$", fileName);
        Assert.Equal(
            Header + "\r\n" +
            "c1,Hôm nay trời đẹp quá,\"Bài này hay, \"\"thật\"\" đấy\",,,,\"Bài này hay, \"\"thật\"\" đấy\",0.7 (Xấu luôn (Rõ ràng)),Chửi thề / Tục tĩu,,\r\n" +
            "c2,,Không có bài viết gốc nhé,,,,Không có bài viết gốc nhé,0.0 (Trong sạch),nan,,\r\n",
            text);
    }

    [Fact]
    public async Task Filters_dedup_and_conversation_order()
    {
        await SeedCrawlerRowsAsync(
            ("b2", "Trùng nội dung nhé", 0.4f, "bad", "Xấu luôn (Rõ ràng)", "p_b", 2),
            ("b1", "Trùng   nội dung nhé", 0.9f, "bad", "Xấu luôn (Rõ ràng)", "p_b", 1),
            ("a1", "Bình thường thôi mà", 0f, "clean", "Trong sạch", "p_a", 3),
            ("amb", "Chưa rõ lắm đâu", 0.35f, "ambiguous", "Chưa rõ (Nghi ngờ)", "p_a", 4));

        // Khử trùng giữ bản điểm cao hơn (b1), rồi xếp theo bài viết và thứ tự gốc
        var all = await PreviewAsync();
        Assert.Equal(["a1", "amb", "b1"], all.Rows.Select(r => r[0]));

        var noDedup = await CsvAsync("?dedup=false");
        Assert.Equal(5, noDedup.Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);

        var bad = await PreviewAsync("?status=bad");
        Assert.Equal(["b1"], bad.Rows.Select(r => r[0]));

        // Như crawler: review_status = 'ambiguous' HOẶC 0.15 <= điểm < 0.5, nên b2 (nhãn bad, điểm 0.4) cũng có
        var ambiguous = await PreviewAsync("?status=ambiguous");
        Assert.Equal(["amb", "b2"], ambiguous.Rows.Select(r => r[0]));

        var shortOnly = await PreviewAsync("?maxLength=15&dedup=false");
        Assert.Equal(["amb"], shortOnly.Rows.Select(r => r[0]));
    }

    [Fact]
    public async Task Full_export_loads_in_batches_and_keeps_order()
    {
        // Ba lô (lô cuối lẻ), có bình luận đã loại rải qua các lô
        var count = ExportEndpoints.LoadBatchSize * 2 + 37;
        await TestDb.ExecuteAsync("INSERT INTO posts (id, url, content) VALUES ('p1', 'https://example.test/p1', 'Bài gốc')");
        await TestDb.ExecuteAsync($$"""
            INSERT INTO comments (id, post_id, post_url, content, reply_level, toxic_score, analysis_data,
                                  is_excluded, excluded_by, excluded_at, f0, f1, f2, f3, review_status, review_status_vi,
                                  severity, user_review, user_review_vi, user_keywords, source_rowid, scraped_at)
            SELECT 'b_' || lpad(i::text, 5, '0'), 'p1', 'https://example.test/p1', 'Bình luận số ' || i, 0, 0.1,
                   '{"matched_words": [], "matched_emojis": [], "categories": [], "has_emoji_slang": false, "is_auto_toxic": false}'::jsonb,
                   i % 500 = 0, CASE WHEN i % 500 = 0 THEN 'seed' END, CASE WHEN i % 500 = 0 THEN NOW() END,
                   'Bình luận số ' || i, '', '', '', 'clean', 'Trong sạch', 'Trong sạch', '', '', '[]',
                   i, '2026-10-01T' || lpad(i::text, 6, '0')
            FROM generate_series(1, {{count}}) AS i
            """);
        // Hai bản trùng nội dung, cùng điểm, nằm ở lô đầu và lô cuối: khử trùng giữ bản đứng trước theo
        // scraped_at giảm dần (bản mới hơn), nên thứ tự ghép các lô phải đúng thứ tự truy vấn.
        var newer = count - 5;
        await TestDb.ExecuteAsync($"UPDATE comments SET content = 'Bình luận trùng', f0 = 'Bình luận trùng' WHERE id IN ('b_00010', 'b_{newer:00000}')");

        var (text, _) = await CsvAsync("?columns=Mã ID&columns=Bài viết");

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal("Mã ID,Bài viết", lines[0]);
        // File xuất toàn bộ xếp theo hội thoại (source_rowid tăng dần), không có bản đã loại và bản trùng cũ hơn
        var expected = Enumerable.Range(1, count)
            .Where(i => i % 500 != 0 && i != 10)
            .Select(i => $"b_{i:00000},Bài gốc");
        Assert.Equal(expected, lines.Skip(1));
    }

    [Fact]
    public async Task Selected_columns_keep_their_order()
    {
        await SeedCrawlerRowsAsync(("c1", "Đẹp quá đi", 0f, "clean", "Trong sạch", "p1", 1));

        var (text, _) = await CsvAsync("?columns=Nội dung&columns=Mã ID&columns=không-tồn-tại");

        Assert.Equal("Nội dung,Mã ID\r\nĐẹp quá đi,c1\r\n", text);
    }

    [Fact]
    public async Task Xlsx_has_crawler_sheet_and_writes_formula_like_text_as_text()
    {
        await SeedCrawlerRowsAsync(("c1", "=))) vui quá đi", 0f, "clean", "Trong sạch", "p1", 1));

        var response = await Http.GetAsync("/api/export/curated/xlsx?columns=Mã ID&columns=Nội dung");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches(@"^threads_danh_gia_4_cot_all_\d{8}_\d{6}\.xlsx$", response.Content.Headers.ContentDisposition?.FileName);

        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        var workbook = await new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open()).ReadToEndAsync();
        var sheet = await new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).ReadToEndAsync();

        Assert.Contains("name=\"Dữ liệu đánh giá\"", workbook);
        Assert.Contains("<col min=\"1\" max=\"1\" width=\"16\" customWidth=\"1\" />", sheet);
        Assert.Contains("<col min=\"2\" max=\"2\" width=\"48\" customWidth=\"1\" />", sheet);
        Assert.Contains("<row r=\"1\" ht=\"28\" customHeight=\"1\">", sheet);
        Assert.Contains("<t>=))) vui quá đi</t>", sheet);
        Assert.DoesNotContain("<f>", sheet);
    }

    [Theory]
    [InlineData("?status=toxic")]
    [InlineData("?type=child")]
    [InlineData("?minLength=-1")]
    [InlineData("?maxLength=5")]
    public async Task Invalid_options_are_rejected(string query)
    {
        var response = await Http.GetAsync("/api/export/curated/xlsx" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
