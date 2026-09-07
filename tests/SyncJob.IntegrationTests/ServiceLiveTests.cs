using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SyncJob.Core.Model;
using SyncJob.Core.Run;
using SyncJob.Database;
using SyncJob.Services;
using SyncJob.Services.Models;

namespace SyncJob.IntegrationTests;

/// <summary>
/// The Windows service path, end to end: a configuration in the local SQLite store, a
/// task from the central queue, and a real source and destination database.
/// <para>
/// This path has never had a live test, which is most of why it still carried a bug the
/// CLI fixed in 2.3. Three of the tests below exist to hold the three failures that were
/// in <c>SyncTaskExecutor.CommitStageToFinal</c> - a publication with no column list, a
/// schema-modification lock, and a check that asked whether the bulk copy lost rows
/// rather than whether the load was plausible - and two of them run the old statement
/// against the same input first, so that what they prove is not merely that the new path
/// is careful but that the old one was not.
/// </para>
/// <para>
/// Everything goes through <see cref="SyncTaskExecutor.ExecuteTaskAsync"/> rather than
/// through the engine directly. The engine has its own live tests; what is under test
/// here is the surface - the configuration it reads, the guard it chooses, the lease it
/// switches on and the history it writes.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ServiceLiveTests(SqlServerFixture fixture) : IDisposable
{
    private const string SourceQuery = "SELECT Id, Name, Amount, ChangedAt FROM dbo.Ledger";

    private static readonly string[] LedgerColumns = ["Id", "Name", "Amount", "ChangedAt"];

    private readonly string _store = Path.Combine(
        Path.GetTempPath(), $"SyncJobIT_{Guid.NewGuid():N}.db");

    /// <summary>
    /// A source that comes back with nothing does not empty the destination.
    /// <para>
    /// This is the live defect. The path this replaces counted the staged rows, compared
    /// them with the rows it had read and published when the two agreed - so an empty
    /// source gave <c>0 == 0</c>, passed, truncated the table and reported success. The
    /// second half of this test runs that exact statement against the same input to show
    /// it, because a test that only asserts the new behaviour cannot tell a fix from a
    /// coincidence.
    /// </para>
    /// </summary>
    [WindowsLiveFact]
    public async Task ASourceThatReturnsNothingDoesNotEmptyTheDestination()
    {
        var (source, destination) = await fixture.CreatePairAsync();

        await PublicationFixture.CreateLedgerAsync(source, "dbo.Ledger");
        await PublicationFixture.CreateLedgerAsync(destination, "dbo.Ledger");
        await PublicationFixture.LoadAsync(destination, "dbo.Ledger", rows: 100, firstId: 1);

        var before = await RowsAsync(destination, "dbo.Ledger");

        var (result, log) = await RunAsync(source, destination, Configuration("empty-source"));

        // The destination first, because it is the thing that matters and because a
        // failure here says what actually happened to the table. Row for row and
        // rowversion included: a table that was emptied and refilled has the right count.
        Assert.Equal(before, await RowsAsync(destination, "dbo.Ledger"));

        Assert.False(result.Success);
        Assert.Contains("the guard refused to publish into dbo.Ledger", result.ErrorMessage);

        Assert.Equal("Failed", Single(HistoryOf("empty-source")).Status);
        Assert.Empty(await LeftoverStagingAsync(destination));

        // A load that did not happen is an error, because a table someone asked to be
        // refreshed is stale and somebody has to look at the source.
        Assert.Contains(log, x => x.Level == LogLevel.Error);

        // ------------------------------------------------------------------ and the old
        // path, given the same input: SyncTaskExecutor.cs:438-452 as it was.
        await SqlServerFixture.ExecuteAsync(destination, """
            CREATE TABLE dbo.Deployed      (Id int NOT NULL, Name nvarchar(120) NOT NULL, Amount decimal(19,4) NOT NULL);
            CREATE TABLE dbo.DeployedStage (Id int NOT NULL, Name nvarchar(120) NOT NULL, Amount decimal(19,4) NOT NULL);
            INSERT INTO dbo.Deployed (Id, Name, Amount)
            SELECT Id, Name, Amount FROM dbo.Ledger;
            """);

        Assert.Equal(100, await SqlServerFixture.CountAsync(destination, "dbo.Deployed"));

        const int rowsReadFromAnEmptySource = 0;
        var stageCount = Convert.ToInt32(
            await SqlServerFixture.ScalarAsync(destination, "SELECT COUNT(*) FROM dbo.DeployedStage;"));

        // "if(stageCount != rowCount) throw" - which is exactly the check that let this
        // through: it asks whether the bulk copy lost rows, not whether the load is
        // plausible, and nothing was lost on the way from nothing.
        Assert.Equal(rowsReadFromAnEmptySource, stageCount);

        await SqlServerFixture.ExecuteAsync(
            destination,
            "TRUNCATE TABLE dbo.Deployed; INSERT INTO dbo.Deployed SELECT * FROM dbo.DeployedStage;");

        Assert.Equal(0, await SqlServerFixture.CountAsync(destination, "dbo.Deployed"));
    }

    /// <summary>
    /// A staging table that has drifted from its destination is rebuilt from the
    /// destination's own shape, not published into it column by position.
    /// <para>
    /// The spec for this package asks for a drifted stage to be <em>refused</em>. It is
    /// not, and it cannot be: the engine creates staging from the destination's catalog
    /// entry on every run, so the drift the old path published through does not survive
    /// long enough to be refused. What the test therefore holds is the same guarantee
    /// from the other end - the rows land in the right columns - and it shows the old
    /// statement putting the same rows in shifted, silently, with the types lining up the
    /// whole way.
    /// </para>
    /// </summary>
    [WindowsLiveFact]
    public async Task AStageThatHasDriftedFromItsDestinationIsRebuiltRatherThanPublishedShifted()
    {
        var (source, destination) = await fixture.CreatePairAsync();

        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.Person (Id int NOT NULL, FirstName nvarchar(50) NOT NULL, LastName nvarchar(50) NOT NULL);
            INSERT INTO dbo.Person (Id, FirstName, LastName) VALUES
                (1, N'Ada', N'Lovelace'), (2, N'Grace', N'Hopper'), (3, N'Edsger', N'Dijkstra');
            """);

        await SqlServerFixture.ExecuteAsync(destination, """
            CREATE TABLE dbo.Person (Id int NOT NULL, FirstName nvarchar(50) NOT NULL, LastName nvarchar(50) NOT NULL);
            INSERT INTO dbo.Person (Id, FirstName, LastName) VALUES (9, N'stale', N'row');
            """);

        // The hand-made staging table the deployed service requires, after somebody
        // reordered the destination and did not reorder this.
        await SqlServerFixture.ExecuteAsync(
            destination,
            "CREATE TABLE dbo.PersonStage (Id int NOT NULL, LastName nvarchar(50) NOT NULL, FirstName nvarchar(50) NOT NULL);");

        var config = Configuration("drifted-stage");
        config.SourceQuery = "SELECT Id, FirstName, LastName FROM dbo.Person";
        config.DestStageTable = "dbo.PersonStage";
        config.DestFinalTable = "dbo.Person";
        config.ColumnMappings = Mappings(config.ConfigId, "Id", "FirstName", "LastName");

        var (result, _) = await RunAsync(source, destination, config);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(3, result.RowsProcessed);

        Assert.Equal(
            new List<string> { "1|Ada|Lovelace", "2|Grace|Hopper", "3|Edsger|Dijkstra" },
            await PeopleAsync(destination, "dbo.Person"));

        // The drifted stage is gone as a shape: it was dropped and rebuilt from the
        // destination, which is why the two can no longer disagree.
        Assert.Equal(
            await ColumnNamesAsync(destination, "dbo.Person"),
            await ColumnNamesAsync(destination, "dbo.PersonStage"));

        // ------------------------------------------------------------------ and the old
        // path, given the same drifted stage: SyncTaskExecutor.cs:447 as it was.
        await SqlServerFixture.ExecuteAsync(destination, """
            CREATE TABLE dbo.Deployed      (Id int NOT NULL, FirstName nvarchar(50) NOT NULL, LastName nvarchar(50) NOT NULL);
            CREATE TABLE dbo.DeployedStage (Id int NOT NULL, LastName nvarchar(50) NOT NULL, FirstName nvarchar(50) NOT NULL);
            INSERT INTO dbo.DeployedStage (Id, LastName, FirstName) VALUES
                (1, N'Lovelace', N'Ada'), (2, N'Hopper', N'Grace'), (3, N'Dijkstra', N'Edsger');
            """);

        await SqlServerFixture.ExecuteAsync(
            destination,
            "TRUNCATE TABLE dbo.Deployed; INSERT INTO dbo.Deployed SELECT * FROM dbo.DeployedStage;");

        // Three rows, no error, every surname in the given-name column.
        Assert.Equal(
            new List<string> { "1|Lovelace|Ada", "2|Hopper|Grace", "3|Dijkstra|Edsger" },
            await PeopleAsync(destination, "dbo.Deployed"));
    }

    /// <summary>
    /// A source column the destination does not have stops the run and names itself,
    /// rather than being matched by position.
    /// <para>
    /// This is the other half of the shifted publication: the day someone renames a
    /// column on one side, anything that falls back to ordinal position reproduces the
    /// failure exactly. The copy refuses instead, and the destination is left holding
    /// what it held.
    /// </para>
    /// </summary>
    [WindowsLiveFact]
    public async Task ASourceColumnTheDestinationDoesNotHaveStopsTheRunAndNamesItself()
    {
        var (source, destination) = await fixture.CreatePairAsync();

        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.Person (Id int NOT NULL, FirstName nvarchar(50) NOT NULL, Surname nvarchar(50) NOT NULL);
            INSERT INTO dbo.Person (Id, FirstName, Surname) VALUES (1, N'Ada', N'Lovelace'), (2, N'Grace', N'Hopper');
            """);

        await SqlServerFixture.ExecuteAsync(destination, """
            CREATE TABLE dbo.Person (Id int NOT NULL, FirstName nvarchar(50) NOT NULL, LastName nvarchar(50) NOT NULL);
            INSERT INTO dbo.Person (Id, FirstName, LastName) VALUES (9, N'stale', N'row');
            """);

        var before = await PeopleAsync(destination, "dbo.Person");

        var config = Configuration("renamed-column");
        config.SourceQuery = "SELECT Id, FirstName, Surname FROM dbo.Person";
        config.DestFinalTable = "dbo.Person";
        config.ColumnMappings = Mappings(config.ConfigId, "Id", "FirstName", "LastName");

        var (result, _) = await RunAsync(source, destination, config);

        Assert.False(result.Success);

        // Pinned to the column match rather than to any failure: a test that only asserts
        // "it did not publish" passes just as well when the server was unreachable.
        // Pinned to the column match rather than to any failure: a test that only asserts
        // "it did not publish" passes just as well when the server was unreachable.
        //
        // The table named in the middle of the message is the generated staging table and
        // not dbo.Person, because it is staging the copy writes into. That is a Core
        // legibility problem and it is in the report: an operator reading
        // "does not line up with [dbo].[Person_stg_cb5cc1ae]" has to already know that
        // staging is a clone of the destination to know which table to go and look at.
        Assert.Contains("the source does not line up with", result.ErrorMessage);
        Assert.Contains("dbo.Person may not have been written", result.ErrorMessage);
        Assert.Contains("Surname", result.ErrorMessage);
        Assert.Contains("LastName", result.ErrorMessage);

        Assert.Equal(before, await PeopleAsync(destination, "dbo.Person"));
        Assert.Equal("Failed", Single(HistoryOf("renamed-column")).Status);
    }

    /// <summary>
    /// A load that collapsed to a sliver of the destination is refused even though no
    /// floor is set - which is the state every deployed configuration is in, because
    /// <c>MinRowThreshold</c> defaults to zero.
    /// <para>
    /// It is also the check a fixed floor cannot make. A floor of a thousand, set when
    /// the table held a thousand rows, passes a load of nine hundred into a table that
    /// has since grown to four million.
    /// </para>
    /// </summary>
    [WindowsLiveFact]
    public async Task ALoadThatCollapsedToASliverOfTheDestinationIsRefusedWithNoFloorSet()
    {
        var (source, destination) = await fixture.CreatePairAsync();

        await PublicationFixture.CreateLedgerAsync(source, "dbo.Ledger");
        await PublicationFixture.CreateLedgerAsync(destination, "dbo.Ledger");
        await PublicationFixture.LoadAsync(source, "dbo.Ledger", rows: 5, firstId: 1);
        await PublicationFixture.LoadAsync(destination, "dbo.Ledger", rows: 1_000, firstId: 1);

        var before = await RowsAsync(destination, "dbo.Ledger");

        var config = Configuration("collapsed");
        Assert.Equal(0, config.MinRowThreshold);

        var (result, _) = await RunAsync(source, destination, config);

        Assert.False(result.Success);
        Assert.Contains("the fraction refused it", result.ErrorMessage);
        Assert.Equal(before, await RowsAsync(destination, "dbo.Ledger"));
    }

    /// <summary>
    /// A normal load still loads, and the history gets the numbers the work produced.
    /// <para>
    /// The counts are the assertion that matters. The path this replaces set
    /// <c>RowsInserted</c> to the number of rows it had read from the source and left
    /// <c>RowsUpdated</c> and <c>RowsDeleted</c> at zero, so a history row saying that a
    /// replace removed a hundred rows is a number the old path could not have produced.
    /// </para>
    /// </summary>
    [WindowsLiveFact]
    public async Task ANormalLoadLoadsAndTheHistoryGetsTheNumbersTheWorkProduced()
    {
        var (source, destination) = await fixture.CreatePairAsync();

        await PublicationFixture.CreateLedgerAsync(source, "dbo.Ledger");
        await PublicationFixture.CreateLedgerAsync(destination, "dbo.Ledger");
        await PublicationFixture.LoadAsync(source, "dbo.Ledger", rows: 500, firstId: 1);
        await PublicationFixture.LoadAsync(destination, "dbo.Ledger", rows: 100, firstId: 9_000);

        var (result, log) = await RunAsync(source, destination, Configuration("normal"));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(result.Skipped);
        Assert.Equal(500, result.RowsProcessed);
        Assert.Equal(500, result.RowsInserted);
        Assert.Equal(0, result.RowsUpdated);
        Assert.Equal(100, result.RowsDeleted);

        Assert.Equal(
            await RowsAsync(source, "dbo.Ledger", withVersion: false),
            await RowsAsync(destination, "dbo.Ledger", withVersion: false));

        var history = Single(HistoryOf("normal"));
        Assert.Equal("Success", history.Status);
        Assert.Equal(500, history.RowsRead);
        Assert.Equal(500, history.RowsInserted);
        Assert.Equal(0, history.RowsUpdated);
        Assert.Equal(100, history.RowsDeleted);
        Assert.Null(history.ErrorMessage);

        // The copy reports every ten thousand rows and the runner once per step, so a
        // five-hundred-row load says its piece once and is not a progress storm.
        Assert.Contains(log, x => x.Message.Contains("500 read, 500 inserted"));

        Assert.Empty(await LeftoverStagingAsync(destination));
    }

    /// <summary>
    /// A run whose job is already claimed by another host does not start, is reported as
    /// a skip rather than a failure, and is logged at information.
    /// <para>
    /// This is the test that holds <c>UseLease</c> on for this surface. With the adapter's
    /// own value - false, which is right for a one-shot CLI - the second run would go
    /// ahead and replace the destination while the first was still writing it, and this
    /// test would fail on every assertion at once.
    /// </para>
    /// <para>
    /// Two hosts racing for one job is a normal Tuesday and not an incident, so the log
    /// is checked for the absence of an error as carefully as for the presence of the
    /// explanation: a service that pages someone at 02:00 because the lease worked is a
    /// service people turn off.
    /// </para>
    /// </summary>
    [WindowsLiveFact]
    public async Task ARunWhoseJobIsClaimedByAnotherHostIsSkippedAndSaysSoAtInformation()
    {
        var (source, destination) = await fixture.CreatePairAsync();

        await PublicationFixture.CreateLedgerAsync(source, "dbo.Ledger");
        await PublicationFixture.CreateLedgerAsync(destination, "dbo.Ledger");
        await PublicationFixture.LoadAsync(source, "dbo.Ledger", rows: 500, firstId: 1);
        await PublicationFixture.LoadAsync(destination, "dbo.Ledger", rows: 100, firstId: 9_000);

        var before = await RowsAsync(destination, "dbo.Ledger");

        // Another host, holding the claim on the same job id. The engine keys the lease
        // by the job's id, which the importer takes from the configuration's own id.
        var leases = new SqlJobLeaseStore();
        await leases.EnsureReadyAsync(destination, CancellationToken.None);

        var held = await leases.TryAcquireAsync(
            destination, "claimed", Guid.NewGuid().ToString("N"), "another-host/1", TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.NotNull(held);

        var (result, log) = await RunAsync(source, destination, Configuration("claimed"));

        Assert.True(result.Success);
        Assert.True(result.Skipped);
        Assert.Null(result.ErrorMessage);
        Assert.Contains("another run holds this job's lease", result.Notes);

        Assert.Equal(before, await RowsAsync(destination, "dbo.Ledger"));

        Assert.Equal("Skipped", Single(HistoryOf("claimed")).Status);

        Assert.Contains(
            log,
            x => x.Level == LogLevel.Information && x.Message.Contains("another run holds this job's lease"));

        Assert.DoesNotContain(
            log,
            x => x.Level >= LogLevel.Error);
    }

    // ========================================================================
    // THE SERVICE'S OWN FURNITURE
    // ========================================================================

    /// <summary>
    /// Runs one task the way the worker does: a fresh SQLite store, two connections and a
    /// configuration in it, and the executor over the top.
    /// </summary>
    private async Task<(SyncTaskExecutionResult Result, List<LogLine> Log)> RunAsync(
        string source, string destination, ConfigurationEntity config)
    {
        DbManager.DbPath = _store;
        DbManager.Initialize();

        ConnectionRepository.Create(Connection("src-" + config.ConfigId, source));
        ConnectionRepository.Create(Connection("dst-" + config.ConfigId, destination));

        config.SourceConnectionId = "src-" + config.ConfigId;
        config.DestConnectionId = "dst-" + config.ConfigId;
        ConfigRepository.Create(config);

        var log = new Recorder();
        var executor = new SyncTaskExecutor(log);

        var result = await executor.ExecuteTaskAsync(
            new SyncTaskEntity
            {
                TaskId = Guid.NewGuid(),
                ProjectId = config.ConfigId,
                ConfigId = config.ConfigId,
                TaskType = "Sync",
                RequestedBy = "a-live-test"
            },
            CancellationToken.None);

        return (result, log.Lines.ToList());
    }

    /// <summary>
    /// A configuration in the shape a deployed one is in: batch size and MaxDOP set, no
    /// row threshold, no tracking, and the column list an operator typed - every entry of
    /// which names the same column on both sides.
    /// </summary>
    private static ConfigurationEntity Configuration(string id) => new()
    {
        ConfigId = id,
        DisplayName = id,
        SourceQuery = SourceQuery,
        DestFinalTable = "dbo.Ledger",
        BatchSize = 10_000,
        MaxDOP = 4,
        KeepIdentity = true,
        MinRowThreshold = 0,
        IsActive = true,
        TrackingMode = "Snapshot",
        MergeStrategy = "Upsert",
        ColumnMappings = Mappings(id, LedgerColumns)
    };

    private static List<ColumnMappingEntity> Mappings(string configId, params string[] columns) =>
        columns
            .Select((x, i) => new ColumnMappingEntity
            {
                ConfigId = configId,
                SourceColumn = x,
                DestColumn = x,
                Ordinal = i
            })
            .ToList();

    /// <summary>
    /// A connection row of the shape the service stores: the whole connection string
    /// under DPAPI, with the server and database also in their own columns.
    /// </summary>
    private static ConnectionEntity Connection(string id, string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);

        return new ConnectionEntity
        {
            ConnectionId = id,
            DisplayName = id,
            ServerName = builder.DataSource,
            DatabaseName = builder.InitialCatalog,
            TrustServerCertificate = builder.TrustServerCertificate,
            Encrypt = builder.Encrypt,
            ConnectionStringEncrypted = System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(connectionString),
                null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser)
        };
    }

    private static List<ExecutionHistoryEntity> HistoryOf(string configId) =>
        ExecutionHistoryRepository.GetByConfigId(configId);

    private static ExecutionHistoryEntity Single(List<ExecutionHistoryEntity> history) =>
        Assert.Single(history);

    private static Task<List<string>> RowsAsync(string connectionString, string table, bool withVersion = true) =>
        PublicationFixture.StringsAsync(connectionString, $"""
            SELECT CONCAT(
                       Id, '|', Name, '|', Amount, '|',
                       ISNULL(CONVERT(nvarchar(30), ChangedAt, 126), ''),
                       '{(withVersion ? "|" : "")}', {(withVersion ? "CONVERT(nvarchar(30), CAST(Version AS binary(8)), 1)" : "''")})
            FROM {table}
            ORDER BY Id;
            """);

    private static Task<List<string>> PeopleAsync(string connectionString, string table) =>
        PublicationFixture.StringsAsync(
            connectionString,
            $"SELECT CONCAT(Id, '|', FirstName, '|', LastName) FROM {table} ORDER BY Id;");

    private static Task<List<string>> ColumnNamesAsync(string connectionString, string table) =>
        PublicationFixture.StringsAsync(
            connectionString,
            $"SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(N'{table}') ORDER BY column_id;");

    /// <summary>Staging tables the engine named for itself and did not take away.</summary>
    private static Task<List<string>> LeftoverStagingAsync(string connectionString) =>
        PublicationFixture.StringsAsync(
            connectionString,
            "SELECT name FROM sys.tables WHERE name LIKE '%[_]stg[_]%' ORDER BY name;");

    public void Dispose()
    {
        // The SQLite store is per-test-class and disposable; the SQL Server databases
        // belong to the fixture and go with it.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if(File.Exists(_store))
                File.Delete(_store);
        }
        catch(IOException)
        {
            // A file that will not delete is a nuisance in the temp folder, not a failure.
        }
    }

    private sealed record LogLine(LogLevel Level, string Message);

    /// <summary>
    /// The service's logger, kept rather than printed, so a test can say what was logged
    /// and at which level. A skip that arrives as an error is a service somebody turns
    /// off, and that is an assertion and not a review comment.
    /// </summary>
    private sealed class Recorder : ILogger<SyncTaskExecutor>
    {
        public ConcurrentQueue<LogLine> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(new LogLine(logLevel, formatter(state, exception)));
    }
}
