using SyncJob.Core.Model;
using SyncJob.Core.Run;

namespace SyncJob.IntegrationTests;

/// <summary>
/// A provenance stamp and an excluded column, through the whole engine.
/// <para>
/// Both were built into the publishers and neither could be reached. Staging is cloned
/// from the destination, so it carries the stamp column and every excluded column; the
/// copy then required the source to fill every destination column it found, and refused
/// the step before a publisher ever saw it. So a step with a stamp failed, a step with
/// an exclusion failed, and the features looked finished from inside their own unit
/// tests. WP 1.5d found it by trying to run one.
/// </para>
/// <para>
/// These tests exist at this level because that is the level the bug lived at: every
/// piece was right on its own.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ProvenanceLiveTests(SqlServerFixture fixture)
{
    [LiveFact]
    public async Task AStepWithAProvenanceStampRunsAndEveryPublishedRowCarriesIt()
    {
        var (source, destination) = await CreatePairAsync();

        var step = Step();
        step.Provenance = new ProvenanceStamp
        {
            Column = "Origin",
            Kind = ProvenanceValueKind.Text,
            Value = "plant-1"
        };

        var run = await RunAsync(source, destination, step);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(4, await SqlServerFixture.CountAsync(destination, "dbo.Part"));

        // Every row, not some: a stamp that reaches only the rows a MERGE happened to
        // insert is worse than no stamp, because it reads as though the others came from
        // somewhere else.
        Assert.Equal(4, Convert.ToInt32(await SqlServerFixture.ScalarAsync(
            destination, "SELECT COUNT(*) FROM dbo.Part WHERE Origin = N'plant-1';")));
    }

    /// <summary>
    /// The stamp is a value, and a value that looks like SQL is still a value.
    /// </summary>
    [LiveFact]
    public async Task ATextStampIsAValueAndNotSql()
    {
        var (source, destination) = await CreatePairAsync();

        var step = Step();
        step.Provenance = new ProvenanceStamp
        {
            Column = "Origin",
            Kind = ProvenanceValueKind.Text,
            Value = "plant '; DROP TABLE dbo.Part; --"
        };

        var run = await RunAsync(source, destination, step);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(4, await SqlServerFixture.CountAsync(destination, "dbo.Part"));
        Assert.Equal("plant '; DROP TABLE dbo.Part; --", await SqlServerFixture.ScalarAsync(
            destination, "SELECT TOP (1) Origin FROM dbo.Part;"));
    }

    /// <summary>
    /// An excluded column is one the destination keeps for itself. The copy has to leave
    /// it alone rather than demand the source fill it.
    /// </summary>
    [LiveFact]
    public async Task AnExcludedColumnDoesNotTravelAndTheDestinationKeepsItsDefault()
    {
        var (source, destination) = await CreatePairAsync(sourceCarriesOrigin: true);

        // The source has the column and a value in it; the step says it does not travel.
        await SqlServerFixture.ExecuteAsync(source, "UPDATE dbo.Part SET Origin = N'from-the-source';");

        var step = Step();
        step.FieldMaps.Add(new FieldMap { Source = "Origin", Target = "Origin", IsExcluded = true });

        var run = await RunAsync(source, destination, step);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(4, await SqlServerFixture.CountAsync(destination, "dbo.Part"));

        // The destination's own default, not the source's value.
        Assert.Equal(4, Convert.ToInt32(await SqlServerFixture.ScalarAsync(
            destination, "SELECT COUNT(*) FROM dbo.Part WHERE Origin = N'unknown';")));
    }

    /// <summary>
    /// Excluding a column and also mapping something onto it says two opposite things,
    /// and the engine refuses rather than picking one.
    /// </summary>
    [LiveFact]
    public async Task AColumnThatIsBothExcludedAndMappedOntoIsRefused()
    {
        var (source, destination) = await CreatePairAsync(sourceCarriesOrigin: true);

        var step = Step();
        step.FieldMaps.Add(new FieldMap { Source = "Origin", Target = "Origin", IsExcluded = true });
        step.FieldMaps.Add(new FieldMap { Source = "Descr", Target = "Origin" });

        var run = await RunAsync(source, destination, step);

        Assert.Equal(RunStatus.Failed, run.Status);

        var result = Assert.Single(run.Steps);
        Assert.Contains("cannot both be what was meant", result.Message!, StringComparison.Ordinal);

        // And nothing was written on the way to deciding that.
        Assert.Equal(0, await SqlServerFixture.CountAsync(destination, "dbo.Part"));
    }

    /// <summary>
    /// The destination always has the <c>Origin</c> column; the source has it only when
    /// the test is about excluding it.
    /// <para>
    /// That asymmetry is the point of a provenance stamp and it is easy to lose. A stamp
    /// records where a row came from, so the source is exactly the side that does not
    /// carry it - and a test whose source happens to have the column proves nothing,
    /// because then the source fills it and the publisher merely overwrites it. Writing
    /// it the easy way first is how this test passed against the unfixed engine.
    /// </para>
    /// </summary>
    private async Task<(string Source, string Destination)> CreatePairAsync(bool sourceCarriesOrigin = false)
    {
        var (source, destination) = await fixture.CreatePairAsync();

        var origin = "Origin nvarchar(60) NOT NULL CONSTRAINT DF_Part_Origin DEFAULT (N'unknown')";

        await SqlServerFixture.ExecuteAsync(source, $"""
            CREATE TABLE dbo.Part (
                Code   nvarchar(20)  NOT NULL CONSTRAINT PK_Part PRIMARY KEY,
                Descr  nvarchar(100) NOT NULL,
                Qty    int           NOT NULL
                {(sourceCarriesOrigin ? ", " + origin.Replace("DF_Part_Origin", "DF_Src_Origin") : string.Empty)}
            );

            INSERT INTO dbo.Part (Code, Descr, Qty) VALUES
                (N'A', N'alpha', 1), (N'B', N'beta', 2), (N'C', N'gamma', 3), (N'D', N'delta', 4);
            """);

        await SqlServerFixture.ExecuteAsync(destination, $"""
            CREATE TABLE dbo.Part (
                Code   nvarchar(20)  NOT NULL CONSTRAINT PK_Part PRIMARY KEY,
                Descr  nvarchar(100) NOT NULL,
                Qty    int           NOT NULL,
                {origin}
            );
            """);

        return (source, destination);
    }

    private static SyncStep Step() => new()
    {
        Id = "parts",
        Name = "parts",
        Order = 1,
        SourceEndpointId = "src",
        Source = new SourceQuery { Table = "dbo.Part" },
        DestinationTable = "dbo.Part",
        CommandTimeoutSeconds = 60,
        Publication = new PublicationPlan
        {
            Mode = PublicationMode.Replace,
            KeepIdentity = false,
            Guard = new PublicationGuard { MinimumRows = 1 }
        }
    };

    private static Task<JobRun> RunAsync(string source, string destination, SyncStep step) =>
        new JobRunner().RunAsync(
            new SyncJobDefinition
            {
                Id = "job",
                Name = "job",
                Destination = new Endpoint { Id = "dst", Name = "dst", ConnectionString = destination },
                Sources = { new Endpoint { Id = "src", Name = "src", ConnectionString = source } },
                Steps = { step }
            },
            new JobRunOptions { TriggeredBy = "test", UseLease = false },
            CancellationToken.None);
}
