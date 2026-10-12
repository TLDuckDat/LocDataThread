using Npgsql;
using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public class AuditIntegrityTests(AppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Deleting_a_comment_that_has_audit_rows_is_refused()
    {
        await TestDb.SeedAsync(new SeedRow("t_001"));
        await MoveAsync(["t_001"], toExcluded: true, "Minh");

        var error = await Assert.ThrowsAsync<PostgresException>(() => TestDb.DeleteCommentAsync("t_001"));

        // 23001 = restrict_violation; 23503 = foreign_key_violation (NO ACTION)
        Assert.Contains(error.SqlState, new[] { "23001", "23503" });
        Assert.Equal(1, await TestDb.CountAsync("SELECT COUNT(*) FROM comments WHERE id = 't_001'"));
        Assert.Single(await TestDb.AuditsAsync());
    }

    [Fact]
    public async Task Deleting_a_comment_without_audit_rows_still_works()
    {
        await TestDb.SeedAsync(new SeedRow("t_001"));

        await TestDb.DeleteCommentAsync("t_001");

        Assert.Equal(0, await TestDb.CountAsync("SELECT COUNT(*) FROM comments"));
    }

    [Fact]
    public async Task Foreign_key_on_audits_is_declared_RESTRICT()
    {
        // Nếu test này fail: database test chưa áp dụng db/migrations/001_audit_restrict.sql
        var deleteRule = await TestDb.CountAsync("""
            SELECT COUNT(*) FROM pg_constraint
            WHERE conrelid = 'moderation_audits'::regclass AND contype = 'f' AND confdeltype = 'r'
            """);

        Assert.Equal(1, deleteRule);
    }
}
