using SyncJob.Core.Import;
using SyncJob.Core.Model;
using SyncJob.Core.Run;

namespace SyncJob.IntegrationTests;

/// <summary>
/// An incremental <c>appsettings.json</c> section, imported and then actually run twice.
/// <para>
/// This is the case that WP 1.5e found could not work at all. The importer produced a
/// watermark plan on a query the operator wrote and no variable naming it, and
/// <c>SourceSql</c> refuses that combination — correctly, because a predicate cannot be
/// appended to SQL this engine did not build. The effect was that every incremental
/// section in the field stopped loading, on all three surfaces at once, with a message
/// that was accurate and unactionable.
/// </para>
/// <para>
/// Two runs, not one. A watermark that is written but never read back is indistinguishable
/// from one that works, and reading it back is the whole point.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class IncrementalSectionLiveTests(SqlServerFixture fixture)
{
    [LiveFact]
    public async Task ASectionWrittenTheDocumentedWayLoadsAndThenLoadsOnlyWhatIsNew()
    {
        var (source, destination) = await CreatePairAsync();

        // Four rows to begin with, two of them old.
        await SqlServerFixture.ExecuteAsync(source, """
            INSERT INTO dbo.Venta (IdVenta, Cliente, Total, FechaModificacion) VALUES
                (1, N'Ada',    10.00, '2024-01-01T00:00:00'),
                (2, N'Grace',  20.00, '2024-01-02T00:00:00'),
                (3, N'Edsger', 30.00, '2024-06-01T00:00:00'),
                (4, N'Barbara',40.00, '2024-06-02T00:00:00');
            """);

        var first = await RunAsync(source, destination);

        Assert.Equal(RunStatus.Succeeded, first.Status);
        Assert.Equal(4, first.RowsRead);
        Assert.Equal(4, await SqlServerFixture.CountAsync(destination, "dbo.Venta"));

        // The watermark is the newest FechaModificacion that actually travelled.
        var step = Assert.Single(first.Steps);
        Assert.Equal("2024-06-02T00:00:00", step.WatermarkValue);

        // One new row and one untouched old row.
        await SqlServerFixture.ExecuteAsync(source, """
            INSERT INTO dbo.Venta (IdVenta, Cliente, Total, FechaModificacion)
            VALUES (5, N'Katherine', 50.00, '2024-09-01T00:00:00');
            """);

        var second = await RunAsync(source, destination);

        Assert.Equal(RunStatus.Succeeded, second.Status);

        // One row read, not five: the boundary reached the operator's own query.
        Assert.Equal(1, second.RowsRead);
        Assert.Equal(5, await SqlServerFixture.CountAsync(destination, "dbo.Venta"));

        var secondStep = Assert.Single(second.Steps);
        Assert.Equal("2024-06-02T00:00:00", secondStep.PreviousWatermarkValue);
        Assert.Equal("2024-09-01T00:00:00", secondStep.WatermarkValue);
    }

    /// <summary>
    /// The engine keeps its own watermark table and leaves the old one alone, because the
    /// old one is the only record of what happened before the upgrade.
    /// </summary>
    [LiveFact]
    public async Task TheDeployedTrackingTableIsLeftAloneWithItsHistory()
    {
        var (source, destination) = await CreatePairAsync();

        await SqlServerFixture.ExecuteAsync(destination, """
            CREATE TABLE dbo.SyncJobTracking (
                JobIdentifier nvarchar(255) NOT NULL PRIMARY KEY,
                LastSyncTime  datetime2     NOT NULL,
                RowsProcessed bigint        NOT NULL
            );

            INSERT INTO dbo.SyncJobTracking VALUES (N'ventas', '2024-06-02T00:00:00', 41000);
            """);

        await SqlServerFixture.ExecuteAsync(source, """
            INSERT INTO dbo.Venta (IdVenta, Cliente, Total, FechaModificacion)
            VALUES (1, N'Ada', 10.00, '2024-01-01T00:00:00');
            """);

        var run = await RunAsync(source, destination);

        Assert.Equal(RunStatus.Succeeded, run.Status);

        // Untouched, history and all.
        Assert.Equal(41000L, Convert.ToInt64(await SqlServerFixture.ScalarAsync(
            destination, "SELECT RowsProcessed FROM dbo.SyncJobTracking WHERE JobIdentifier = N'ventas';")));

        // And the engine kept its own, keyed by step rather than by job.
        Assert.Equal(1, await SqlServerFixture.CountAsync(destination, "dbo.SyncJobWatermark"));
    }

    /// <summary>
    /// The importer says what it changed. An operator who is not told that the watermark
    /// starts again is an operator who thinks the first run reloading everything is a bug.
    /// </summary>
    [LiveFact]
    public async Task TheImportSaysThatTheWatermarkStartsAgainAndThatTheQueryWasWrapped()
    {
        var imported = await new AppSettingsJobImporter(SectionJson).ImportAsync(CancellationToken.None);

        Assert.Contains(imported.Losses, x =>
            x.Contains("dbo.SyncJobTracking", StringComparison.Ordinal) &&
            x.Contains("still holds the history", StringComparison.Ordinal));

        Assert.Contains(imported.Losses, x =>
            x.Contains("wrapped", StringComparison.Ordinal) &&
            x.Contains("linked server", StringComparison.Ordinal));
    }

    private const string SectionJson = """
        {
          "Ventas": {
            "Source": {
              "ConnectionString": "Server=.;Database=x;Integrated Security=true",
              "Query": "SELECT IdVenta, Cliente, Total, FechaModificacion FROM dbo.Venta"
            },
            "Destination": {
              "ConnectionString": "Server=.;Database=y;Integrated Security=true",
              "FinalTable": "dbo.Venta"
            },
            "ColumnMappings": [ { "Source": "IdVenta", "Dest": "IdVenta" } ],
            "Incremental": {
              "Enabled": true,
              "Mode": "Timestamp",
              "TrackingColumn": "FechaModificacion",
              "TrackingTable": "dbo.SyncJobTracking",
              "JobIdentifier": "ventas",
              "MergeStrategy": "Upsert",
              "PrimaryKeyColumns": [ "IdVenta" ]
            }
          }
        }
        """;

    private async Task<(string Source, string Destination)> CreatePairAsync()
    {
        var (source, destination) = await fixture.CreatePairAsync();

        foreach(var connectionString in new[] { source, destination })
        {
            await SqlServerFixture.ExecuteAsync(connectionString, """
                CREATE TABLE dbo.Venta (
                    IdVenta           int           NOT NULL CONSTRAINT PK_Venta PRIMARY KEY,
                    Cliente           nvarchar(60)  NOT NULL,
                    Total             decimal(12,2) NOT NULL,
                    FechaModificacion datetime      NOT NULL
                );
                """);
        }

        return (source, destination);
    }

    /// <summary>
    /// The section as the documentation tells an operator to write it, run through the
    /// importer and the engine exactly as a surface would.
    /// </summary>
    private static async Task<JobRun> RunAsync(string source, string destination)
    {
        var imported = await new AppSettingsJobImporter(SectionJson).ImportAsync(CancellationToken.None);
        var job = imported.Jobs.Single();

        job.Destination!.ConnectionString = destination;
        job.Sources[0].ConnectionString = source;

        return await new JobRunner().RunAsync(
            job,
            new JobRunOptions { TriggeredBy = "test", UseLease = false },
            CancellationToken.None);
    }
}
