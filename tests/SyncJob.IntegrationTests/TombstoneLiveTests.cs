using SyncJob.Core.Incremental;
using SyncJob.Core.Model;

namespace SyncJob.IntegrationTests;

/// <summary>
/// Deletions read from a ledger on the source and applied to the destination.
/// <para>
/// The three-part key is the case worth the server time. The deployed implementation splits
/// the legacy key with <c>PARSENAME</c>, which counts from the right and gives up past four
/// parts: the leading slots come back null, the join matches nothing, and the run deletes
/// nothing and reports nothing. Every one of these tests would pass against that
/// implementation if it only counted rows, so they check which rows survived.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class TombstoneLiveTests(SqlServerFixture fixture)
{
    /// <summary>
    /// The one the deployed engine silently misses.
    /// </summary>
    [LiveFact]
    public async Task AThreePartKeyDeletesTheRowsItNamesAndMarksTheLedger()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await CreateLedgerAsync(source);
        await CreatePartsAsync(destination);

        await SqlServerFixture.ExecuteAsync(source, """
            INSERT INTO dbo.Tombstone (KeyValue) VALUES (N'P1;L1;B'), (N'P1;L1;C');
            """);

        var deleted = await new SqlTombstoneApplier().ApplyAsync(
            source, destination, Step(), Ledger(), CancellationToken.None);

        Assert.Equal(2, deleted);

        // Which rows, not how many. Two of the four are gone and they are the two named.
        Assert.Equal(["P1|L1|A", "P1|L1|D"], await ReadKeysAsync(destination));

        Assert.Equal(2, Convert.ToInt32(await SqlServerFixture.ScalarAsync(
            source, "SELECT COUNT(*) FROM dbo.Tombstone WHERE Processed = 1;")));
    }

    [LiveFact]
    public async Task TheSameLedgerAppliedTwiceDeletesNothingTheSecondTime()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await CreateLedgerAsync(source);
        await CreatePartsAsync(destination);

        await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.Tombstone (KeyValue) VALUES (N'P1;L1;B');");

        var applier = new SqlTombstoneApplier();

        Assert.Equal(1, await applier.ApplyAsync(source, destination, Step(), Ledger(), CancellationToken.None));
        Assert.Equal(0, await applier.ApplyAsync(source, destination, Step(), Ledger(), CancellationToken.None));

        Assert.Equal(["P1|L1|A", "P1|L1|C", "P1|L1|D"], await ReadKeysAsync(destination));
    }

    [LiveFact]
    public async Task AKeyInOneColumnPerPartDeletesTheSameRows()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await CreatePartsAsync(destination);
        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.Tombstone (
                Id        bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Plant     nvarchar(20) NOT NULL,
                Line      nvarchar(20) NOT NULL,
                Code      nvarchar(20) NOT NULL,
                Processed bit NOT NULL CONSTRAINT DF_Tombstone_Processed DEFAULT (0)
            );

            INSERT INTO dbo.Tombstone (Plant, Line, Code) VALUES (N'P1', N'L1', N'B'), (N'P1', N'L1', N'D');
            """);

        var deleted = await new SqlTombstoneApplier().ApplyAsync(
            source, destination, Step(), Ledger(format: TombstoneKeyFormat.Columns), CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal(["P1|L1|A", "P1|L1|C"], await ReadKeysAsync(destination));
    }

    /// <summary>
    /// A ledger row whose key does not match the delete key is said out loud, the rows that
    /// were well formed are still applied, and the malformed ones are left unprocessed so
    /// that fixing them is possible at all.
    /// </summary>
    [LiveFact]
    public async Task AKeyWithTheWrongNumberOfPartsIsReportedRatherThanQuietlyMissed()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await CreateLedgerAsync(source);
        await CreatePartsAsync(destination);

        await SqlServerFixture.ExecuteAsync(source, """
            INSERT INTO dbo.Tombstone (KeyValue) VALUES (N'P1;L1;B'), (N'P1;L1'), (N'P1;L1;C;EXTRA');
            """);

        var failure = await Assert.ThrowsAsync<TombstoneKeyFormatException>(() =>
            new SqlTombstoneApplier().ApplyAsync(source, destination, Step(), Ledger(), CancellationToken.None));

        Assert.Equal(2, failure.Problems.Count);
        Assert.Contains(failure.Problems, x => x.Parts == 2);
        Assert.Contains(failure.Problems, x => x.Parts == 4);

        // The well-formed deletion still happened: a bad row costs the run its green tick,
        // not the work that was correct.
        Assert.Equal(["P1|L1|A", "P1|L1|C", "P1|L1|D"], await ReadKeysAsync(destination));

        Assert.Equal(1, Convert.ToInt32(await SqlServerFixture.ScalarAsync(
            source, "SELECT COUNT(*) FROM dbo.Tombstone WHERE Processed = 1;")));
        Assert.Equal(2, Convert.ToInt32(await SqlServerFixture.ScalarAsync(
            source, "SELECT COUNT(*) FROM dbo.Tombstone WHERE Processed = 0;")));
    }

    /// <summary>
    /// Where the source may not be written to, the destination keeps its own record of how
    /// far it has got, and that record and the deletions are one transaction because they
    /// are in one database.
    /// </summary>
    [LiveFact]
    public async Task WithoutWriteAccessToTheSourceTheDestinationRemembersWhatItApplied()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await CreateLedgerAsync(source);
        await CreatePartsAsync(destination);

        await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.Tombstone (KeyValue) VALUES (N'P1;L1;B'), (N'P1;L1;C');");

        var applier = new SqlTombstoneApplier();
        var ledger = Ledger(markProcessed: false);

        Assert.Equal(2, await applier.ApplyAsync(source, destination, Step(), ledger, CancellationToken.None));

        // The source was not written to at all.
        Assert.Equal(0, Convert.ToInt32(await SqlServerFixture.ScalarAsync(
            source, "SELECT COUNT(*) FROM dbo.Tombstone WHERE Processed = 1;")));

        Assert.Equal(2L, Convert.ToInt64(await SqlServerFixture.ScalarAsync(
            destination, $"SELECT AppliedThroughId FROM {SqlTombstoneApplier.DefaultHighWaterTable};")));

        // The same ledger again reads nothing, because the destination knows where it got to.
        Assert.Equal(0, await applier.ApplyAsync(source, destination, Step(), ledger, CancellationToken.None));

        // And a new deletion after that one is still picked up.
        await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.Tombstone (KeyValue) VALUES (N'P1;L1;D');");

        Assert.Equal(1, await applier.ApplyAsync(source, destination, Step(), ledger, CancellationToken.None));
        Assert.Equal(["P1|L1|A"], await ReadKeysAsync(destination));
    }

    /// <summary>
    /// The ledger stores its keys as text and the destination's key is an integer. The
    /// parameter is the side that gets converted, so the destination's index still works and
    /// the comparison is the one the column's type defines.
    /// </summary>
    [LiveFact]
    public async Task ATextKeyDeletesFromADestinationWhoseKeyIsNot()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await CreateLedgerAsync(source);
        await SqlServerFixture.SeedAsync(destination, "dbo.Customer", rows: 4);

        await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.Tombstone (KeyValue) VALUES (N'2'), (N'4');");

        var step = new SyncStep
        {
            Id = "customers",
            Name = "customers",
            DestinationTable = "dbo.Customer",
            FieldMaps = [new FieldMap { Target = "Id", IsDeleteKey = true }]
        };

        Assert.Equal(2, await new SqlTombstoneApplier().ApplyAsync(source, destination, step, Ledger(), CancellationToken.None));

        Assert.Equal("1,3", await SqlServerFixture.ScalarAsync(
            destination, "SELECT STRING_AGG(CAST(Id AS nvarchar(10)), ',') WITHIN GROUP (ORDER BY Id) FROM dbo.Customer;"));
    }

    [LiveFact]
    public async Task AStepWithNoDeleteKeyIsRefused()
    {
        var (source, destination) = await fixture.CreatePairAsync();

        var step = new SyncStep { Id = "parts", Name = "parts", DestinationTable = "dbo.Part" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqlTombstoneApplier().ApplyAsync(source, destination, step, Ledger(), CancellationToken.None));
    }

    private static SyncStep Step() => new()
    {
        Id = "parts",
        Name = "parts",
        DestinationTable = "dbo.Part",
        FieldMaps =
        [
            new FieldMap { Target = "Plant", IsDeleteKey = true },
            new FieldMap { Target = "Line", IsDeleteKey = true },
            new FieldMap { Target = "Code", IsDeleteKey = true }
        ]
    };

    private static TombstoneLedger Ledger(
        bool markProcessed = true,
        TombstoneKeyFormat format = TombstoneKeyFormat.Legacy) => new()
    {
        Table = "dbo.Tombstone",
        KeyFormat = format,
        LegacySeparator = ";",
        MarkProcessed = markProcessed
    };

    private static Task CreateLedgerAsync(string connectionString) =>
        SqlServerFixture.ExecuteAsync(connectionString, """
            CREATE TABLE dbo.Tombstone (
                Id        bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
                KeyValue  nvarchar(400)            NULL,
                Processed bit NOT NULL CONSTRAINT DF_Tombstone_Processed DEFAULT (0)
            );
            """);

    private static Task CreatePartsAsync(string connectionString) =>
        SqlServerFixture.ExecuteAsync(connectionString, """
            CREATE TABLE dbo.Part (
                Plant nvarchar(20)  NOT NULL,
                Line  nvarchar(20)  NOT NULL,
                Code  nvarchar(20)  NOT NULL,
                Descr nvarchar(100) NOT NULL,
                PRIMARY KEY (Plant, Line, Code)
            );

            INSERT INTO dbo.Part (Plant, Line, Code, Descr) VALUES
                (N'P1', N'L1', N'A', N'alpha'),
                (N'P1', N'L1', N'B', N'beta'),
                (N'P1', N'L1', N'C', N'gamma'),
                (N'P1', N'L1', N'D', N'delta');
            """);

    private static async Task<string[]> ReadKeysAsync(string connectionString)
    {
        var keys = await SqlServerFixture.ScalarAsync(connectionString, """
            SELECT STRING_AGG(CONCAT(Plant, N'|', Line, N'|', Code), N',') WITHIN GROUP (ORDER BY Plant, Line, Code)
            FROM dbo.Part;
            """);

        return keys is null or DBNull ? [] : ((string)keys).Split(',');
    }
}
