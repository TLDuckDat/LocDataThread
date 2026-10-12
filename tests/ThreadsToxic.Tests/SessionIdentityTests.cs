using System.Net;
using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public class SessionIdentityTests(AppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Move_without_a_session_token_is_unauthorized_and_changes_nothing()
    {
        await TestDb.SeedAsync(new SeedRow("a"));

        var (status, _) = await MoveWithTokenAsync(token: null, new { ids = new[] { "a" }, toExcluded = true });

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.False(await TestDb.IsExcludedAsync("a"));
        Assert.Empty(await TestDb.AuditsAsync());
    }

    [Fact]
    public async Task Move_with_an_unknown_token_is_unauthorized()
    {
        await TestDb.SeedAsync(new SeedRow("a"));

        var (status, _) = await MoveWithTokenAsync("not-a-real-token", new { ids = new[] { "a" }, toExcluded = true });

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.False(await TestDb.IsExcludedAsync("a"));
    }

    [Fact]
    public async Task Body_user_name_is_ignored_and_the_session_name_is_audited()
    {
        await TestDb.SeedAsync(new SeedRow("a"));
        var minh = await ActorAsync("Minh");

        var (status, result) = await MoveWithTokenAsync(minh.Token,
            new { ids = new[] { "a" }, toExcluded = true, userName = "Admin-Giả-Mạo" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, result!.Moved);
        var audit = Assert.Single(await TestDb.AuditsAsync());
        Assert.Equal("Minh", audit.User);
        Assert.Equal("Minh", (await ListAsync("?bucket=excluded")).Items.Single().ExcludedBy);
    }

    [Fact]
    public async Task Each_users_token_acts_only_as_that_user()
    {
        await TestDb.SeedAsync(new SeedRow("a"), new SeedRow("b"));
        var minh = await ActorAsync("Minh");
        var lan = await ActorAsync("Lan");

        await MoveWithTokenAsync(minh.Token, new { ids = new[] { "a" }, toExcluded = true });
        await MoveWithTokenAsync(lan.Token, new { ids = new[] { "b" }, toExcluded = true });

        var audits = await TestDb.AuditsAsync();
        Assert.Contains(("a", "Minh", "exclude"), audits);
        Assert.Contains(("b", "Lan", "exclude"), audits);
    }

    [Fact]
    public async Task Old_token_is_rejected_after_reconnect_and_the_new_token_works()
    {
        await TestDb.SeedAsync(new SeedRow("a"), new SeedRow("b"));
        var before = await HubTestClient.ConnectAsync(Factory, "Minh");
        var oldToken = before.Token!;
        Assert.Equal(HttpStatusCode.OK,
            (await MoveWithTokenAsync(oldToken, new { ids = new[] { "a" }, toExcluded = true })).Status);

        // Mô phỏng reconnect: kết nối cũ chết, kết nối mới (connection id khác) Join lại.
        await before.DisposeAsync();
        await using var after = await HubTestClient.ConnectAsync(Factory, "Minh");

        Assert.NotEqual(oldToken, after.Token);

        // Server xử lý ngắt kết nối bất đồng bộ: chờ tới khi token cũ bị từ chối.
        // Dùng id không tồn tại để request thăm dò không làm đổi dữ liệu nếu token còn hiệu lực.
        var status = HttpStatusCode.OK;
        for (var i = 0; i < 200 && status != HttpStatusCode.Unauthorized; i++)
        {
            status = (await MoveWithTokenAsync(oldToken, new { ids = new[] { "no-such-id" }, toExcluded = true })).Status;
            if (status != HttpStatusCode.Unauthorized) await Task.Delay(25);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, status);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await MoveWithTokenAsync(oldToken, new { ids = new[] { "b" }, toExcluded = true })).Status);
        Assert.False(await TestDb.IsExcludedAsync("b"));
        Assert.Equal(HttpStatusCode.OK,
            (await MoveWithTokenAsync(after.Token, new { ids = new[] { "b" }, toExcluded = true })).Status);
    }

    [Fact]
    public async Task Joining_again_on_the_same_connection_invalidates_the_previous_token()
    {
        await TestDb.SeedAsync(new SeedRow("a"));
        await using var minh = await HubTestClient.ConnectAsync(Factory, "Minh");
        var oldToken = minh.Token!;

        var newToken = await minh.JoinAsync();

        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await MoveWithTokenAsync(oldToken, new { ids = new[] { "a" }, toExcluded = true })).Status);
        Assert.Equal(HttpStatusCode.OK,
            (await MoveWithTokenAsync(newToken, new { ids = new[] { "a" }, toExcluded = true })).Status);
    }

    [Fact]
    public async Task Session_token_never_appears_in_application_logs()
    {
        await TestDb.SeedAsync(new SeedRow("a"), new SeedRow("b"));
        var minh = await ActorAsync("Minh");
        var token = minh.Token!;

        await MoveWithTokenAsync(token, new { ids = new[] { "a" }, toExcluded = true });
        await MoveWithTokenAsync("sai-" + token, new { ids = new[] { "b" }, toExcluded = true }); // đường 401
        await minh.FocusAsync("a");

        Assert.NotEmpty(Factory.Logs.Entries);   // chắc chắn đã bắt được log
        Assert.DoesNotContain(Factory.Logs.Entries, entry => entry.Contains(token, StringComparison.Ordinal));
    }
}
