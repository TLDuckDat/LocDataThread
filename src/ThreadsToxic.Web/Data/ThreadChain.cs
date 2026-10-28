using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ThreadsToxic.Web.Data;

// Một nút liên quan trong luồng: Id là con cháu (hoặc tổ tiên), Origin là bình luận được chọn đã dẫn tới nó
public record ChainLink(string Id, string Origin, bool IsExcluded);

/// <summary>
/// Duyệt cây hội thoại F0..F3 theo comments.thread_parent_id (tối đa 3 cấp).
/// Ràng buộc "cha bị loại thì con cũng bị loại" được DB kiểm tra lúc commit (constraint trigger
/// comments_exclusion_chain); các hàm ở đây giúp API làm đúng ngay từ đầu thay vì bị từ chối.
/// </summary>
public static class ThreadChain
{
    public const string ExclusionConstraint = "comments_exclusion_chain";

    // Con cháu của các bình luận đã chọn (không gồm chính chúng). Một con cháu chỉ xuất hiện một lần.
    public static async Task<List<ChainLink>> DescendantsAsync(AppDbContext db, string[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var rows = await db.Database.SqlQuery<ChainLink>($"""
            WITH RECURSIVE d(id, origin, is_excluded, depth) AS (
                SELECT c.id, c.thread_parent_id, c.is_excluded, 1
                FROM comments c WHERE c.thread_parent_id = ANY({ids})
                UNION ALL
                SELECT c.id, d.origin, c.is_excluded, d.depth + 1
                FROM comments c JOIN d ON c.thread_parent_id = d.id
                WHERE d.depth < 3
            )
            SELECT id, origin, is_excluded FROM d WHERE NOT (id = ANY({ids}))
            """).ToListAsync(ct);
        return rows.DistinctBy(r => r.Id).ToList();
    }

    // Tổ tiên (cha, ông, cụ) của các bình luận đã chọn; mỗi cặp (tổ tiên, bình luận đã chọn) một dòng.
    public static async Task<List<ChainLink>> AncestorsAsync(AppDbContext db, string[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        return await db.Database.SqlQuery<ChainLink>($"""
            WITH RECURSIVE a(id, parent_id, origin, is_excluded, depth) AS (
                SELECT p.id, p.thread_parent_id, c.id, p.is_excluded, 1
                FROM comments c JOIN comments p ON p.id = c.thread_parent_id
                WHERE c.id = ANY({ids})
                UNION ALL
                SELECT p.id, p.thread_parent_id, a.origin, p.is_excluded, a.depth + 1
                FROM comments p JOIN a ON p.id = a.parent_id
                WHERE a.depth < 3
            )
            SELECT id, origin, is_excluded FROM a
            """).ToListAsync(ct);
    }

    // Cha trực tiếp của từng bình luận (để kiểm tra khôi phục không để con đứng dưới cha đã loại)
    public static async Task<Dictionary<string, (string ParentId, bool ParentExcluded)>> ParentsAsync(
        AppDbContext db, string[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var rows = await db.Database.SqlQuery<ChainLink>($"""
            SELECT p.id, c.id AS origin, p.is_excluded
            FROM comments c JOIN comments p ON p.id = c.thread_parent_id
            WHERE c.id = ANY({ids})
            """).ToListAsync(ct);
        return rows.ToDictionary(r => r.Origin, r => (r.Id, r.IsExcluded));
    }

    // Transaction bị DB từ chối vì vi phạm ràng buộc chuỗi (thường do người khác thao tác cùng lúc)
    public static bool IsChainViolation(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) is { ConstraintName: ExclusionConstraint };
}
