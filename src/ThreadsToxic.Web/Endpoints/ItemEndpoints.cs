using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ThreadsToxic.Web.Data;
using ThreadsToxic.Web.Hubs;
using ThreadsToxic.Web.Services;

namespace ThreadsToxic.Web.Endpoints;

public record ItemQuery(
    string? Bucket,      // "active" (mặc định) | "excluded"
    double? MinScore,
    int? ReplyLevel,     // 0..3
    string? Keyword,     // tìm trong nội dung
    string? Word,        // từ khóa đã khớp (JSONB)
    int? Page,
    int? PageSize);

public record ItemDto(
    string Id,
    string Content,
    string? AuthorUsername,
    string? AuthorName,
    string PostUrl,
    string? CommentUrl,
    string? PostedAt,
    int ReplyLevel,
    float ToxicScore,
    string? Severity,
    string F0,
    string F1,
    string F2,
    string F3,
    List<string> MatchedWords,
    List<string> Categories,
    bool IsExcluded,
    string? ExcludedBy,
    DateTimeOffset? ExcludedAt);

public record MoveRequest(List<string> Ids, bool ToExcluded, string UserName);

public static class ItemEndpoints
{
    private const int MaxMoveBatch = 500;

    public static void MapItemEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/items");
        group.MapGet("/", ListAsync);
        group.MapGet("/counts", CountsAsync);
        group.MapPost("/move", MoveAsync);
    }

    private static async Task<IResult> ListAsync([AsParameters] ItemQuery q, AppDbContext db, CancellationToken ct)
    {
        var excluded = string.Equals(q.Bucket, "excluded", StringComparison.OrdinalIgnoreCase);
        var query = db.Comments.AsNoTracking().Where(c => c.IsExcluded == excluded);

        if (q.MinScore is { } minScore)
            query = query.Where(c => c.ToxicScore >= minScore);

        if (q.ReplyLevel is { } level)
            query = query.Where(c => c.ReplyLevel == level);

        if (!string.IsNullOrWhiteSpace(q.Keyword))
        {
            var pattern = $"%{EscapeLike(q.Keyword.Trim())}%";
            query = query.Where(c => EF.Functions.ILike(c.Content, pattern));
        }

        if (!string.IsNullOrWhiteSpace(q.Word))
        {
            // analysis_data @> {"matched_words": ["..."]}: dùng được GIN index jsonb
            var containment = JsonSerializer.Serialize(new { matched_words = new[] { q.Word.Trim() } });
            query = query.Where(c => EF.Functions.JsonContains(c.AnalysisData, containment));
        }

        var page = Math.Max(1, q.Page ?? 1);
        var pageSize = Math.Clamp(q.PageSize ?? 50, 10, 200);

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(c => c.ToxicScore)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new { c.Id, c.Content, c.AuthorUsername, c.AuthorName, c.PostUrl, c.CommentUrl,
                c.PostedAt, c.ReplyLevel, c.ToxicScore, c.Severity, c.F0, c.F1, c.F2, c.F3,
                c.AnalysisData, c.IsExcluded, c.ExcludedBy, c.ExcludedAt })
            .ToListAsync(ct);

        var items = rows.Select(c => new ItemDto(
            c.Id, c.Content, c.AuthorUsername, c.AuthorName, c.PostUrl, c.CommentUrl,
            c.PostedAt, c.ReplyLevel, c.ToxicScore, c.Severity,
            c.F0, c.F1, c.F2, c.F3,
            c.AnalysisData.MatchedWords, c.AnalysisData.Categories,
            c.IsExcluded, c.ExcludedBy, c.ExcludedAt));

        return Results.Ok(new { total, page, pageSize, items });
    }

    private static async Task<IResult> CountsAsync(AppDbContext db, CancellationToken ct)
    {
        var counts = await db.Comments
            .GroupBy(c => c.IsExcluded)
            .Select(g => new { Excluded = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var excluded = counts.FirstOrDefault(x => x.Excluded)?.Count ?? 0;
        var active = counts.FirstOrDefault(x => !x.Excluded)?.Count ?? 0;
        return Results.Ok(new { active, excluded });
    }

    private static async Task<Results<Ok<object>, BadRequest<string>>> MoveAsync(
        MoveRequest req,
        AppDbContext db,
        IHubContext<CollabHub> hub,
        CollabState state,
        CancellationToken ct)
    {
        var userName = (req.UserName ?? "").Trim();
        if (userName.Length is 0 or > 40)
            return TypedResults.BadRequest("Thiếu tên người thao tác.");

        var ids = (req.Ids ?? new()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length is 0)
            return TypedResults.BadRequest("Chưa chọn bình luận nào.");
        if (ids.Length > MaxMoveBatch)
            return TypedResults.BadRequest($"Mỗi lần chỉ thao tác tối đa {MaxMoveBatch} bình luận.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Cập nhật có điều kiện: chỉ dòng thực sự đổi bucket mới được trả về.
        // Hai người bấm cùng lúc sẽ không tạo ra thao tác trùng.
        List<string> changed;
        string action;
        if (req.ToExcluded)
        {
            action = "exclude";
            changed = await db.Database.SqlQuery<string>($"""
                UPDATE comments
                SET is_excluded = TRUE, excluded_by = {userName}, excluded_at = NOW()
                WHERE id = ANY({ids}) AND is_excluded = FALSE
                RETURNING id
                """).ToListAsync(ct);
        }
        else
        {
            action = "restore";
            changed = await db.Database.SqlQuery<string>($"""
                UPDATE comments
                SET is_excluded = FALSE, excluded_by = NULL, excluded_at = NULL
                WHERE id = ANY({ids}) AND is_excluded = TRUE
                RETURNING id
                """).ToListAsync(ct);
        }

        if (changed.Count > 0)
        {
            var changedArray = changed.ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO moderation_audits (comment_id, user_name, action)
                SELECT x, {userName}, {action} FROM unnest({changedArray}) AS x
                """, ct);
        }

        await tx.CommitAsync(ct);

        if (changed.Count > 0)
        {
            foreach (var id in state.ReleaseItems(changed))
                await hub.Clients.Group(CollabHub.Room).SendAsync("LockChanged", id, null, ct);

            await hub.Clients.Group(CollabHub.Room).SendAsync("ItemsMoved",
                new { ids = changed, toExcluded = req.ToExcluded, by = userName }, ct);
        }

        return TypedResults.Ok<object>(new { moved = changed.Count, skipped = ids.Length - changed.Count });
    }

    // Tránh ký tự đại diện của LIKE bị hiểu nhầm khi người dùng gõ % hoặc _
    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
