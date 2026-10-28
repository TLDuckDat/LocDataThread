using Microsoft.EntityFrameworkCore;
using Npgsql;
using ThreadsToxic.Web;
using ThreadsToxic.Web.Data;
using ThreadsToxic.Web.Endpoints;
using ThreadsToxic.Web.Hubs;
using ThreadsToxic.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Chạy trên máy: lấy chuỗi kết nối Supabase từ .env. Khi deploy, đặt biến môi trường ConnectionStrings__Default.
if (builder.Environment.IsDevelopment())
    DotEnv.AddTo(builder.Configuration, builder.Environment.ContentRootPath);

var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "Thiếu ConnectionStrings:Default. Chạy trên máy: thêm dòng ConnectionStrings__Default vào .env ở gốc repo. " +
        "Khi deploy: đặt biến môi trường ConnectionStrings__Default.");

// Lắng nghe mọi địa chỉ để các máy trong mạng LAN cùng vào được.
// Railway (và đa số PaaS) cấp cổng qua biến môi trường PORT.
var port = Environment.GetEnvironmentVariable("PORT");
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? $"http://0.0.0.0:{(string.IsNullOrEmpty(port) ? "5000" : port)}");

// Npgsql 10 yêu cầu bật tường minh việc ánh xạ POCO sang jsonb
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.EnableDynamicJson(jsonbClrTypes: [typeof(CommentAnalysis)]);
var dataSource = dataSourceBuilder.Build();

// Factory (kèm AppDbContext scoped như AddDbContext): endpoint đọc tạo DbContext riêng cho từng truy vấn
// để chạy song song, vì mỗi lượt đi về Supabase mất vài trăm ms.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(dataSource)
           .UseSnakeCaseNamingConvention());

builder.Services.AddSignalR();
builder.Services.Configure<CollabOptions>(builder.Configuration.GetSection("Collab"));
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.AddSingleton<AccessTokens>();
builder.Services.AddSingleton<CollabState>();
builder.Services.AddHostedService<LeaseSweeper>();

var app = builder.Build();

// Kiểm tra cấu hình tài khoản ngay khi khởi động: thiếu hoặc sai thì dừng, không chạy app mở cho mọi người
app.Services.GetRequiredService<AccessTokens>();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapItemEndpoints();
app.MapExportEndpoints();
app.MapReviewEndpoints();
app.MapAssignEndpoints();
app.MapHub<CollabHub>("/hubs/collab");

app.Run();

// Cho phép WebApplicationFactory<Program> dùng trong test
public partial class Program;
