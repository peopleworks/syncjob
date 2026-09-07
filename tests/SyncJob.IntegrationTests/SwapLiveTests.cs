using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SyncJob.Core;
using SyncJob.Core.Model;
using SyncJob.Core.Publication;

namespace SyncJob.IntegrationTests;

/// <summary>
/// Publication by swap, against a real destination with real permissions on it.
/// <para>
/// The acceptance test is <see cref="TwoPublications_LeaveTheIndexesAndTheGrantsExactlyAsTheyWere"/>.
/// Indexes, constraints and permissions belong to the physical object and not to its
/// name, so the swap that renames a bare staging table into place leaves the
/// destination carrying nothing - no filtered index, no <c>GRANT</c>, no <c>DENY</c>.
/// That is what these check, twice over, because the first publication of a deployment
/// can look right and the second one is where a re-granting implementation drifts.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SwapLiveTests(SqlServerFixture fixture)
{
    [LiveFact]
    public async Task TwoPublications_LeaveTheIndexesAndTheGrantsExactlyAsTheyWere()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Ledger");
        await PublicationFixture.LoadAsync(connectionString, "dbo.Ledger", rows: 500, firstId: 1);

        var login = "SyncJobIT_reader_" + Guid.NewGuid().ToString("N")[..8];
        await CreateLoginAsync(connectionString, login);
        try
        {
            await SqlServerFixture.ExecuteAsync(connectionString, $"""
                GRANT SELECT ON dbo.Ledger TO [{login}];
                GRANT UPDATE (Name) ON dbo.Ledger TO [{login}];
                DENY DELETE ON dbo.Ledger TO [{login}];
                """);

            var indexes = await PublicationFixture.IndexesAsync(connectionString, "dbo.Ledger");
            var permissions = await PublicationFixture.PermissionsAsync(connectionString, "dbo.Ledger");
            var objectId = await ObjectIdAsync(connectionString, "dbo.Ledger");

            await PublishAsync(connectionString, "dbo.Ledger", rows: 400, firstId: 1_000);
            await PublishAsync(connectionString, "dbo.Ledger", rows: 300, firstId: 5_000);

            Assert.Equal(indexes, await PublicationFixture.IndexesAsync(connectionString, "dbo.Ledger"));
            Assert.Equal(permissions, await PublicationFixture.PermissionsAsync(connectionString, "dbo.Ledger"));

            // Not incidental: the permissions and the indexes survived because the
            // destination is still the same object, which is the whole argument for
            // SWITCH over a rename.
            Assert.Equal(objectId, await ObjectIdAsync(connectionString, "dbo.Ledger"));
            Assert.Equal(300, await SqlServerFixture.CountAsync(connectionString, "dbo.Ledger"));

            // And the grant is not just a catalog row: it still decides an answer.
            Assert.Equal(1, Convert.ToInt32(await SqlServerFixture.ScalarAsync(connectionString, $"""
                EXECUTE AS USER = '{login}';
                SELECT HAS_PERMS_BY_NAME('dbo.Ledger', 'OBJECT', 'SELECT');
                REVERT;
                """)));
        }
        finally
        {
            await DropLoginAsync(login);
        }
    }

    /// <summary>
    /// The reason for the whole exercise: a truncate inside the publishing transaction
    /// keeps its schema-modification lock until the insert after it finishes, and a
    /// reader - even one that asked for <c>NOLOCK</c> - waits that long. The
    /// comparison is against the statement this publisher replaces, run by hand on the
    /// same rows.
    /// </summary>
    [LiveFact]
    public async Task TheSwapDoesNotHoldTheDestinationForTheLengthOfTheInsert()
    {
        const int rows = 200_000;
        var connectionString = await fixture.CreateDatabaseAsync();

        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Swapped");
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Truncated");
        await PublicationFixture.LoadAsync(connectionString, "dbo.Swapped", rows, firstId: 1);
        await PublicationFixture.LoadAsync(connectionString, "dbo.Truncated", rows, firstId: 1);

        var factory = new StagingTableFactory();
        var forSwap = await factory.CreateAsync(connectionString, "dbo.Swapped", null, default);
        var forTruncate = await factory.CreateAsync(connectionString, "dbo.Truncated", null, default);
        await PublicationFixture.LoadAsync(connectionString, forSwap, rows, firstId: 1_000_000);
        await PublicationFixture.LoadAsync(connectionString, forTruncate, rows, firstId: 1_000_000);

        var swapStall = await LongestReadAsync(connectionString, "dbo.Swapped", () =>
            new SwapPublisher().PublishAsync(connectionString, forSwap, Step("dbo.Swapped"), default));

        var truncateStall = await LongestReadAsync(connectionString, "dbo.Truncated", () =>
            SqlServerFixture.ExecuteAsync(connectionString, $"""
                BEGIN TRANSACTION;
                TRUNCATE TABLE dbo.Truncated;
                SET IDENTITY_INSERT dbo.Truncated ON;
                INSERT INTO dbo.Truncated (Id, Name, Amount, ChangedAt)
                    SELECT Id, Name, Amount, ChangedAt FROM {forTruncate};
                SET IDENTITY_INSERT dbo.Truncated OFF;
                COMMIT TRANSACTION;
                """));

        Assert.Equal(rows, await SqlServerFixture.CountAsync(connectionString, "dbo.Swapped"));
        Assert.True(
            swapStall < truncateStall / 2,
            $"a reader waited {swapStall.TotalMilliseconds:N0} ms on the swap and " +
            $"{truncateStall.TotalMilliseconds:N0} ms on the truncate-and-insert");
    }

    /// <summary>
    /// The staged rows are checked against the destination's constraints before
    /// anything is exchanged, so a load that would break one is refused with the
    /// destination untouched - rather than half way through an insert, with the
    /// destination already emptied.
    /// </summary>
    [LiveFact]
    public async Task AFailedPublicationLeavesTheDestinationExactlyAsItWas()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Ledger");
        await PublicationFixture.LoadAsync(connectionString, "dbo.Ledger", rows: 500, firstId: 1);
        var before = await PublicationFixture.StringsAsync(
            connectionString, "SELECT CONCAT(Id, '|', Name) FROM dbo.Ledger ORDER BY Id;");

        var staging = await new StagingTableFactory().CreateAsync(connectionString, "dbo.Ledger", null, default);
        await SqlServerFixture.ExecuteAsync(connectionString, $"""
            SET IDENTITY_INSERT {staging} ON;
            INSERT INTO {staging} (Id, Name, Amount) VALUES (9001, N'breaks the check', -1);
            SET IDENTITY_INSERT {staging} OFF;
            """);

        await Assert.ThrowsAsync<SqlException>(
            () => new SwapPublisher().PublishAsync(connectionString, staging, Step("dbo.Ledger"), default));

        Assert.Equal(before, await PublicationFixture.StringsAsync(
            connectionString, "SELECT CONCAT(Id, '|', Name) FROM dbo.Ledger ORDER BY Id;"));
    }

    /// <summary>
    /// A view has no storage to exchange, so publication falls back to emptying it and
    /// filling it again - with an explicit column list, which is the part the deployed
    /// service gets wrong.
    /// </summary>
    [LiveFact]
    public async Task AViewDestinationFallsBackToAnExplicitColumnList()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await SqlServerFixture.ExecuteAsync(connectionString, """
            CREATE TABLE dbo.LedgerBase (
                Id     int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                Name   nvarchar(120)     NOT NULL,
                Amount decimal(19,4)     NOT NULL);
            INSERT INTO dbo.LedgerBase (Name, Amount) VALUES (N'old one', 1), (N'old two', 2);
            """);
        await SqlServerFixture.ExecuteAsync(
            connectionString, "CREATE VIEW dbo.LedgerView AS SELECT Id, Name, Amount FROM dbo.LedgerBase;");

        var staging = await new StagingTableFactory().CreateAsync(connectionString, "dbo.LedgerView", null, default);
        await SqlServerFixture.ExecuteAsync(
            connectionString, $"INSERT INTO {staging} (Name, Amount) VALUES (N'new one', 10), (N'new two', 20);");

        await new SwapPublisher().PublishAsync(connectionString, staging, Step("dbo.LedgerView"), default);

        Assert.Equal(
            ["new one", "new two"],
            await PublicationFixture.StringsAsync(connectionString, "SELECT Name FROM dbo.LedgerBase ORDER BY Name;"));
    }

    /// <summary>
    /// The column list is by name, not by position. This is the deployed failure in
    /// miniature: the Windows service publishes with
    /// <c>INSERT INTO final SELECT * FROM stage</c>, so the day the two stop agreeing
    /// on the order of two columns of the same type, every row goes in with the values
    /// swapped and nothing complains.
    /// </summary>
    [LiveFact]
    public async Task TheFallbackMatchesColumnsByName_NotByPosition()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await SqlServerFixture.ExecuteAsync(connectionString, """
            CREATE TABLE dbo.PairBase (
                Id     int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                First  nvarchar(50)      NOT NULL,
                Second nvarchar(50)      NOT NULL);
            """);
        await SqlServerFixture.ExecuteAsync(
            connectionString, "CREATE VIEW dbo.PairView AS SELECT Id, First, Second FROM dbo.PairBase;");

        var staging = await new StagingTableFactory().CreateAsync(connectionString, "dbo.PairView", null, default);
        await SqlServerFixture.ExecuteAsync(
            connectionString, $"INSERT INTO {staging} (First, Second) VALUES (N'first', N'second');");

        // The drift, on the destination side this time.
        await SqlServerFixture.ExecuteAsync(
            connectionString, "ALTER VIEW dbo.PairView AS SELECT Id, Second, First FROM dbo.PairBase;");

        await new SwapPublisher().PublishAsync(connectionString, staging, Step("dbo.PairView"), default);

        Assert.Equal(
            ["first|second"],
            await PublicationFixture.StringsAsync(
                connectionString, "SELECT CONCAT(First, '|', Second) FROM dbo.PairBase;"));
    }

    /// <summary>
    /// Staging in another schema swaps like any other. It is <c>sp_rename</c> that
    /// cannot move an object between schemas, not <c>SWITCH</c> - so this is the
    /// fallback that is not needed.
    /// <para>
    /// The swap is told from the fallback by the state staging is left in: an exchange
    /// moves the storage out of it, an insert reads it and leaves it full.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task StagingInAnotherSchemaIsStillSwapped()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await SqlServerFixture.ExecuteAsync(connectionString, "CREATE SCHEMA loading;");
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Ledger");
        await PublicationFixture.LoadAsync(connectionString, "dbo.Ledger", rows: 100, firstId: 1);

        var staging = await new StagingTableFactory()
            .CreateAsync(connectionString, "dbo.Ledger", "loading.LedgerStage", default);
        await PublicationFixture.LoadAsync(connectionString, staging, rows: 60, firstId: 500);

        await new SwapPublisher().PublishAsync(connectionString, staging, Step("dbo.Ledger"), default);

        Assert.Equal(60, await SqlServerFixture.CountAsync(connectionString, "dbo.Ledger"));
        Assert.Equal(0, await SqlServerFixture.CountAsync(connectionString, staging));
    }

    /// <summary>
    /// A switch leaves the destination's identity counter at its seed. The
    /// truncate-and-insert this replaces did not, so putting it back is not a
    /// refinement - it is not regressing.
    /// </summary>
    [LiveFact]
    public async Task TheIdentityCounterFollowsTheRowsThatWerePublished()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await PublicationFixture.CreateLedgerAsync(connectionString, "dbo.Ledger");
        await PublicationFixture.LoadAsync(connectionString, "dbo.Ledger", rows: 50, firstId: 1);

        await PublishAsync(connectionString, "dbo.Ledger", rows: 20, firstId: 7_000);

        await SqlServerFixture.ExecuteAsync(
            connectionString, "INSERT INTO dbo.Ledger (Name, Amount) VALUES (N'the next one', 1);");

        Assert.Equal(7_020, Convert.ToInt32(await SqlServerFixture.ScalarAsync(
            connectionString, "SELECT MAX(Id) FROM dbo.Ledger;")));
    }

    [LiveFact]
    public async Task AppendAndMergeBelongToAnotherPublisher()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var step = Step("dbo.Ledger");
        step.Publication.Mode = PublicationMode.Merge;

        await Assert.ThrowsAsync<NotSupportedException>(
            () => new SwapPublisher().PublishAsync(connectionString, "dbo.Stage", step, default));
    }

    private static async Task<PublishResult> PublishAsync(
        string connectionString, string destination, int rows, int firstId)
    {
        var factory = new StagingTableFactory();
        var staging = await factory.CreateAsync(connectionString, destination, null, default);
        await PublicationFixture.LoadAsync(connectionString, staging, rows, firstId);

        var result = await new SwapPublisher().PublishAsync(connectionString, staging, Step(destination), default);
        await factory.DropAsync(connectionString, staging, default);
        return result;
    }

    /// <summary>
    /// How long the slowest single read of <paramref name="table"/> took while
    /// <paramref name="publish"/> ran. <c>NOLOCK</c> is deliberate: it still takes a
    /// schema-stability lock, which is exactly what a truncate's
    /// schema-modification lock keeps it waiting for.
    /// </summary>
    private static async Task<TimeSpan> LongestReadAsync(string connectionString, string table, Func<Task> publish)
    {
        using var stop = new CancellationTokenSource();
        var reading = new TaskCompletionSource();
        var longest = TimeSpan.Zero;

        var reader = Task.Run(async () =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            while(!stop.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    await using var command =
                        new SqlCommand($"SELECT TOP (1) Id FROM {table} WITH (NOLOCK);", connection);
                    await command.ExecuteScalarAsync();
                }
                catch(SqlException)
                {
                    // A read that was thrown out is a read that waited; the time it
                    // waited is the measurement, and the failure itself is not.
                }

                var elapsed = Stopwatch.GetElapsedTime(started);
                if(elapsed > longest)
                    longest = elapsed;

                reading.TrySetResult();
            }
        });

        await reading.Task;
        await publish();
        await stop.CancelAsync();
        await reader;

        return longest;
    }

    private static async Task<int> ObjectIdAsync(string connectionString, string table) =>
        Convert.ToInt32(await SqlServerFixture.ScalarAsync(connectionString, $"SELECT OBJECT_ID(N'{table}');"));

    /// <summary>
    /// A server login and a database user for it - not a user without a login, because
    /// the permission this test is about is the one a real reader holds.
    /// </summary>
    private static async Task CreateLoginAsync(string connectionString, string login)
    {
        await SqlServerFixture.ExecuteAsync(
            SqlServerFixture.ServerConnectionString,
            $"CREATE LOGIN [{login}] WITH PASSWORD = '{Guid.NewGuid()}Aa1!', CHECK_POLICY = OFF;");

        await SqlServerFixture.ExecuteAsync(connectionString, $"CREATE USER [{login}] FOR LOGIN [{login}];");
    }

    /// <summary>
    /// A login outlives the database that used it, so it is dropped by name whatever
    /// the test did - the databases the fixture hands out are the only thing it cleans
    /// up for us.
    /// </summary>
    private static async Task DropLoginAsync(string login)
    {
        try
        {
            await SqlServerFixture.ExecuteAsync(
                SqlServerFixture.ServerConnectionString, $"DROP LOGIN [{login}];");
        }
        catch(SqlException)
        {
        }
    }

    private static SyncStep Step(string destination) => new()
    {
        Id = "swap-live",
        DestinationTable = destination,
        Publication = new PublicationPlan { Mode = PublicationMode.Replace }
    };
}
