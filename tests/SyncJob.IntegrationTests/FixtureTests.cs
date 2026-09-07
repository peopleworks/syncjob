namespace SyncJob.IntegrationTests;

/// <summary>
/// The harness testing itself. Every other live test in this project is built on these
/// three guarantees, so when they break it is worth knowing that before reading a
/// failure in the engine that is not the engine's fault.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class FixtureTests(SqlServerFixture fixture)
{
    [LiveFact]
    public async Task APairOfDatabases_IsTwoDifferentDatabases()
    {
        var (source, destination) = await fixture.CreatePairAsync();

        var sourceName = await SqlServerFixture.ScalarAsync(source, "SELECT DB_NAME();");
        var destinationName = await SqlServerFixture.ScalarAsync(destination, "SELECT DB_NAME();");

        Assert.NotEqual(sourceName, destinationName);
        Assert.StartsWith(SqlServerFixture.DatabasePrefix, (string)sourceName!, StringComparison.Ordinal);
        Assert.StartsWith(SqlServerFixture.DatabasePrefix, (string)destinationName!, StringComparison.Ordinal);
    }

    [LiveFact]
    public async Task SeedingPutsTheRowsThere_AndCountingFindsThem()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        await SqlServerFixture.SeedAsync(connectionString, "dbo.Customer", rows: 2_500);

        Assert.Equal(2_500, await SqlServerFixture.CountAsync(connectionString, "dbo.Customer"));
    }

    /// <summary>
    /// A count of -1 rather than an exception: a guard that has to decide whether the
    /// destination exists should not have to catch anything to find out.
    /// </summary>
    [LiveFact]
    public async Task CountingATableThatIsNotThere_SaysMinusOne()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        Assert.Equal(-1, await SqlServerFixture.CountAsync(connectionString, "dbo.NoSuchTable"));
    }
}
