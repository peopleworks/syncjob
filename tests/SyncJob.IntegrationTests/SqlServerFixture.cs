using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace SyncJob.IntegrationTests;

/// <summary>
/// Hands out throwaway databases on the server named by <c>SYNCJOB_TEST_CONN</c> and
/// removes every one of them when the collection finishes.
/// <para>
/// A data-movement test needs two of them - somewhere to read from and somewhere to
/// write to - so <see cref="CreatePairAsync"/> is the usual entry point rather than
/// <see cref="CreateDatabaseAsync"/>.
/// </para>
/// <para>
/// Scratch databases are named <c>SyncJobIT_&lt;8 hex&gt;</c> so a run that is killed
/// half way through leaves an obvious, greppable trace on the server rather than
/// something that looks like real data. The drop is unconditional - it runs after
/// failing tests too, because a failed copy is exactly the run that leaves the most
/// rubbish behind.
/// </para>
/// <para>
/// The fixture never touches the server from its constructor: xunit builds it even
/// when every test in the collection is skipped, so all the work is deferred to the
/// first call.
/// </para>
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string ConnectionEnvironmentVariable = "SYNCJOB_TEST_CONN";

    /// <summary>Prefix for every database this fixture creates.</summary>
    public const string DatabasePrefix = "SyncJobIT_";

    private const int CommandTimeoutSeconds = 180;

    private readonly ConcurrentQueue<string> _databases = new();

    /// <summary>True when the environment names a server to test against.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(RawConnectionString);

    private static string? RawConnectionString => Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

    /// <summary>The configured connection string, pointed at <c>master</c>.</summary>
    public static string ServerConnectionString => Build("master");

    /// <summary>
    /// A source database and a destination database, fresh and empty. Two databases
    /// rather than two schemas in one: a copy that accidentally reads and writes the
    /// same physical table passes a one-database test and fails in production.
    /// </summary>
    public async Task<(string Source, string Destination)> CreatePairAsync() =>
        (await CreateDatabaseAsync(), await CreateDatabaseAsync());

    /// <summary>
    /// Creates a fresh, empty database and returns a connection string scoped to it.
    /// The name is registered for cleanup before the database is created, so one that
    /// fails half way through still gets dropped.
    /// </summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = DatabasePrefix + Guid.NewGuid().ToString("N")[..8];
        _databases.Enqueue(name);

        // CREATE DATABASE cannot run inside a transaction.
        await ExecuteAsync(ServerConnectionString, $"CREATE DATABASE [{name}];");
        return Build(name);
    }

    /// <summary>
    /// A table of <paramref name="rows"/> rows in the shape most of these tests want:
    /// an identity key, a name, a decimal, a nullable date and a rowversion, so a copy
    /// that mangles a type has somewhere to show it.
    /// </summary>
    public static async Task SeedAsync(string connectionString, string table, int rows)
    {
        await ExecuteAsync(connectionString, $"""
            CREATE TABLE {table} (
                Id          int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Name        nvarchar(120)     NOT NULL,
                Amount      decimal(19,4)     NOT NULL,
                ChangedAt   datetime2(3)          NULL,
                Version     rowversion        NOT NULL
            );
            """);

        if(rows <= 0)
            return;

        // A tally built from a cross join rather than a loop: seeding a million rows
        // one INSERT at a time turns a 20-second test into a 20-minute one.
        await ExecuteAsync(connectionString, $"""
            WITH n(i) AS (
                SELECT TOP ({rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
                FROM sys.all_columns a CROSS JOIN sys.all_columns b
            )
            INSERT INTO {table} (Name, Amount, ChangedAt)
            SELECT CONCAT(N'row ', i), i * 1.5, DATEADD(minute, -i, SYSUTCDATETIME())
            FROM n;
            """);
    }

    /// <summary>
    /// The row count of a table, or -1 when the table is not there at all.
    /// <para>
    /// Two round trips rather than one <c>CASE</c>: a batch that names a missing table
    /// anywhere in it fails to compile, whichever branch would have run. Deferred name
    /// resolution only applies inside a module.
    /// </para>
    /// </summary>
    public static async Task<int> CountAsync(string connectionString, string table)
    {
        var exists = await ScalarAsync(connectionString, $"SELECT OBJECT_ID(N'{table}');");
        if(exists is null or DBNull)
            return -1;

        return Convert.ToInt32(await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM {table};"));
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
        return await command.ExecuteScalarAsync();
    }

    private static string Build(string database) =>
        new SqlConnectionStringBuilder(RawConnectionString ?? string.Empty) { InitialCatalog = database }
            .ConnectionString;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if(!IsConfigured || _databases.IsEmpty)
            return;

        // A pooled connection to a database keeps SET SINGLE_USER waiting, and the
        // drop then blocks the whole test run on nothing.
        SqlConnection.ClearAllPools();

        while(_databases.TryDequeue(out var name))
        {
            try
            {
                await ExecuteAsync(
                    ServerConnectionString,
                    $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
            }
            catch(SqlException)
            {
                // Best effort: a database that will not drop is a nuisance, not a test
                // failure, and the prefix makes it easy to find by hand.
            }
        }
    }
}

/// <summary>Every live test shares one fixture, so one cleanup drops everything.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql-server";
}
