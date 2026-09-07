using SyncJob.Core;
using SyncJob.Core.Copy;

namespace SyncJob.IntegrationTests;

/// <summary>
/// The copy engine against a real server, because every one of the failures it exists
/// to prevent is a failure that only a real server shows: a type the driver will not
/// carry, a column order nobody thought about, a million rows that fit on disk and not
/// in memory.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CopyLiveTests(SqlServerFixture fixture)
{
    /// <summary>
    /// The ceiling the million-row copy's live managed heap may rise by.
    /// <para>
    /// 64 MB, chosen against what it is testing rather than against what this copy
    /// happens to use. Both ends were measured on the same million rows of the seeded
    /// shape. Streaming the reader into <c>SqlBulkCopy</c> survives no collection at
    /// all: live heap growth is 0.0 MB, because nothing is kept. Reading the same rows
    /// into a <c>List&lt;object[]&gt;</c> first - which is what all three of the copy
    /// paths this engine replaces do - holds 182.7 MB, and 497.2 MB once it has also
    /// been poured into the <c>DataTable</c> the writer wants; all of it stays live
    /// until the copy ends.
    /// </para>
    /// <para>
    /// So 64 MB is not a guess at this copy's appetite. It is a line drawn in the gap:
    /// wide enough above zero that a different machine, a server GC or a chattier
    /// driver cannot cross it, and low enough that the cheapest possible accumulating
    /// implementation misses it by a factor of three.
    /// </para>
    /// </summary>
    private const long HeapBudgetBytes = 64L * 1024 * 1024;

    private const int MillionRows = 1_000_000;

    /// <summary>
    /// The heart of it: a table larger than a comfortable memory budget copies, and the
    /// heap does not grow with the row count.
    /// <para>
    /// This is the test the three implementations being replaced cannot pass at any
    /// batch size, because the batch size governs what they write and never what they
    /// hold.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task AMillionRows_CopyWithoutTheHeapGrowingWithThem()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await SqlServerFixture.SeedAsync(source, "dbo.Big", MillionRows);
        await SqlServerFixture.SeedAsync(destination, "dbo.Big", rows: 0);

        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var peak = baseline;
        var nextSample = 200_000L;

        // Sampled from the progress callback, which is the only place in a streaming
        // copy where the caller gets a look in. A full collection each time, so what is
        // measured is what survives rather than what has not been swept up yet.
        var recorder = new Recorder(x =>
        {
            if(x.RowsCopied < nextSample)
                return;

            nextSample += 200_000;
            peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true));
        });

        var copier = new SqlTableCopier();
        var result = await copier.CopyAsync(
            Request(source, destination, "SELECT Id, Name, Amount, ChangedAt FROM dbo.Big", recorder),
            "dbo.Big",
            CancellationToken.None);

        Assert.Equal(MillionRows, result.RowsCopied);
        Assert.Equal(MillionRows, await SqlServerFixture.CountAsync(destination, "dbo.Big"));

        var growth = peak - baseline;
        Assert.True(
            growth < HeapBudgetBytes,
            $"the copy held {growth / 1024 / 1024:N0} MB of live heap, over the {HeapBudgetBytes / 1024 / 1024:N0} MB " +
            "budget: something is accumulating rows instead of streaming them");

        // Progress that arrives once, at the end, is not progress.
        Assert.True(
            recorder.Reports.Count > 1,
            $"a copy of {MillionRows:N0} rows reported progress {recorder.Reports.Count} time(s)");

        Assert.Equal(MillionRows, recorder.Reports[^1].RowsCopied);
    }

    /// <summary>
    /// Names decide, positions do not. The two tables hold the same columns in opposite
    /// orders, and the values arrive where their names say they should.
    /// <para>
    /// Written the other way round - matched by position, which is what the deployed
    /// service path falls back to - this copy would put the name in the amount and the
    /// amount in the name, and SQL Server would report success.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task ColumnsInADifferentOrderOnTheTwoSides_ArriveWhereTheirNamesSay()
    {
        var (source, destination) = await fixture.CreatePairAsync();

        await SqlServerFixture.ExecuteAsync(source,
            "CREATE TABLE dbo.Ledger (Id int NOT NULL, Note nvarchar(50) NULL, Payload varbinary(max) NULL);");
        await SqlServerFixture.ExecuteAsync(destination,
            "CREATE TABLE dbo.Ledger (Payload varbinary(max) NULL, Note nvarchar(50) NULL, Id int NOT NULL);");
        await SqlServerFixture.ExecuteAsync(source,
            "INSERT INTO dbo.Ledger VALUES (7, N'seven', 0x0A0B0C), (8, N'eight', NULL);");

        var copier = new SqlTableCopier();
        var result = await copier.CopyAsync(
            Request(source, destination, "SELECT Id, Note, Payload FROM dbo.Ledger"),
            "dbo.Ledger",
            CancellationToken.None);

        Assert.Equal(2, result.RowsCopied);
        Assert.Equal("seven", await SqlServerFixture.ScalarAsync(destination, "SELECT Note FROM dbo.Ledger WHERE Id = 7;"));
        Assert.Equal(
            1,
            await SqlServerFixture.ScalarAsync(
                destination, "SELECT COUNT(*) FROM dbo.Ledger WHERE Id = 7 AND Payload = 0x0A0B0C;"));
        Assert.Equal(
            1,
            await SqlServerFixture.ScalarAsync(
                destination, "SELECT COUNT(*) FROM dbo.Ledger WHERE Id = 8 AND Payload IS NULL;"));
    }

    /// <summary>
    /// The types that usually earn themselves a special case somewhere in a copy engine,
    /// carried by the same bulk path as an int and compared back value by value.
    /// </summary>
    [LiveFact]
    public async Task TheAwkwardTypes_TravelThroughTheSameBulkPath()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        var sourceDatabase = (string)(await SqlServerFixture.ScalarAsync(source, "SELECT DB_NAME();"))!;

        await SqlServerFixture.ExecuteAsync(source, """
            CREATE TABLE dbo.Awkward (
                Id       int              NOT NULL,
                Doc      xml                  NULL,
                Anything sql_variant          NULL,
                Stamp    datetimeoffset(7)    NULL,
                Blob     varbinary(max)       NULL,
                Note     nvarchar(max)        NULL,
                [Order]  int                  NULL,
                Version  rowversion       NOT NULL);
            """);

        // The destination takes the source's rowversion into binary(8): a rowversion
        // column of its own would be its own value, not the one that travelled.
        await SqlServerFixture.ExecuteAsync(destination, """
            CREATE TABLE dbo.Awkward (
                Id       int              NOT NULL,
                Doc      xml                  NULL,
                Anything sql_variant          NULL,
                Stamp    datetimeoffset(7)    NULL,
                Blob     varbinary(max)       NULL,
                Note     nvarchar(max)        NULL,
                [Order]  int                  NULL,
                Version  binary(8)            NULL);
            """);

        await SqlServerFixture.ExecuteAsync(source, """
            INSERT INTO dbo.Awkward (Id, Doc, Anything, Stamp, Blob, Note, [Order])
            VALUES (
                1,
                CAST(N'<invoice id="7"><line sku="abc"/></invoice>' AS xml),
                CAST(N'a variant that is a string' AS sql_variant),
                TODATETIMEOFFSET(CAST('2026-02-03T04:05:06.1234567' AS datetime2(7)), '+05:30'),
                CAST(REPLICATE(CAST('AB' AS varchar(max)), 1000000) AS varbinary(max)),
                REPLICATE(CAST(N'n' AS nvarchar(max)), 1000000),
                42);
            """);

        var copier = new SqlTableCopier();
        var result = await copier.CopyAsync(
            Request(source, destination, "SELECT * FROM dbo.Awkward"),
            "dbo.Awkward",
            CancellationToken.None);

        Assert.Equal(1, result.RowsCopied);

        await AssertSurvived(destination, sourceDatabase, "dbo.Awkward",
            ("xml", "CAST(d.Doc AS nvarchar(max)) = CAST(s.Doc AS nvarchar(max))"),
            ("sql_variant", "d.Anything = s.Anything"),
            ("sql_variant base type",
                "SQL_VARIANT_PROPERTY(d.Anything, 'BaseType') = SQL_VARIANT_PROPERTY(s.Anything, 'BaseType')"),
            ("datetimeoffset", "d.Stamp = s.Stamp AND DATEPART(tzoffset, d.Stamp) = DATEPART(tzoffset, s.Stamp)"),
            ("varbinary(max)", "DATALENGTH(d.Blob) = 2000000 AND d.Blob = s.Blob"),
            ("nvarchar(max)", "DATALENGTH(d.Note) = 2000000 AND d.Note = s.Note"),
            ("a reserved word for a name", "d.[Order] = s.[Order]"),
            ("rowversion", "d.Version = CAST(s.Version AS binary(8))"));
    }

    /// <summary>
    /// Spatial columns, on a machine with no <c>Microsoft.SqlServer.Types</c> anywhere
    /// near it - which is every machine that has not deliberately installed it.
    /// <para>
    /// Handed the reader raw, this copy dies inside the driver with
    /// <c>FileNotFoundException: Microsoft.SqlServer.Types</c>, because rebuilding the
    /// CLR object is the only way it knows to read the column. The server only ever
    /// wanted the bytes back.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task SpatialColumns_TravelWithoutTheSpatialAssembly()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        var sourceDatabase = (string)(await SqlServerFixture.ScalarAsync(source, "SELECT DB_NAME();"))!;

        const string table = "CREATE TABLE dbo.Places (Id int NOT NULL, Place geography NULL, Shape geometry NULL);";
        await SqlServerFixture.ExecuteAsync(source, table);
        await SqlServerFixture.ExecuteAsync(destination, table);
        await SqlServerFixture.ExecuteAsync(source, """
            INSERT INTO dbo.Places VALUES
                (1, geography::Point(40.4168, -3.7038, 4326), geometry::STGeomFromText('LINESTRING(0 0, 3 4)', 0)),
                (2, NULL, NULL);
            """);

        var copier = new SqlTableCopier();
        var result = await copier.CopyAsync(
            Request(source, destination, "SELECT Id, Place, Shape FROM dbo.Places"),
            "dbo.Places",
            CancellationToken.None);

        Assert.Equal(2, result.RowsCopied);

        await AssertSurvived(destination, sourceDatabase, "dbo.Places",
            ("geography", "(d.Place IS NULL AND s.Place IS NULL) OR d.Place.STEquals(s.Place) = 1"),
            ("geometry", "(d.Shape IS NULL AND s.Shape IS NULL) OR d.Shape.STEquals(s.Shape) = 1"),
            ("geography SRID", "(d.Place IS NULL AND s.Place IS NULL) OR d.Place.STSrid = s.Place.STSrid"));
    }

    /// <summary>
    /// A stored procedure is a source like any other, parameters and all.
    /// </summary>
    [LiveFact]
    public async Task AStoredProcedureSource_CopiesLikeAQuery()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await SqlServerFixture.SeedAsync(source, "dbo.Customer", rows: 500);
        await SqlServerFixture.SeedAsync(destination, "dbo.Customer", rows: 0);
        await SqlServerFixture.ExecuteAsync(source, """
            CREATE PROCEDURE dbo.ReadCustomers @Above int AS
            SELECT Id, Name, Amount, ChangedAt FROM dbo.Customer WHERE Id > @Above;
            """);

        var copier = new SqlTableCopier();
        var result = await copier.CopyAsync(
            new CopyRequest
            {
                SourceConnectionString = source,
                DestinationConnectionString = destination,
                Sql = "dbo.ReadCustomers",
                IsStoredProcedure = true,
                Parameters = { ["Above"] = 400 }
            },
            "dbo.Customer",
            CancellationToken.None);

        Assert.Equal(100, result.RowsCopied);
        Assert.Equal(100, await SqlServerFixture.CountAsync(destination, "dbo.Customer"));
    }

    /// <summary>
    /// A column the destination has never heard of stops the copy before a single row
    /// moves, and the message says which column.
    /// </summary>
    [LiveFact]
    public async Task AColumnTheDestinationDoesNotHave_StopsTheCopyWithTheColumnsName()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await SqlServerFixture.ExecuteAsync(source,
            "CREATE TABLE dbo.Customer (Id int NOT NULL, Name nvarchar(50) NULL, Discount decimal(9,2) NULL);");
        await SqlServerFixture.ExecuteAsync(destination,
            "CREATE TABLE dbo.Customer (Id int NOT NULL, Name nvarchar(50) NULL);");
        await SqlServerFixture.ExecuteAsync(source, "INSERT INTO dbo.Customer VALUES (1, N'one', 9.99);");

        var copier = new SqlTableCopier();
        var error = await Assert.ThrowsAsync<ColumnMatchException>(() => copier.CopyAsync(
            Request(source, destination, "SELECT * FROM dbo.Customer"),
            "dbo.Customer",
            CancellationToken.None));

        Assert.Contains("'Discount'", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await SqlServerFixture.CountAsync(destination, "dbo.Customer"));
    }

    /// <summary>
    /// A cancelled copy stops, and says how much of the destination it had already
    /// written - which is the number the caller needs to decide what to clean up.
    /// </summary>
    [LiveFact]
    public async Task ACancelledCopy_StopsAndSaysWhatItWrote()
    {
        const int rows = 400_000;

        var (source, destination) = await fixture.CreatePairAsync();
        await SqlServerFixture.SeedAsync(source, "dbo.Big", rows);
        await SqlServerFixture.SeedAsync(destination, "dbo.Big", rows: 0);

        using var cancellation = new CancellationTokenSource();
        var recorder = new Recorder(_ => cancellation.Cancel());

        var copier = new SqlTableCopier();

        // Batched, so that some rows are committed before the cancellation lands and the
        // count the exception carries is a count of rows that are really there.
        var request = new CopyRequest
        {
            SourceConnectionString = source,
            DestinationConnectionString = destination,
            Sql = "SELECT Id, Name, Amount, ChangedAt FROM dbo.Big",
            Progress = recorder,
            BatchSize = 10_000
        };

        var error = await Assert.ThrowsAsync<CopyCanceledException>(() => copier.CopyAsync(
            request, "dbo.Big", cancellation.Token));

        Assert.Equal("dbo.Big", error.TargetTable);
        Assert.True(
            error.RowsWritten < rows,
            $"the copy reported {error.RowsWritten:N0} rows written of {rows:N0}, so it did not actually stop");

        var landed = await SqlServerFixture.CountAsync(destination, "dbo.Big");
        Assert.True(landed < rows, $"{landed:N0} rows of {rows:N0} reached the destination after a cancellation");
    }

    /// <summary>
    /// Even a copy too small to trigger a single notification reports once, so that a
    /// caller watching progress always ends on a real number.
    /// </summary>
    [LiveFact]
    public async Task ACopyTooSmallToNotify_StillReportsItsTotalOnTheWayOut()
    {
        var (source, destination) = await fixture.CreatePairAsync();
        await SqlServerFixture.SeedAsync(source, "dbo.Customer", rows: 10);
        await SqlServerFixture.SeedAsync(destination, "dbo.Customer", rows: 0);

        var recorder = new Recorder();
        var copier = new SqlTableCopier();

        await copier.CopyAsync(
            Request(source, destination, "SELECT Id, Name, Amount, ChangedAt FROM dbo.Customer", recorder),
            "dbo.Customer",
            CancellationToken.None);

        Assert.Equal(10, Assert.Single(recorder.Reports).RowsCopied);
    }

    private static CopyRequest Request(
        string source,
        string destination,
        string sql,
        IProgress<CopyProgress>? progress = null) =>
        new()
        {
            SourceConnectionString = source,
            DestinationConnectionString = destination,
            Sql = sql,
            Progress = progress
        };

    /// <summary>
    /// Compares the copy against its source across the two databases, column by column,
    /// so a failure names the type that did not survive rather than a row count.
    /// </summary>
    private static async Task AssertSurvived(
        string destination,
        string sourceDatabase,
        string table,
        params (string What, string Predicate)[] checks)
    {
        foreach(var (what, predicate) in checks)
        {
            var matching = await SqlServerFixture.ScalarAsync(destination, $"""
                SELECT COUNT(*)
                FROM {table} AS d
                JOIN [{sourceDatabase}].{table} AS s ON s.Id = d.Id
                WHERE {predicate};
                """);

            var total = await SqlServerFixture.CountAsync(destination, table);
            Assert.True(
                Convert.ToInt32(matching) == total,
                $"{what} did not survive the copy: {matching} of {total} rows still match the source");
        }
    }

    /// <summary>
    /// Collects what a copy reported, on the thread that reported it.
    /// <para>
    /// Deliberately not <see cref="Progress{T}"/>: that one hands the callback to the
    /// thread pool, so a test asserting on how many reports arrived would be asserting
    /// on a race.
    /// </para>
    /// </summary>
    private sealed class Recorder(Action<CopyProgress>? onReport = null) : IProgress<CopyProgress>
    {
        private readonly List<CopyProgress> _reports = [];

        public IReadOnlyList<CopyProgress> Reports
        {
            get
            {
                lock(_reports)
                    return _reports.ToList();
            }
        }

        public void Report(CopyProgress value)
        {
            lock(_reports)
                _reports.Add(value);

            onReport?.Invoke(value);
        }
    }
}
