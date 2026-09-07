using SyncJob.Core.Incremental;

namespace SyncJob.IntegrationTests;

/// <summary>
/// The watermark store against a real server.
/// <para>
/// The value is the easy half. The previous value is the half that matters, because what an
/// operator does when a load goes wrong is re-run from where it was before, and the only
/// alternative to having kept that value is guessing it.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class WatermarkLiveTests(SqlServerFixture fixture)
{
    /// <summary>
    /// The catch on error 2714 in <c>EnsureTableAsync</c> actually fires.
    /// <para>
    /// It was written on the reasoning that two runs of the same job starting together can
    /// both find the table missing - which is sound, and was not evidence. WP 1.5c found
    /// while testing the same catch in the lease store that this race takes real pressure
    /// to provoke: eight hosts calling once never collided there, and it took a dozen
    /// rounds on a dozen different table names. So this test has that shape, and the catch
    /// is no longer a comment about a race nobody had seen.
    /// </para>
    /// <para>
    /// A fresh table per round is the point: once the table exists the <c>IF</c> short
    /// circuits and there is nothing left to race.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task TheStateTableSurvivesHostsRacingToCreateIt()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        for(var round = 0; round < 12; round++)
        {
            var table = $"dbo.Watermark{round}";

            var hosts = Enumerable.Range(0, 8).Select(host => Task.Run(async () =>
                await new SqlWatermarkStore(table).WriteAsync(
                    connectionString, "job", $"step{host}", $"v{host}", CancellationToken.None)));

            // Any host that lost the race and did not swallow 2714 surfaces here.
            await Task.WhenAll(hosts);

            Assert.Equal(8, await SqlServerFixture.CountAsync(connectionString, table));
        }
    }

    /// <summary>
    /// The upgrade path every incremental installation is about to walk.
    /// <para>
    /// SyncJob's deployed engine keeps <c>dbo.SyncJobTracking</c>, and INCREMENTAL_SYNC.md
    /// and README.md both tell operators to write exactly that name into their
    /// configuration. Its shape is one row per job. This store keys a step, and its
    /// CREATE is guarded by OBJECT_ID - so against a real installation it creates nothing
    /// and the first read fails with "Invalid column name", which tells the operator
    /// neither what happened nor what to do.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task ATableThatIsNotAWatermarkTableIsRefusedBeforeItIsRead()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        // The deployed table, exactly as IncrementalSync.cs:185 creates it.
        await SqlServerFixture.ExecuteAsync(connectionString, """
            CREATE TABLE dbo.SyncJobTracking (
                JobIdentifier  nvarchar(255) NOT NULL PRIMARY KEY,
                LastSyncTime   datetime2     NOT NULL CONSTRAINT DF_T_LastSyncTime DEFAULT GETUTCDATE(),
                LastRowVersion varbinary(8)      NULL,
                RowsProcessed  bigint        NOT NULL CONSTRAINT DF_T_RowsProcessed DEFAULT 0,
                Success        bit           NOT NULL CONSTRAINT DF_T_Success DEFAULT 1
            );

            INSERT INTO dbo.SyncJobTracking (JobIdentifier, RowsProcessed) VALUES (N'nightly', 4200);
            """);

        var store = new SqlWatermarkStore("dbo.SyncJobTracking");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ReadAsync(connectionString, "nightly", "step", CancellationToken.None));

        // It names the table, what is missing, what the table actually is, and the fix.
        Assert.Contains("dbo.SyncJobTracking", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'JobId'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("keyed a whole job by one row", refused.Message, StringComparison.Ordinal);
        Assert.Contains(SqlWatermarkStore.DefaultTable, refused.Message, StringComparison.Ordinal);

        // And the history of the runs that came before is still there.
        Assert.Equal(4200L, Convert.ToInt64(await SqlServerFixture.ScalarAsync(
            connectionString, "SELECT RowsProcessed FROM dbo.SyncJobTracking WHERE JobIdentifier = N'nightly';")));
    }

    [LiveFact]
    public async Task AStepWithNoWatermarkYetReadsAsNothing()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlWatermarkStore();

        Assert.Null(await store.ReadAsync(connectionString, "job", "step", CancellationToken.None));

        // Reading created the table, so a first run does not have to have written first.
        Assert.Equal(0, await SqlServerFixture.CountAsync(connectionString, SqlWatermarkStore.DefaultTable));
    }

    [LiveFact]
    public async Task AWatermarkIsWrittenAndReadBack()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlWatermarkStore();

        await store.WriteAsync(connectionString, "job", "step", "2026-09-01T00:00:00Z", CancellationToken.None);

        var watermark = await store.ReadAsync(connectionString, "job", "step", CancellationToken.None);

        Assert.NotNull(watermark);
        Assert.Equal("2026-09-01T00:00:00Z", watermark.Value);

        // Nothing came before the first run, and saying so is different from saying the
        // previous value was the same as this one.
        Assert.Null(watermark.PreviousValue);
        Assert.True(watermark.UpdatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    /// <summary>
    /// Two runs, and the second one can still say where the first one started.
    /// </summary>
    [LiveFact]
    public async Task AfterTwoRunsThePreviousValueIsWhatTheFirstRunLeft()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlWatermarkStore();

        await store.WriteAsync(connectionString, "job", "step", "100", CancellationToken.None);
        await store.WriteAsync(connectionString, "job", "step", "200", CancellationToken.None);

        var watermark = await store.ReadAsync(connectionString, "job", "step", CancellationToken.None);

        Assert.NotNull(watermark);
        Assert.Equal("200", watermark.Value);
        Assert.Equal("100", watermark.PreviousValue);
    }

    [LiveFact]
    public async Task ThreeRunsKeepOnlyTheOneBefore()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlWatermarkStore();

        await store.WriteAsync(connectionString, "job", "step", "100", CancellationToken.None);
        await store.WriteAsync(connectionString, "job", "step", "200", CancellationToken.None);
        await store.WriteAsync(connectionString, "job", "step", "300", CancellationToken.None);

        var watermark = await store.ReadAsync(connectionString, "job", "step", CancellationToken.None);

        Assert.NotNull(watermark);
        Assert.Equal("300", watermark.Value);
        Assert.Equal("200", watermark.PreviousValue);
    }

    /// <summary>
    /// A run that finds nothing new writes the same watermark again. An unguarded shift
    /// would put that value into the previous slot as well, and the operator's way back
    /// would be gone - destroyed by the run that did nothing at all.
    /// </summary>
    [LiveFact]
    public async Task RewritingTheSameValueDoesNotDestroyTheValueBeforeIt()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlWatermarkStore();

        await store.WriteAsync(connectionString, "job", "step", "100", CancellationToken.None);
        await store.WriteAsync(connectionString, "job", "step", "200", CancellationToken.None);

        var before = await store.ReadAsync(connectionString, "job", "step", CancellationToken.None);

        await store.WriteAsync(connectionString, "job", "step", "200", CancellationToken.None);

        var after = await store.ReadAsync(connectionString, "job", "step", CancellationToken.None);

        Assert.NotNull(after);
        Assert.Equal("200", after.Value);
        Assert.Equal("100", after.PreviousValue);

        // UpdatedAt says when the watermark moved, so a run that did not move it does not
        // touch it either.
        Assert.Equal(before!.UpdatedAt, after.UpdatedAt);
    }

    [LiveFact]
    public async Task EveryStepOfEveryJobKeepsItsOwn()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlWatermarkStore();

        await store.WriteAsync(connectionString, "job-a", "step-1", "a1", CancellationToken.None);
        await store.WriteAsync(connectionString, "job-a", "step-2", "a2", CancellationToken.None);
        await store.WriteAsync(connectionString, "job-b", "step-1", "b1", CancellationToken.None);

        Assert.Equal("a1", (await store.ReadAsync(connectionString, "job-a", "step-1", CancellationToken.None))!.Value);
        Assert.Equal("a2", (await store.ReadAsync(connectionString, "job-a", "step-2", CancellationToken.None))!.Value);
        Assert.Equal("b1", (await store.ReadAsync(connectionString, "job-b", "step-1", CancellationToken.None))!.Value);
        Assert.Null(await store.ReadAsync(connectionString, "job-b", "step-2", CancellationToken.None));
    }

    /// <summary>
    /// A step whose plan names its own state table gets it, and it is created too - the
    /// deployed systems require the operator to have made it by hand first.
    /// </summary>
    [LiveFact]
    public async Task AStateTableNamedByThePlanIsUsedAndCreated()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlWatermarkStore("dbo.SyncHistory");

        await store.WriteAsync(connectionString, "job", "step", "100", CancellationToken.None);

        Assert.Equal(1, await SqlServerFixture.CountAsync(connectionString, "dbo.SyncHistory"));
        Assert.Equal(-1, await SqlServerFixture.CountAsync(connectionString, SqlWatermarkStore.DefaultTable));
        Assert.Equal("100", (await store.ReadAsync(connectionString, "job", "step", CancellationToken.None))!.Value);
    }
}
