using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ThreadsToxic.Web.Data;
using ThreadsToxic.Web.Hubs;
using ThreadsToxic.Web.Services;

namespace ThreadsToxic.Web.Endpoints;

// Members: danh sách người nhận việc; trống = mọi tài khoản vai trò member
public record SplitRequest(List<string>? Members);

// Chuyển toàn bộ phần CHƯA xử lý của From sang To (phần đã xử lý vẫn ghi tên người đã làm)
public record TransferRequest(string From, string To);

public record AssigneeLoad(string Name, int Assigned, int Done, int Remaining);

// UnassignedRemaining: chưa giao và chưa xử lý; LongLeaves: bình luận dài hơn 300 ký tự và không có trả lời,
// không giao vì trang xuất cũng không lấy (vẫn xem/xử lý được ở danh sách chung)
public record AssignmentSummary(int UnassignedRemaining, int LongLeaves, List<AssigneeLoad> People);

/// <summary>
/// Chia việc theo bài viết: cả bài (nên cả luồng F0..F3) về một người. Chỉ admin chia / chuyển;
/// ai cũng xem được tổng quan. Giao việc chỉ để sắp xếp, không chặn ai xử lý bình luận nào.
/// </summary>
public static class AssignEndpoints
{
    // Ngưỡng giống trang xuất (MaxLength mặc định 300, đếm sau khi gộp khoảng trắng)
    private const int LongLeafChars = 300;

    public static void MapAssignEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/items").RequireUser().MapGet("/assignments", SummaryAsync);

        var admin = app.MapGroup("/api/admin/assign").RequireUser();
        admin.MapPost("/split", SplitAsync);
        admin.MapPost("/transfer", TransferAsync);
    }

    private static async Task<IResult> SummaryAsync(AppDbContext db, AccessTokens tokens, CancellationToken ct) =>
        Results.Ok(await GetSummaryAsync(db, tokens, ct));

    private sealed record NameCount(string Name, int Assigned, int Done, int Remaining);

    internal static async Task<AssignmentSummary> GetSummaryAsync(AppDbContext db, AccessTokens tokens, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<NameCount>($"""
            SELECT assigned_to AS name,
                   COUNT(*)::int AS assigned,
                   COUNT(*) FILTER (WHERE is_excluded OR reviewed_by IS NOT NULL)::int AS done,
                   COUNT(*) FILTER (WHERE NOT is_excluded AND reviewed_by IS NULL)::int AS remaining
            FROM comments WHERE assigned_to IS NOT NULL
            GROUP BY assigned_to
            """).ToListAsync(ct);

        var unassigned = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM comments c
            WHERE c.assigned_to IS NULL AND NOT c.is_excluded AND c.reviewed_by IS NULL
              AND NOT (char_length(regexp_replace(btrim(c.content), '\s+', ' ', 'g')) > {LongLeafChars}
                       AND NOT EXISTS (SELECT 1 FROM comments k WHERE k.thread_parent_id = c.id))
            """).SingleAsync(ct);

        var longLeaves = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM comments c
            WHERE c.assigned_to IS NULL AND NOT c.is_excluded AND c.reviewed_by IS NULL
              AND char_length(regexp_replace(btrim(c.content), '\s+', ' ', 'g')) > {LongLeafChars}
              AND NOT EXISTS (SELECT 1 FROM comments k WHERE k.thread_parent_id = c.id)
            """).SingleAsync(ct);

        // Người có tài khoản nhưng chưa được giao gì vẫn hiện (0 việc)
        var people = rows.Select(r => new AssigneeLoad(r.Name, r.Assigned, r.Done, r.Remaining)).ToList();
        foreach (var user in tokens.Users.Where(u => people.All(p => p.Name != u.Name)))
            people.Add(new AssigneeLoad(user.Name, 0, 0, 0));

        return new AssignmentSummary(unassigned, longLeaves,
            people.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    private sealed record PostCount(string PostId, int Count);
    private sealed record PostOwner(string PostId, string Name, int Count);

    private static async Task<Results<Ok<AssignmentSummary>, BadRequest<string>, UnauthorizedHttpResult, JsonHttpResult<string>>> SplitAsync(
        SplitRequest req,
        [FromHeader(Name = ItemEndpoints.SessionHeader)] string? sessionToken,
        AppDbContext db,
        AccessTokens tokens,
        IHubContext<CollabHub> hub,
        CollabState state,
        CancellationToken ct)
    {
        var session = state.ResolveSession(sessionToken);
        if (session is null) return TypedResults.Unauthorized();
        if (!session.IsAdmin) return AuthFilters.AdminOnly();

        var known = tokens.Users.Select(u => u.Name).ToHashSet();
        var members = (req.Members is { Count: > 0 }
                ? req.Members.Select(m => (m ?? "").Trim())
                : tokens.Users.Where(u => !u.IsAdmin).Select(u => u.Name))
            .Distinct()
            .ToList();
        if (members.Count == 0)
            return TypedResults.BadRequest("Chưa có người nhận việc (cần ít nhất một tài khoản member).");
        if (members.FirstOrDefault(m => !known.Contains(m)) is { } unknown)
            return TypedResults.BadRequest($"Không có tài khoản tên '{unknown}'.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Việc cần giao: đang lọc, chưa xử lý, chưa giao; bỏ bình luận quá dài không có trả lời
        var posts = await db.Database.SqlQuery<PostCount>($"""
            SELECT c.post_id, COUNT(*)::int AS count FROM comments c
            WHERE c.assigned_to IS NULL AND NOT c.is_excluded AND c.reviewed_by IS NULL
              AND NOT (char_length(regexp_replace(btrim(c.content), '\s+', ' ', 'g')) > {LongLeafChars}
                       AND NOT EXISTS (SELECT 1 FROM comments k WHERE k.thread_parent_id = c.id))
            GROUP BY c.post_id
            """).ToListAsync(ct);

        // Bài đã có người phụ trách (giao lần trước): phần mới của bài về đúng người đó
        var owners = (await db.Database.SqlQuery<PostOwner>($"""
                SELECT post_id, assigned_to AS name, COUNT(*)::int AS count FROM comments
                WHERE assigned_to IS NOT NULL GROUP BY post_id, assigned_to
                """).ToListAsync(ct))
            .Where(o => members.Contains(o.Name))
            .GroupBy(o => o.PostId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.Count).ThenBy(o => o.Name).First().Name);

        var load = members.ToDictionary(m => m, _ => 0);
        foreach (var person in (await GetSummaryAsync(db, tokens, ct)).People.Where(p => load.ContainsKey(p.Name)))
            load[person.Name] = person.Remaining;

        // Cân tải: bài lớn trước, mỗi bài cho người đang ít việc nhất (thứ tự tên để kết quả ổn định)
        var plan = new List<(string PostId, string Name)>();
        foreach (var post in posts.OrderByDescending(p => p.Count).ThenBy(p => p.PostId, StringComparer.Ordinal))
        {
            var name = owners.GetValueOrDefault(post.PostId)
                ?? members.OrderBy(m => load[m]).ThenBy(m => members.IndexOf(m)).First();
            plan.Add((post.PostId, name));
            load[name] += post.Count;
        }

        if (plan.Count > 0)
        {
            var postIds = plan.Select(p => p.PostId).ToArray();
            var names = plan.Select(p => p.Name).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE comments c SET assigned_to = x.name
                FROM unnest({postIds}, {names}) AS x(post_id, name)
                WHERE c.post_id = x.post_id
                  AND c.assigned_to IS NULL AND NOT c.is_excluded AND c.reviewed_by IS NULL
                  AND NOT (char_length(regexp_replace(btrim(c.content), '\s+', ' ', 'g')) > {LongLeafChars}
                           AND NOT EXISTS (SELECT 1 FROM comments k WHERE k.thread_parent_id = c.id))
                """, ct);
        }
        await tx.CommitAsync(ct);

        var summary = await GetSummaryAsync(db, tokens, ct);
        await hub.Clients.Group(CollabHub.Room).SendAsync("AssignmentsChanged", new { by = session.UserName }, ct);
        return TypedResults.Ok(summary);
    }

    private static async Task<Results<Ok<object>, BadRequest<string>, UnauthorizedHttpResult, JsonHttpResult<string>>> TransferAsync(
        TransferRequest req,
        [FromHeader(Name = ItemEndpoints.SessionHeader)] string? sessionToken,
        AppDbContext db,
        AccessTokens tokens,
        IHubContext<CollabHub> hub,
        CollabState state,
        CancellationToken ct)
    {
        var session = state.ResolveSession(sessionToken);
        if (session is null) return TypedResults.Unauthorized();
        if (!session.IsAdmin) return AuthFilters.AdminOnly();

        var from = (req.From ?? "").Trim();
        var to = (req.To ?? "").Trim();
        if (from.Length == 0 || to.Length == 0 || from == to)
            return TypedResults.BadRequest("Cần chọn hai người khác nhau.");
        if (tokens.Users.All(u => u.Name != to))
            return TypedResults.BadRequest($"Không có tài khoản tên '{to}'.");

        var moved = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE comments SET assigned_to = {to}
            WHERE assigned_to = {from} AND NOT is_excluded AND reviewed_by IS NULL
            """, ct);

        await hub.Clients.Group(CollabHub.Room).SendAsync("AssignmentsChanged", new { by = session.UserName }, ct);
        return TypedResults.Ok<object>(new { moved, summary = await GetSummaryAsync(db, tokens, ct) });
    }
}
