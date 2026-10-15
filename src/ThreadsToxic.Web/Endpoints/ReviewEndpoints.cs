using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ThreadsToxic.Web.Data;
using ThreadsToxic.Web.Hubs;
using ThreadsToxic.Web.Services;

namespace ThreadsToxic.Web.Endpoints;

public record ReviewQuery(
    string? Decision,    // pending (mặc định) | promoted | rejected
    string? Type,        // comment | post; trống = cả hai
    string? Reason,      // duplicate_id | duplicate_content | non_vietnamese | meta_ai ...
    string? Keyword,     // tìm trong nội dung
    string? InMain,      // hide (mặc định: ẩn bản đã có trong CSDL chính) | only | all
    int? Page,
    int? PageSize);

public record ReviewIdsRequest(List<string> Ids);

public record ReviewBulkRequest(
    string Action,          // promote | reject
    string? Type,
    string? Reason,
    string? Keyword,
    int ExpectedCount);     // số lượng người dùng đã thấy trên màn hình; sai lệch thì từ chối

public record ReviewItemDto(
    string Id, string RecordType, string Reason, string RecordId, string PostId, string PostUrl,
    string? CommentUrl, string? AuthorUsername, string? AuthorName, string Content, string? PostedAt,
    int ReplyLevel, string F0, string F1, string F2, string F3, float ToxicScore, string? Severity,
    string? ReviewStatusVi, List<string> MatchedWords, string? Categories, int Occurrences,
    string? LastSeen, bool ExistsInMain, string Decision, string? DecidedBy, DateTimeOffset? DecidedAt);

public record ReasonCount(string RecordType, string Reason, int Count);

public record ReviewCounts(int Pending, int PendingInMain, int Promoted, int Rejected, List<ReasonCount> Reasons);

public record ReviewActionResult(int Promoted, int PromotedComments, int PromotedPosts, int Rejected,
    int Restored, int SkippedInMain, List<string> Changed);

/// <summary>
/// Kho phụ: bản ghi từ threads_review.db chờ người dùng quyết định.
/// "Đưa vào CSDL chính" tạo bình luận trong mục Đang lọc (gắn source = lý do), "Loại" đánh dấu bỏ qua.
/// Mọi thao tác ghi cần token phiên, tên người thao tác lấy từ phiên phía server.
/// </summary>
public static class ReviewEndpoints
{
    private const int MaxBatch = 500;
    private const int MaxBulk = 20000;
    private const int BulkChunk = 500;

    private static readonly string[] Decisions = ["pending", "promoted", "rejected"];

    public static void MapReviewEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/review");
        group.MapGet("/", ListAsync);
        group.MapGet("/counts", CountsAsync);
        group.MapPost("/promote", PromoteAsync);
        group.MapPost("/reject", RejectAsync);
        group.MapPost("/restore", RestoreAsync);
        group.MapPost("/bulk", BulkAsync);
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] ReviewQuery q, IDbContextFactory<AppDbContext> dbf, CancellationToken ct)
    {
        var decision = (q.Decision ?? "pending").Trim().ToLowerInvariant();
        if (!Decisions.Contains(decision))
            return Results.BadRequest("Trạng thái không hợp lệ.");

        var page = Math.Max(1, q.Page ?? 1);
        var pageSize = Math.Clamp(q.PageSize ?? 50, 10, 200);
        IQueryable<ReviewQueueItem> Filtered(AppDbContext db) => Filter(db, decision, q.Type, q.Reason, q.Keyword, q.InMain);

        // Đếm, lấy trang và thống kê chạy song song, mỗi truy vấn một kết nối
        var totalTask = dbf.RunAsync(db => Filtered(db).CountAsync(ct), ct);
        var rowsTask = dbf.RunAsync(db => Filtered(db)
            .OrderByDescending(r => r.LastSeen)
            .ThenBy(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct), ct);
        var countsTask = GetCountsAsync(dbf, ct);
        await Task.WhenAll(totalTask, rowsTask, countsTask);

        var items = rowsTask.Result.Select(ToDto).ToList();
        return Results.Ok(new { total = totalTask.Result, page, pageSize, items, counts = countsTask.Result });
    }

    // Chỉ số đếm (huy hiệu "Kho phụ" ở trang chính), không tải bản ghi nào
    private static async Task<IResult> CountsAsync(IDbContextFactory<AppDbContext> dbf, CancellationToken ct) =>
        Results.Ok(await GetCountsAsync(dbf, ct));

    private static IQueryable<ReviewQueueItem> Filter(
        AppDbContext db, string decision, string? type, string? reason, string? keyword, string? inMain)
    {
        var query = db.ReviewQueue.AsNoTracking().Where(r => r.Decision == decision);

        // Mặc định ẩn bản đã có trong CSDL chính (tự đóng khi nạp)
        query = (inMain ?? "hide").ToLowerInvariant() switch
        {
            "only" => query.Where(r => r.ExistsInMain),
            "all" => query,
            _ => query.Where(r => !r.ExistsInMain),
        };

        if (type is "comment" or "post")
            query = query.Where(r => r.RecordType == type);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            var value = reason.Trim();
            query = query.Where(r => r.Reason == value);
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{ItemEndpoints.EscapeLike(keyword.Trim())}%";
            // Phải truyền escape tường minh (xem ItemEndpoints.ListAsync)
            query = query.Where(r => EF.Functions.ILike(r.Content, pattern, @"\"));
        }
        return query;
    }

    private static async Task<ReviewCounts> GetCountsAsync(IDbContextFactory<AppDbContext> dbf, CancellationToken ct)
    {
        var decisionsTask = dbf.RunAsync(db => db.ReviewQueue
            .GroupBy(r => r.Decision)
            .Select(g => new { Decision = g.Key, Count = g.Count() })
            .ToListAsync(ct), ct);
        var pendingInMainTask = dbf.RunAsync(
            db => db.ReviewQueue.CountAsync(r => r.Decision == "pending" && r.ExistsInMain, ct), ct);
        var reasonsTask = dbf.RunAsync(db => db.ReviewQueue
            .AsNoTracking()
            .Where(r => r.Decision == "pending" && !r.ExistsInMain)
            .GroupBy(r => new { r.RecordType, r.Reason })
            .Select(g => new { g.Key.RecordType, g.Key.Reason, Count = g.Count() })
            .OrderByDescending(r => r.Count)
            .ToListAsync(ct), ct);
        await Task.WhenAll(decisionsTask, pendingInMainTask, reasonsTask);

        var decisions = decisionsTask.Result;
        var pendingInMain = pendingInMainTask.Result;
        var reasons = reasonsTask.Result.Select(r => new ReasonCount(r.RecordType, r.Reason, r.Count)).ToList();

        int Count(string decision) => decisions.FirstOrDefault(d => d.Decision == decision)?.Count ?? 0;
        return new ReviewCounts(
            Pending: reasons.Sum(r => r.Count),
            PendingInMain: pendingInMain,
            Promoted: Count("promoted"),
            Rejected: Count("rejected"),
            Reasons: reasons);
    }

    private static ReviewItemDto ToDto(ReviewQueueItem r) => new(
        r.Id, r.RecordType, r.Reason, r.RecordId, r.PostId, r.PostUrl, r.CommentUrl,
        r.AuthorUsername, r.AuthorName, r.Content, r.PostedAt, r.ReplyLevel,
        r.F0, r.F1, r.F2, r.F3, r.ToxicScore, r.Severity, r.ReviewStatusVi,
        r.AnalysisData.MatchedWords, r.Categories, r.Occurrences, r.LastSeen,
        r.ExistsInMain, r.Decision, r.DecidedBy, r.DecidedAt);

    private static async Task<Results<Ok<ReviewActionResult>, BadRequest<string>, UnauthorizedHttpResult>> PromoteAsync(
        ReviewIdsRequest req,
        [FromHeader(Name = ItemEndpoints.SessionHeader)] string? sessionToken,
        AppDbContext db,
        IHubContext<CollabHub> hub,
        CollabState state,
        CancellationToken ct)
    {
        var session = state.ResolveSession(sessionToken);
        if (session is null) return TypedResults.Unauthorized();

        var ids = CleanIds(req.Ids);
        if (ids.Length is 0) return TypedResults.BadRequest("Chưa chọn bản ghi nào.");
        if (ids.Length > MaxBatch) return TypedResults.BadRequest($"Mỗi lần chỉ thao tác tối đa {MaxBatch} bản ghi.");

        var result = await PromoteCoreAsync(db, hub, session.UserName, ids, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<ReviewActionResult>, BadRequest<string>, UnauthorizedHttpResult>> RejectAsync(
        ReviewIdsRequest req,
        [FromHeader(Name = ItemEndpoints.SessionHeader)] string? sessionToken,
        AppDbContext db,
        IHubContext<CollabHub> hub,
        CollabState state,
        CancellationToken ct)
    {
        var session = state.ResolveSession(sessionToken);
        if (session is null) return TypedResults.Unauthorized();

        var ids = CleanIds(req.Ids);
        if (ids.Length is 0) return TypedResults.BadRequest("Chưa chọn bản ghi nào.");
        if (ids.Length > MaxBatch) return TypedResults.BadRequest($"Mỗi lần chỉ thao tác tối đa {MaxBatch} bản ghi.");

        var result = await RejectCoreAsync(db, hub, session.UserName, ids, ct);
        return TypedResults.Ok(result);
    }

    // Hoàn tác "Loại": chỉ người đã loại mới đưa về được chờ duyệt.
    // Đưa vào CSDL chính không hoàn tác ở đây: bình luận đã vào thì dùng Loại ở trang chính.
    private static async Task<Results<Ok<ReviewActionResult>, BadRequest<string>, UnauthorizedHttpResult>> RestoreAsync(
        ReviewIdsRequest req,
        [FromHeader(Name = ItemEndpoints.SessionHeader)] string? sessionToken,
        AppDbContext db,
        IHubContext<CollabHub> hub,
        CollabState state,
        CancellationToken ct)
    {
        var session = state.ResolveSession(sessionToken);
        if (session is null) return TypedResults.Unauthorized();

        var ids = CleanIds(req.Ids);
        if (ids.Length is 0) return TypedResults.BadRequest("Chưa chọn bản ghi nào.");
        if (ids.Length > MaxBatch) return TypedResults.BadRequest($"Mỗi lần chỉ thao tác tối đa {MaxBatch} bản ghi.");

        var userName = session.UserName;
        var restored = await db.Database.SqlQuery<string>($"""
            UPDATE review_queue
            SET decision = 'pending', decided_by = NULL, decided_at = NULL
            WHERE id = ANY({ids}) AND decision = 'rejected' AND decided_by = {userName}
            RETURNING id
            """).ToListAsync(ct);

        if (restored.Count > 0)
            await hub.Clients.Group(CollabHub.Room).SendAsync("QueueChanged",
                new { restored = restored.Count, by = userName }, ct);

        return TypedResults.Ok(new ReviewActionResult(0, 0, 0, 0, restored.Count, 0, restored));
    }

    // Hàng loạt: áp dụng cho mọi bản ghi khớp bộ lọc đang xem. Số lượng phải khớp với số người dùng đã thấy,
    // nếu không (dữ liệu đã đổi) thì từ chối để không làm trên một tập khác với tập họ đã xem.
    private static async Task<Results<Ok<ReviewActionResult>, BadRequest<string>, UnauthorizedHttpResult, Conflict<object>>> BulkAsync(
        ReviewBulkRequest req,
        [FromHeader(Name = ItemEndpoints.SessionHeader)] string? sessionToken,
        AppDbContext db,
        IHubContext<CollabHub> hub,
        CollabState state,
        CancellationToken ct)
    {
        var session = state.ResolveSession(sessionToken);
        if (session is null) return TypedResults.Unauthorized();

        var action = (req.Action ?? "").Trim().ToLowerInvariant();
        if (action is not ("promote" or "reject"))
            return TypedResults.BadRequest("Hành động không hợp lệ (promote | reject).");

        var filtered = Filter(db, "pending", req.Type, req.Reason, req.Keyword, "hide");
        var ids = await filtered.Select(r => r.Id).OrderBy(id => id).Take(MaxBulk + 1).ToListAsync(ct);
        if (ids.Count > MaxBulk)
            return TypedResults.BadRequest($"Tập này có hơn {MaxBulk} bản ghi; hãy thu hẹp bộ lọc trước.");
        if (ids.Count != req.ExpectedCount)
            return TypedResults.Conflict<object>(new { message = "Dữ liệu đã thay đổi, hãy tải lại danh sách.", current = ids.Count });
        if (ids.Count == 0)
            return TypedResults.Ok(new ReviewActionResult(0, 0, 0, 0, 0, 0, []));

        var total = new ReviewActionResult(0, 0, 0, 0, 0, 0, []);
        var userName = session.UserName;
        for (var start = 0; start < ids.Count; start += BulkChunk)
        {
            var chunk = ids.Skip(start).Take(BulkChunk).ToArray();
            var part = action == "promote"
                ? await PromoteCoreAsync(db, hub, userName, chunk, ct)
                : await RejectCoreAsync(db, hub, userName, chunk, ct);
            total = Sum(total, part);
        }
        return TypedResults.Ok(total);
    }

    private static async Task<ReviewActionResult> PromoteCoreAsync(
        AppDbContext db, IHubContext<CollabHub> hub, string userName, string[] ids, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Khóa các dòng đủ điều kiện: hai người bấm cùng lúc không đưa trùng được.
        var eligible = await db.Database.SqlQuery<string>($"""
            SELECT id FROM review_queue
            WHERE id = ANY({ids}) AND decision = 'pending' AND exists_in_main = FALSE
            ORDER BY id
            FOR UPDATE
            """).ToListAsync(ct);

        var insertedComments = eligible.Count == 0
            ? new List<string>()
            : await db.Database.SqlQuery<string>($"""
                INSERT INTO comments (
                    id, post_id, post_url, comment_url, author_username, author_name, content, posted_at,
                    likes, reply_level, parent_comment_id, f0, f1, f2, f3, toxic_score, severity,
                    analysis_data, scraped_at, is_reply, review_status, review_status_vi, user_keywords,
                    source, is_excluded)
                SELECT q.id, q.post_id, q.post_url, q.comment_url, q.author_username, q.author_name, q.content,
                    q.posted_at, q.likes, q.reply_level, q.parent_comment_id, q.f0, q.f1, q.f2, q.f3,
                    q.toxic_score, q.severity, q.analysis_data, q.first_seen, q.is_reply, q.review_status,
                    q.review_status_vi, '[]', q.reason, FALSE
                FROM review_queue q
                WHERE q.id = ANY({eligible}) AND q.record_type = 'comment'
                ON CONFLICT (id) DO NOTHING
                RETURNING id
                """).ToListAsync(ct);

        var insertedPosts = eligible.Count == 0
            ? new List<string>()
            : await db.Database.SqlQuery<string>($"""
                INSERT INTO posts (id, url, author_username, author_name, content, categories)
                SELECT q.id, q.post_url, q.author_username, q.author_name, q.content, q.categories
                FROM review_queue q
                WHERE q.id = ANY({eligible}) AND q.record_type = 'post'
                ON CONFLICT (id) DO NOTHING
                RETURNING id
                """).ToListAsync(ct);

        var inserted = insertedComments.Concat(insertedPosts).ToArray();

        // Trùng id với dữ liệu chính (xuất hiện sau lần nạp): đánh dấu và không đưa
        var conflicted = eligible.Except(inserted).ToArray();
        if (conflicted.Length > 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE review_queue SET exists_in_main = TRUE WHERE id = ANY({conflicted})
                """, ct);
        }

        if (inserted.Length > 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE review_queue SET decision = 'promoted', decided_by = {userName}, decided_at = NOW()
                WHERE id = ANY({inserted}) AND decision = 'pending'
                """, ct);
        }

        if (insertedComments.Count > 0)
        {
            var commentArray = insertedComments.ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO moderation_audits (comment_id, user_name, action)
                SELECT x, {userName}, 'promote' FROM unnest({commentArray}) AS x
                """, ct);
        }

        await tx.CommitAsync(ct);

        if (inserted.Length > 0)
            await hub.Clients.Group(CollabHub.Room).SendAsync("QueueChanged",
                new { promoted = inserted.Length, by = userName }, ct);

        return new ReviewActionResult(inserted.Length, insertedComments.Count, insertedPosts.Count, 0, 0,
            conflicted.Length, inserted.ToList());
    }

    private static async Task<ReviewActionResult> RejectCoreAsync(
        AppDbContext db, IHubContext<CollabHub> hub, string userName, string[] ids, CancellationToken ct)
    {
        var rejected = await db.Database.SqlQuery<string>($"""
            UPDATE review_queue
            SET decision = 'rejected', decided_by = {userName}, decided_at = NOW()
            WHERE id = ANY({ids}) AND decision = 'pending'
            RETURNING id
            """).ToListAsync(ct);

        if (rejected.Count > 0)
            await hub.Clients.Group(CollabHub.Room).SendAsync("QueueChanged",
                new { rejected = rejected.Count, by = userName }, ct);

        return new ReviewActionResult(0, 0, 0, rejected.Count, 0, 0, rejected);
    }

    private static ReviewActionResult Sum(ReviewActionResult a, ReviewActionResult b) => new(
        a.Promoted + b.Promoted, a.PromotedComments + b.PromotedComments, a.PromotedPosts + b.PromotedPosts,
        a.Rejected + b.Rejected, a.Restored + b.Restored, a.SkippedInMain + b.SkippedInMain,
        a.Changed.Concat(b.Changed).ToList());

    private static string[] CleanIds(IEnumerable<string>? ids) =>
        (ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
}
