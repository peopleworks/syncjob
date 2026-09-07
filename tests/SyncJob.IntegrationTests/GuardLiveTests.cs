using SyncJob.Core.Model;
using SyncJob.Core.Publication;

namespace SyncJob.IntegrationTests;

/// <summary>
/// The guard against real tables, and - the part that matters - the destination still
/// holding its rows after it refused.
/// <para>
/// The failure being tested for is a night when the source came back empty and the job
/// emptied a production table and reported success. Counting correctly is half of it;
/// the other half is that a refusal leaves everything alone.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class GuardLiveTests(SqlServerFixture fixture)
{
    [LiveFact]
    public async Task AnEmptyLoadIsRefused_AndTheDestinationStillHasItsRows()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Ledger");
        await PublicationFixture.LoadAsync(connectionString, "dbo.Ledger", rows: 2_000, firstId: 1);

        var factory = new StagingTableFactory();
        var staging = await factory.CreateAsync(connectionString, "dbo.Ledger", null, default);

        // Nothing is loaded into staging: the view broke, the filter matched nothing,
        // the linked server came back empty rather than failing.
        var verdict = await new PublicationGuardEvaluator().EvaluateAsync(
            connectionString, staging, "dbo.Ledger", new PublicationGuard { MinimumRows = 1 }, default);

        if(verdict.MayPublish)
            await new SwapPublisher().PublishAsync(connectionString, staging, Step(), default);

        Assert.False(verdict.MayPublish);
        Assert.Equal(0, verdict.StagedRows);
        Assert.Equal(2_000, verdict.DestinationRows);
        Assert.Equal(2_000, await SqlServerFixture.CountAsync(connectionString, "dbo.Ledger"));
    }

    [LiveFact]
    public async Task ACollapsedLoadIsRefusedByTheFraction_AndTheReasonNamesBothCounts()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Ledger");
        await PublicationFixture.LoadAsync(connectionString, "dbo.Ledger", rows: 2_000, firstId: 1);

        var staging = await new StagingTableFactory().CreateAsync(connectionString, "dbo.Ledger", null, default);
        await PublicationFixture.LoadAsync(connectionString, staging, rows: 400, firstId: 1);

        var verdict = await new PublicationGuardEvaluator().EvaluateAsync(
            connectionString, staging, "dbo.Ledger",
            new PublicationGuard { MinimumFractionOfDestination = 0.5 }, default);

        Assert.False(verdict.MayPublish);
        Assert.Contains("400", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("2,000", verdict.Reason, StringComparison.Ordinal);
    }

    [LiveFact]
    public async Task AGoodLoadIsAllowedThrough()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Ledger");
        await PublicationFixture.LoadAsync(connectionString, "dbo.Ledger", rows: 2_000, firstId: 1);

        var staging = await new StagingTableFactory().CreateAsync(connectionString, "dbo.Ledger", null, default);
        await PublicationFixture.LoadAsync(connectionString, staging, rows: 2_100, firstId: 1);

        var verdict = await new PublicationGuardEvaluator().EvaluateAsync(
            connectionString, staging, "dbo.Ledger",
            new PublicationGuard { MinimumRows = 1_000, MinimumFractionOfDestination = 0.9 }, default);

        Assert.True(verdict.MayPublish, verdict.Reason);
        Assert.Equal(2_100, verdict.StagedRows);
        Assert.Equal(2_000, verdict.DestinationRows);
    }

    /// <summary>
    /// A destination that is not there is a first run: the fraction has nothing to
    /// compare against, and the count says so rather than reading as zero rows.
    /// </summary>
    [LiveFact]
    public async Task ADestinationThatDoesNotExistIsAFirstRun_NotAFailure()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Ledger");

        var staging = await new StagingTableFactory().CreateAsync(connectionString, "dbo.Ledger", null, default);
        await PublicationFixture.LoadAsync(connectionString, staging, rows: 10, firstId: 1);

        var verdict = await new PublicationGuardEvaluator().EvaluateAsync(
            connectionString, staging, "dbo.NotYetThere",
            new PublicationGuard { MinimumRows = 1, MinimumFractionOfDestination = 0.9 }, default);

        Assert.True(verdict.MayPublish, verdict.Reason);
        Assert.Equal(PublicationGuardEvaluator.DestinationAbsent, verdict.DestinationRows);
    }

    private static SyncStep Step() => new()
    {
        Id = "guard-live",
        DestinationTable = "dbo.Ledger",
        Publication = new PublicationPlan { Mode = PublicationMode.Replace }
    };
}
