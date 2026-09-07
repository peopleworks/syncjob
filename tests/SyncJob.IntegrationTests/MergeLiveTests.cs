using Microsoft.Data.SqlClient;
using SyncJob.Core;
using SyncJob.Core.Model;
using SyncJob.Core.Publication;

namespace SyncJob.IntegrationTests;

/// <summary>
/// Append and merge against a real server.
/// <para>
/// Rows are read back and compared, not counted. Every failure this package exists to
/// prevent passes a count: a merge that rewrites every row it matched has the right count,
/// a publication that puts the data in shifted has the right count, and a delete that
/// matched nothing has the right count of zero.
/// </para>
/// <para>
/// The destinations carry a <c>rowversion</c>, which SQL Server moves when and only when a
/// row is actually written. That makes "left alone" something a test can assert rather than
/// infer, and it is what tells a merge that skipped an unchanged row from one that wrote it
/// back identically.
/// </para>
/// <para>
/// The staging tables are created here with plain SQL. <c>IStagingTableFactory</c> belongs
/// to another package being written in parallel, and a test that waits on it tests two
/// things at once. Append lives in this file for a plainer reason: the package owns no
/// other publication test file.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class MergeLiveTests(SqlServerFixture fixture)
{
    [LiveFact]
    public async Task AMergeInsertsWhatIsMissing_UpdatesWhatDiffers_AndLeavesEverythingElseUntouched()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await CreatePartTableAsync(connectionString, "dbo.Part");
        await CreateStageTableAsync(connectionString, "dbo.PartStage");

        await SqlServerFixture.ExecuteAsync(connectionString, """
            INSERT INTO dbo.Part (Plant, Line, Code, Descr, Qty) VALUES
                (N'P1', N'L1', N'A', N'alpha', 1),
                (N'P1', N'L1', N'B', N'beta',  2),
                (N'P1', N'L1', N'C', N'gamma', 3);

            INSERT INTO dbo.PartStage (Plant, Line, Code, Descr, Qty) VALUES
                (N'P1', N'L1', N'B', N'beta',          2),
                (N'P1', N'L1', N'C', N'gamma changed', 30),
                (N'P1', N'L1', N'D', N'delta',         4);
            """);

        var before = await ReadPartsAsync(connectionString);

        var result = await new MergePublisher().PublishAsync(connectionString, "dbo.PartStage", MergeStep(), CancellationToken.None);

        Assert.Equal(new PublishResult(1, 1, 0), result);

        var after = await ReadPartsAsync(connectionString);
        Assert.Equal(4, after.Count);

        // Not in the staged set at all. A merge does not take absence for a deletion,
        // because absence from a filtered read is evidence of a filter.
        Assert.Equal(("alpha", 1), (after["P1|L1|A"].Descr, after["P1|L1|A"].Qty));
        Assert.Equal(before["P1|L1|A"].Version, after["P1|L1|A"].Version);

        // Staged, and identical. The row must not have been written: a bare WHEN MATCHED
        // would have rewritten it, moved this rowversion and fired every update trigger.
        Assert.Equal(("beta", 2), (after["P1|L1|B"].Descr, after["P1|L1|B"].Qty));
        Assert.Equal(before["P1|L1|B"].Version, after["P1|L1|B"].Version);

        // Staged and different, so written.
        Assert.Equal(("gamma changed", 30), (after["P1|L1|C"].Descr, after["P1|L1|C"].Qty));
        Assert.NotEqual(before["P1|L1|C"].Version, after["P1|L1|C"].Version);

        // Staged and missing, so inserted.
        Assert.Equal(("delta", 4), (after["P1|L1|D"].Descr, after["P1|L1|D"].Qty));
    }

    /// <summary>
    /// The headline requirement. Running the same merge twice changes nothing the second
    /// time - not the counts it reports, and not one row on disk.
    /// </summary>
    [LiveFact]
    public async Task TheSameMergeRunTwiceChangesNothingTheSecondTime()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await CreatePartTableAsync(connectionString, "dbo.Part");
        await CreateStageTableAsync(connectionString, "dbo.PartStage");

        await SqlServerFixture.ExecuteAsync(connectionString, """
            INSERT INTO dbo.Part (Plant, Line, Code, Descr, Qty) VALUES
                (N'P1', N'L1', N'A', N'alpha', 1);

            INSERT INTO dbo.PartStage (Plant, Line, Code, Descr, Qty) VALUES
                (N'P1', N'L1', N'A', N'alpha changed', 10),
                (N'P1', N'L1', N'B', N'beta',          2);
            """);

        var publisher = new MergePublisher();

        var first = await publisher.PublishAsync(connectionString, "dbo.PartStage", MergeStep(), CancellationToken.None);
        Assert.Equal(new PublishResult(1, 1, 0), first);

        var afterFirst = await ReadPartsAsync(connectionString);

        var second = await publisher.PublishAsync(connectionString, "dbo.PartStage", MergeStep(), CancellationToken.None);

        Assert.Equal(new PublishResult(0, 0, 0), second);

        var afterSecond = await ReadPartsAsync(connectionString);
        Assert.Equal(afterFirst.Keys.Order(), afterSecond.Keys.Order());

        foreach(var key in afterFirst.Keys)
        {
            Assert.Equal(afterFirst[key].Descr, afterSecond[key].Descr);
            Assert.Equal(afterFirst[key].Qty, afterSecond[key].Qty);

            // The proof that nothing was written, as opposed to written back the same.
            Assert.Equal(afterFirst[key].Version, afterSecond[key].Version);
        }
    }

    /// <summary>
    /// Every staged row is visited exactly once across the batches, and the batch size is
    /// the one that was configured. The deployed loader would have replaced a size like this
    /// with a tenth of the total.
    /// </summary>
    [LiveFact]
    public async Task ABatchedMergeVisitsEveryStagedRowExactlyOnce()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await CreatePartTableAsync(connectionString, "dbo.Part");
        await CreateStageTableAsync(connectionString, "dbo.PartStage");

        // A thousand rows already there with the wrong quantity, three thousand staged.
        await SqlServerFixture.ExecuteAsync(connectionString, """
            WITH n(i) AS (
                SELECT TOP (3000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
                FROM sys.all_columns a CROSS JOIN sys.all_columns b
            )
            INSERT INTO dbo.PartStage (Plant, Line, Code, Descr, Qty)
            SELECT N'P1', N'L1', RIGHT(N'00000' + CAST(i AS nvarchar(10)), 5), CONCAT(N'part ', i), i
            FROM n;

            INSERT INTO dbo.Part (Plant, Line, Code, Descr, Qty)
            SELECT Plant, Line, Code, Descr, -1
            FROM dbo.PartStage
            WHERE Code <= N'01000';
            """);

        var result = await new MergePublisher().PublishAsync(
            connectionString, "dbo.PartStage", MergeStep(batchSize: 500), CancellationToken.None);

        Assert.Equal(new PublishResult(2_000, 1_000, 0), result);
        Assert.Equal(3_000, await SqlServerFixture.CountAsync(connectionString, "dbo.Part"));

        // Every row got the staged quantity, so no page was skipped and none was visited
        // twice with the wrong value.
        Assert.Equal(0, Convert.ToInt32(await SqlServerFixture.ScalarAsync(connectionString, """
            SELECT COUNT(*)
            FROM dbo.Part AS p
            INNER JOIN dbo.PartStage AS s ON s.Plant = p.Plant AND s.Line = p.Line AND s.Code = p.Code
            WHERE p.Qty <> s.Qty OR p.Descr <> s.Descr;
            """)));

        var again = await new MergePublisher().PublishAsync(
            connectionString, "dbo.PartStage", MergeStep(batchSize: 500), CancellationToken.None);

        Assert.Equal(new PublishResult(0, 0, 0), again);
    }

    [LiveFact]
    public async Task AnAppendAddsTheStagedRowsAndKeepsWhatWasAlreadyThere()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await SqlServerFixture.SeedAsync(connectionString, "dbo.Customer", rows: 3);
        await SqlServerFixture.ExecuteAsync(connectionString, """
            CREATE TABLE dbo.CustomerStage (
                Id        int           NOT NULL,
                Name      nvarchar(120) NOT NULL,
                Amount    decimal(19,4) NOT NULL,
                ChangedAt datetime2(3)      NULL
            );

            INSERT INTO dbo.CustomerStage (Id, Name, Amount, ChangedAt) VALUES
                (100, N'new one', 10.0, NULL),
                (101, N'new two', 20.0, NULL);
            """);

        var before = await ReadCustomersAsync(connectionString);

        var result = await new AppendPublisher().PublishAsync(
            connectionString, "dbo.CustomerStage", AppendStep(keepIdentity: true), CancellationToken.None);

        Assert.Equal(new PublishResult(2, 0, 0), result);

        var after = await ReadCustomersAsync(connectionString);
        Assert.Equal(5, after.Count);

        // Nothing that was there before was touched, by value or by rowversion.
        foreach(var (id, row) in before)
        {
            Assert.Equal(row.Name, after[id].Name);
            Assert.Equal(row.Version, after[id].Version);
        }

        // KeepIdentity, so the staged keys are the keys that landed.
        Assert.Equal("new one", after[100].Name);
        Assert.Equal("new two", after[101].Name);
    }

    /// <summary>
    /// The other half of KeepIdentity: the identity column does not travel and the
    /// destination assigns its own, so the staged ids are not the ids that land.
    /// </summary>
    [LiveFact]
    public async Task AnAppendThatDoesNotKeepIdentityLetsTheDestinationAssignTheKeys()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await SqlServerFixture.SeedAsync(connectionString, "dbo.Customer", rows: 3);
        await SqlServerFixture.ExecuteAsync(connectionString, """
            CREATE TABLE dbo.CustomerStage (
                Id        int           NOT NULL,
                Name      nvarchar(120) NOT NULL,
                Amount    decimal(19,4) NOT NULL,
                ChangedAt datetime2(3)      NULL
            );

            INSERT INTO dbo.CustomerStage (Id, Name, Amount, ChangedAt) VALUES (999, N'assigned', 1.0, NULL);
            """);

        var result = await new AppendPublisher().PublishAsync(
            connectionString, "dbo.CustomerStage", AppendStep(keepIdentity: false), CancellationToken.None);

        Assert.Equal(new PublishResult(1, 0, 0), result);

        var landed = await ReadCustomersAsync(connectionString);
        Assert.Equal(4, landed.Count);
        Assert.DoesNotContain(999, landed.Keys);
        Assert.Equal("assigned", landed[4].Name);
    }

    /// <summary>
    /// The failure the whole package is built against: a destination that has grown a column
    /// its staging table has not. There is no publication by position here, so it stops.
    /// </summary>
    [LiveFact]
    public async Task AStagingTableThatHasDriftedFromItsDestinationIsRefusedRatherThanShifted()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await CreatePartTableAsync(connectionString, "dbo.Part");
        await SqlServerFixture.ExecuteAsync(connectionString, """
            CREATE TABLE dbo.PartStage (
                Plant nvarchar(20)  NOT NULL,
                Line  nvarchar(20)  NOT NULL,
                Code  nvarchar(20)  NOT NULL,
                Descr nvarchar(100) NOT NULL
            );
            """);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MergePublisher().PublishAsync(connectionString, "dbo.PartStage", MergeStep(), CancellationToken.None));

        Assert.Contains("Qty", failure.Message, StringComparison.Ordinal);
        Assert.Contains("drifted apart", failure.Message, StringComparison.Ordinal);
    }

    [LiveFact]
    public async Task AMergeRefusesAStepThatIsNotConfiguredToMerge()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var step = MergeStep();
        step.Publication.Mode = PublicationMode.Replace;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new MergePublisher().PublishAsync(connectionString, "dbo.PartStage", step, CancellationToken.None));
    }

    private static SyncStep MergeStep(int batchSize = 0) => new()
    {
        Id = "parts",
        Name = "parts",
        DestinationTable = "dbo.Part",
        BatchSize = batchSize,
        Publication = new PublicationPlan { Mode = PublicationMode.Merge },
        FieldMaps =
        [
            new FieldMap { Target = "Plant", IsUniqueKey = true },
            new FieldMap { Target = "Line", IsUniqueKey = true },
            new FieldMap { Target = "Code", IsUniqueKey = true }
        ]
    };

    private static SyncStep AppendStep(bool keepIdentity) => new()
    {
        Id = "customers",
        Name = "customers",
        DestinationTable = "dbo.Customer",
        Publication = new PublicationPlan { Mode = PublicationMode.Append, KeepIdentity = keepIdentity }
    };

    private static Task CreatePartTableAsync(string connectionString, string table) =>
        SqlServerFixture.ExecuteAsync(connectionString, $"""
            CREATE TABLE {table} (
                Plant   nvarchar(20)  NOT NULL,
                Line    nvarchar(20)  NOT NULL,
                Code    nvarchar(20)  NOT NULL,
                Descr   nvarchar(100) NOT NULL,
                Qty     int           NOT NULL,
                Version rowversion    NOT NULL,
                PRIMARY KEY (Plant, Line, Code)
            );
            """);

    /// <summary>
    /// Staging in the shape a factory would give it: the destination's columns, without the
    /// rowversion the server stamps itself and without the indexes rows load faster without.
    /// </summary>
    private static Task CreateStageTableAsync(string connectionString, string table) =>
        SqlServerFixture.ExecuteAsync(connectionString, $"""
            CREATE TABLE {table} (
                Plant nvarchar(20)  NOT NULL,
                Line  nvarchar(20)  NOT NULL,
                Code  nvarchar(20)  NOT NULL,
                Descr nvarchar(100) NOT NULL,
                Qty   int           NOT NULL
            );
            """);

    private static async Task<Dictionary<string, (string Descr, int Qty, byte[] Version)>> ReadPartsAsync(string connectionString)
    {
        var rows = new Dictionary<string, (string, int, byte[])>(StringComparer.Ordinal);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT Plant, Line, Code, Descr, Qty, Version FROM dbo.Part;", connection);
        await using var reader = await command.ExecuteReaderAsync();

        while(await reader.ReadAsync())
        {
            rows[$"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}"] =
                (reader.GetString(3), reader.GetInt32(4), (byte[])reader.GetValue(5));
        }

        return rows;
    }

    private static async Task<Dictionary<int, (string Name, byte[] Version)>> ReadCustomersAsync(string connectionString)
    {
        var rows = new Dictionary<int, (string, byte[])>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT Id, Name, Version FROM dbo.Customer;", connection);
        await using var reader = await command.ExecuteReaderAsync();

        while(await reader.ReadAsync())
            rows[reader.GetInt32(0)] = (reader.GetString(1), (byte[])reader.GetValue(2));

        return rows;
    }
}
