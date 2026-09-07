using SyncJob.Core.Model;
using SyncJob.Core.Run;

namespace SyncJob.IntegrationTests;

/// <summary>
/// Whole jobs, source database to destination database, through <see cref="JobRunner"/>.
/// <para>
/// Two databases rather than two schemas in one, as everywhere else here: a pipeline that
/// accidentally reads and writes the same physical table passes a one-database test and
/// fails in production.
/// </para>
/// <para>
/// The destinations are built by <c>PublicationFixture</c>, so every one of them carries
/// the things a hand-maintained staging table gets wrong - an identity, a computed column,
/// a rowversion, a default, a check constraint, a clustered key and a filtered index. The
/// rowversion is what makes "left alone" something these tests can assert rather than
/// infer: SQL Server moves it when and only when a row is actually written, so a
/// destination that is unchanged is unchanged row for row and not merely by count.
/// </para>
/// <para>
/// Most of these set <see cref="JobRunOptions.UseLease"/> to false. The lease has its own
/// package and its own live tests; <see cref="ARunWhoseLeaseIsHeldElsewhereTouchesNothing"/>
/// is the one that goes through it, because refusing to start is the runner's decision and
/// not the store's.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RunnerLiveTests(SqlServerFixture fixture)
{
    /// <summary>
    /// The whole of a replace, and the watermark with it.
    /// <para>
    /// The watermark is the interesting half. It is read out of the staging table, and it
    /// has to be read before the publication rather than after: a replace publishes by
    /// exchanging the staged table's storage with the destination's, so a maximum read
    /// after the swap is a maximum over an empty table. If this ever reports no watermark
    /// while the rows arrived, that ordering is what broke.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task AReplaceMovesTheWholeTableAndLeavesTheWatermarkWhereTheRowsEnded()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await PublicationFixture.CreateLedgerAsync(source, "dbo.Ledger");
        await PublicationFixture.CreateLedgerAsync(destination, "dbo.Ledger");
        await PublicationFixture.LoadAsync(source, "dbo.Ledger", rows: 500, firstId: 1);
        await PublicationFixture.LoadAsync(destination, "dbo.Ledger", rows: 100, firstId: 9_000);

        var step = Step(PublicationMode.Replace);
        step.Incremental = new IncrementalPlan { Watermark = new WatermarkPlan() };
        step.FieldMaps.Add(new FieldMap { Source = "Id", Target = "Id", IsSyncKey = true });

        var run = await RunAsync(source, destination, step);

        Assert.Equal(RunStatus.Succeeded, run.Status);

        var result = Assert.Single(run.Steps);
        Assert.Equal(500, result.RowsRead);
        Assert.Equal(500, result.RowsInserted);
        Assert.Equal(100, result.RowsDeleted);

        // Row for row, and not by count: a publication that goes in shifted has the right
        // count.
        Assert.Equal(
            await RowsAsync(source, "dbo.Ledger", withVersion: false),
            await RowsAsync(destination, "dbo.Ledger", withVersion: false));

        Assert.Equal("500", result.WatermarkValue);
        Assert.Equal("500", await SqlServerFixture.ScalarAsync(
            destination, "SELECT [Value] FROM dbo.SyncJobWatermark;"));

        Assert.Empty(await LeftoverStagingAsync(destination));
    }

    [LiveFact]
    public async Task AnAppendAddsItsRowsAndTouchesNothingThatWasAlreadyThere()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await PublicationFixture.CreateLedgerAsync(source, "dbo.Ledger");
        await PublicationFixture.CreateLedgerAsync(destination, "dbo.Ledger");
        await PublicationFixture.LoadAsync(source, "dbo.Ledger", rows: 50, firstId: 1_000);
        await PublicationFixture.LoadAsync(destination, "dbo.Ledger", rows: 100, firstId: 1);

        var before = await RowsAsync(destination, "dbo.Ledger");

        var run = await RunAsync(source, destination, Step(PublicationMode.Append));

        Assert.Equal(RunStatus.Succeeded, run.Status);

        var result = Assert.Single(run.Steps);
        Assert.Equal(50, result.RowsInserted);
        Assert.Equal(0, result.RowsUpdated);
        Assert.Equal(0, result.RowsDeleted);

        var after = await RowsAsync(destination, "dbo.Ledger");
        Assert.Equal(150, after.Count);

        // Every row that was there is still there with its rowversion untouched, which is
        // what "nothing existing is touched" actually means.
        Assert.Equal(before, after.Take(100).ToList());

        Assert.Empty(await LeftoverStagingAsync(destination));
    }

    /// <summary>
    /// A merge, judged on what it did to each row rather than on its counts. A merge that
    /// rewrote every row it matched has the right count; the rowversion is what tells the
    /// two apart.
    /// </summary>
    [LiveFact]
    public async Task AMergeInsertsWhatIsMissingAndUpdatesOnlyWhatDiffers()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await CreatePartsAsync(source);
        await CreatePartsAsync(destination);

        await SqlServerFixture.ExecuteAsync(destination, """
            INSERT INTO dbo.Part (Code, Descr, Qty) VALUES
                (N'A', N'alpha', 1), (N'B', N'beta', 2), (N'C', N'gamma', 3);
            """);

        await SqlServerFixture.ExecuteAsync(source, """
            INSERT INTO dbo.Part (Code, Descr, Qty) VALUES
                (N'B', N'beta', 2), (N'C', N'gamma changed', 30), (N'D', N'delta', 4);
            """);

        var before = await PartsAsync(destination);

        var step = Step(PublicationMode.Merge);
        step.Source = new SourceQuery { Sql = "SELECT Code, Descr, Qty FROM dbo.Part" };
        step.DestinationTable = "dbo.Part";
        step.FieldMaps.Add(new FieldMap { Source = "Code", Target = "Code", IsUniqueKey = true });

        var run = await RunAsync(source, destination, step);

        Assert.Equal(RunStatus.Succeeded, run.Status);

        var result = Assert.Single(run.Steps);
        Assert.Equal(3, result.RowsRead);
        Assert.Equal(1, result.RowsInserted);
        Assert.Equal(1, result.RowsUpdated);

        var after = await PartsAsync(destination);
        Assert.Equal(4, after.Count);

        // Not staged at all, so left alone: absence from a filtered read is evidence of a
        // filter and not of a deletion.
        Assert.Equal(before[0], after[0]);

        // Staged and identical, so not written: a bare WHEN MATCHED would have moved this
        // rowversion and fired every update trigger on the table.
        Assert.Equal(before[1], after[1]);

        Assert.NotEqual(before[2], after[2]);
        Assert.Contains("gamma changed", after[2], StringComparison.Ordinal);
        Assert.Contains("delta", after[3], StringComparison.Ordinal);

        Assert.Empty(await LeftoverStagingAsync(destination));
    }

    /// <summary>
    /// The failure the guard exists for, end to end. A source that comes back nearly empty
    /// followed by a replace is a job that empties a production table and reports success;
    /// here the destination is compared row for row, rowversion included, so a
    /// publication that emptied and refilled it identically would still be caught.
    /// </summary>
    [LiveFact]
    public async Task AGuardThatRefusesLeavesTheDestinationExactlyAsItWasRowForRow()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await PublicationFixture.CreateLedgerAsync(source, "dbo.Ledger");
        await PublicationFixture.CreateLedgerAsync(destination, "dbo.Ledger");
        await PublicationFixture.LoadAsync(source, "dbo.Ledger", rows: 5, firstId: 1);
        await PublicationFixture.LoadAsync(destination, "dbo.Ledger", rows: 300, firstId: 1);

        var before = await RowsAsync(destination, "dbo.Ledger");

        var step = Step(PublicationMode.Replace);
        step.Publication.Guard = new PublicationGuard
        {
            MinimumFractionOfDestination = 0.5,
            OnFailure = GuardFailureAction.Abort
        };

        var run = await RunAsync(source, destination, step);

        Assert.Equal(RunStatus.Failed, run.Status);

        var result = Assert.Single(run.Steps);
        Assert.Equal(5, result.RowsRead);
        Assert.Equal(0, result.RowsInserted);
        Assert.Contains("the guard refused", result.Message!, StringComparison.Ordinal);
        Assert.Contains("150 required", result.Message, StringComparison.Ordinal);

        Assert.Equal(before, await RowsAsync(destination, "dbo.Ledger"));
        Assert.Empty(await LeftoverStagingAsync(destination));
    }

    /// <summary>
    /// A dry run that reports "OK" is worth nothing. This one has really read the source
    /// and really counted what it staged, so the number in its message is the number a
    /// real run would publish - and the destination is untouched to the rowversion.
    /// </summary>
    [LiveFact]
    public async Task ADryRunSaysWhatItWouldPublishAndWritesNothing()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await PublicationFixture.CreateLedgerAsync(source, "dbo.Ledger");
        await PublicationFixture.CreateLedgerAsync(destination, "dbo.Ledger");
        await PublicationFixture.LoadAsync(source, "dbo.Ledger", rows: 1_240, firstId: 1);
        await PublicationFixture.LoadAsync(destination, "dbo.Ledger", rows: 300, firstId: 1);

        var before = await RowsAsync(destination, "dbo.Ledger");

        var step = Step(PublicationMode.Replace);
        step.Incremental = new IncrementalPlan { Watermark = new WatermarkPlan() };
        step.FieldMaps.Add(new FieldMap { Source = "Id", Target = "Id", IsSyncKey = true });

        var run = await RunAsync(source, destination, step, dryRun: true);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.True(run.DryRun);

        var result = Assert.Single(run.Steps);
        Assert.Equal(1_240, result.RowsRead);
        Assert.Equal(0, result.RowsInserted);
        Assert.Contains("1,240 rows were staged", result.Message!, StringComparison.Ordinal);
        Assert.Contains("would replace dbo.Ledger", result.Message, StringComparison.Ordinal);
        Assert.Contains("would move to 1240", result.Message, StringComparison.Ordinal);

        Assert.Equal(before, await RowsAsync(destination, "dbo.Ledger"));
        Assert.Empty(await LeftoverStagingAsync(destination));

        // The watermark is reported and not moved: a dry run that advanced it would make
        // the real run that follows read from a point it never loaded.
        //
        // The state table itself is there and empty, which is the one write a dry run
        // does make and is worth knowing about: SqlWatermarkStore.ReadAsync ensures its
        // table, and a dry run that skipped the read would report the wrong source query
        // for a step that has a watermark - which is the whole thing a dry run is for.
        Assert.Null(result.WatermarkValue);
        Assert.Equal(0, await SqlServerFixture.CountAsync(destination, "dbo.SyncJobWatermark"));
    }

    /// <summary>
    /// A step that fails after its staging table exists still takes it away. The deployed
    /// system does not, and the tables accumulate until somebody notices the database has
    /// three hundred of them.
    /// </summary>
    [LiveFact]
    public async Task AStepThatFailsAfterStagingWasCreatedStillTakesItAway()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await PublicationFixture.CreateLedgerAsync(source, "dbo.Ledger");
        await PublicationFixture.CreateLedgerAsync(destination, "dbo.Ledger");
        await PublicationFixture.LoadAsync(source, "dbo.Ledger", rows: 10, firstId: 1);

        var step = Step(PublicationMode.Replace);

        // A column the destination has never heard of. The copy refuses by name rather
        // than falling back to position, which is the whole of WP 1.1 - and it refuses
        // after the staging table has been created, which is the point here.
        step.Source = new SourceQuery { Sql = "SELECT Id, Name, Amount, ChangedAt, Amount AS Invented FROM dbo.Ledger" };

        var run = await RunAsync(source, destination, step);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("Invented", Assert.Single(run.Steps).Message!, StringComparison.Ordinal);
        Assert.Empty(await LeftoverStagingAsync(destination));
    }

    /// <summary>
    /// The scheduler's normal Tuesday: another host is already running this job. The run
    /// comes back saying so, and nothing on the destination moves - not the table, not the
    /// watermark and not a staging table.
    /// </summary>
    [LiveFact]
    public async Task ARunWhoseLeaseIsHeldElsewhereTouchesNothing()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await PublicationFixture.CreateLedgerAsync(source, "dbo.Ledger");
        await PublicationFixture.CreateLedgerAsync(destination, "dbo.Ledger");
        await PublicationFixture.LoadAsync(source, "dbo.Ledger", rows: 200, firstId: 1);
        await PublicationFixture.LoadAsync(destination, "dbo.Ledger", rows: 300, firstId: 1);

        var before = await RowsAsync(destination, "dbo.Ledger");
        var job = Job(source, destination, Step(PublicationMode.Replace));

        var leases = new SqlJobLeaseStore();
        await leases.EnsureTableAsync(destination, CancellationToken.None);

        var held = await leases.TryAcquireAsync(
            destination, job.Id, "a-run-on-another-host", "elsewhere/1", TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.NotNull(held);

        var refused = await new JobRunner(leases).RunAsync(job, new JobRunOptions { UseLease = true }, CancellationToken.None);

        Assert.Equal(RunStatus.Skipped, refused.Status);
        Assert.Contains("another run holds this job's lease", Assert.Single(refused.Steps).Message!, StringComparison.Ordinal);
        Assert.Equal(before, await RowsAsync(destination, "dbo.Ledger"));
        Assert.Empty(await LeftoverStagingAsync(destination));

        // And once the claim is given back, the same job runs and gives its own back.
        await leases.ReleaseAsync(destination, job.Id, held, CancellationToken.None);

        var run = await new JobRunner(leases).RunAsync(job, new JobRunOptions { UseLease = true }, CancellationToken.None);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.NotNull(run.Lease);
        Assert.Equal(200, await SqlServerFixture.CountAsync(destination, "dbo.Ledger"));

        var again = await leases.TryAcquireAsync(
            destination, job.Id, "someone-later", "elsewhere/2", TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.NotNull(again);
    }

    /// <summary>
    /// Two steps into two tables, in order, with the first one failing. A failure stops
    /// the run unless the job says otherwise, and the second table is the evidence.
    /// </summary>
    [LiveFact]
    public async Task ContinueOnErrorDecidesWhetherTheSecondTableIsLoaded()
    {
        foreach(var carryOn in new[] { false, true })
        {
            var (source, destination) = await fixture.CreatePairAsync();
            await PublicationFixture.CreateLedgerAsync(source, "dbo.Ledger");
            await PublicationFixture.CreateLedgerAsync(destination, "dbo.First");
            await PublicationFixture.CreateLedgerAsync(destination, "dbo.Second");
            await PublicationFixture.LoadAsync(source, "dbo.Ledger", rows: 20, firstId: 1);

            var broken = Step(PublicationMode.Replace, id: "first");
            broken.DestinationTable = "dbo.First";
            broken.Source = new SourceQuery { Sql = "SELECT Id, Name, Amount, ChangedAt, Amount AS Invented FROM dbo.Ledger" };

            var good = Step(PublicationMode.Replace, id: "second");
            good.Order = 2;
            good.DestinationTable = "dbo.Second";

            var run = await new JobRunner().RunAsync(
                Job(source, destination, broken, good),
                new JobRunOptions { UseLease = false, ContinueOnError = carryOn, TriggeredBy = nameof(RunnerLiveTests) },
                CancellationToken.None);

            Assert.Equal(RunStatus.Failed, run.Status);
            Assert.Equal(carryOn ? 2 : 1, run.Steps.Count);
            Assert.Equal(0, await SqlServerFixture.CountAsync(destination, "dbo.First"));
            Assert.Equal(carryOn ? 20 : 0, await SqlServerFixture.CountAsync(destination, "dbo.Second"));
            Assert.Empty(await LeftoverStagingAsync(destination));
        }
    }

    // ------------------------------------------------------------------ setup

    private static Task<JobRun> RunAsync(string source, string destination, SyncStep step, bool dryRun = false) =>
        new JobRunner().RunAsync(
            Job(source, destination, step),
            new JobRunOptions { UseLease = false, DryRun = dryRun, TriggeredBy = nameof(RunnerLiveTests) },
            CancellationToken.None);

    private static SyncJobDefinition Job(string source, string destination, params SyncStep[] steps) => new()
    {
        // A fresh id per test, so that two of them cannot meet over one job's lease or
        // one job's watermark row.
        Id = "live-" + Guid.NewGuid().ToString("N")[..8],
        Name = "a live job",
        Destination = new Endpoint { Id = "dst", Name = "the destination", ConnectionString = destination },
        Sources = { new Endpoint { Id = "src", Name = "the source", ConnectionString = source } },
        Steps = [.. steps]
    };

    private static SyncStep Step(PublicationMode mode, string id = "step") => new()
    {
        Id = id,
        Name = id,
        Order = 1,
        SourceEndpointId = "src",
        Source = new SourceQuery { Sql = "SELECT Id, Name, Amount, ChangedAt FROM dbo.Ledger" },
        DestinationTable = "dbo.Ledger",
        Publication = new PublicationPlan { Mode = mode, Guard = new PublicationGuard { MinimumRows = 1 } }
    };

    private static Task CreatePartsAsync(string connectionString) =>
        SqlServerFixture.ExecuteAsync(connectionString, """
            CREATE TABLE dbo.Part (
                Code    nvarchar(20)  NOT NULL CONSTRAINT PK_Part PRIMARY KEY,
                Descr   nvarchar(100) NOT NULL,
                Qty     int           NOT NULL,
                Version rowversion    NOT NULL
            );
            """);

    /// <summary>
    /// Every row of a ledger as one string, rowversion included, so that "unchanged" is
    /// something these tests assert rather than infer from a count.
    /// </summary>
    private static Task<List<string>> RowsAsync(string connectionString, string table, bool withVersion = true) =>
        PublicationFixture.StringsAsync(connectionString, $"""
            SELECT CONCAT(
                       Id, '|', Name, '|', Amount, '|',
                       ISNULL(CONVERT(nvarchar(30), ChangedAt, 126), ''),
                       '{(withVersion ? "|" : "")}', {(withVersion ? "CONVERT(nvarchar(30), CAST(Version AS binary(8)), 1)" : "''")})
            FROM {table}
            ORDER BY Id;
            """);

    private static Task<List<string>> PartsAsync(string connectionString) =>
        PublicationFixture.StringsAsync(connectionString, """
            SELECT CONCAT(Code, '|', Descr, '|', Qty, '|', CONVERT(nvarchar(30), CAST(Version AS binary(8)), 1))
            FROM dbo.Part
            ORDER BY Code;
            """);

    /// <summary>
    /// Staging tables the engine named and did not take away. Recognised by the shape the
    /// factory generates - <c>&lt;destination&gt;_stg_&lt;8 hex&gt;</c>.
    /// </summary>
    private static Task<List<string>> LeftoverStagingAsync(string connectionString) =>
        PublicationFixture.StringsAsync(
            connectionString,
            "SELECT name FROM sys.tables WHERE name LIKE '%[_]stg[_]%' ORDER BY name;");
}
