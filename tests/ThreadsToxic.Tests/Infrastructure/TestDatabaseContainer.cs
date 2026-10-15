using Testcontainers.PostgreSql;

namespace ThreadsToxic.Tests.Infrastructure;

/// <summary>
/// Postgres tạm trong Docker, dùng chung cho cả lần chạy test. Dựng từ db/schema.sql rồi
/// db/migrations theo thứ tự tên file, cùng major version với Supabase.
/// Testcontainers (Ryuk) tự xóa container khi tiến trình test kết thúc.
/// </summary>
internal static class TestDatabaseContainer
{
    private const string Image = "postgres:17-alpine";

    public static string Start()
    {
        var container = new PostgreSqlBuilder(Image)
            .WithDatabase(TestEnvironment.RequiredDatabase)
            .Build();

        try
        {
            container.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Không dựng được Postgres test trong Docker. Bật Docker Desktop rồi chạy lại, " +
                "hoặc đặt THREADS_TEST_CONNECTION trỏ tới một database threads_toxic_test có sẵn.", ex);
        }

        var dbDir = FindDbDirectory();
        var scripts = new[] { Path.Combine(dbDir, "schema.sql") }
            .Concat(Directory.GetFiles(Path.Combine(dbDir, "migrations"), "*.sql").Order(StringComparer.Ordinal));
        foreach (var script in scripts)
        {
            // psql trong container: file migration có khối DO $$ và cần dừng ngay ở lỗi đầu tiên
            var result = container.ExecScriptAsync("\\set ON_ERROR_STOP on\n" + File.ReadAllText(script))
                .GetAwaiter().GetResult();
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Lỗi khi chạy {Path.GetFileName(script)}: {result.Stderr}");
        }

        return container.GetConnectionString();
    }

    private static string FindDbDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "db");
            if (File.Exists(Path.Combine(candidate, "schema.sql"))) return candidate;
        }

        throw new InvalidOperationException("Không tìm thấy thư mục db/ chứa schema.sql.");
    }
}
