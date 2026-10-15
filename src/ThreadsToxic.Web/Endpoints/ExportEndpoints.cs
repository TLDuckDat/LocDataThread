using Microsoft.EntityFrameworkCore;
using ThreadsToxic.Web.Data;
using ThreadsToxic.Web.Services.Export;

namespace ThreadsToxic.Web.Endpoints;

public record ExportQuery(
    string? Status,      // all (mặc định) | bad | ambiguous | clean
    string? Type,        // all (mặc định) | root | reply
    int? MinLength,      // mặc định 2
    int? MaxLength,      // mặc định 300
    bool? Dedup,         // mặc định true
    bool? Reviewed,      // true: chỉ bình luận đã có người xử lý (reviewed_by)
    string[]? Columns);  // cột cần xuất, theo thứ tự; trống = tất cả

/// <summary>
/// Xuất "BẢNG 4 CỘT CHUẨN ĐỂ TỰ ĐÁNH GIÁ" giống trang "📥 Xuất dữ liệu Excel & CSV" của crawler.
/// Chỉ lấy bình luận trong mục "Đang lọc": bình luận đã loại không có trong file, giống như đã xóa bên crawler.
/// </summary>
public static class ExportEndpoints
{
    // Bên crawler xem trước bằng LIMIT 60 rồi mới lọc, nên mẫu có thể ít hơn 60 dòng
    public const int PreviewLimit = 60;

    // Xuất toàn bộ: tải bình luận theo lô thay vì một truy vấn khổng lồ (dễ bị pooler của Supabase
    // cắt giữa chừng), vài lô chạy song song để bù độ trễ mỗi lượt đi về database.
    internal const int LoadBatchSize = 2000;
    private const int LoadParallelism = 4;

    public static void MapExportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/export/curated");
        group.MapGet("/", PreviewAsync);
        group.MapGet("/xlsx", XlsxAsync);
        group.MapGet("/csv", CsvAsync);
    }

    private static async Task<IResult> PreviewAsync(
        [AsParameters] ExportQuery q, IDbContextFactory<AppDbContext> dbf, CancellationToken ct)
    {
        if (Validate(q) is { } error) return Results.BadRequest(error);
        var options = ToOptions(q);
        var sources = await LoadAsync(dbf, options, PreviewLimit, q.Reviewed == true, ct);
        var rows = CuratedExport.Build(sources, options, limited: true);
        return Results.Ok(new { columns = CuratedExport.Columns, rows, previewLimit = PreviewLimit });
    }

    private static async Task<IResult> XlsxAsync(
        [AsParameters] ExportQuery q, IDbContextFactory<AppDbContext> dbf, CancellationToken ct)
    {
        if (Validate(q) is { } error) return Results.BadRequest(error);
        var (options, columns, rows) = await BuildAllAsync(q, dbf, ct);
        return Results.File(CuratedFiles.WriteXlsx(columns, rows), CuratedFiles.XlsxContentType,
            CuratedFiles.FileName(options.Status, "xlsx", DateTime.Now));
    }

    private static async Task<IResult> CsvAsync(
        [AsParameters] ExportQuery q, IDbContextFactory<AppDbContext> dbf, CancellationToken ct)
    {
        if (Validate(q) is { } error) return Results.BadRequest(error);
        var (options, columns, rows) = await BuildAllAsync(q, dbf, ct);
        return Results.File(CuratedFiles.WriteCsv(columns, rows), CuratedFiles.CsvContentType,
            CuratedFiles.FileName(options.Status, "csv", DateTime.Now));
    }

    private static async Task<(CuratedExportOptions, IReadOnlyList<string>, List<string[]>)> BuildAllAsync(
        ExportQuery q, IDbContextFactory<AppDbContext> dbf, CancellationToken ct)
    {
        var options = ToOptions(q);
        var sources = await LoadAsync(dbf, options, limit: null, q.Reviewed == true, ct);
        var columns = CuratedExport.ResolveColumns(q.Columns);
        var rows = CuratedExport.Project(CuratedExport.Build(sources, options), columns);
        return (options, columns, rows);
    }

    private static string? Validate(ExportQuery q)
    {
        if (q.Status is { } status && !CuratedExport.Statuses.Contains(status))
            return "Nhóm dữ liệu không hợp lệ (all, bad, ambiguous, clean).";
        if (q.Type is { } type && !CuratedExport.CommentTypes.Contains(type))
            return "Loại bình luận không hợp lệ (all, root, reply).";
        if (q.MinLength is < 0 or > 500)
            return "Độ dài tối thiểu phải từ 0 đến 500 ký tự.";
        if (q.MaxLength is < 10 or > 10000)
            return "Độ dài tối đa phải từ 10 đến 10000 ký tự.";
        return null;
    }

    private static CuratedExportOptions ToOptions(ExportQuery q) => new(
        Status: q.Status ?? "all",
        CommentType: q.Type ?? "all",
        MinLength: q.MinLength ?? 2,
        MaxLength: q.MaxLength ?? 300,
        Deduplicate: q.Dedup ?? true);

    // Tương đương câu SQL của get_curated_export_df: comments LEFT JOIN posts, lọc trạng thái/loại,
    // ORDER BY scraped_at DESC (so sánh nhị phân như SQLite, NULL xếp cuối).
    // Tải từ từ thay vì một lần: (1) chỉ lấy id theo đúng thứ tự (nhẹ), (2) song song: bài viết một lần
    // (không lặp nội dung bài viết trên mỗi bình luận như JOIN) và bình luận theo lô LoadBatchSize,
    // tối đa LoadParallelism lô cùng lúc, lô bị đứt kết nối thì tải lại; (3) ghép lại theo thứ tự ở (1).
    private static async Task<List<CuratedSource>> LoadAsync(
        IDbContextFactory<AppDbContext> dbf, CuratedExportOptions options, int? limit, bool reviewedOnly,
        CancellationToken ct)
    {
        var keys = await dbf.RunWithRetryAsync(db =>
        {
            var ordered = Filter(db, options, reviewedOnly)
                .OrderBy(c => c.ScrapedAt == null)
                .ThenByDescending(c => EF.Functions.Collate(c.ScrapedAt, "C"))
                .ThenByDescending(c => c.SourceRowid);
            return (limit is { } n ? ordered.Take(n) : ordered)
                .Select(c => new CommentKey(c.Id, c.PostId))
                .ToListAsync(ct);
        }, ct);
        if (keys.Count == 0) return [];

        var postIds = keys.Select(k => k.PostId).Distinct().ToList();
        var postsTask = dbf.RunWithRetryAsync(db => db.Posts.AsNoTracking()
            .Where(p => postIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct), ct);

        var batches = keys.Select(k => k.Id).Chunk(LoadBatchSize).ToList();
        var loaded = new List<CommentRow>[batches.Count];
        var batchesTask = Parallel.ForEachAsync(Enumerable.Range(0, batches.Count),
            new ParallelOptions { MaxDegreeOfParallelism = LoadParallelism, CancellationToken = ct },
            async (i, token) =>
            {
                var batch = batches[i];
                // Lọc lại bằng điều kiện ở (1): bình luận vừa bị loại trong lúc tải thì không xuất
                loaded[i] = await dbf.RunWithRetryAsync(db => Filter(db, options, reviewedOnly)
                    .Where(c => batch.Contains(c.Id))
                    .Select(c => new CommentRow(
                        c.Id, c.SourceRowid, c.PostId, c.Content, c.AuthorUsername, c.AuthorName,
                        c.F0, c.F1, c.F2, c.F3, c.ReplyLevel, c.IsReply, c.ToxicScore, c.Severity,
                        c.ReviewStatusVi, c.UserReview, c.UserReviewVi, c.UserKeywords, c.AnalysisData))
                    .ToListAsync(token), token);
            });
        await Task.WhenAll(postsTask, batchesTask);

        var posts = postsTask.Result;
        var rows = loaded.SelectMany(batch => batch).ToDictionary(r => r.Id);
        var sources = new List<CuratedSource>(keys.Count);
        foreach (var key in keys)
        {
            if (!rows.TryGetValue(key.Id, out var r)) continue;
            var p = posts.GetValueOrDefault(r.PostId);
            sources.Add(new CuratedSource(
                r.Id, r.SourceRowid, r.PostId, r.Content, r.AuthorUsername, r.AuthorName,
                r.F0, r.F1, r.F2, r.F3, r.ReplyLevel, r.IsReply, r.ToxicScore, r.Severity,
                r.ReviewStatusVi, r.UserReview, r.UserReviewVi, r.UserKeywords, r.AnalysisData.Categories,
                p?.Content, p?.AuthorUsername, p?.AuthorName, p?.Url, p?.Categories));
        }
        return sources;
    }

    private static IQueryable<Comment> Filter(AppDbContext db, CuratedExportOptions options, bool reviewedOnly)
    {
        var comments = db.Comments.AsNoTracking().Where(c => !c.IsExcluded);
        if (reviewedOnly)
            comments = comments.Where(c => c.ReviewedBy != null);

        comments = options.Status switch
        {
            "bad" => comments.Where(c => c.ReviewStatus == "bad" || c.ToxicScore >= 0.5),
            "ambiguous" => comments.Where(c => c.ReviewStatus == "ambiguous" || (c.ToxicScore >= 0.15 && c.ToxicScore < 0.5)),
            "clean" => comments.Where(c => c.ReviewStatus == "clean" || c.ToxicScore < 0.15),
            _ => comments,
        };

        return options.CommentType switch
        {
            "root" => comments.Where(c => !c.IsReply),
            "reply" => comments.Where(c => c.IsReply),
            _ => comments,
        };
    }

    private sealed record CommentKey(string Id, string PostId);

    private sealed record CommentRow(
        string Id, long? SourceRowid, string PostId, string Content, string? AuthorUsername, string? AuthorName,
        string F0, string F1, string F2, string F3, int ReplyLevel, bool IsReply, float ToxicScore, string? Severity,
        string? ReviewStatusVi, string? UserReview, string? UserReviewVi, string? UserKeywords,
        CommentAnalysis AnalysisData);
}
