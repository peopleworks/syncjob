using SyncJob.Core.Import;
using SyncJob.Core.Model;

namespace SyncJob.Core.Tests;

/// <summary>
/// SyncJob's SQLite store, turned into jobs.
/// <para>
/// The importer never opens the file - the Core may not reference Microsoft.Data.Sqlite
/// and <see cref="CorePurityTests"/> fails if it ever does - so these tests build the
/// rows the way the CLI would hand them over. That is the whole shape of the API: rows
/// in, jobs out, no connection anywhere.
/// </para>
/// </summary>
public sealed class ImportSqliteTests
{
    // --------------------------------------------------------------- a job, field by field

    [Fact]
    public async Task AConfigurationRow_BecomesOneJobWithOneStep()
    {
        var job = (await ImportAsync()).Jobs.Single();

        Assert.Equal("ventas-diarias", job.Id);
        Assert.Equal("Ventas diarias", job.Name);
        Assert.Equal("Carga nocturna de ventas", job.Description);
        Assert.True(job.IsActive);
        Assert.Equal(["nightly", "ventas"], job.Tags);
        Assert.Equal("pedro", job.Extensions["syncjob.sqlite.createdBy"]);

        var step = Assert.Single(job.Steps);
        Assert.Equal("Ventas diarias", step.Name);
        Assert.Equal("SELECT IdVenta, ClienteId, Monto, Nombre, Estado, FechaModificacion FROM dbo.Ventas", step.Source.Sql);
        Assert.Equal("dbo.Ventas_Final", step.DestinationTable);
        Assert.Equal("dbo.Ventas_Stage", step.Publication.StagingTable);
        Assert.Equal(10000, step.BatchSize);
        Assert.Equal(60, step.CommandTimeoutSeconds);
        Assert.Equal(4, step.Publication.MaxDegreeOfParallelism);
        Assert.Equal(500, step.Publication.Guard.MinimumRows);
        Assert.True(step.Publication.KeepIdentity);
        Assert.Equal(PublicationMode.Merge, step.Publication.Mode);
    }

    [Fact]
    public async Task TheImportedJobRunsCleanly()
    {
        ImportAssertions.RunsCleanly((await ImportAsync()).Jobs.Single());
    }

    [Fact]
    public async Task TheConnectionRows_BecomeEndpointsWithNoCredentialsInThem()
    {
        var result = await ImportAsync();
        var job = result.Jobs.Single();

        var source = Assert.Single(job.Sources);
        Assert.Equal("source", source.Id);
        Assert.Equal("Origen ventas", source.Name);
        Assert.Contains("ORIGEN", source.ConnectionString, StringComparison.Ordinal);
        Assert.Contains("DBVentas", source.ConnectionString, StringComparison.Ordinal);
        Assert.Contains("usuario", source.ConnectionString, StringComparison.Ordinal);
        Assert.Equal("origen", source.Extensions["syncjob.sqlite.connectionId"]);

        // The blob itself is never handed to the Core, only the fact of it - the endpoint
        // gets a name to resolve, not a secret to hold.
        Assert.Equal("syncjob/ventas-diarias/source/password", source.SecretRef);
        ImportAssertions.Names(result.Losses, "DPAPI blob", "syncjob/ventas-diarias/source/password");

        Assert.Contains("Integrated Security=True", job.Destination!.ConnectionString, StringComparison.Ordinal);
        Assert.Null(job.Destination.SecretRef);
    }

    // -------------------------------------------------------------------- the field maps

    [Fact]
    public async Task OnlyTheMappingsThatDepartFromMatchingByName_BecomeFieldMaps()
    {
        var result = await ImportAsync();
        var step = result.Jobs.Single().Steps[0];

        // Monto maps to itself and says nothing else, so it is not a field map.
        Assert.DoesNotContain(step.FieldMaps, x => x.Source == "Monto");

        var key = Assert.Single(step.FieldMaps, x => x.Source == "IdVenta");
        Assert.True(key.IsUniqueKey);
        Assert.Null(key.Target);

        var renamed = Assert.Single(step.FieldMaps, x => x.Source == "ClienteId");
        Assert.Equal("IdCliente", renamed.Target);

        var expression = Assert.Single(step.FieldMaps, x => x.Source == "Estado");
        Assert.Equal("UPPER(Estado)", expression.Expression);
        Assert.Equal("Upper", expression.Extensions["syncjob.sqlite.transformType"]);

        ImportAssertions.Names(result.Losses, "column mapping(s) that name the same column");
    }

    /// <summary>
    /// The per-column transform is the SQLite format's own: the JSON sections have no
    /// field for it at all, and neither does the model, which carries an expression and
    /// nothing else. A named transform with no expression cannot be reconstructed.
    /// </summary>
    [Fact]
    public async Task ATransformTypeWithNoExpression_CannotBeApplied_AndSaysSo()
    {
        var result = await ImportAsync();
        var step = result.Jobs.Single().Steps[0];

        var map = Assert.Single(step.FieldMaps, x => x.Source == "Nombre");
        Assert.Null(map.Expression);
        Assert.Equal("Trim", map.Extensions["syncjob.sqlite.transformType"]);

        ImportAssertions.Names(result.Losses, "Nombre (Trim)", "no field for a named transform");
    }

    // ---------------------------------------------------------------------- the watermark

    /// <summary>
    /// Where the watermark lives is the clearest divergence between the two SyncJob
    /// formats: the JSON sections name a TrackingTable and this store has no column for
    /// one, so the value has to be carried across by hand.
    /// </summary>
    [Fact]
    public async Task TheWatermarkHasNoStateTable_BecauseThisFormatHasNoColumnForOne()
    {
        var result = await ImportAsync();
        var step = result.Jobs.Single().Steps[0];

        Assert.True(Assert.Single(step.FieldMaps, x => x.Source == "FechaModificacion").IsSyncKey);
        Assert.NotNull(step.Incremental!.Watermark);
        Assert.Null(step.Incremental.Watermark!.StateTable);

        ImportAssertions.Names(result.Losses, "Incremental.TrackingTable", "dbo.SyncJobTracking");
    }

    /// <summary>
    /// The snapshot mode keeps a row hash per key in syncjob.db itself. There is nothing
    /// in this model that reads it, and pretending otherwise would mean a job that claims
    /// to be incremental and reads everything.
    /// </summary>
    [Fact]
    public async Task SnapshotTracking_HasNoEquivalent_AndTheRestOfTheJobStillImports()
    {
        var result = await ImportAsync(Configuration(trackingMode: "Snapshot"));
        var step = result.Jobs.Single().Steps[0];

        Assert.Null(step.Incremental);
        Assert.DoesNotContain(step.FieldMaps, x => x.IsSyncKey);
        Assert.Equal("Snapshot", step.Extensions["syncjob.sqlite.trackingMode"]);

        ImportAssertions.Names(result.Losses, "tracks changes by snapshot", "row hash");
        ImportAssertions.RunsCleanly(result.Jobs[0]);
    }

    [Theory]
    [InlineData("ChangeTracking")]
    [InlineData("ChangeDataCapture")]
    public async Task AChangeFeedTrackingMode_HasNoEquivalentEither(string mode)
    {
        var result = await ImportAsync(Configuration(trackingMode: mode));

        Assert.Null(result.Jobs.Single().Steps[0].Incremental);
        ImportAssertions.Names(result.Losses, $"tracks changes with '{mode}'");
    }

    // -------------------------------------------------------------------- the publication

    /// <summary>
    /// The strategy was never executed: IncrementalSyncEngine.ExecuteMerge is written,
    /// documented and called from nowhere, so every run replaced the destination. Acting
    /// on the configuration is the right thing to do and is still a change in behaviour.
    /// </summary>
    [Fact]
    public async Task AConfiguredMerge_IsHonoured_AndTheChangeInBehaviourIsNamed()
    {
        var result = await ImportAsync();

        Assert.Equal(PublicationMode.Merge, result.Jobs.Single().Steps[0].Publication.Mode);
        ImportAssertions.Names(result.Losses, "ExecuteMerge is never called");
    }

    [Fact]
    public async Task MergeStrategyInsert_BecomesAppend()
    {
        var result = await ImportAsync(Configuration(mergeStrategy: "Insert"));

        Assert.Equal(PublicationMode.Append, result.Jobs.Single().Steps[0].Publication.Mode);
    }

    [Fact]
    public async Task MergeStrategyFull_BecomesAMerge_AndSaysWhatItStoppedDoing()
    {
        var result = await ImportAsync(Configuration(mergeStrategy: "Full"));

        Assert.Equal(PublicationMode.Merge, result.Jobs.Single().Steps[0].Publication.Mode);
        ImportAssertions.Names(result.Losses, "MergeStrategy 'Full'", "tombstone ledger");
    }

    // ------------------------------------------------------------------- what goes wrong

    [Fact]
    public async Task AConnectionRowTheCallerCouldNotFind_IsReportedRatherThanAssumed()
    {
        var result = await new SqliteJobImporter([
            new SqliteJobRows { Configuration = Configuration(), DestinationConnection = Destination() }
        ]).ImportAsync(CancellationToken.None);

        Assert.Empty(result.Jobs.Single().Sources[0].ConnectionString);
        ImportAssertions.Names(
            result.Losses, "names the connection 'origen'", "no such row was handed to the importer");
    }

    [Fact]
    public async Task ATagsColumnThatIsNotJson_IsKeptVerbatim()
    {
        var result = await ImportAsync(Configuration(tags: "nightly, ventas"));

        Assert.Empty(result.Jobs.Single().Tags);
        Assert.Equal("nightly, ventas", result.Jobs[0].Extensions["syncjob.sqlite.tags"]);
        ImportAssertions.Names(result.Losses, "not a JSON array of strings");
    }

    [Fact]
    public async Task AResolvedConnectionStringWithAPassword_IsStrippedOnTheWayThrough()
    {
        var result = await new SqliteJobImporter([
            new SqliteJobRows
            {
                Configuration = Configuration(),
                SourceConnection = new SqliteConnectionRow
                {
                    ConnectionId = "origen",
                    ConnectionString = "Server=ORIGEN;Database=DBVentas;User Id=usuario;Password=n0-deberia-viajar;"
                },
                DestinationConnection = Destination()
            }
        ]).ImportAsync(CancellationToken.None);

        var source = result.Jobs.Single().Sources[0];

        Assert.DoesNotContain("n0-deberia-viajar", source.ConnectionString, StringComparison.Ordinal);
        Assert.Equal("syncjob/ventas-diarias/source/password", source.SecretRef);
        ImportAssertions.Names(result.Losses, "clear text");
    }

    // ---------------------------------------------------------------------- the builders

    private static Task<ImportResult> ImportAsync(SqliteConfigurationRow? configuration = null) =>
        new SqliteJobImporter([
            new SqliteJobRows
            {
                Configuration = configuration ?? Configuration(),
                SourceConnection = Source(),
                DestinationConnection = Destination(),
                ColumnMappings = Mappings()
            }
        ], "syncjob.db").ImportAsync(CancellationToken.None);

    private static SqliteConfigurationRow Configuration(
        string trackingMode = "Timestamp",
        string mergeStrategy = "Upsert",
        string tags = """["nightly","ventas"]""") => new()
    {
        ConfigId = "ventas-diarias",
        DisplayName = "Ventas diarias",
        Description = "Carga nocturna de ventas",
        SourceConnectionId = "origen",
        SourceQuery = "SELECT IdVenta, ClienteId, Monto, Nombre, Estado, FechaModificacion FROM dbo.Ventas",
        DestConnectionId = "central",
        DestStageTable = "dbo.Ventas_Stage",
        DestFinalTable = "dbo.Ventas_Final",
        BatchSize = 10000,
        MaxDOP = 4,
        BulkCopyTimeout = 60,
        KeepIdentity = true,
        MinRowThreshold = 500,
        IsActive = true,
        TrackingMode = trackingMode,
        TrackingColumn = "FechaModificacion",
        MergeStrategy = mergeStrategy,
        CreatedBy = "pedro",
        Tags = tags
    };

    private static SqliteConnectionRow Source() => new()
    {
        ConnectionId = "origen",
        DisplayName = "Origen ventas",
        ServerName = "ORIGEN",
        DatabaseName = "DBVentas",
        Username = "usuario",
        HasStoredPassword = true,
        TrustServerCertificate = true,
        Encrypt = false
    };

    private static SqliteConnectionRow Destination() => new()
    {
        ConnectionId = "central",
        DisplayName = "Central",
        ServerName = "DESTINO",
        DatabaseName = "DBAnalytics"
    };

    private static List<SqliteColumnMappingRow> Mappings() =>
    [
        new() { MappingId = 1, Ordinal = 0, SourceColumn = "IdVenta", DestColumn = "IdVenta", IsPrimaryKey = true },
        new() { MappingId = 2, Ordinal = 1, SourceColumn = "ClienteId", DestColumn = "IdCliente" },
        new() { MappingId = 3, Ordinal = 2, SourceColumn = "Monto", DestColumn = "Monto" },
        new() { MappingId = 4, Ordinal = 3, SourceColumn = "Nombre", DestColumn = "Nombre", TransformType = "Trim" },
        new()
        {
            MappingId = 5,
            Ordinal = 4,
            SourceColumn = "Estado",
            DestColumn = "Estado",
            TransformType = "Upper",
            TransformExpression = "UPPER(Estado)"
        },
        new() { MappingId = 6, Ordinal = 5, SourceColumn = "FechaModificacion", DestColumn = "FechaModificacion" }
    ];
}
