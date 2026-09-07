using System.Text.Json;
using SyncJob.Core.Import;
using SyncJob.Core.Model;

namespace SyncJob.Core.Tests;

/// <summary>
/// DataSync's XAF catalog, turned into jobs. The rows here are modelled on the deployed
/// process this engine replaces - "Sincronizar Factory1", loading Tparadas into
/// T_PRFM_Stops_ByDay through a linked server.
/// </summary>
public sealed class ImportDataSyncTests
{
    private const string ClearTextPassword = "Sup3rSecret!";

    // --------------------------------------------------------------- a job, field by field

    [Fact]
    public async Task AProcess_BecomesAJobKeyedByItsSurrogate_NotByItsName()
    {
        var job = (await ImportAsync()).Jobs.Single();

        // The deployed procedure looks a process up by Description, which is also what an
        // operator renames. Identity follows SyncProcessID so a rename keeps its history.
        Assert.Equal("datasync-process-2", job.Id);
        Assert.Equal("Sincronizar Factory1", job.Name);
        Assert.Equal("2", job.Extensions["datasync.syncProcessId"]);
        Assert.Equal("Sincronizar Factory1", job.Extensions["datasync.description"]);
        Assert.True(job.IsActive);
    }

    [Fact]
    public async Task AStep_CarriesItsOrderItsSqlAndItsBatchSize()
    {
        var step = (await ImportAsync()).Jobs.Single().Steps.Single();

        Assert.Equal("datasync-step-10", step.Id);
        Assert.Equal("T_PRFM_Stops_ByDay", step.Name);

        // A float on purpose: a step can be dropped between two others.
        Assert.Equal(1.5d, step.Order);

        Assert.Equal("server-2", step.SourceEndpointId);
        Assert.Contains("OPENQUERY", step.Source.Sql!, StringComparison.Ordinal);
        Assert.Equal("T_PRFM_Stops_ByDay", step.DestinationTable);
        Assert.Equal(5000, step.BatchSize);
        Assert.Equal(PublicationMode.Merge, step.Publication.Mode);
        Assert.Equal(VariableSyntax.Legacy, step.VariableSyntax);

        // SourceTable is informational - the procedure reads CustomSQL and never it.
        Assert.Equal("dbo.Tparadas", step.Extensions["datasync.sourceTable"]);
    }

    [Fact]
    public async Task TheImportedJobRunsCleanly()
    {
        ImportAssertions.RunsCleanly((await ImportAsync()).Jobs.Single());
    }

    // ------------------------------------------------------------------- the field maps

    /// <summary>
    /// The one thing this importer must not do. DataSync's engine builds its column list
    /// from INFORMATION_SCHEMA at run time and the only queries that read
    /// SyncStepFieldMap select SourceField where UniqueKey, SyncKey or DeleteKey is set -
    /// so a map row that carries no role says nothing, and a row per column would turn
    /// the model's default into forty rows of noise that then have to be maintained.
    /// </summary>
    [Fact]
    public async Task OnlyTheKeyRoleMapsAreImported_NeverOnePerColumn()
    {
        var step = (await ImportAsync()).Jobs.Single().Steps.Single();

        Assert.Equal(3, step.FieldMaps.Count);
        Assert.Equal(
            ["PPL", "YearWeekDayShiftLinecode", "LastSyncValue"],
            step.FieldMaps.Select(x => x.Source));

        Assert.DoesNotContain(step.FieldMaps, x => x.Source is "Stops" or "Duration" or "Comment");
        Assert.Equal("3", step.Extensions["datasync.fieldMapRowsWithNoRole"]);
    }

    [Fact]
    public async Task TheKeyRolesComeAcrossAsTheyWereSet()
    {
        var step = (await ImportAsync()).Jobs.Single().Steps.Single();

        var unique = Assert.Single(step.FieldMaps, x => x.Source == "PPL");
        Assert.True(unique.IsUniqueKey);
        Assert.False(unique.IsSyncKey);
        Assert.Null(unique.Target);

        var both = Assert.Single(step.FieldMaps, x => x.Source == "YearWeekDayShiftLinecode");
        Assert.True(both.IsUniqueKey);
        Assert.True(both.IsDeleteKey);

        Assert.True(Assert.Single(step.FieldMaps, x => x.Source == "LastSyncValue").IsSyncKey);
    }

    /// <summary>
    /// A rename is a departure from matching by name, so it comes across - but DataSync
    /// never applied it, and an operator has to know that before the first run writes to
    /// a column it has never written to.
    /// </summary>
    [Fact]
    public async Task ARenameIsKept_AndTheImportSaysItWasNeverApplied()
    {
        var maps = FieldMaps();
        maps.Add(new DataSyncFieldMapRow
        {
            SyncStepFieldMapID = 7,
            SyncStepID = 10,
            SourceField = "Comentario",
            TargetField = "Comment"
        });

        var result = await ImportAsync(fieldMaps: maps);
        var map = Assert.Single(result.Jobs.Single().Steps[0].FieldMaps, x => x.Source == "Comentario");

        Assert.Equal("Comment", map.Target);
        Assert.False(map.IsUniqueKey);

        ImportAssertions.Names(result.Losses, "Comentario to Comment", "TargetField is read by nothing");
    }

    // ------------------------------------------------------------------- the clear text

    /// <summary>
    /// RemoteServerConfig.UserPassword is a plain nvarchar column and its value is handed
    /// to dbo.ManageLinkedServer on every save. It does not come across at any strength.
    /// </summary>
    [Fact]
    public async Task AClearTextPassword_NeverReachesTheModel()
    {
        var result = await ImportAsync();
        var job = result.Jobs.Single();

        // Not in a connection string, not in an extension, not anywhere in the object.
        Assert.DoesNotContain(ClearTextPassword, JsonSerializer.Serialize(job), StringComparison.Ordinal);

        var source = Assert.Single(job.Sources);
        Assert.Equal("syncjob/datasync-process-2/server-2/password", source.SecretRef);

        ImportAssertions.Names(
            result.Losses,
            "clear text",
            "RemoteServerConfig.UserPassword",
            "syncjob/datasync-process-2/server-2/password");
    }

    [Fact]
    public async Task TheEndpointStillCarriesEverythingThatIsNotTheSecret()
    {
        var source = (await ImportAsync()).Jobs.Single().Sources.Single();

        Assert.Equal("Factory1", source.Name);
        Assert.Contains("FACTORY1-SQL", source.ConnectionString, StringComparison.Ordinal);
        Assert.Contains("Paradas", source.ConnectionString, StringComparison.Ordinal);
        Assert.Equal("dbo.SmallLookup", source.SpeedProbeTable);
        Assert.Equal("Factory1_LinkedServer", source.Extensions["datasync.linkedServerName"]);
    }

    // --------------------------------------------------------------- the deferred deletion

    [Fact]
    public async Task ASoftDeletedProcess_IsSkippedAndCounted()
    {
        var result = await ImportAsync();

        Assert.Single(result.Jobs);
        Assert.DoesNotContain(result.Jobs, x => x.Name == "Proceso borrado");

        ImportAssertions.Names(result.Losses, "1 process(es)", "GCRecord is not null");
    }

    [Fact]
    public async Task ASoftDeletedFieldMap_IsNotAKeyAnyMore()
    {
        var maps = FieldMaps();
        maps.Add(new DataSyncFieldMapRow
        {
            SyncStepFieldMapID = 8,
            SyncStepID = 10,
            SourceField = "OldKey",
            UniqueKey = true,
            GCRecord = 4
        });

        var step = (await ImportAsync(fieldMaps: maps)).Jobs.Single().Steps[0];

        Assert.DoesNotContain(step.FieldMaps, x => x.Source == "OldKey");
    }

    // ---------------------------------------------------------------------- the tombstones

    [Fact]
    public async Task TheDeleteTableBecomesALedgerInTheShapeThatIsDeployed()
    {
        var result = await ImportAsync();
        var ledger = result.Jobs.Single().Steps[0].Incremental!.Tombstones;

        Assert.NotNull(ledger);
        Assert.Equal("dbo.DeletedRecords", ledger!.Table);
        Assert.Equal(TombstoneKeyFormat.Legacy, ledger.KeyFormat);
        Assert.Equal(";", ledger.LegacySeparator);
        Assert.True(ledger.MarkProcessed);

        ImportAssertions.Names(result.Losses, "TableName = 'T_PRFM_Stops_ByDay'", "Processed = 0");
    }

    /// <summary>
    /// PARSENAME counts from the right and stops at five, so any key with fewer than five
    /// parts left the leading slots null and the join matched nothing - no deletion, no
    /// error. Reading it correctly means a backlog that never applied starts applying.
    /// </summary>
    [Fact]
    public async Task ALedgerKeyWithFewerThanFiveParts_NeverDeletedAnything_AndTheImportSaysSo()
    {
        var result = await ImportAsync();

        ImportAssertions.Names(result.Losses, "PARSENAME", "1 delete key column(s)", "backlog");
    }

    [Fact]
    public async Task ADeleteTableWithNoDeleteKey_ImportsNoLedgerAndSaysWhy()
    {
        var maps = FieldMaps();
        maps.RemoveAll(x => x.DeleteKey);
        maps.Add(new DataSyncFieldMapRow
        {
            SyncStepFieldMapID = 2,
            SyncStepID = 10,
            SourceField = "YearWeekDayShiftLinecode",
            TargetField = "YearWeekDayShiftLinecode",
            UniqueKey = true
        });

        var result = await ImportAsync(fieldMaps: maps);
        var step = result.Jobs.Single().Steps[0];

        Assert.Null(step.Incremental?.Tombstones);
        Assert.Equal("dbo.DeletedRecords", step.Extensions["datasync.sourceDeleteTable"]);

        ImportAssertions.Names(result.Losses, "no field map carries the DeleteKey role", "deleted nothing");
        ImportAssertions.RunsCleanly(result.Jobs[0]);
    }

    // ---------------------------------------------------------------------- the provenance

    /// <summary>
    /// The enum is stored as an integer and this model numbers the same three kinds
    /// differently, so the mapping is written out rather than cast: DataSync's Function=0
    /// is this model's Expression=2, and its String=2 is this model's Text=0. A cast would
    /// turn a stamp of GETDATE() into the literal text "GETDATE()".
    /// </summary>
    [Theory]
    [InlineData(0, ProvenanceValueKind.Expression)]
    [InlineData(1, ProvenanceValueKind.Number)]
    [InlineData(2, ProvenanceValueKind.Text)]
    public async Task TheStoredIntegerMapsToTheKindItMeant_NotToTheSameNumber(int stored, ProvenanceValueKind expected)
    {
        var result = await ImportAsync(step: Step(fieldUpdateType: stored));

        Assert.Equal(expected, result.Jobs.Single().Steps[0].Provenance!.Kind);
    }

    [Fact]
    public async Task TheStamp_IsImported_AndTheImportSaysItProbablyNeverApplied()
    {
        var result = await ImportAsync();
        var stamp = result.Jobs.Single().Steps[0].Provenance;

        Assert.NotNull(stamp);
        Assert.Equal("PlantCode", stamp!.Column);
        Assert.Equal("F1", stamp.Value);
        Assert.Equal(ProvenanceValueKind.Text, stamp.Kind);

        ImportAssertions.Names(result.Losses, "stamps 'PlantCode'", "no branch matches", "staging copy");
    }

    // ----------------------------------------------------------------------- the variables

    [Fact]
    public async Task AVariableComesAcrossWithoutItsAt_AndReadsTheDestination()
    {
        var variable = Assert.Single((await ImportAsync()).Jobs.Single().Steps[0].Variables);

        Assert.Equal("LastSyncValue", variable.Name);
        Assert.Contains("SyncHistory", variable.Sql, StringComparison.Ordinal);
        Assert.True(variable.RunAgainstDestination);
    }

    /// <summary>
    /// Legacy substitution is textual, so @Last eats @LastSyncValue. The validator warns
    /// too; the importer saw it first and can say which step it was.
    /// </summary>
    [Fact]
    public async Task AVariableNameThatIsAPrefixOfAnother_IsReported()
    {
        var result = await ImportAsync(variables:
        [
            new DataSyncVariableRow { SyncStepVariableID = 1, SyncStepID = 10, VariableName = "@Last", CustomSQL = "SELECT 1" },
            new DataSyncVariableRow { SyncStepVariableID = 2, SyncStepID = 10, VariableName = "@LastSyncValue", CustomSQL = "SELECT 2" }
        ]);

        ImportAssertions.Names(result.Losses, "'Last'", "prefix of", "'LastSyncValue'");
    }

    // ------------------------------------------------------------------------- the guards

    /// <summary>
    /// DataSync counts the rows it moved and prints the number. There is nothing in the
    /// catalog that says how few is too few, so nothing can be imported into the guard and
    /// the operator has to set one.
    /// </summary>
    [Fact]
    public async Task ThereIsNoGuardToImport_AndTheImportSaysSoRatherThanLeavingItQuiet()
    {
        var result = await ImportAsync();

        Assert.Equal(0, result.Jobs.Single().Steps[0].Publication.Guard.MinimumRows);
        ImportAssertions.Names(result.Losses, "no minimum-row guard to import", "only prints the number");
    }

    [Fact]
    public async Task TwoSyncKeys_AreReportedByTheImporterBeforeTheValidatorSeesThem()
    {
        var maps = FieldMaps();
        maps.Add(new DataSyncFieldMapRow
        {
            SyncStepFieldMapID = 9,
            SyncStepID = 10,
            SourceField = "Version",
            SyncKey = true
        });

        var result = await ImportAsync(fieldMaps: maps);

        ImportAssertions.Names(result.Losses, "marks 2 columns as SyncKey", "MAX()");
    }

    // ---------------------------------------------------------------------- the builders

    private static Task<ImportResult> ImportAsync(
        DataSyncStepRow? step = null,
        List<DataSyncFieldMapRow>? fieldMaps = null,
        List<DataSyncVariableRow>? variables = null) =>
        new DataSyncCatalogImporter(new DataSyncCatalogRows
        {
            Processes =
            [
                new DataSyncProcessRow
                {
                    SyncProcessID = 2,
                    Description = "Sincronizar Factory1",
                    TargetServerID = 1,
                    IsActive = true,
                    LastStatus = 2
                },
                new DataSyncProcessRow
                {
                    SyncProcessID = 3,
                    Description = "Proceso borrado",
                    TargetServerID = 1,
                    IsActive = true,
                    GCRecord = 7
                }
            ],
            ProcessSources =
            [
                new DataSyncProcessSourceRow
                {
                    SyncProcessSourceID = 1,
                    SyncProcessID = 2,
                    RemoteServerConfigID = 2,
                    IsCreatedLinkedServer = true
                }
            ],
            Steps = [step ?? Step()],
            FieldMaps = fieldMaps ?? FieldMaps(),
            Variables = variables ??
            [
                new DataSyncVariableRow
                {
                    SyncStepVariableID = 1,
                    SyncStepID = 10,
                    VariableName = "@LastSyncValue",
                    CustomSQL = "SELECT top 1 LastSyncValue FROM [dbo].[SyncHistory] WHERE SyncProcessID = 2"
                }
            ],
            Servers =
            [
                new DataSyncRemoteServerRow
                {
                    ServerID = 1,
                    ServerName = "PRODETIQ",
                    DataSource = "PRODETIQ-SQL",
                    InitialCatalog = "PRODETIQ",
                    TableForSpeedTest = "dbo.SmallLookup",
                    IsActive = true
                },
                new DataSyncRemoteServerRow
                {
                    ServerID = 2,
                    ServerName = "Factory1",
                    DataSource = "FACTORY1-SQL",
                    InitialCatalog = "Paradas",
                    UserID = "sincronizador",
                    UserPassword = ClearTextPassword,
                    TableForSpeedTest = "dbo.SmallLookup",
                    IsActive = true
                }
            ]
        }).ImportAsync(CancellationToken.None);

    private static DataSyncStepRow Step(int fieldUpdateType = 2) => new()
    {
        SyncStepID = 10,
        SyncProcessID = 2,
        OrderNumber = 1.5,
        RemoteServerConfigID = 2,
        SourceTable = "dbo.Tparadas",
        DestinationTable = "T_PRFM_Stops_ByDay",
        CustomSQL = "SELECT * FROM OPENQUERY([Factory1_LinkedServer],'Select * from dbo.GetTparadasData(''@LastSyncValue'')')",
        BatchSize = 5000,
        IsActive = true,
        SourceDeleteTable = "dbo.DeletedRecords",
        FieldToUpdate = "PlantCode",
        FieldUpdateType = fieldUpdateType,
        UpdateValue = "F1",
        LastStatus = 2
    };

    private static List<DataSyncFieldMapRow> FieldMaps() =>
    [
        new() { SyncStepFieldMapID = 1, SyncStepID = 10, SourceField = "PPL", TargetField = "PPL", UniqueKey = true },
        new()
        {
            SyncStepFieldMapID = 2,
            SyncStepID = 10,
            SourceField = "YearWeekDayShiftLinecode",
            TargetField = "YearWeekDayShiftLinecode",
            UniqueKey = true,
            DeleteKey = true
        },
        new()
        {
            SyncStepFieldMapID = 3,
            SyncStepID = 10,
            SourceField = "LastSyncValue",
            TargetField = "LastSyncValue",
            SyncKey = true
        },
        new() { SyncStepFieldMapID = 4, SyncStepID = 10, SourceField = "Stops", TargetField = "Stops" },
        new() { SyncStepFieldMapID = 5, SyncStepID = 10, SourceField = "Duration", TargetField = "Duration" },
        new() { SyncStepFieldMapID = 6, SyncStepID = 10, SourceField = "Comment", TargetField = "Comment" }
    ];
}
