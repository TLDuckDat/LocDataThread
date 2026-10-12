using Microsoft.EntityFrameworkCore;
using Npgsql;
using ThreadsToxic.Web.Data;
using ThreadsToxic.Web.Endpoints;
using ThreadsToxic.Web.Hubs;
using ThreadsToxic.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Lắng nghe mọi địa chỉ để các máy trong mạng LAN cùng vào được
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5000");

// Npgsql 10 yêu cầu bật tường minh việc ánh xạ POCO sang jsonb
var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.Configuration.GetConnectionString("Default"));
dataSourceBuilder.EnableDynamicJson(jsonbClrTypes: [typeof(CommentAnalysis)]);
var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(dataSource)
           .UseSnakeCaseNamingConvention());

builder.Services.AddSignalR();
builder.Services.AddSingleton<CollabState>();
builder.Services.AddHostedService<LeaseSweeper>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapItemEndpoints();
app.MapHub<CollabHub>("/hubs/collab");

app.Run();

// Cho phép WebApplicationFactory<Program> dùng trong test
public partial class Program;
