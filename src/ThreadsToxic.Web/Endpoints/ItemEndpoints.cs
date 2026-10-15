using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
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
    string? Source,      // "main" | "review" | lý do cụ thể (vd "duplicate_content"); trống = mọi nguồn
    int? Page,
    int? PageSize,
    string? Review,      // "unreviewed" | "reviewed"; trống = tất cả
    string? ReviewedBy); // lọc theo người xử lý gần nhất

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
    DateTimeOffset? ExcludedAt,
    string? Source,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt);

// Tên người thao tác lấy từ phiên phía server (header X-Session-Token), không nhận từ body.
// Force = true: vượt qua khóa của người khác (được ghi audit với action có tiền tố "force_").
public record BucketCounts(int Active, int Excluded);

public record MoveRequest(List<string> Ids, bool ToExcluded, bool Force = false);

// Hoàn tác: ToExcluded là trạng thái muốn đưa về (ngược với thao tác ban đầu).
public record UndoRequest(List<string> Ids, bool ToExcluded);

public record ApproveRequest(List<string> Ids);

public record ReviewerCount(string Name, int Count);

public record ProgressResult(int Active, int Excluded, int Reviewed, int ReviewedToday,
    List<ReviewerCount> ByReviewer, List<ReviewerCount> TodayByReviewer);

public static class ItemEndpoints
{
    private const int MaxMoveBatch = 500;

    // Token phiên: tuyệt đối không ghi log header này (không bật HttpLogging cho request header).
    public const string SessionHeader = "X-Session-Token";

    public static void MapItemEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/items");
        group.MapGet("/", ListAsync);
        group.MapGet("/counts", CountsAsync);
        group.MapPost("/move", MoveAsync);
        group.MapPost("/undo", UndoAsync);
        group.MapPost("/approve", ApproveAsync);
        group.MapGet("/progress", ProgressAsync);
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] ItemQuery q, IDbContextFactory<AppDbContext> dbf, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page ?? 1);
        var pageSize = Math.Clamp(q.PageSize ?? 50, 10, 200);

        // Ba truy vấn độc lập chạy song song, mỗi cái một kết nối
        var totalTask = dbf.RunAsync(db => Filter(db, q).CountAsync(ct), ct);
        var rowsTask = dbf.RunAsync(db => Filter(db, q)
            .OrderByDescending(c => c.ToxicScore)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new { c.Id, c.Content, c.AuthorUsername, c.AuthorName, c.PostUrl, c.CommentUrl,
                c.PostedAt, c.ReplyLevel, c.ToxicScore, c.Severity, c.F0, c.F1, c.F2, c.F3,
                c.AnalysisData, c.IsExcluded, c.ExcludedBy, c.ExcludedAt, c.Source, c.ReviewedBy, c.ReviewedAt })
            .ToListAsync(ct), ct);
        var countsTask = dbf.RunAsync(db => GetCountsAsync(db, ct), ct);
        await Task.WhenAll(totalTask, rowsTask, countsTask);

        var items = rowsTask.Result.Select(c => new ItemDto(
            c.Id, c.Content, c.AuthorUsername, c.AuthorName, c.PostUrl, c.CommentUrl,
            c.PostedAt, c.ReplyLevel, c.ToxicScore, c.Severity,
            c.F0, c.F1, c.F2, c.F3,
            c.AnalysisData.MatchedWords, c.AnalysisData.Categories,
            c.IsExcluded, c.ExcludedBy, c.ExcludedAt, c.Source, c.ReviewedBy, c.ReviewedAt));

        return Results.Ok(new { total = totalTask.Result, page, pageSize, items, counts = countsTask.Result });
    }

    private static IQueryable<Comment> Filter(AppDbContext db, ItemQuery q)
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
            // Phải truyền escape tường minh: ILike 2 tham số bị EF sinh thành "ESCAPE ''",
            // khi đó '\' do EscapeLike thêm vào không còn là ký tự escape.
            query = query.Where(c => EF.Functions.ILike(c.Content, pattern, @"\"));
        }

        if (!string.IsNullOrWhiteSpace(q.Word))
        {
            // analysis_data @> {"matched_words": ["..."]}: dùng được GIN index jsonb
            var containment = JsonSerializer.Serialize(new { matched_words = new[] { q.Word.Trim() } });
            query = query.Where(c => EF.Functions.JsonContains(c.AnalysisData, containment));
        }

        if (string.Equals(q.Review, "unreviewed", StringComparison.OrdinalIgnoreCase))
            query = query.Where(c => c.ReviewedBy == null);
        else if (string.Equals(q.Review, "reviewed", StringComparison.OrdinalIgnoreCase))
            query = query.Where(c => c.ReviewedBy != null);

        if (!string.IsNullOrWhiteSpace(q.ReviewedBy))
        {
            var reviewer = q.ReviewedBy.Trim();
            query = query.Where(c => c.ReviewedBy == reviewer);
        }

        if (!string.IsNullOrWhiteSpace(q.Source))
        {
            var source = q.Source.Trim();
            query = source.ToLowerInvariant() switch
            {
                "main" => query.Where(c => c.Source == null),
                "review" => query.Where(c => c.Source != null),
                _ => query.Where(c => c.Source == source),
            };
        }

        return query;
    }

    private static async Task<IResult> CountsAsync(AppDbContext db, CancellationToken ct) =>
        Results.Ok(await GetCountsAsync(db, ct));

    private static async Task<BucketCounts> GetCountsAsync(AppDbContext db, CancellationToken ct)
    {
        var counts = await db.Comments
            .GroupBy(c => c.IsExcluded)
            .Select(g => new { Excluded = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return new BucketCounts(
            Active: counts.FirstOrDefault(x => !x.Excluded)?.Count ?? 0,
            Excluded: counts.FirstOrDefault(x => x.Excluded)?.Count ?? 0);
    }

    private static async Task<Results<Ok<object>, BadRequest<string>, UnauthorizedHttpResult>> MoveAsync(
        MoveRequest req,
        [FromHeader(Name = SessionHeader)] string? sessionToken,
        AppDbContext db,
        IHubContext<CollabHub> hub,
        CollabState state,
        CancellationToken ct)
    {
        var session = state.ResolveSession(sessionToken);
        if (session is null)
            return TypedResults.Unauthorized();
        var userName = session.UserName;

        var ids = (req.Ids ?? new()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length is 0)
            return TypedResults.BadRequest("Chưa chọn bình luận nào.");
        if (ids.Length > MaxMoveBatch)
            return TypedResults.BadRequest($"Mỗi lần chỉ thao tác tối đa {MaxMoveBatch} bình luận.");

        // Bình luận đang bị NGƯỜI KHÁC giữ lease còn hạn thì bỏ qua (trừ khi Force).
        // Kiểm tra khóa và UPDATE không nguyên tử: nếu có người Focus đúng lúc đó thì dòng vẫn đổi
        // và biến mất khỏi màn hình họ; dữ liệu không mất vì khôi phục được.
        var heldByOthers = state.GetLeases(DateTimeOffset.UtcNow)
            .Where(lease => lease.Holder != userName)
            .Select(lease => lease.ItemId)
            .ToHashSet();
        var lockedIds = req.Force ? [] : ids.Where(heldByOthers.Contains).ToArray();
        var forcedIds = req.Force ? ids.Where(heldByOthers.Contains).ToArray() : [];
        var targetIds = lockedIds.Length == 0 ? ids : ids.Except(lockedIds).ToArray();

        if (targetIds.Length == 0)
            return TypedResults.Ok<object>(new { moved = 0, skipped = 0, locked = lockedIds });

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
                SET is_excluded = TRUE, excluded_by = {userName}, excluded_at = NOW(),
                    reviewed_by = {userName}, reviewed_at = NOW()
                WHERE id = ANY({targetIds}) AND is_excluded = FALSE
                RETURNING id
                """).ToListAsync(ct);
        }
        else
        {
            action = "restore";
            changed = await db.Database.SqlQuery<string>($"""
                UPDATE comments
                SET is_excluded = FALSE, excluded_by = NULL, excluded_at = NULL,
                    reviewed_by = {userName}, reviewed_at = NOW()
                WHERE id = ANY({targetIds}) AND is_excluded = TRUE
                RETURNING id
                """).ToListAsync(ct);
        }

        if (changed.Count > 0)
        {
            var changedArray = changed.ToArray();
            var forceAction = "force_" + action;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO moderation_audits (comment_id, user_name, action)
                SELECT x, {userName}, CASE WHEN x = ANY({forcedIds}) THEN {forceAction} ELSE {action} END
                FROM unnest({changedArray}) AS x
                """, ct);
        }

        await tx.CommitAsync(ct);

        await BroadcastMovedAsync(db, hub, state, changed, req.ToExcluded, userName, undo: false, ct);

        return TypedResults.Ok<object>(new
        {
            moved = changed.Count,
            movedIds = changed,       // đúng các id đã đổi thật: dùng cho Hoàn tác
            skipped = targetIds.Length - changed.Count,
            locked = lockedIds,
        });
    }

    // Hoàn tác thao tác của CHÍNH MÌNH: chỉ áp dụng cho bình luận mà audit gần nhất vẫn là của người gọi.
    // Bình luận mà người khác đã thao tác sau đó được báo trong "conflicts" và không bị đổi.
    private static async Task<Results<Ok<object>, BadRequest<string>, UnauthorizedHttpResult>> UndoAsync(
        UndoRequest req,
        [FromHeader(Name = SessionHeader)] string? sessionToken,
        AppDbContext db,
        IHubContext<CollabHub> hub,
        CollabState state,
        CancellationToken ct)
    {
        var session = state.ResolveSession(sessionToken);
        if (session is null)
            return TypedResults.Unauthorized();
        var userName = session.UserName;

        var ids = (req.Ids ?? new()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length is 0)
            return TypedResults.BadRequest("Chưa chọn bình luận nào.");
        if (ids.Length > MaxMoveBatch)
            return TypedResults.BadRequest($"Mỗi lần chỉ thao tác tối đa {MaxMoveBatch} bình luận.");

        // Hoàn tác cũng tôn trọng khóa của người khác (giống move, không có force).
        var heldByOthers = state.GetLeases(DateTimeOffset.UtcNow)
            .Where(lease => lease.Holder != userName)
            .Select(lease => lease.ItemId)
            .ToHashSet();
        var lockedIds = ids.Where(heldByOthers.Contains).ToArray();
        var candidates = ids.Except(lockedIds).ToArray();

        if (candidates.Length == 0)
        {
            return TypedResults.Ok<object>(new
            {
                undone = 0, undoneIds = Array.Empty<string>(), conflicts = Array.Empty<string>(),
                skipped = 0, locked = lockedIds,
            });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Khóa các dòng trước khi đọc audit để không ai chen vào giữa lúc kiểm tra và cập nhật.
        await db.Database.SqlQuery<string>($"""
            SELECT id FROM comments WHERE id = ANY({candidates}) ORDER BY id FOR UPDATE
            """).ToListAsync(ct);

        var mine = await db.Database.SqlQuery<string>($"""
            SELECT comment_id FROM (
                SELECT DISTINCT ON (comment_id) comment_id, user_name
                FROM moderation_audits
                WHERE comment_id = ANY({candidates})
                ORDER BY comment_id, id DESC
            ) latest
            WHERE user_name = {userName}
            """).ToListAsync(ct);

        var eligible = mine.ToArray();
        var conflicts = candidates.Except(eligible).ToArray();

        var changed = new List<string>();
        if (eligible.Length > 0)
        {
            if (req.ToExcluded)
            {
                changed = await db.Database.SqlQuery<string>($"""
                    UPDATE comments
                    SET is_excluded = TRUE, excluded_by = {userName}, excluded_at = NOW(),
                        reviewed_by = {userName}, reviewed_at = NOW()
                    WHERE id = ANY({eligible}) AND is_excluded = FALSE
                    RETURNING id
                    """).ToListAsync(ct);
            }
            else
            {
                changed = await db.Database.SqlQuery<string>($"""
                    UPDATE comments
                    SET is_excluded = FALSE, excluded_by = NULL, excluded_at = NULL,
                        reviewed_by = {userName}, reviewed_at = NOW()
                    WHERE id = ANY({eligible}) AND is_excluded = TRUE
                    RETURNING id
                    """).ToListAsync(ct);
            }
        }

        if (changed.Count > 0)
        {
            var changedArray = changed.ToArray();
            var action = req.ToExcluded ? "exclude" : "restore";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO moderation_audits (comment_id, user_name, action)
                SELECT x, {userName}, {action} FROM unnest({changedArray}) AS x
                """, ct);
        }

        await tx.CommitAsync(ct);

        await BroadcastMovedAsync(db, hub, state, changed, req.ToExcluded, userName, undo: true, ct);

        return TypedResults.Ok<object>(new
        {
            undone = changed.Count,
            undoneIds = changed,
            conflicts,
            skipped = eligible.Length - changed.Count,
            locked = lockedIds,
        });
    }

    // Giữ lại: đánh dấu đã duyệt, bình luận vẫn ở "Đang lọc". Chỉ áp dụng cho bình luận đang lọc.
    private static async Task<Results<Ok<object>, BadRequest<string>, UnauthorizedHttpResult>> ApproveAsync(
        ApproveRequest req,
        [FromHeader(Name = SessionHeader)] string? sessionToken,
        AppDbContext db,
        IHubContext<CollabHub> hub,
        CollabState state,
        CancellationToken ct)
    {
        var session = state.ResolveSession(sessionToken);
        if (session is null)
            return TypedResults.Unauthorized();
        var userName = session.UserName;

        var ids = (req.Ids ?? new()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length is 0)
            return TypedResults.BadRequest("Chưa chọn bình luận nào.");
        if (ids.Length > MaxMoveBatch)
            return TypedResults.BadRequest($"Mỗi lần chỉ thao tác tối đa {MaxMoveBatch} bình luận.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var approved = await db.Database.SqlQuery<string>($"""
            UPDATE comments
            SET reviewed_by = {userName}, reviewed_at = NOW()
            WHERE id = ANY({ids}) AND is_excluded = FALSE
            RETURNING id
            """).ToListAsync(ct);

        if (approved.Count > 0)
        {
            var approvedArray = approved.ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO moderation_audits (comment_id, user_name, action)
                SELECT x, {userName}, 'approve' FROM unnest({approvedArray}) AS x
                """, ct);
        }
        await tx.CommitAsync(ct);

        if (approved.Count > 0)
            await hub.Clients.Group(CollabHub.Room).SendAsync("ItemsReviewed", new { ids = approved, by = userName }, ct);

        return TypedResults.Ok<object>(new
        {
            approved = approved.Count,
            approvedIds = approved,
            skipped = ids.Length - approved.Count,
        });
    }

    // Tiến độ lọc: tổng đã xử lý và theo từng người (tổng và trong ngày theo giờ máy chủ)
    private static async Task<IResult> ProgressAsync(IDbContextFactory<AppDbContext> dbf, CancellationToken ct)
    {
        // Npgsql chỉ nhận timestamptz theo UTC: đổi mốc đầu ngày địa phương sang UTC
        var startOfToday = new DateTimeOffset(DateTime.Today).ToUniversalTime();
        static IQueryable<Comment> Reviewed(AppDbContext db) =>
            db.Comments.AsNoTracking().Where(c => c.ReviewedBy != null);

        // Gom nhóm trả về anonymous rồi map trong bộ nhớ (projection về record không dịch được sang SQL)
        static async Task<List<ReviewerCount>> TopReviewersAsync(IQueryable<Comment> reviewed, CancellationToken ct) =>
            (await reviewed
                .GroupBy(c => c.ReviewedBy!)
                .Select(g => new { Name = g.Key, Count = g.Count() })
                .OrderByDescending(r => r.Count)
                .Take(30)
                .ToListAsync(ct))
            .Select(r => new ReviewerCount(r.Name, r.Count)).ToList();

        // Năm truy vấn độc lập chạy song song, mỗi cái một kết nối
        var countsTask = dbf.RunAsync(db => GetCountsAsync(db, ct), ct);
        var reviewedTask = dbf.RunAsync(db => Reviewed(db).CountAsync(ct), ct);
        var reviewedTodayTask = dbf.RunAsync(db => Reviewed(db).CountAsync(c => c.ReviewedAt >= startOfToday, ct), ct);
        var byReviewerTask = dbf.RunAsync(db => TopReviewersAsync(Reviewed(db), ct), ct);
        var todayByReviewerTask = dbf.RunAsync(
            db => TopReviewersAsync(Reviewed(db).Where(c => c.ReviewedAt >= startOfToday), ct), ct);
        await Task.WhenAll(countsTask, reviewedTask, reviewedTodayTask, byReviewerTask, todayByReviewerTask);

        var counts = countsTask.Result;
        return Results.Ok(new ProgressResult(counts.Active, counts.Excluded, reviewedTask.Result,
            reviewedTodayTask.Result, byReviewerTask.Result, todayByReviewerTask.Result));
    }

    private static async Task BroadcastMovedAsync(
        AppDbContext db, IHubContext<CollabHub> hub, CollabState state, List<string> changed,
        bool toExcluded, string userName, bool undo, CancellationToken ct)
    {
        if (changed.Count == 0) return;

        foreach (var id in state.ReleaseItems(changed))
            await hub.Clients.Group(CollabHub.Room).SendAsync("LockChanged", id, null, ct);

        // Số đếm tính sau commit để client cập nhật huy hiệu ngay. Hai thao tác chồng nhau có thể
        // làm số đếm của sự kiện đến sau cũ hơn; client vẫn tải lại một lần để về đúng trạng thái.
        var counts = await GetCountsAsync(db, ct);

        await hub.Clients.Group(CollabHub.Room).SendAsync("ItemsMoved",
            new { ids = changed, toExcluded, by = userName, undo, counts }, ct);
    }

    // Tránh ký tự đại diện của LIKE bị hiểu nhầm khi người dùng gõ % hoặc _
    internal static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
