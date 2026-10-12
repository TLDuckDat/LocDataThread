using System.Text.Json;
using Npgsql;

namespace ThreadsToxic.Tests.Infrastructure;

public record SeedRow(
    string Id,
    string? Content = null,
    int ReplyLevel = 0,
    float Score = 0.5f,
    string[]? Words = null,
    bool Excluded = false);

/// <summary>
/// Truy cập trực tiếp database test để dựng và kiểm tra dữ liệu.
/// Luôn đi qua TestEnvironment nên không thể trỏ nhầm sang DB chính.
/// </summary>
public static class TestDb
{
    private static NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(TestEnvironment.ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Xóa sạch dữ liệu test (audit trước, vì FK RESTRICT).</summary>
    public static async Task ResetAsync()
    {
        await using var connection = Open();
        await using var cmd = new NpgsqlCommand("DELETE FROM moderation_audits; DELETE FROM comments;", connection);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task SeedAsync(params SeedRow[] rows)
    {
        await using var connection = Open();
        foreach (var row in rows)
        {
            var analysis = JsonSerializer.Serialize(new
            {
                matched_words = row.Words ?? [],
                matched_emojis = Array.Empty<string>(),
                categories = Array.Empty<string>(),
                has_emoji_slang = false,
                is_auto_toxic = false,
            });

            await using var cmd = new NpgsqlCommand("""
                INSERT INTO comments (id, post_id, post_url, content, reply_level, toxic_score,
                                      analysis_data, is_excluded, excluded_by, excluded_at)
                VALUES (@id, 'post_test', 'https://example.test/post', @content, @level, @score,
                        @analysis::jsonb, @excluded,
                        CASE WHEN @excluded THEN 'seed' END,
                        CASE WHEN @excluded THEN NOW() END)
                """, connection);
            cmd.Parameters.AddWithValue("id", row.Id);
            cmd.Parameters.AddWithValue("content", row.Content ?? $"Nội dung {row.Id}");
            cmd.Parameters.AddWithValue("level", row.ReplyLevel);
            cmd.Parameters.AddWithValue("score", row.Score);
            cmd.Parameters.AddWithValue("analysis", analysis);
            cmd.Parameters.AddWithValue("excluded", row.Excluded);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Sinh n bình luận có id duy nhất: t_001, t_002...</summary>
    public static SeedRow[] Rows(int count, Func<int, SeedRow>? customize = null) =>
        Enumerable.Range(1, count)
            .Select(i => customize?.Invoke(i) ?? new SeedRow($"t_{i:000}"))
            .ToArray();

    public static async Task<bool> IsExcludedAsync(string id)
    {
        await using var connection = Open();
        await using var cmd = new NpgsqlCommand("SELECT is_excluded FROM comments WHERE id = @id", connection);
        cmd.Parameters.AddWithValue("id", id);
        return (bool)(await cmd.ExecuteScalarAsync() ?? throw new InvalidOperationException($"Không có {id}"));
    }

    public static async Task<int> CountAsync(string sql)
    {
        await using var connection = Open();
        await using var cmd = new NpgsqlCommand(sql, connection);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    public static async Task<List<(string CommentId, string User, string Action)>> AuditsAsync()
    {
        await using var connection = Open();
        await using var cmd = new NpgsqlCommand(
            "SELECT comment_id, user_name, action FROM moderation_audits ORDER BY id", connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<(string, string, string)>();
        while (await reader.ReadAsync())
            list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }
}
