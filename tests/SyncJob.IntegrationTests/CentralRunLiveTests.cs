using Microsoft.Data.Sqlite;
using SyncJob.Commands;
using SyncJob.Database;

namespace SyncJob.IntegrationTests;

/// <summary>
/// The <c>run-db</c> command, end to end: a configuration read out of a real SQLite
/// catalog, run against two real databases, with the execution history it wrote.
/// <para>
/// It drives <see cref="RunFromDbCommand.RunAsync"/> rather than the engine directly,
/// because everything this work package changed is on the way in and the way out - the
/// importer, the command-line overrides, the credential the model refuses to hold, and
/// the row counts that reach the history. A test that called
/// <see cref="SyncJob.Core.Run.JobRunner"/> would prove the engine works, which
/// <c>RunnerLiveTests</c> already does, and would prove nothing about this command.
/// </para>
/// <para>
/// The destination carries a <c>rowversion</c>, which is what makes "left alone"
/// something these tests assert rather than infer: SQL Server moves it when and only
/// when a row is actually written, so a destination that is unchanged is unchanged row
/// for row and not merely by count.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CentralRunLiveTests(SqlServerFixture fixture)
{
    private const string Table = "dbo.Ventas";

    private const string Query = "SELECT IdVenta, Cliente, Monto FROM dbo.Ventas";

    // ------------------------------------------------------------------ the whole path

    /// <summary>
    /// A configuration in the catalog, run against two databases, judged on what it did
    /// to each row.
    /// <para>
    /// The counts are the assertion that matters. This command used to record the source
    /// row count as <c>RowsInserted</c> and zero for everything else; here 500 rows are
    /// read, 498 of them are new and 2 already existed with different values, and the
    /// history has to say 498 and 2. A regression to "rows read" gives 500 and 0 and
    /// fails.
    /// </para>
    /// </summary>
    [WindowsLiveFact]
    public async Task AConfigurationFromTheCatalogRunsEndToEndAndRecordsTheRealNumbers()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await CreateSalesAsync(source);
        await CreateSalesAsync(destination);
        await SeedAsync(source, 1, 500);

        // Two rows the source will change, and one it has never heard of.
        await SqlServerFixture.ExecuteAsync(destination, """
            INSERT INTO dbo.Ventas (IdVenta, Cliente, Monto) VALUES
                (1, N'viejo 1', 0), (2, N'viejo 2', 0), (9001, N'ajeno', 99);
            """);

        var untouchedBefore = await SalesAsync(destination, "IdVenta = 9001", withVersion: true);

        using var catalog = new Catalog();
        catalog.Add(source, destination, minRowThreshold: 100);

        var exit = await RunFromDbCommand.RunAsync(catalog.Settings(), CancellationToken.None);

        Assert.Equal(0, exit);

        var execution = LastExecution(catalog);
        Assert.Equal("Success", execution.Status);
        Assert.Equal(500, execution.RowsRead);
        Assert.Equal(498, execution.RowsInserted);
        Assert.Equal(2, execution.RowsUpdated);
        Assert.Equal(0, execution.RowsDeleted);

        // Row for row, and not by count: a publication that goes in shifted has the right
        // count.
        Assert.Equal(
            await SalesAsync(source, "IdVenta BETWEEN 1 AND 500"),
            await SalesAsync(destination, "IdVenta BETWEEN 1 AND 500"));

        Assert.Equal(501, await SqlServerFixture.CountAsync(destination, Table));

        // The row the source never mentioned still has the rowversion it had, which is
        // what "a merge left it alone" actually means.
        Assert.Equal(untouchedBefore, await SalesAsync(destination, "IdVenta = 9001", withVersion: true));

        // The catalog named its own staging table, so the engine rebuilt it from the
        // destination's shape and left it where the operator expects it.
        Assert.Equal(500, await SqlServerFixture.CountAsync(destination, "dbo.Ventas_Stage"));

        Assert.Empty(await LeftoverStagingAsync(destination));
    }

    // --------------------------------------------------------------- the empty source

    /// <summary>
    /// The failure the guard exists for: a source that comes back with nothing, and a
    /// destination that still holds everything it held.
    /// <para>
    /// The catalog sets a floor of its own here, so breaching it is a failure and the
    /// run exits one - which is what <c>PuedeCommitear</c>'s throw did.
    /// </para>
    /// </summary>
    [WindowsLiveFact]
    public async Task ASourceThatReturnsNothingLeavesTheDestinationExactlyAsItWas()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await CreateSalesAsync(source);
        await CreateSalesAsync(destination);
        await SeedAsync(destination, 1, 300);

        var before = await SalesAsync(destination, withVersion: true);

        using var catalog = new Catalog();
        catalog.Add(source, destination, minRowThreshold: 100, query: Query + " WHERE 1 = 0");

        var exit = await RunFromDbCommand.RunAsync(catalog.Settings(), CancellationToken.None);

        Assert.Equal(1, exit);

        var execution = LastExecution(catalog);
        Assert.Equal("Failed", execution.Status);
        Assert.Equal(0, execution.RowsInserted);
        Assert.Contains("0 staged", execution.ErrorMessage);
        Assert.Contains("100 required", execution.ErrorMessage);

        // Every row, its rowversion included, so "untouched" is asserted and not inferred.
        Assert.Equal(before, await SalesAsync(destination, withVersion: true));
        Assert.Equal(300, await SqlServerFixture.CountAsync(destination, Table));

        Assert.Empty(await LeftoverStagingAsync(destination));
    }

    /// <summary>
    /// The same empty source against a configuration that set no floor at all, which is
    /// the default a <c>syncjob config add</c> leaves behind.
    /// <para>
    /// The engine's floor is never below one row here, because the command it replaces
    /// refused to write an empty source before it ever looked at
    /// <c>MinRowThresholdToCommit</c>. Without that the guard would pass - zero staged is
    /// not below a floor of zero - and the run would report success for having published
    /// nothing. It is a skip rather than a failure, and the exit code is zero, because
    /// that is what the old early return gave it.
    /// </para>
    /// </summary>
    [WindowsLiveFact]
    public async Task AnEmptySourceWithNoFloorConfiguredIsASkipAndNotASuccess()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await CreateSalesAsync(source);
        await CreateSalesAsync(destination);
        await SeedAsync(destination, 1, 40);

        var before = await SalesAsync(destination, withVersion: true);

        using var catalog = new Catalog();
        catalog.Add(source, destination, minRowThreshold: 0, query: Query + " WHERE 1 = 0");

        var exit = await RunFromDbCommand.RunAsync(catalog.Settings(), CancellationToken.None);

        Assert.Equal(0, exit);

        var execution = LastExecution(catalog);
        Assert.Equal("Skipped", execution.Status);
        Assert.Contains("0 staged", execution.ErrorMessage);
        Assert.Contains("1 required", execution.ErrorMessage);

        Assert.Equal(before, await SalesAsync(destination, withVersion: true));
        Assert.Empty(await LeftoverStagingAsync(destination));
    }

    // ------------------------------------------------------------------- the credential

    /// <summary>
    /// The password stays in the catalog. No server: this is about what the model holds.
    /// <para>
    /// The job the command hands the engine carries a connection string with no
    /// credential in it and a <c>SecretRef</c> naming what completes it. The complete one
    /// exists only inside <c>JobRunOptions.ResolveConnectionString</c>, which the engine
    /// calls and never logs.
    /// </para>
    /// </summary>
    [WindowsFact]
    public async Task TheStoredCredentialNeverReachesTheModel()
    {
        using var catalog = new Catalog();

        catalog.Add(
            "Server=ORIGEN;Database=DBVentas;User Id=usuario;Password=n0-deberia-viajar;",
            "Server=DESTINO;Database=DBAnalytics;Integrated Security=true;",
            minRowThreshold: 0);

        var config = ConfigRepository.GetById(Catalog.ConfigId)!;
        var mappings = ColumnMappingRepository.GetByConfigId(Catalog.ConfigId);

        var (job, losses) = await RunFromDbCommand.BuildJobAsync(
            config,
            mappings,
            ConnectionRepository.GetById(config.SourceConnectionId)!,
            ConnectionRepository.GetById(config.DestConnectionId)!,
            catalog.Settings(),
            CancellationToken.None);

        var endpoint = Assert.Single(job.Sources);

        Assert.DoesNotContain("n0-deberia-viajar", endpoint.ConnectionString, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", endpoint.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"syncjob/{Catalog.ConfigId}/source/password", endpoint.SecretRef);

        // And the operator is told, rather than left to discover it on the first run.
        Assert.Contains(losses, x => x.Contains(endpoint.SecretRef!, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------ the setup

    /// <summary>
    /// A throwaway <c>syncjob.db</c> with one configuration in it, written through the
    /// repositories the CLI itself writes with - including
    /// <see cref="ConnectionRepository.EncryptString"/>, so the blob in the file is
    /// exactly the blob <c>syncjob connection add</c> leaves behind.
    /// </summary>
    private sealed class Catalog : IDisposable
    {
        public const string ConfigId = "ventas-diarias";

        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "SyncJobIT_" + Guid.NewGuid().ToString("N")[..8]);

        public Catalog()
        {
            Directory.CreateDirectory(_directory);
            DbPath = Path.Combine(_directory, "syncjob.db");

            DbManager.DbPath = DbPath;
            DbManager.Initialize();
        }

        public string DbPath { get; }

        public void Add(string sourceConnectionString, string destinationConnectionString, int minRowThreshold, string? query = null)
        {
            ConnectionRepository.Create(new ConnectionEntity
            {
                ConnectionId = "origen",
                DisplayName = "Origen",
                ServerName = "no-se-usa",
                DatabaseName = "no-se-usa",
                ConnectionStringEncrypted = ConnectionRepository.EncryptString(sourceConnectionString)
            });

            ConnectionRepository.Create(new ConnectionEntity
            {
                ConnectionId = "destino",
                DisplayName = "Destino",
                ServerName = "no-se-usa",
                DatabaseName = "no-se-usa",
                ConnectionStringEncrypted = ConnectionRepository.EncryptString(destinationConnectionString)
            });

            ConfigRepository.Create(new ConfigurationEntity
            {
                ConfigId = ConfigId,
                DisplayName = "Ventas diarias",
                SourceConnectionId = "origen",
                DestConnectionId = "destino",
                SourceQuery = query ?? Query,
                DestStageTable = "dbo.Ventas_Stage",
                DestFinalTable = Table,
                BatchSize = 1000,
                MaxDOP = 1,
                BulkCopyTimeout = 0,
                KeepIdentity = true,
                MinRowThreshold = minRowThreshold,
                IsActive = true,
                TrackingMode = "None",
                MergeStrategy = "Upsert",
                ColumnMappings =
                {
                    new ColumnMappingEntity { SourceColumn = "IdVenta", DestColumn = "IdVenta", IsPrimaryKey = true, Ordinal = 0 },
                    new ColumnMappingEntity { SourceColumn = "Cliente", DestColumn = "Cliente", Ordinal = 1 },
                    new ColumnMappingEntity { SourceColumn = "Monto", DestColumn = "Monto", Ordinal = 2 }
                }
            });
        }

        /// <summary>
        /// The command line as a scheduled run would give it: the catalog, and quiet.
        /// </summary>
        public RunFromDbSettings Settings() => new()
        {
            ConfigId = ConfigId,
            DbPath = DbPath,
            Quiet = true,
            LogFile = Path.Combine(_directory, "run.log")
        };

        public void Dispose()
        {
            // SQLite pools its connections and a pooled one keeps the file open, so the
            // directory would not delete.
            SqliteConnection.ClearAllPools();

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch(IOException)
            {
                // A temporary directory that will not go is a nuisance, not a failure.
            }
        }
    }

    private static ExecutionHistoryEntity LastExecution(Catalog catalog)
    {
        DbManager.DbPath = catalog.DbPath;

        return ExecutionHistoryRepository.GetLastExecution(Catalog.ConfigId)
            ?? throw new InvalidOperationException("the run recorded no execution history at all");
    }

    private static Task CreateSalesAsync(string connectionString) =>
        SqlServerFixture.ExecuteAsync(connectionString, $"""
            CREATE TABLE {Table} (
                IdVenta int           NOT NULL CONSTRAINT PK_Ventas PRIMARY KEY,
                Cliente nvarchar(100) NOT NULL,
                Monto   decimal(19,4) NOT NULL,
                Version rowversion    NOT NULL
            );
            """);

    private static Task SeedAsync(string connectionString, int firstId, int rows) =>
        SqlServerFixture.ExecuteAsync(connectionString, $"""
            WITH n(i) AS (
                SELECT TOP ({rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
                FROM sys.all_columns a CROSS JOIN sys.all_columns b
            )
            INSERT INTO {Table} (IdVenta, Cliente, Monto)
            SELECT {firstId} + i - 1, CONCAT(N'cliente ', {firstId} + i - 1), i * 1.5
            FROM n;
            """);

    private static Task<List<string>> SalesAsync(
        string connectionString, string filter = "1 = 1", bool withVersion = false) =>
        PublicationFixture.StringsAsync(connectionString, $"""
            SELECT CONCAT(IdVenta, '|', Cliente, '|', Monto
                          {(withVersion ? ", '|', CONVERT(nvarchar(30), CAST(Version AS binary(8)), 1)" : string.Empty)})
            FROM {Table}
            WHERE {filter}
            ORDER BY IdVenta;
            """);

    /// <summary>
    /// Staging tables the engine named and did not take away, recognised by the shape the
    /// factory generates - <c>&lt;destination&gt;_stg_&lt;8 hex&gt;</c>.
    /// <para>
    /// The table the catalog named for itself, <c>dbo.Ventas_Stage</c>, is deliberately
    /// not in this list: the factory rebuilds it from the destination's shape on every run
    /// and then leaves it, because a table the caller named is theirs and is the one thing
    /// an operator has to look at after a run that refused to publish.
    /// </para>
    /// </summary>
    private static Task<List<string>> LeftoverStagingAsync(string connectionString) =>
        PublicationFixture.StringsAsync(
            connectionString,
            "SELECT name FROM sys.tables WHERE name LIKE '%[_]stg[_]%' ORDER BY name;");
}
