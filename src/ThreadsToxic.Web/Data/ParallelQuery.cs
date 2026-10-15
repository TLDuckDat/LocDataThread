using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ThreadsToxic.Web.Data;

/// <summary>
/// Truy vấn chỉ đọc trên DbContext (và kết nối) riêng. Một DbContext không chạy được hai truy vấn
/// cùng lúc, nên các truy vấn độc lập của một request (đếm, lấy trang, thống kê) mỗi cái dùng một
/// context để chạy song song: tổng thời gian bằng truy vấn chậm nhất thay vì cộng dồn từng lượt
/// đi về database.
/// </summary>
public static class ParallelQuery
{
    private const int MaxAttempts = 3;

    public static async Task<T> RunAsync<T>(this IDbContextFactory<AppDbContext> factory,
        Func<AppDbContext, Task<T>> query, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await query(db);
    }

    /// <summary>
    /// Như <see cref="RunAsync{T}"/>, thử lại khi kết nối đứt giữa chừng (pooler của Supabase thỉnh thoảng
    /// cắt kết nối khi đang truyền dữ liệu). Chỉ dùng cho truy vấn đọc: chạy lại không đổi dữ liệu.
    /// Lỗi do chính câu SQL (PostgresException) không thử lại.
    /// </summary>
    public static async Task<T> RunWithRetryAsync<T>(this IDbContextFactory<AppDbContext> factory,
        Func<AppDbContext, Task<T>> query, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await factory.RunAsync(query, ct);
            }
            catch (NpgsqlException ex) when (ex is not PostgresException && attempt < MaxAttempts
                                              && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
        }
    }
}
