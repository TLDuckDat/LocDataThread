using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
