using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ThreadsToxic.Web.Services;

namespace ThreadsToxic.Tests.Infrastructure;

/// <summary>
/// Chạy app thật trong bộ nhớ, trỏ vào threads_toxic_test.
/// Mỗi factory có CollabState riêng nên khóa/presence không rò giữa các lớp test.
/// </summary>
public class AppFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;

    /// <summary>Mọi dòng log (mọi mức) mà app đã ghi, để kiểm tra không lộ bí mật.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    // Tài khoản test: Minh là admin, còn lại là member. Mã truy cập của mỗi người là TokenFor(tên).
    public const string AdminName = "Minh";
    public static readonly string[] MemberNames = ["Lan", "Huy", "An", "Late", "Thu"];

    public static string TokenFor(string name) => $"test-access-token-{name}";

    public AppFactory() : this(null, null)
    {
    }

    protected AppFactory(int? leaseSeconds, int? sweepSeconds)
    {
        // Program.cs đọc chuỗi kết nối ngay khi khởi động, nên đặt qua biến môi trường.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", TestEnvironment.ConnectionString);

        _settings = new Dictionary<string, string?>();
        if (leaseSeconds is { } lease) _settings["Collab:LeaseSeconds"] = lease.ToString();
        if (sweepSeconds is { } sweep) _settings["Collab:SweepSeconds"] = sweep.ToString();

        var users = new[] { (AdminName, Roles.Admin) }.Concat(MemberNames.Select(n => (n, Roles.Member))).ToArray();
        for (var i = 0; i < users.Length; i++)
        {
            _settings[$"Auth:Users:{i}:Name"] = users[i].Item1;
            _settings[$"Auth:Users:{i}:Role"] = users[i].Item2;
            _settings[$"Auth:Users:{i}:TokenSha256"] = AccessTokens.Hash(TokenFor(users[i].Item1));
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));
        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(Logs);
        });
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            sink.Enqueue($"{category} [{logLevel}] {formatter(state, exception)} {exception}");
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                foreach (var field in fields)
                    sink.Enqueue($"{category} field {field.Key}={field.Value}");
            }
        }
    }
}
