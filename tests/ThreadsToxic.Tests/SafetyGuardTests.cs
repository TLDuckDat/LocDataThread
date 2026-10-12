using ThreadsToxic.Tests.Infrastructure;

namespace ThreadsToxic.Tests;

public class SafetyGuardTests
{
    [Theory]
    [InlineData("Host=localhost;Database=threads_toxic_db;Username=postgres")]
    [InlineData("Host=localhost;Database=postgres;Username=postgres")]
    [InlineData("Host=localhost;Database=threads_toxic_test_backup;Username=postgres")]
    [InlineData("Host=localhost;Database=threads_toxic_db;Application Name=threads_toxic_test")]
    public void Validate_rejects_any_database_other_than_the_test_one(string connection)
    {
        Assert.Throws<InvalidOperationException>(() => TestEnvironment.Validate(connection));
    }

    [Fact]
    public void Validate_accepts_the_test_database()
    {
        const string ok = "Host=localhost;Database=threads_toxic_test;Username=postgres";
        Assert.Equal(ok, TestEnvironment.Validate(ok));
    }

    [Fact]
    public void Configured_connection_points_at_the_test_database()
    {
        Assert.Contains(TestEnvironment.RequiredDatabase, TestEnvironment.ConnectionString);
    }
}
