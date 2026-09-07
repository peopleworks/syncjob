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
