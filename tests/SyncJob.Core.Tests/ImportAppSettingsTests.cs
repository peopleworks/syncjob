using SyncJob.Core.Import;
using SyncJob.Core.Model;

namespace SyncJob.Core.Tests;

/// <summary>
/// SyncJob's JSON sections, turned into jobs. The assertions that matter are the ones
/// about what did not come across: a format that quietly loses a setting is worse than
/// one that refuses it, because nobody finds out until the night it mattered.
/// </summary>
public sealed class ImportAppSettingsTests
{
    /// <summary>
    /// Modelled on src/SyncJob.Cli/appsettings.ejemplo.json and appsettings.incremental.json,
    /// with a real password in place of the asterisks so the redaction has something to do.
    /// </summary>
    private const string TwoSections = """
    {
      "ClienteSync": {
        "Source": {
          "ConnectionString": "Server=SQL_ORIGEN;Database=DBOrigen;User Id=usuario;Password=s3cret-origen;",
          "Query": "SELECT IdCliente, NombreCompleto, SaldoActual FROM dbo.VistaCliente"
        },
        "Destination": {
          "ConnectionString": "Server=SQL_DESTINO;Database=DBDestino;Integrated Security=true;",
          "StageTable": "dbo.Cliente_Stage",
          "FinalTable": "dbo.Cliente_Final"
        },
        "ColumnMappings": [
          { "Source": "IdCliente", "Dest": "IdCliente" },
          { "Source": "NombreCompleto", "Dest": "NombreCompleto" },
          { "Source": "SaldoActual", "Dest": "Saldo" }
        ],
        "Options": {
          "BatchSize": 10000,
          "MaxDegreeOfParallelism": 4,
          "BulkCopyTimeoutSeconds": 120,
          "KeepIdentity": true,
          "MinRowThresholdToCommit": 1000
        }
      },
      "VentasIncremental": {
        "Source": {
          "ConnectionString": "Server=ORIGEN;Database=DBVentas;Integrated Security=true;",
          "Query": "SELECT IdVenta, Monto, FechaModificacion FROM dbo.Ventas"
        },
        "Destination": {
          "ConnectionString": "Server=DESTINO;Database=DBAnalytics;Integrated Security=true;",
          "StageTable": "dbo.Ventas_Stage",
          "FinalTable": "dbo.Ventas_Final"
        },
        "ColumnMappings": [
          { "Source": "IdVenta", "Dest": "IdVenta" },
          { "Source": "Monto", "Dest": "Monto" },
          { "Source": "FechaModificacion", "Dest": "FechaModificacion" }
        ],
        "Options": { "BatchSize": 20000, "MaxDegreeOfParallelism": 2, "MinRowThresholdToCommit": 0 },
        "Incremental": {
          "Enabled": true,
          "Mode": "Timestamp",
          "TrackingColumn": "FechaModificacion",
          "TrackingTable": "dbo.SyncJobTracking",
          "JobIdentifier": "Ventas_SQL2008_to_SQL2022",
          "PrimaryKeyColumns": [ "IdVenta" ],
          "MergeStrategy": "Upsert"
        }
      },
      "Logging": { "LogLevel": { "Default": "Information" } }
    }
    """;

    // -------------------------------------------------------------- a job, field by field

    [Fact]
    public async Task APlainSection_BecomesOneJobWithOneStep()
    {
        var job = (await ImportAsync()).Jobs.Single(x => x.Name == "ClienteSync");

        Assert.Equal("ClienteSync", job.Id);
        Assert.True(job.IsActive);

        var step = Assert.Single(job.Steps);
        Assert.Equal("ClienteSync", step.Name);
        Assert.Equal(1d, step.Order);
        Assert.Equal("source", step.SourceEndpointId);
        Assert.Equal("SELECT IdCliente, NombreCompleto, SaldoActual FROM dbo.VistaCliente", step.Source.Sql);
        Assert.Null(step.Source.StoredProcedure);
        Assert.Equal("dbo.Cliente_Final", step.DestinationTable);
        Assert.Equal("dbo.Cliente_Stage", step.Publication.StagingTable);
        Assert.Equal(10000, step.BatchSize);
        Assert.Equal(120, step.CommandTimeoutSeconds);
        Assert.Equal(4, step.Publication.MaxDegreeOfParallelism);
        Assert.True(step.Publication.KeepIdentity);
        Assert.Equal(PublicationMode.Replace, step.Publication.Mode);
        Assert.Null(step.Incremental);
    }

    /// <summary>
    /// MinRowThresholdToCommit is the one guard SyncJob's CLI already had, and the one its
    /// Windows service never called. It is the whole reason PublicationGuard exists.
    /// </summary>
    [Fact]
    public async Task TheRowThreshold_BecomesTheGuardsFloor()
    {
        var job = (await ImportAsync()).Jobs.Single(x => x.Name == "ClienteSync");

        Assert.Equal(1000, job.Steps[0].Publication.Guard.MinimumRows);
        Assert.Equal(GuardFailureAction.Abort, job.Steps[0].Publication.Guard.OnFailure);
    }

    [Fact]
    public async Task EverySectionValidatesWithNoErrors()
    {
        var result = await ImportAsync();

        Assert.Equal(2, result.Jobs.Count);

        foreach(var job in result.Jobs)
            ImportAssertions.RunsCleanly(job);
    }

    /// <summary>An appsettings file holds logging and the rest beside the jobs.</summary>
    [Fact]
    public async Task ASectionThatIsNotAJob_IsNotImportedAndIsNotReportedAsALoss()
    {
        var result = await ImportAsync();

        Assert.DoesNotContain(result.Jobs, x => x.Name == "Logging");
        Assert.DoesNotContain(result.Losses, x => x.Contains("Logging", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------- the credentials

    [Fact]
    public async Task APasswordInAConnectionString_DoesNotReachTheModel()
    {
        var result = await ImportAsync();
        var job = result.Jobs.Single(x => x.Name == "ClienteSync");
        var source = Assert.Single(job.Sources);

        Assert.DoesNotContain("s3cret-origen", source.ConnectionString, StringComparison.Ordinal);
        Assert.Contains("SQL_ORIGEN", source.ConnectionString, StringComparison.Ordinal);
        Assert.Equal("syncjob/ClienteSync/source/password", source.SecretRef);

        Assert.Contains(
            result.Losses,
            x => x.Contains("clear text", StringComparison.Ordinal) &&
                 x.Contains("syncjob/ClienteSync/source/password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AConnectionStringWithNoPassword_IsCarriedWithNoSecretRef()
    {
        var job = (await ImportAsync()).Jobs.Single(x => x.Name == "ClienteSync");

        Assert.Null(job.Destination!.SecretRef);
        Assert.Contains("SQL_DESTINO", job.Destination.ConnectionString, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------- the column list

    /// <summary>
    /// The JSON list is both the bulk-copy mapping and the explicit destination column
    /// list. Matching by name is the model's default, so only the rename survives - and
    /// the list itself is kept rather than thrown away.
    /// </summary>
    [Fact]
    public async Task IdentityColumnMappings_AreNotImportedAsFieldMaps_ButAreKeptAndReported()
    {
        var result = await ImportAsync();
        var step = result.Jobs.Single(x => x.Name == "ClienteSync").Steps[0];

        var map = Assert.Single(step.FieldMaps);
        Assert.Equal("SaldoActual", map.Source);
        Assert.Equal("Saldo", map.Target);

        Assert.Equal(
            "IdCliente=IdCliente; NombreCompleto=NombreCompleto; SaldoActual=Saldo",
            step.Extensions["syncjob.json.columnMappings"]);

        ImportAssertions.Names(result.Losses, "2 column mapping(s) that name the same column");
    }

    // -------------------------------------------------------------------- the incremental

    [Fact]
    public async Task AnIncrementalSection_BecomesKeysAndAWatermark()
    {
        var job = (await ImportAsync()).Jobs.Single(x => x.Name == "VentasIncremental");

        // Identity follows JobIdentifier, because that is what the watermark was keyed by.
        Assert.Equal("Ventas_SQL2008_to_SQL2022", job.Id);
        Assert.Equal("VentasIncremental", job.Extensions["syncjob.json.section"]);

        var step = job.Steps[0];
        Assert.Equal(PublicationMode.Merge, step.Publication.Mode);

        Assert.Contains(step.FieldMaps, x => x.Source == "IdVenta" && x.IsUniqueKey);
        Assert.Contains(step.FieldMaps, x => x.Source == "FechaModificacion" && x.IsSyncKey);
        // The old table's name is NOT carried into StateTable. dbo.SyncJobTracking keys a
        // whole job by one row, because the engine that made it had no steps; pointing
        // this store at it gets the section refused. It is remembered, said out loud, and
        // left alone - it still holds the history of the runs before this one.
        Assert.Null(step.Incremental!.Watermark!.StateTable);
        Assert.Equal("dbo.SyncJobTracking", step.Extensions["syncjob.json.incremental.trackingTable"]);
        Assert.False(step.Incremental.ForceFullRead);

        // And the section is runnable, which is the part that matters. A watermark plan
        // on a query the operator wrote is refused by SourceSql unless the query names a
        // variable - so the import puts one there.
        Assert.Contains("${Watermark}", step.Source.Sql!, StringComparison.Ordinal);
        Assert.Equal("1900-01-01T00:00:00", step.Incremental.Watermark.InitialValue);
    }

    /// <summary>
    /// A column that is both the key and the tracking column is one map with two roles.
    /// Two maps would read as two sync keys, which the validator refuses - correctly.
    /// </summary>
    [Fact]
    public async Task AColumnThatIsBothKeyAndTracking_IsOneFieldMap()
    {
        var result = await new AppSettingsJobImporter(Section("""
            "Incremental": {
              "Enabled": true, "Mode": "RowVersion", "TrackingColumn": "RowVer",
              "PrimaryKeyColumns": [ "RowVer" ], "MergeStrategy": "Upsert"
            }
            """)).ImportAsync(CancellationToken.None);

        var step = result.Jobs.Single().Steps[0];
        var map = Assert.Single(step.FieldMaps);

        Assert.True(map.IsUniqueKey);
        Assert.True(map.IsSyncKey);
        ImportAssertions.RunsCleanly(result.Jobs[0]);
    }

    [Fact]
    public async Task MergeStrategyInsert_BecomesAppend()
    {
        var result = await new AppSettingsJobImporter(Section("""
            "Incremental": { "Enabled": true, "TrackingColumn": "FechaModificacion", "MergeStrategy": "Insert" }
            """)).ImportAsync(CancellationToken.None);

        Assert.Equal(PublicationMode.Append, result.Jobs.Single().Steps[0].Publication.Mode);
    }

    /// <summary>
    /// "Full" also meant deleting the rows the source stopped returning. A merge does not,
    /// and the difference is a table that quietly stops shrinking.
    /// </summary>
    [Fact]
    public async Task MergeStrategyFull_BecomesAMerge_AndSaysWhatItStoppedDoing()
    {
        var result = await new AppSettingsJobImporter(Section("""
            "Incremental": {
              "Enabled": true, "TrackingColumn": "FechaModificacion",
              "PrimaryKeyColumns": [ "IdVenta" ], "MergeStrategy": "Full"
            }
            """)).ImportAsync(CancellationToken.None);

        Assert.Equal(PublicationMode.Merge, result.Jobs.Single().Steps[0].Publication.Mode);

        Assert.Contains(
            result.Losses,
            x => x.Contains("MergeStrategy 'Full'", StringComparison.Ordinal) &&
                 x.Contains("tombstone ledger", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ChangeTracking")]
    [InlineData("ChangeDataCapture")]
    public async Task AChangeFeedTrackingMode_ImportsTheRestOfTheJobAndSaysWhatItCouldNotCarry(string mode)
    {
        var result = await new AppSettingsJobImporter(Section($$"""
            "Incremental": {
              "Enabled": true, "Mode": "{{mode}}", "TrackingColumn": "FechaModificacion",
              "PrimaryKeyColumns": [ "IdVenta" ], "MergeStrategy": "Upsert"
            }
            """)).ImportAsync(CancellationToken.None);

        var job = result.Jobs.Single();
        var step = job.Steps[0];

        // The rest of the job is there.
        Assert.Contains(step.FieldMaps, x => x.Source == "IdVenta" && x.IsUniqueKey);
        Assert.Equal(PublicationMode.Merge, step.Publication.Mode);

        // The tracking is not, and it says so.
        Assert.Null(step.Incremental);
        Assert.DoesNotContain(step.FieldMaps, x => x.IsSyncKey);
        Assert.Equal(mode, step.Extensions["syncjob.json.incremental.mode"]);
        Assert.Contains(result.Losses, x => x.Contains($"tracks changes with '{mode}'", StringComparison.Ordinal));

        ImportAssertions.RunsCleanly(job);
    }

    // ------------------------------------------------------- what the JSON side has alone

    /// <summary>
    /// DeleteDetection is the JSON format's own: the SQLite store has no column for any
    /// of it. Nothing in the model reads a soft-delete flag, so it is kept and named.
    /// </summary>
    [Fact]
    public async Task DeleteDetection_HasNoFieldInTheModel_AndIsKeptAndNamed()
    {
        var result = await new AppSettingsJobImporter(Section("""
            "Incremental": {
              "Enabled": true, "TrackingColumn": "FechaModificacion",
              "PrimaryKeyColumns": [ "IdVenta" ], "MergeStrategy": "Upsert",
              "DeleteDetection": { "Enabled": true, "Mode": "SoftDelete", "SoftDeleteColumn": "Estado", "SoftDeleteValue": "CANCELADO" }
            }
            """)).ImportAsync(CancellationToken.None);

        var step = result.Jobs.Single().Steps[0];

        Assert.Contains("Estado", step.Extensions["syncjob.json.deleteDetection"], StringComparison.Ordinal);
        Assert.Contains("CANCELADO", step.Extensions["syncjob.json.deleteDetection"], StringComparison.Ordinal);

        Assert.Contains(
            result.Losses,
            x => x.Contains("detects deletions with 'SoftDelete'", StringComparison.Ordinal) &&
                 x.Contains("ExecuteDeleteDetection is never called", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIncrementalBlockThatIsSwitchedOff_IsKeptRatherThanActedOn()
    {
        var result = await new AppSettingsJobImporter(Section("""
            "Incremental": { "Enabled": false, "Mode": "Timestamp", "TrackingColumn": "FechaModificacion", "MergeStrategy": "Full" }
            """)).ImportAsync(CancellationToken.None);

        var step = result.Jobs.Single().Steps[0];

        Assert.Equal(PublicationMode.Replace, step.Publication.Mode);
        Assert.Null(step.Incremental);
        Assert.Contains("Timestamp", step.Extensions["syncjob.json.incremental.disabled"], StringComparison.Ordinal);
        Assert.Contains(result.Losses, x => x.Contains("Enabled=false", StringComparison.Ordinal));
    }

    /// <summary>
    /// The commented example section in appsettings.incremental.json puts thirty-three
    /// _COMENTARIO_ keys inside Incremental. Unknown settings are kept and counted rather
    /// than dropped on the floor.
    /// </summary>
    [Fact]
    public async Task SettingsSyncJobDoesNotDefine_AreKeptAndCounted()
    {
        var result = await new AppSettingsJobImporter(Section("""
            "Incremental": { "_COMENTARIO_1": "how to use this", "_COMENTARIO_2": "and this" }
            """)).ImportAsync(CancellationToken.None);

        var step = result.Jobs.Single().Steps[0];

        Assert.Equal("_COMENTARIO_1, _COMENTARIO_2", step.Extensions["syncjob.json.incremental.unknown"]);
        ImportAssertions.Names(result.Losses, "2 setting(s) SyncJob does not define");
    }

    /// <summary>
    /// The CLI's probe runs the procedure (Program.cs:1103) and its copy runs the query
    /// (Program.cs:1462), so a section with both was measured against one and loaded from
    /// the other. Neither is chosen here; the job stops at validation instead.
    /// </summary>
    [Fact]
    public async Task ASectionWithBothAQueryAndAProcedure_IsReported_AndRefusedByTheValidator()
    {
        var result = await new AppSettingsJobImporter("""
            {
              "Ambos": {
                "Source": {
                  "ConnectionString": "Server=O;Database=D;Integrated Security=true;",
                  "Query": "SELECT 1",
                  "StoredProcedure": "dbo.SP_Clientes"
                },
                "Destination": { "ConnectionString": "Server=D;Database=D;Integrated Security=true;", "FinalTable": "dbo.T" },
                "Options": { "MinRowThresholdToCommit": 1 }
              }
            }
            """).ImportAsync(CancellationToken.None);

        var job = result.Jobs.Single();

        Assert.Equal("SELECT 1", job.Steps[0].Source.Sql);
        Assert.Equal("dbo.SP_Clientes", job.Steps[0].Source.StoredProcedure);

        Assert.Contains(result.Losses, x => x.Contains("pre-flight probe", StringComparison.Ordinal));
        Assert.Contains(
            JobValidator.Validate(job),
            x => x.Message.Contains("more than one source", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStoredProcedureSection_CarriesItsParameters()
    {
        var result = await new AppSettingsJobImporter("""
            {
              "ClienteSP": {
                "Source": {
                  "ConnectionString": "Server=O;Database=D;Integrated Security=true;",
                  "StoredProcedure": "dbo.SP_ObtenerClientes",
                  "Parameters": { "FechaDesde": "2025-01-01", "Top": "1000" }
                },
                "Destination": { "ConnectionString": "Server=D;Database=D;Integrated Security=true;", "FinalTable": "dbo.Cliente_Final" },
                "Options": { "MinRowThresholdToCommit": 1 }
              }
            }
            """).ImportAsync(CancellationToken.None);

        var step = result.Jobs.Single().Steps[0];

        Assert.Equal("dbo.SP_ObtenerClientes", step.Source.StoredProcedure);
        Assert.Equal("2025-01-01", step.Source.Parameters["FechaDesde"]);
        Assert.Equal("1000", step.Source.Parameters["Top"]);
    }

    // ---------------------------------------------------------------------- the builders

    private static Task<ImportResult> ImportAsync() =>
        new AppSettingsJobImporter(TwoSections, "appsettings.incremental.json")
            .ImportAsync(CancellationToken.None);

    /// <summary>One section with the given extra block, for the one-thing-at-a-time tests.</summary>
    private static string Section(string extra) => $$"""
    {
      "VentasIncremental": {
        "Source": {
          "ConnectionString": "Server=ORIGEN;Database=DBVentas;Integrated Security=true;",
          "Query": "SELECT IdVenta, Monto, FechaModificacion, RowVer FROM dbo.Ventas"
        },
        "Destination": {
          "ConnectionString": "Server=DESTINO;Database=DBAnalytics;Integrated Security=true;",
          "StageTable": "dbo.Ventas_Stage",
          "FinalTable": "dbo.Ventas_Final"
        },
        "Options": { "BatchSize": 20000, "MinRowThresholdToCommit": 1 },
        {{extra}}
      }
    }
    """;
}
