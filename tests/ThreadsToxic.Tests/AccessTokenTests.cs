using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using ThreadsToxic.Tests.Infrastructure;
using ThreadsToxic.Web.Services;

namespace ThreadsToxic.Tests;

/// <summary>
/// Mã truy cập vĩnh viễn (Auth:Users) và phân quyền admin / member.
/// Tài khoản test: Minh = admin; Lan, Huy, An, Late, Thu = member (xem AppFactory).
/// </summary>
public class AccessTokenTests(AppFactory factory) : IntegrationTestBase(factory)
{
    private HttpClient AnonymousClient() => Factory.CreateClient();

    [Theory]
    [InlineData("/api/items")]
    [InlineData("/api/items/counts")]
    [InlineData("/api/items/progress")]
    [InlineData("/api/review")]
    [InlineData("/api/export/curated")]
    [InlineData("/api/export/curated/csv")]
    [InlineData("/api/export/curated/xlsx")]
    public async Task Reading_data_without_an_access_token_is_unauthorized(string path)
    {
        await TestDb.SeedAsync(new SeedRow("a", "Bình luận bí mật"));
        using var anonymous = AnonymousClient();

        var response = await anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("Bình luận bí mật", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reading_with_a_wrong_access_token_is_unauthorized()
    {
        using var client = AnonymousClient();
        client.DefaultRequestHeaders.Add(AccessTokens.Header, "test-access-token-Ghost");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/items")).StatusCode);
    }

    [Fact]
    public async Task Reading_with_a_valid_access_token_works()
    {
        await TestDb.SeedAsync(new SeedRow("a"));
        using var client = AnonymousClient();
        client.DefaultRequestHeaders.Add(AccessTokens.Header, AppFactory.TokenFor("Huy"));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/items")).StatusCode);
    }

    [Fact]
    public async Task Access_token_alone_cannot_write()
    {
        // Ghi cần token phiên của kết nối hub (để khóa / presence nhất quán), không chỉ mã truy cập
        await TestDb.SeedAsync(new SeedRow("a"));
        using var client = AnonymousClient();
        client.DefaultRequestHeaders.Add(AccessTokens.Header, AppFactory.TokenFor("Minh"));

        var response = await client.PostAsJsonAsync("/api/items/move", new { ids = new[] { "a" }, toExcluded = true });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(await TestDb.IsExcludedAsync("a"));
    }

    [Fact]
    public async Task Join_returns_the_configured_name_and_role()
    {
        await using var minh = await HubTestClient.ConnectAsync(Factory, "Minh", join: false);
        await using var lan = await HubTestClient.ConnectAsync(Factory, "Lan", join: false);

        var admin = await minh.Connection.InvokeAsync<JoinResult>("Join", AppFactory.TokenFor("Minh"));
        var member = await lan.Connection.InvokeAsync<JoinResult>("Join", AppFactory.TokenFor("Lan"));

        Assert.Equal(("Minh", "admin"), (admin.Name, admin.Role));
        Assert.Equal(("Lan", "member"), (member.Name, member.Role));
    }

    [Fact]
    public async Task The_same_access_token_works_again_after_reconnecting()
    {
        // "Vĩnh viễn": mã không đổi qua các lần kết nối; chỉ token phiên là mới
        await TestDb.SeedAsync(new SeedRow("a"), new SeedRow("b"));
        var first = await HubTestClient.ConnectAsync(Factory, "Lan");
        Assert.Equal(HttpStatusCode.OK,
            (await MoveWithTokenAsync(first.Token, new { ids = new[] { "a" }, toExcluded = true })).Status);
        await first.DisposeAsync();

        await using var second = await HubTestClient.ConnectAsync(Factory, "Lan");

        Assert.Equal(HttpStatusCode.OK,
            (await MoveWithTokenAsync(second.Token, new { ids = new[] { "b" }, toExcluded = true })).Status);
        Assert.Contains(("b", "Lan", "exclude"), await TestDb.AuditsAsync());
    }

    [Fact]
    public async Task Bulk_queue_action_is_admin_only()
    {
        await TestDb.SeedQueueAsync("q1", "non_vietnamese", "hello");
        var lan = await ActorAsync("Lan");
        var minh = await ActorAsync("Minh");

        async Task<HttpStatusCode> BulkAsync(string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/review/bulk")
            {
                Content = JsonContent.Create(new { action = "reject", reason = "non_vietnamese", expectedCount = 1 }),
            };
            request.Headers.Add("X-Session-Token", token);
            return (await Http.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Forbidden, await BulkAsync(lan.Token));
        Assert.Equal(1, await TestDb.CountAsync("SELECT COUNT(*) FROM review_queue WHERE decision = 'pending'"));
        Assert.Equal(HttpStatusCode.OK, await BulkAsync(minh.Token));
        Assert.Equal(1, await TestDb.CountAsync("SELECT COUNT(*) FROM review_queue WHERE decision = 'rejected'"));
    }

    [Fact]
    public async Task Access_token_never_appears_in_application_logs()
    {
        await TestDb.SeedAsync(new SeedRow("a"));
        var token = AppFactory.TokenFor("Huy");
        await ActorAsync("Huy");
        using var client = AnonymousClient();
        client.DefaultRequestHeaders.Add(AccessTokens.Header, token);
        await client.GetAsync("/api/items");
        await using var wrong = await HubTestClient.ConnectAsync(Factory, "Ghost", join: false);
        await Assert.ThrowsAnyAsync<Exception>(() => wrong.Connection.InvokeAsync<JoinResult>("Join", "sai-" + token));

        Assert.NotEmpty(Factory.Logs.Entries);
        Assert.DoesNotContain(Factory.Logs.Entries, entry => entry.Contains(token, StringComparison.Ordinal));
    }
}

/// <summary>Kiểm tra cấu hình Auth:Users: sai thì app không khởi động (không chạy mở cho mọi người).</summary>
public class AccessTokenConfigTests
{
    private static AccessTokens Build(params AuthUser[] users) =>
        new(Options.Create(new AuthOptions { Users = users.ToList() }));

    private static AuthUser User(string name, string token, string role = "member") =>
        new() { Name = name, Role = role, TokenSha256 = AccessTokens.Hash(token) };

    [Fact]
    public void Hash_is_lowercase_sha256_hex_like_the_python_tool()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", AccessTokens.Hash("abc"));
    }

    [Fact]
    public void Resolves_name_and_role_and_ignores_surrounding_spaces()
    {
        var tokens = Build(User("Minh", "tok-1", "admin"), User("Lan", "tok-2"));

        Assert.Equal(new AppUser("Minh", "admin"), tokens.Resolve(" tok-1 "));
        Assert.Equal(new AppUser("Lan", "member"), tokens.Resolve("tok-2"));
        Assert.Null(tokens.Resolve("tok-3"));
        Assert.Null(tokens.Resolve(null));
        Assert.Null(tokens.Resolve(""));
    }

    [Fact]
    public void Missing_users_stops_startup()
    {
        Assert.Throws<InvalidOperationException>(() => Build());
    }

    [Theory]
    [InlineData("dup")]
    [InlineData("role")]
    [InlineData("hash")]
    [InlineData("same-token")]
    [InlineData("name")]
    public void Invalid_user_entries_stop_startup(string problem)
    {
        AuthUser[] users = problem switch
        {
            "dup" => [User("Minh", "a"), User("minh", "b")],
            "role" => [User("Minh", "a", "owner")],
            "hash" => [new AuthUser { Name = "Minh", TokenSha256 = "abc" }],
            "same-token" => [User("Minh", "a"), User("Lan", "a")],
            _ => [User("", "a")],
        };

        Assert.Throws<InvalidOperationException>(() => Build(users));
    }
}
