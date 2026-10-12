using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public class InfrastructureSmokeTests(AppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task App_starts_against_the_empty_test_database()
    {
        var counts = await CountsAsync();

        Assert.Equal(0, counts.Active);
        Assert.Equal(0, counts.Excluded);
    }

    [Fact]
    public async Task Hub_accepts_a_joined_client()
    {
        await using var client = await HubTestClient.ConnectAsync(Factory, "Minh");

        await Wait.UntilAsync(() => client.Presence.Any(p => p.Any(u => u.Name == "Minh")),
            "presence phải có Minh sau khi Join");
    }
}
