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
    string? ReviewedBy,  // lọc theo người xử lý gần nhất
    string? Assigned,    // "me" (phần của người gọi) | "none" (chưa giao) | tên người; trống = tất cả
    string? Order);      // "thread": theo bài viết rồi theo luồng F0 -> F3; trống = điểm giảm dần

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
    DateTimeOffset? ReviewedAt,
    string? ThreadParentId,
    string? AssignedTo);

// Tên người thao tác lấy từ phiên phía server (header X-Session-Token), không nhận từ body.
// Force = true: vượt qua khóa của người khác (được ghi audit với action có tiền tố "force_").
public record BucketCounts(int Active, int Excluded);

// WithAncestors = true (khôi phục): khôi phục kèm cha/ông đang ở "Đã loại" thay vì bị chặn.
public record MoveRequest(List<string> Ids, bool ToExcluded, bool Force = false, bool WithAncestors = false);

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
        var group = app.MapGroup("/api/items").RequireUser();
        group.MapGet("/", ListAsync);
        group.MapGet("/counts", CountsAsync);
        group.MapPost("/move", MoveAsync);
        group.MapPost("/undo", UndoAsync);
        group.MapPost("/approve", ApproveAsync);
        group.MapGet("/progress", ProgressAsync);
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] ItemQuery q, IDbContextFactory<AppDbContext> dbf, HttpContext http, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page ?? 1);
        var pageSize = Math.Clamp(q.PageSize ?? 50, 10, 200);
        var me = AuthFilters.CurrentUser(http)?.Name;

        // Theo luồng: cùng bài, cùng gốc F0; trong luồng sắp theo (F1, F2, cấp) nên cha luôn đứng trước con
        IQueryable<Comment> Ordered(IQueryable<Comment> query) =>
            string.Equals(q.Order, "thread", StringComparison.OrdinalIgnoreCase)
                ? query.OrderBy(c => c.PostId).ThenBy(c => c.ThreadRootId).ThenBy(c => c.F1).ThenBy(c => c.F2)
                    .ThenBy(c => c.ReplyLevel).ThenBy(c => c.Id)
                : query.OrderByDescending(c => c.ToxicScore).ThenBy(c => c.Id);

        // Ba truy vấn độc lập chạy song song, mỗi cái một kết nối
        var totalTask = dbf.RunAsync(db => Filter(db, q, me).CountAsync(ct), ct);
        var rowsTask = dbf.RunAsync(db => Ordered(Filter(db, q, me))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new { c.Id, c.Content, c.AuthorUsername, c.AuthorName, c.PostUrl, c.CommentUrl,
                c.PostedAt, c.ReplyLevel, c.ToxicScore, c.Severity, c.F0, c.F1, c.F2, c.F3,
                c.AnalysisData, c.IsExcluded, c.ExcludedBy, c.ExcludedAt, c.Source, c.ReviewedBy, c.ReviewedAt,
                c.ThreadParentId, c.AssignedTo })
            .ToListAsync(ct), ct);
        var countsTask = dbf.RunAsync(db => GetCountsAsync(db, ct), ct);
        await Task.WhenAll(totalTask, rowsTask, countsTask);

        var items = rowsTask.Result.Select(c => new ItemDto(
            c.Id, c.Content, c.AuthorUsername, c.AuthorName, c.PostUrl, c.CommentUrl,
            c.PostedAt, c.ReplyLevel, c.ToxicScore, c.Severity,
            c.F0, c.F1, c.F2, c.F3,
            c.AnalysisData.MatchedWords, c.AnalysisData.Categories,
            c.IsExcluded, c.ExcludedBy, c.ExcludedAt, c.Source, c.ReviewedBy, c.ReviewedAt, c.ThreadParentId, c.AssignedTo));

        return Results.Ok(new { total = totalTask.Result, page, pageSize, items, counts = countsTask.Result });
    }

    // me: tên người gọi (cho Assigned=me)
    private static IQueryable<Comment> Filter(AppDbContext db, ItemQuery q, string? me)
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

        if (!string.IsNullOrWhiteSpace(q.Assigned))
        {
            var assigned = q.Assigned.Trim();
            if (string.Equals(assigned, "none", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(c => c.AssignedTo == null);
            }
            else
            {
                var name = string.Equals(assigned, "me", StringComparison.OrdinalIgnoreCase) ? me : assigned;
                query = query.Where(c => c.AssignedTo == name);
            }
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

    private static async Task<Results<Ok<object>, BadRequest<string>, UnauthorizedHttpResult, Conflict<string>, JsonHttpResult<string>>> MoveAsync(
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
        // "Vẫn chuyển" (vượt khóa của người khác) chỉ dành cho admin
        if (req.Force && !session.IsAdmin)
            return AuthFilters.AdminOnly();

        var ids = (req.Ids ?? new()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
        if (ids.Length is 0)
            return TypedResults.BadRequest("Chưa chọn bình luận nào.");
        if (ids.Length > MaxMoveBatch)
            return TypedResults.BadRequest($"Mỗi lần chỉ thao tác tối đa {MaxMoveBatch} bình luận.");

        // Bình luận đang bị NGƯỜI KHÁC giữ lease còn hạn thì bỏ qua (trừ khi Force), kể cả khi nó chỉ nằm
        // trong nhánh bị kéo theo. Kiểm tra khóa và UPDATE không nguyên tử: nếu có người Focus đúng lúc đó
        // thì dòng vẫn đổi và biến mất khỏi màn hình họ; dữ liệu không mất vì khôi phục được.
        var heldByOthers = HeldByOthers(state, userName);

        MoveOutcome outcome;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                outcome = req.ToExcluded
                    ? await ExcludeCoreAsync(db, ids, heldByOthers, req.Force, userName, ct)
                    : await RestoreCoreAsync(db, ids, heldByOthers, req.Force, req.WithAncestors, userName, ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ThreadChain.IsChainViolation(ex))
            {
                return TypedResults.Conflict(ChainConflictMessage);
            }
        }

        await BroadcastMovedAsync(db, hub, state, outcome.AllChanged, req.ToExcluded, userName, undo: false, ct);

        return TypedResults.Ok<object>(new
        {
            moved = outcome.Changed.Count,
            movedIds = outcome.AllChanged,          // đúng các id đã đổi thật (gồm nhánh kéo theo): dùng cho Hoàn tác
            skipped = outcome.Skipped,
            locked = outcome.Locked,
            cascaded = outcome.Cascaded,            // con cháu bị loại theo
            restoredAncestors = outcome.Ancestors,  // cha/ông được khôi phục kèm (WithAncestors)
            blocked = outcome.Blocked,              // không khôi phục được vì cha/ông vẫn ở "Đã loại"
            blockingAncestors = outcome.BlockingAncestors,
        });
    }

    private const string ChainConflictMessage =
        "Luồng hội thoại vừa bị người khác thay đổi (cha/con không còn khớp). Tải lại rồi thử lại.";

    private static HashSet<string> HeldByOthers(CollabState state, string userName) =>
        state.GetLeases(DateTimeOffset.UtcNow)
            .Where(lease => lease.Holder != userName)
            .Select(lease => lease.ItemId)
            .ToHashSet();

    // Changed: bình luận được chọn đã đổi thật. AllChanged: thêm nhánh kéo theo
    // (con cháu khi loại, tổ tiên khi khôi phục cả chuỗi).
    private sealed record MoveOutcome(
        List<string> Changed, List<string> AllChanged, List<string> Cascaded, List<string> Ancestors,
        string[] Locked, int Skipped, string[] Blocked, string[] BlockingAncestors);

    private static readonly MoveOutcome NoChange = new([], [], [], [], [], 0, [], []);

    // Loại kèm toàn bộ con cháu đang lọc (cha bị loại thì con phải bị loại, xem migration 007).
    // Con cháu ghi audit "exclude_cascade" với cause_id là bình luận được chọn.
    private static async Task<MoveOutcome> ExcludeCoreAsync(
        AppDbContext db, string[] ids, HashSet<string> heldByOthers, bool force, string userName, CancellationToken ct)
    {
        var descendants = await ThreadChain.DescendantsAsync(db, ids, ct);

        // Không loại nửa vời: nếu một bình luận trong nhánh đang bị người khác xem thì cả nhánh bị bỏ qua
        bool BranchHeld(string id) =>
            heldByOthers.Contains(id)
            || descendants.Any(d => d.Origin == id && !d.IsExcluded && heldByOthers.Contains(d.Id));
        var lockedIds = force ? [] : ids.Where(BranchHeld).ToArray();
        var targetIds = ids.Except(lockedIds).ToArray();
        var forcedIds = force ? targetIds.Where(BranchHeld).ToArray() : [];
        var cascade = descendants.Where(d => targetIds.Contains(d.Origin)).ToList();
        var allIds = targetIds.Concat(cascade.Select(d => d.Id)).ToArray();

        if (allIds.Length == 0)
            return NoChange with { Locked = lockedIds };

        await db.Database.SqlQuery<string>($"""
            SELECT id FROM comments WHERE id = ANY({allIds}) ORDER BY id FOR UPDATE
            """).ToListAsync(ct);

        // Cập nhật có điều kiện: chỉ dòng thực sự đổi bucket mới được trả về.
        // Hai người bấm cùng lúc sẽ không tạo ra thao tác trùng.
        var changedAll = await db.Database.SqlQuery<string>($"""
            UPDATE comments
            SET is_excluded = TRUE, excluded_by = {userName}, excluded_at = NOW(),
                reviewed_by = CASE WHEN id = ANY({targetIds}) THEN {userName} ELSE reviewed_by END,
                reviewed_at = CASE WHEN id = ANY({targetIds}) THEN NOW() ELSE reviewed_at END
            WHERE id = ANY({allIds}) AND is_excluded = FALSE
            RETURNING id
            """).ToListAsync(ct);

        var changedSet = changedAll.ToHashSet();
        var changed = targetIds.Where(changedSet.Contains).ToList();
        var cascaded = cascade.Where(d => changedSet.Contains(d.Id)).ToList();

        if (changed.Count > 0)
        {
            var changedArray = changed.ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO moderation_audits (comment_id, user_name, action)
                SELECT x, {userName}, CASE WHEN x = ANY({forcedIds}) THEN 'force_exclude' ELSE 'exclude' END
                FROM unnest({changedArray}) AS x
                """, ct);
        }
        if (cascaded.Count > 0)
        {
            var cascadedIds = cascaded.Select(d => d.Id).ToArray();
            var origins = cascaded.Select(d => d.Origin).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO moderation_audits (comment_id, user_name, action, cause_id)
                SELECT x.id, {userName}, 'exclude_cascade', x.origin
                FROM unnest({cascadedIds}, {origins}) AS x(id, origin)
                """, ct);
        }

        return new MoveOutcome(changed, changedAll, cascaded.Select(d => d.Id).ToList(), [],
            lockedIds, targetIds.Length - changed.Count, [], []);
    }

    // Khôi phục. Bình luận có cha/ông vẫn ở "Đã loại" bị chặn (nằm trong Blocked), trừ khi
    // withAncestors: khi đó tổ tiên đã loại được khôi phục kèm (audit "restore", cause_id = bình luận được chọn).
    // Khôi phục không kéo theo con cháu: con bị loại vẫn ở "Đã loại" cho tới khi được chọn.
    private static async Task<MoveOutcome> RestoreCoreAsync(
        AppDbContext db, string[] ids, HashSet<string> heldByOthers, bool force, bool withAncestors,
        string userName, CancellationToken ct)
    {
        var idSet = ids.ToHashSet();
        var excludedAncestors = (await ThreadChain.AncestorsAsync(db, ids, ct))
            .Where(a => a.IsExcluded && !idSet.Contains(a.Id))
            .ToList();

        string[] blocked = withAncestors
            ? []
            : ids.Where(id => excludedAncestors.Any(a => a.Origin == id)).ToArray();
        var blocking = excludedAncestors.Where(a => blocked.Contains(a.Origin)).Select(a => a.Id).Distinct().ToArray();

        var candidates = ids.Except(blocked).ToArray();
        bool BranchHeld(string id) =>
            heldByOthers.Contains(id)
            || (withAncestors && excludedAncestors.Any(a => a.Origin == id && heldByOthers.Contains(a.Id)));
        var lockedIds = force ? [] : candidates.Where(BranchHeld).ToArray();
        var targetIds = candidates.Except(lockedIds).ToArray();
        var forcedIds = force ? targetIds.Where(BranchHeld).ToArray() : [];
        var pulled = withAncestors
            ? excludedAncestors.Where(a => targetIds.Contains(a.Origin)).DistinctBy(a => a.Id).ToList()
            : [];
        var allIds = targetIds.Concat(pulled.Select(a => a.Id)).ToArray();

        if (allIds.Length == 0)
            return NoChange with { Locked = lockedIds, Blocked = blocked, BlockingAncestors = blocking };

        await db.Database.SqlQuery<string>($"""
            SELECT id FROM comments WHERE id = ANY({allIds}) ORDER BY id FOR UPDATE
            """).ToListAsync(ct);

        var changedAll = await db.Database.SqlQuery<string>($"""
            UPDATE comments
            SET is_excluded = FALSE, excluded_by = NULL, excluded_at = NULL,
                reviewed_by = {userName}, reviewed_at = NOW()
            WHERE id = ANY({allIds}) AND is_excluded = TRUE
            RETURNING id
            """).ToListAsync(ct);

        var changedSet = changedAll.ToHashSet();
        var changed = targetIds.Where(changedSet.Contains).ToList();
        var ancestors = pulled.Where(a => changedSet.Contains(a.Id)).ToList();

        if (changed.Count > 0)
        {
            var changedArray = changed.ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO moderation_audits (comment_id, user_name, action)
                SELECT x, {userName}, CASE WHEN x = ANY({forcedIds}) THEN 'force_restore' ELSE 'restore' END
                FROM unnest({changedArray}) AS x
                """, ct);
        }
        if (ancestors.Count > 0)
        {
            var ancestorIds = ancestors.Select(a => a.Id).ToArray();
            var origins = ancestors.Select(a => a.Origin).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO moderation_audits (comment_id, user_name, action, cause_id)
                SELECT x.id, {userName}, 'restore', x.origin
                FROM unnest({ancestorIds}, {origins}) AS x(id, origin)
                """, ct);
        }

        return new MoveOutcome(changed, changedAll, [], ancestors.Select(a => a.Id).ToList(),
            lockedIds, targetIds.Length - changed.Count, blocked, blocking);
    }

    // Hoàn tác thao tác của CHÍNH MÌNH: chỉ áp dụng cho bình luận mà audit gần nhất vẫn là của người gọi.
    // Bình luận mà người khác đã thao tác sau đó được báo trong "conflicts" và không bị đổi.
    // Hoàn tác một lần loại cũng khôi phục đúng các con cháu đã bị loại theo lần đó (exclude_cascade của mình);
    // hoàn tác một lần khôi phục thì loại lại (lan xuống con cháu), kèm tổ tiên đã được khôi phục cùng lần đó.
    private static async Task<Results<Ok<object>, BadRequest<string>, UnauthorizedHttpResult, Conflict<string>>> UndoAsync(
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
        var heldByOthers = HeldByOthers(state, userName);
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

        MoveOutcome outcome;
        var conflicts = new List<string>();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                // Khóa các dòng trước khi đọc audit để không ai chen vào giữa lúc kiểm tra và cập nhật.
                await db.Database.SqlQuery<string>($"""
                    SELECT id FROM comments WHERE id = ANY({candidates}) ORDER BY id FOR UPDATE
                    """).ToListAsync(ct);

                var eligible = (await LatestAuditsByAsync(db, candidates, userName, ct)).ToArray();
                conflicts.AddRange(candidates.Except(eligible));

                // Những dòng mình đã kéo theo trong thao tác gốc (audit gần nhất vẫn là của mình, cause_id thuộc eligible)
                var dragged = (await DraggedByAsync(db, eligible, userName, ct))
                    .Where(d => !eligible.Contains(d.Id))
                    .ToList();

                if (req.ToExcluded)
                {
                    var reExclude = eligible
                        .Concat(dragged.Where(d => d.Action == "restore").Select(d => d.Id))
                        .ToArray();
                    outcome = await ExcludeCoreAsync(db, reExclude, heldByOthers, force: false, userName, ct);
                }
                else
                {
                    var restoreSet = eligible
                        .Concat(dragged.Where(d => d.Action == "exclude_cascade").Select(d => d.Id))
                        .ToHashSet();

                    // Không để con đứng dưới cha đã loại: bỏ những dòng có cha vẫn bị loại và không cùng được khôi phục
                    var parents = await ThreadChain.ParentsAsync(db, restoreSet.ToArray(), ct);
                    bool removed;
                    do
                    {
                        removed = false;
                        foreach (var id in restoreSet.ToList())
                        {
                            if (parents.TryGetValue(id, out var p) && p.ParentExcluded && !restoreSet.Contains(p.ParentId))
                            {
                                restoreSet.Remove(id);
                                removed = true;
                            }
                        }
                    } while (removed);

                    conflicts.AddRange(eligible.Where(id => !restoreSet.Contains(id)));
                    outcome = restoreSet.Count == 0
                        ? NoChange
                        : await RestoreAllAsync(db, restoreSet.ToArray(), eligible, userName, ct);
                }

                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ThreadChain.IsChainViolation(ex))
            {
                return TypedResults.Conflict(ChainConflictMessage);
            }
        }

        await BroadcastMovedAsync(db, hub, state, outcome.AllChanged, req.ToExcluded, userName, undo: true, ct);

        return TypedResults.Ok<object>(new
        {
            undone = outcome.Changed.Count,
            undoneIds = outcome.AllChanged,
            conflicts,
            skipped = outcome.Skipped,
            locked = lockedIds.Concat(outcome.Locked).ToArray(),
            cascaded = outcome.Cascaded,
        });
    }

    private sealed record AuditRef(string Id, string Action);

    // Các bình luận mà audit gần nhất là của userName
    private static async Task<List<string>> LatestAuditsByAsync(
        AppDbContext db, string[] ids, string userName, CancellationToken ct) =>
        await db.Database.SqlQuery<string>($"""
            SELECT comment_id FROM (
                SELECT DISTINCT ON (comment_id) comment_id, user_name
                FROM moderation_audits
                WHERE comment_id = ANY({ids})
                ORDER BY comment_id, id DESC
            ) latest
            WHERE user_name = {userName}
            """).ToListAsync(ct);

    // Các bình luận bị kéo theo bởi thao tác của userName trên origins, và audit gần nhất của chúng vẫn là lần kéo theo đó
    private static async Task<List<AuditRef>> DraggedByAsync(
        AppDbContext db, string[] origins, string userName, CancellationToken ct)
    {
        if (origins.Length == 0) return [];
        return await db.Database.SqlQuery<AuditRef>($"""
            SELECT comment_id AS id, action FROM (
                SELECT DISTINCT ON (comment_id) comment_id, user_name, action, cause_id
                FROM moderation_audits
                WHERE comment_id IN (SELECT comment_id FROM moderation_audits WHERE cause_id = ANY({origins}))
                ORDER BY comment_id, id DESC
            ) latest
            WHERE user_name = {userName} AND cause_id = ANY({origins})
              AND action IN ('exclude_cascade', 'restore')
            """).ToListAsync(ct);
    }

    // Khôi phục cả tập (dùng cho hoàn tác): bình luận được chọn ghi "restore", phần kéo theo ghi kèm cause_id cũ
    private static async Task<MoveOutcome> RestoreAllAsync(
        AppDbContext db, string[] ids, string[] selected, string userName, CancellationToken ct)
    {
        await db.Database.SqlQuery<string>($"""
            SELECT id FROM comments WHERE id = ANY({ids}) ORDER BY id FOR UPDATE
            """).ToListAsync(ct);

        var changedAll = await db.Database.SqlQuery<string>($"""
            UPDATE comments
            SET is_excluded = FALSE, excluded_by = NULL, excluded_at = NULL,
                reviewed_by = {userName}, reviewed_at = NOW()
            WHERE id = ANY({ids}) AND is_excluded = TRUE
            RETURNING id
            """).ToListAsync(ct);

        if (changedAll.Count > 0)
        {
            var changedArray = changedAll.ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO moderation_audits (comment_id, user_name, action, cause_id)
                SELECT x, {userName}, 'restore',
                       CASE WHEN x = ANY({selected}) THEN NULL
                            ELSE (SELECT a.cause_id FROM moderation_audits a
                                  WHERE a.comment_id = x ORDER BY a.id DESC LIMIT 1) END
                FROM unnest({changedArray}) AS x
                """, ct);
        }

        var changed = changedAll.Where(selected.Contains).ToList();
        var dragged = changedAll.Except(changed).ToList();
        return new MoveOutcome(changed, changedAll, dragged, [], [], ids.Count(selected.Contains) - changed.Count, [], []);
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
