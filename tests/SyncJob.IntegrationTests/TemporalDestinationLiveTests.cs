using SyncJob.Core;
using SyncJob.Core.Copy;
using SyncJob.Core.Model;
using SyncJob.Core.Publication;

namespace SyncJob.IntegrationTests;

/// <summary>
/// A system-versioned destination, end to end.
/// <para>
/// These exist because of what upgrading the schema reader did. Until
/// SqlSchemaDiff 1.7.0 the renderer did not know that a table was system-versioned and
/// dropped the period columns silently, so staging came out an ordinary heap by
/// accident and everything worked. 1.7.0 renders them faithfully - correct for a schema
/// diff, and fatal here: staging would be created with <c>GENERATED ALWAYS</c> columns
/// that cannot be bulk copied into, and the failure would arrive at the first
/// production run against a temporal table rather than at build time.
/// </para>
/// <para>
/// So the version bump is proved rather than assumed, and the property being proved is
/// not "the code compiles against 1.7.0" but "a temporal destination still works".
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class TemporalDestinationLiveTests(SqlServerFixture fixture)
{
    [LiveFact]
    public async Task StagingForASystemVersionedDestination_IsAnOrdinaryTable()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await CreateTemporalAsync(connectionString, "dbo.Employee");

        var staging = await new StagingTableFactory().CreateAsync(connectionString, "dbo.Employee", null, default);

        // No versioning, no period, and no history table of its own.
        Assert.Equal(0, Convert.ToInt32(await SqlServerFixture.ScalarAsync(
            connectionString, $"SELECT temporal_type FROM sys.tables WHERE object_id = OBJECT_ID('{staging}');")));

        Assert.Equal(0, Convert.ToInt32(await SqlServerFixture.ScalarAsync(
            connectionString,
            $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('{staging}') AND generated_always_type <> 0;")));

        // And the period columns are not there as ordinary columns either: kept, they
        // would be two columns staging has and the source does not, and the copy would
        // refuse the step for a column nobody asked for.
        Assert.Equal(
            ["Id", "Name", "Salary"],
            await ColumnNamesAsync(connectionString, staging));
    }

    /// <summary>
    /// The whole path: read a plain source, stage it, publish into a system-versioned
    /// destination, and let the server keep its own history.
    /// </summary>
    [LiveFact]
    public async Task ReplacingASystemVersionedDestination_PublishesAndTheHistoryIsKept()
    {
        var (source, destination) = await fixture.CreatePairAsync();

        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.Employee (Id int NOT NULL, Name nvarchar(60) NOT NULL, Salary decimal(10, 2) NOT NULL);
            INSERT INTO dbo.Employee (Id, Name, Salary) VALUES
                (1, N'Ada', 100.00), (2, N'Grace', 120.00), (3, N'Edsger', 90.00);
            """);

        await CreateTemporalAsync(destination, "dbo.Employee");

        // A row already there, so the history has something to keep.
        await SqlServerFixture.ExecuteAsync(
            destination, "INSERT INTO dbo.Employee (Id, Name, Salary) VALUES (1, N'Ada', 10.00);");

        var step = Step();
        var staging = await new StagingTableFactory().CreateAsync(destination, step.DestinationTable, null, default);

        var copied = await new SqlTableCopier().CopyAsync(
            new CopyRequest
            {
                SourceConnectionString = source,
                DestinationConnectionString = destination,
                Sql = "SELECT Id, Name, Salary FROM dbo.Employee;",
                CommandTimeoutSeconds = 60,
                KeepIdentity = false
            },
            staging,
            default);

        Assert.Equal(3L, copied.RowsCopied);

        var published = await new SwapPublisher().PublishAsync(destination, staging, step, default);

        Assert.Equal(3L, published.Inserted);
        Assert.Equal(3, await SqlServerFixture.CountAsync(destination, "dbo.Employee"));

        // The destination is still system-versioned - publishing did not quietly turn
        // versioning off to get the rows in.
        Assert.Equal(2, Convert.ToInt32(await SqlServerFixture.ScalarAsync(
            destination, "SELECT temporal_type FROM sys.tables WHERE object_id = OBJECT_ID('dbo.Employee');")));

        // And the row that was replaced is in the history, which is the whole reason
        // the destination is temporal in the first place.
        Assert.Equal(10.00m, Convert.ToDecimal(await SqlServerFixture.ScalarAsync(
            destination, "SELECT Salary FROM dbo.EmployeeHistory WHERE Id = 1;")));

        Assert.Equal(100.00m, Convert.ToDecimal(await SqlServerFixture.ScalarAsync(
            destination, "SELECT Salary FROM dbo.Employee WHERE Id = 1;")));
    }

    /// <summary>
    /// A source that hands over a column aimed at a period column is refused by name,
    /// before anything is written, rather than by the server at insert time with
    /// "Cannot insert an explicit value into a GENERATED ALWAYS column".
    /// </summary>
    [LiveFact]
    public async Task ASourceColumnAimedAtAPeriodColumn_IsRefusedByName()
    {
        var (source, destination) = await fixture.CreatePairAsync();

        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.Employee (Id int NOT NULL, Name nvarchar(60) NOT NULL, Salary decimal(10, 2) NOT NULL,
                                       ValidFrom datetime2 NOT NULL);
            INSERT INTO dbo.Employee VALUES (1, N'Ada', 100.00, '2020-01-01');
            """);

        await CreateTemporalAsync(destination, "dbo.Employee");

        var failure = await Assert.ThrowsAsync<ColumnMatchException>(() =>
            new SqlTableCopier().CopyAsync(
                new CopyRequest
                {
                    SourceConnectionString = source,
                    DestinationConnectionString = destination,
                    Sql = "SELECT Id, Name, Salary, ValidFrom FROM dbo.Employee;",
                    CommandTimeoutSeconds = 60
                },

                // Straight at the destination rather than at staging: staging has no
                // period columns, so the destination is where the refusal has to come
                // from.
                "dbo.Employee",
                default));

        Assert.Contains("ValidFrom", failure.Message, StringComparison.Ordinal);
        Assert.Contains("system-versioning period column", failure.Message, StringComparison.Ordinal);
    }

    private static SyncStep Step() => new()
    {
        Id = "employees",
        Name = "employees",
        DestinationTable = "dbo.Employee",
        CommandTimeoutSeconds = 60,
        Publication = new PublicationPlan { Mode = PublicationMode.Replace, KeepIdentity = false }
    };

    private static Task CreateTemporalAsync(string connectionString, string table) =>
        SqlServerFixture.ExecuteAsync(connectionString, $"""
            CREATE TABLE {table} (
                Id        int           NOT NULL PRIMARY KEY,
                Name      nvarchar(60)  NOT NULL,
                Salary    decimal(10,2) NOT NULL,
                ValidFrom datetime2 GENERATED ALWAYS AS ROW START NOT NULL,
                ValidTo   datetime2 GENERATED ALWAYS AS ROW END   NOT NULL,
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
            );

            ALTER TABLE {table}
                SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = {table}History));
            """);

    private static async Task<string[]> ColumnNamesAsync(string connectionString, string table)
    {
        var names = await SqlServerFixture.ScalarAsync(connectionString, $"""
            SELECT STRING_AGG(CAST(name AS nvarchar(128)), ',') WITHIN GROUP (ORDER BY column_id)
            FROM sys.columns WHERE object_id = OBJECT_ID('{table}');
            """);

        return names is null or DBNull ? [] : ((string)names).Split(',');
    }
}
