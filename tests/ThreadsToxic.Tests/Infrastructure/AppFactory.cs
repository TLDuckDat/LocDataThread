using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ThreadsToxic.Tests.Infrastructure;

/// <summary>
/// Chạy app thật trong bộ nhớ, trỏ vào threads_toxic_test.
/// Mỗi factory có CollabState riêng nên khóa/presence không rò giữa các lớp test.
/// </summary>
public class AppFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;

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
    }
}
