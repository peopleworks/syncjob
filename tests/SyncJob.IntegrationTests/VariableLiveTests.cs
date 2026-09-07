using System.Data;
using Microsoft.Data.SqlClient;
using SyncJob.Core;
using SyncJob.Core.Model;
using SyncJob.Core.Run;

namespace SyncJob.IntegrationTests;

/// <summary>
/// The variables and the SQL they end up in, against a real server.
/// <para>
/// The shape being reproduced here is the deployed one, which is why two databases are
/// needed rather than one: the variable reads the watermark from the destination, where
/// DataSync keeps its SyncHistory, and the query it feeds reads the source. Everything
/// that goes wrong with this - a value that fell back to nothing, a second variable
/// holding the first one's answer, a date the server read day-first - goes wrong
/// silently, with the right number of rows nowhere in sight.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class VariableLiveTests(SqlServerFixture fixture)
{
    /// <summary>
    /// A server that is not there, and a connect timeout short enough that a test which
    /// wrongly opens it fails quickly rather than looking hung.
    /// </summary>
    private const string Nowhere =
        "Server=syncjob-no-such-host;Integrated Security=true;Encrypt=false;Connect Timeout=1";

    private static SyncStep Step(params SqlVariable[] variables) => new()
    {
        Id = "step-1",
        Name = "Tparadas",
        DestinationTable = "dbo.Tparadas",
        Variables = [.. variables]
    };

    // ------------------------------------------------------------------- resolving the values

    /// <summary>
    /// The deployed shape: the watermark lives in the destination and the step reads the
    /// source. The source connection string here names a server that does not exist, so
    /// this also says that a step whose variables all read the destination never opens the
    /// source at all.
    /// </summary>
    [LiveFact]
    public async Task AVariableReadsTheDestinationWhileTheStepsSourceIsElsewhere()
    {
        var destination = await fixture.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(destination, """
            CREATE TABLE dbo.SyncHistory (SyncProcessID int NOT NULL, LastSyncValue datetime2(3) NOT NULL);
            INSERT INTO dbo.SyncHistory VALUES (2, '2026-01-15T10:00:00');
            """);

        var step = Step(new SqlVariable
        {
            Name = "LastSyncValue",
            Sql = "SELECT TOP 1 LastSyncValue FROM dbo.SyncHistory WHERE SyncProcessID = 2",
            RunAgainstDestination = true
        });

        var values = await VariableResolver.ResolveAsync(step, Nowhere, destination, CancellationToken.None);

        var value = Assert.Single(values);
        Assert.Equal("LastSyncValue", value.Name);
        Assert.Equal("2026-01-15T10:00:00", value.Value);
        Assert.False(value.CameFromDefault);
    }

    /// <summary>
    /// What the deployed jobs achieve with a UNION and a sentinel row ordered last. The
    /// flag matters as much as the value: a watermark that fell back is a step about to
    /// read from the beginning of time.
    /// </summary>
    [LiveFact]
    public async Task AVariableThatFindsNoRowFallsBackToItsDefault()
    {
        var destination = await fixture.CreateDatabaseAsync();
        await SqlServerFixture.ExecuteAsync(destination, "CREATE TABLE dbo.SyncHistory (v datetime2(3) NOT NULL);");

        var step = Step(new SqlVariable
        {
            Name = "LastSyncValue",
            Sql = "SELECT TOP 1 v FROM dbo.SyncHistory",
            RunAgainstDestination = true,
            DefaultValue = "1900-01-01T00:00:00"
        });

        var value = Assert.Single(await VariableResolver.ResolveAsync(step, Nowhere, destination, CancellationToken.None));

        Assert.Equal("1900-01-01T00:00:00", value.Value);
        Assert.True(value.CameFromDefault);
    }

    /// <summary>
    /// No row and no default is the case the deployed loader carries on from with an empty
    /// string, which asks the source for everything newer than nothing.
    /// </summary>
    [LiveFact]
    public async Task AVariableThatFindsNoRowAndHasNoDefaultIsRefusedByName()
    {
        var destination = await fixture.CreateDatabaseAsync();
        await SqlServerFixture.ExecuteAsync(destination, "CREATE TABLE dbo.SyncHistory (v datetime2(3) NOT NULL);");

        var step = Step(new SqlVariable
        {
            Name = "LastSyncValue",
            Sql = "SELECT TOP 1 v FROM dbo.SyncHistory",
            RunAgainstDestination = true
        });

        var exception = await Assert.ThrowsAsync<VariableResolutionException>(() =>
            VariableResolver.ResolveAsync(step, Nowhere, destination, CancellationToken.None));

        Assert.Equal("LastSyncValue", exception.VariableName);
        Assert.Contains("whole table", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A variable's SQL may legitimately be "SELECT value FROM ... ORDER BY ...", which is
    /// what TOP 1 semantics mean here. The first row, and nothing said about the rest.
    /// </summary>
    [LiveFact]
    public async Task AVariableWhoseSqlReturnsSeveralRowsTakesTheFirst()
    {
        var destination = await fixture.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(destination, """
            CREATE TABLE dbo.SyncHistory (RunOn datetime2(3) NOT NULL, LastSyncValue nvarchar(50) NOT NULL);
            INSERT INTO dbo.SyncHistory VALUES
                ('2026-03-01T00:00:00', 'newest'), ('2026-02-01T00:00:00', 'middle'), ('2026-01-01T00:00:00', 'oldest');
            """);

        var step = Step(new SqlVariable
        {
            Name = "LastSyncValue",
            Sql = "SELECT LastSyncValue FROM dbo.SyncHistory ORDER BY RunOn DESC",
            RunAgainstDestination = true
        });

        var value = Assert.Single(await VariableResolver.ResolveAsync(step, Nowhere, destination, CancellationToken.None));

        Assert.Equal("newest", value.Value);
        Assert.False(value.CameFromDefault);
    }

    /// <summary>
    /// The deployed loader reads every variable into one table variable and never clears
    /// it, so a second variable that finds no row keeps the first one's value - and the
    /// step then filters by a number that belongs to another question entirely.
    /// </summary>
    [LiveFact]
    public async Task EachVariableIsResolvedOnItsOwn()
    {
        var destination = await fixture.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(destination, """
            CREATE TABLE dbo.Setting (k nvarchar(10) NOT NULL, v nvarchar(50) NOT NULL);
            INSERT INTO dbo.Setting VALUES ('plant', 'F1');
            """);

        var step = Step(
            new SqlVariable
            {
                Name = "Plant",
                Sql = "SELECT v FROM dbo.Setting WHERE k = 'plant'",
                RunAgainstDestination = true
            },
            new SqlVariable
            {
                Name = "LastSyncValue",
                Sql = "SELECT v FROM dbo.Setting WHERE k = 'watermark'",
                RunAgainstDestination = true,
                DefaultValue = "1900-01-01T00:00:00"
            });

        var values = await VariableResolver.ResolveAsync(step, Nowhere, destination, CancellationToken.None);

        Assert.Equal("F1", values[0].Value);
        Assert.Equal("1900-01-01T00:00:00", values[1].Value);
        Assert.True(values[1].CameFromDefault);
    }

    // --------------------------------------------------------------- the SQL the step then sends

    /// <summary>
    /// The predicate the engine builds for a table source, run against a real table: what
    /// is being checked is that the boundary is a boundary and not a string comparison
    /// that happens to look right.
    /// </summary>
    [LiveFact]
    public async Task TheWatermarkPredicateReadsOnlyTheRowsPastIt()
    {
        var source = await fixture.CreateDatabaseAsync();

        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.Tparadas (Id int NOT NULL, FechaCambio datetime2(3) NOT NULL);
            INSERT INTO dbo.Tparadas VALUES
                (1, '2026-01-14T09:00:00'), (2, '2026-01-15T11:00:00'), (3, '2026-01-16T09:00:00');
            """);

        var step = Incremental("dbo.Tparadas", null);
        var watermark = new Watermark("2026-01-15T10:00:00.0000000", null, DateTimeOffset.UtcNow);

        Assert.Equal(2, await RowsAsync(source, SourceSql.Build(step, [], watermark)));
        Assert.Equal(3, await RowsAsync(source, SourceSql.Build(step, [], null)));
    }

    /// <summary>
    /// A rowversion watermark goes as bytes, and the second half of this test is why: the
    /// server refuses the same query outright when the boundary arrives as its 0x... text,
    /// so a run that sent it that way would fail every night rather than filter wrongly.
    /// </summary>
    [LiveFact]
    public async Task ARowversionWatermarkIsComparedAsBytesBecauseItsTextIsRefused()
    {
        var source = await fixture.CreateDatabaseAsync();
        await SqlServerFixture.SeedAsync(source, "dbo.Tparadas", 10);

        var reached = (string)(await SqlServerFixture.ScalarAsync(
            source, "SELECT CONVERT(nvarchar(50), CONVERT(varbinary(8), MIN(Version)), 1) FROM dbo.Tparadas;"))!;

        var step = Incremental("dbo.Tparadas", null, syncKey: "Version");
        var built = SourceSql.Build(step, [], new Watermark(reached, null, DateTimeOffset.UtcNow));

        Assert.Equal(9, await RowsAsync(source, built));

        var asText = new ResolvedSource(
            built.Sql, built.IsStoredProcedure, new Dictionary<string, object?> { [SourceSql.WatermarkParameter] = reached });

        var exception = await Assert.ThrowsAsync<SqlException>(() => RowsAsync(source, asText));
        Assert.Contains("nvarchar to timestamp", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The date came out of one server as a DateTime and goes into another one's SQL as
    /// text, and a column of the old datetime type reads that text according to the
    /// session's DATEFORMAT. Written with a space - which is what SyncJob's own
    /// incremental path builds - '2026-01-15 00:00:00' raises "out-of-range value" the
    /// moment a session runs dmy. Written ISO 8601 it is read the same way everywhere.
    /// </summary>
    [LiveFact]
    public async Task ADateSubstitutedIntoSqlIsReadTheSameWayUnderADayFirstDateFormat()
    {
        var (source, destination) = await fixture.CreatePairAsync();

        await SqlServerFixture.ExecuteAsync(destination, """
            CREATE TABLE dbo.SyncHistory (LastSyncValue datetime NOT NULL);
            INSERT INTO dbo.SyncHistory VALUES ('2026-01-15T10:00:00');
            """);

        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.Tparadas (Id int NOT NULL, FechaCambio datetime NOT NULL);
            INSERT INTO dbo.Tparadas VALUES
                (1, '2026-01-14T09:00:00'), (2, '2026-01-16T09:00:00'), (3, '2026-01-17T09:00:00');
            """);

        var step = Step(new SqlVariable
        {
            Name = "LastSyncValue",
            Sql = "SELECT TOP 1 LastSyncValue FROM dbo.SyncHistory",
            RunAgainstDestination = true
        });

        step.Source = new SourceQuery { Sql = "SELECT * FROM dbo.Tparadas WHERE FechaCambio > '${LastSyncValue}'" };

        var values = await VariableResolver.ResolveAsync(step, source, destination, CancellationToken.None);

        Assert.Equal(2, await RowsAsync(source, SourceSql.Build(step, values, null), "SET DATEFORMAT dmy; "));
    }

    private static SyncStep Incremental(string table, string? where, string syncKey = "FechaCambio")
    {
        var step = Step();
        step.Source = new SourceQuery { Table = table, Where = where };
        step.FieldMaps = [new FieldMap { Source = syncKey, Target = syncKey, IsSyncKey = true }];
        step.Incremental = new IncrementalPlan { Watermark = new WatermarkPlan() };

        return step;
    }

    /// <summary>Runs what the engine built, exactly as the copier would, and counts what came back.</summary>
    private static async Task<int> RowsAsync(string connectionString, ResolvedSource source, string prelude = "")
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(prelude + source.Sql, connection)
        {
            CommandType = source.IsStoredProcedure ? CommandType.StoredProcedure : CommandType.Text
        };

        foreach(var (name, value) in source.Parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync();

        var rows = 0;
        while(await reader.ReadAsync())
            rows++;

        return rows;
    }
}
