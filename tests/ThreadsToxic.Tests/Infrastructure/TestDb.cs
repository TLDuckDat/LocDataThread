using System.Text.Json;
using Npgsql;

namespace ThreadsToxic.Tests.Infrastructure;

public record SeedRow(
    string Id,
    string? Content = null,
    int ReplyLevel = 0,
    float Score = 0.5f,
    string[]? Words = null,
    bool Excluded = false,
    string? Source = null,
    string? Parent = null,          // bắt buộc với bản trả lời (ReplyLevel > 0): chuỗi F được dựng từ cha
    string PostId = "post_test");

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
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM moderation_audits; DELETE FROM comments; DELETE FROM posts; DELETE FROM review_queue;", connection);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Chạy một câu SQL tùy ý (dùng để đặt các cột không có trong SeedRow).</summary>
    public static async Task ExecuteAsync(string sql)
    {
        await using var connection = Open();
        await using var cmd = new NpgsqlCommand(sql, connection);
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

            var content = row.Content ?? $"Nội dung {row.Id}";
            var chain = await ChainForAsync(connection, row, content);

            await using var cmd = new NpgsqlCommand("""
                INSERT INTO comments (id, post_id, post_url, content, reply_level, is_reply, toxic_score,
                                      analysis_data, is_excluded, excluded_by, excluded_at, source,
                                      f0, f1, f2, f3)
                VALUES (@id, @post, 'https://example.test/post', @content, @level, @level > 0, @score,
                        @analysis::jsonb, @excluded,
                        CASE WHEN @excluded THEN 'seed' END,
                        CASE WHEN @excluded THEN NOW() END, @source,
                        @f0, @f1, @f2, @f3)
                """, connection);
            cmd.Parameters.AddWithValue("id", row.Id);
            cmd.Parameters.AddWithValue("post", row.PostId);
            cmd.Parameters.AddWithValue("content", content);
            cmd.Parameters.AddWithValue("level", row.ReplyLevel);
            cmd.Parameters.AddWithValue("score", row.Score);
            cmd.Parameters.AddWithValue("analysis", analysis);
            cmd.Parameters.AddWithValue("excluded", row.Excluded);
            cmd.Parameters.AddWithValue("source", (object?)row.Source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("f0", chain[0]);
            cmd.Parameters.AddWithValue("f1", chain[1]);
            cmd.Parameters.AddWithValue("f2", chain[2]);
            cmd.Parameters.AddWithValue("f3", chain[3]);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // Chuỗi F0..F3 hợp lệ: F_L = nội dung, các cấp trên lấy từ cha (cha phải được seed trước)
    private static async Task<string[]> ChainForAsync(NpgsqlConnection connection, SeedRow row, string content)
    {
        var chain = new[] { "", "", "", "" };
        if (row.ReplyLevel > 0)
        {
            if (row.Parent is null)
                throw new InvalidOperationException($"Bản trả lời {row.Id} (F{row.ReplyLevel}) cần Parent.");
            await using var cmd = new NpgsqlCommand(
                "SELECT f0, f1, f2, content, reply_level FROM comments WHERE id = @id", connection);
            cmd.Parameters.AddWithValue("id", row.Parent);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException($"Chưa seed cha {row.Parent} của {row.Id}.");
            var parentLevel = reader.GetInt32(4);
            for (var k = 0; k < parentLevel; k++) chain[k] = reader.GetString(k);
            chain[parentLevel] = reader.GetString(3);
        }
        chain[row.ReplyLevel] = content;
        return chain;
    }

    /// <summary>Luồng F0 -> F1 -> F2 -> F3 với id {prefix}0..{prefix}3 (con cấp L là con của cấp L-1).</summary>
    public static Task SeedThreadAsync(string prefix, int depth = 3, string postId = "post_test") =>
        SeedAsync(Enumerable.Range(0, depth + 1)
            .Select(level => new SeedRow($"{prefix}{level}", $"Nội dung {prefix} cấp {level}", ReplyLevel: level,
                Parent: level == 0 ? null : $"{prefix}{level - 1}", PostId: postId))
            .ToArray());

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

    public static async Task DeleteCommentAsync(string id)
    {
        await using var connection = Open();
        await using var cmd = new NpgsqlCommand("DELETE FROM comments WHERE id = @id", connection);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Chạy SQL có tham số (không nối chuỗi dữ liệu test vào câu lệnh).</summary>
    public static async Task ExecuteParamAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = Open();
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Đọc một giá trị có tham số; trả về null nếu SQL không có dòng nào.</summary>
    public static async Task<object?> QueryScalarAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = Open();
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    /// <summary>Dòng kho phụ tối thiểu: id là id bình luận/bài viết, crawler_ids có một phần tử.</summary>
    public static Task SeedQueueAsync(string id, string reason, string content, string recordType = "comment",
        bool existsInMain = false) =>
        ExecuteParamAsync("""
            INSERT INTO review_queue (id, record_type, reason, record_id, post_id, post_url, content,
                f0, f1, f2, f3, reply_level, toxic_score, review_status, review_status_vi, analysis_data,
                occurrences, first_seen, last_seen, exists_in_main, crawler_ids)
            VALUES (@id, @type, @reason, @id, 'post_q', 'https://example.test/q', @content,
                @content, '', '', '', 0, 0.5, 'clean', 'Trong sạch', '{}'::jsonb,
                1, '2026-10-01', '2026-10-01', @inMain, ARRAY[@crawler]::text[])
            """,
            ("id", id), ("type", recordType), ("reason", reason), ("content", content),
            ("inMain", existsInMain), ("crawler", "cr_" + id));

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
