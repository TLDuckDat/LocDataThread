using Npgsql;

namespace ThreadsToxic.Tests.Infrastructure;

/// <summary>
/// Chuỗi kết nối cho test. Fail-fast nếu không trỏ tới database threads_toxic_test,
/// để test không bao giờ chạm vào dữ liệu thật.
/// </summary>
public static class TestEnvironment
{
    public const string RequiredDatabase = "threads_toxic_test";

    private const string DefaultConnection =
        "Host=localhost;Port=5432;Database=threads_toxic_test;Username=postgres";

    public static string ConnectionString { get; } =
        Validate(Environment.GetEnvironmentVariable("THREADS_TEST_CONNECTION") ?? DefaultConnection);

    public static string Validate(string connection)
    {
        var database = new NpgsqlConnectionStringBuilder(connection).Database;
        if (!connection.Contains(RequiredDatabase, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(database, RequiredDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"DỪNG: chuỗi kết nối test phải trỏ tới database '{RequiredDatabase}' " +
                $"(hiện là '{database}'). Test không được chạy trên database khác.");
        }

        return connection;
    }
}
