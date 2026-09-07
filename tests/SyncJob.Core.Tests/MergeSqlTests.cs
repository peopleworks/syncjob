using SyncJob.Core.Publication;

namespace SyncJob.Core.Tests;

/// <summary>
/// What the merge actually asks the server to do, asserted without a server.
/// <para>
/// Every one of these is a statement that would run without error and give a wrong answer:
/// a key column in the <c>SET</c> list, a bare <c>WHEN MATCHED</c> that rewrites rows that
/// did not change, a <c>WHEN NOT MATCHED BY SOURCE</c> that turns a page boundary into a
/// deletion. None of them would fail a smoke test.
/// </para>
/// </summary>
public sealed class MergeSqlTests
{
    private static readonly string[] Columns = ["Plant", "Line", "Code", "Descr", "Qty"];
    private static readonly string[] Keys = ["Plant", "Line", "Code"];

    private static string Merge() =>
        AppendMergeSql.Merge("dbo.Stage", "dbo.Part", Columns, Keys);

    [Fact]
    public void TheSetListLeavesOutTheKeyColumns()
    {
        var sql = Merge();
        var setList = sql[sql.IndexOf("THEN UPDATE SET", StringComparison.Ordinal)..];

        Assert.Contains("t.[Descr] = s.[Descr]", setList, StringComparison.Ordinal);
        Assert.Contains("t.[Qty] = s.[Qty]", setList, StringComparison.Ordinal);

        // Setting a key column to itself is harmless right up until the key is what the
        // ON clause matched on and something else is midway through changing it.
        Assert.DoesNotContain("t.[Plant] = s.[Plant]", setList, StringComparison.Ordinal);
        Assert.DoesNotContain("t.[Line] = s.[Line]", setList, StringComparison.Ordinal);
        Assert.DoesNotContain("t.[Code] = s.[Code]", setList, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyColumnsStillDecideTheMatch()
    {
        Assert.Contains("ON t.[Plant] = s.[Plant] AND t.[Line] = s.[Line] AND t.[Code] = s.[Code]", Merge(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The idempotency guard. Without it every matched row is rewritten whether or not
    /// anything about it changed, so a second run of an unchanged load moves every
    /// rowversion, fires every update trigger and logs the whole table.
    /// </summary>
    [Fact]
    public void AMatchedRowIsOnlyWrittenWhenSomethingAboutItDiffers()
    {
        var sql = Merge();

        Assert.Contains("WHEN MATCHED AND EXISTS (", sql, StringComparison.Ordinal);
        Assert.Contains("EXCEPT", sql, StringComparison.Ordinal);

        // The comparison covers exactly the columns that would be written, so a column that
        // can change and is not compared cannot exist.
        var guard = sql[sql.IndexOf("WHEN MATCHED AND EXISTS (", StringComparison.Ordinal)..sql.IndexOf("THEN UPDATE SET", StringComparison.Ordinal)];
        Assert.Contains("s.[Descr]", guard, StringComparison.Ordinal);
        Assert.Contains("s.[Qty]", guard, StringComparison.Ordinal);
        Assert.Contains("t.[Descr]", guard, StringComparison.Ordinal);
        Assert.Contains("t.[Qty]", guard, StringComparison.Ordinal);
    }

    /// <summary>
    /// A merge never deletes. Absence from a filtered read is evidence of a filter, and
    /// absence from a batched read is evidence of a page boundary - one batch would take
    /// out everything the other batches hold.
    /// </summary>
    [Fact]
    public void NothingIsEverDeletedForBeingAbsentFromTheStagedSet()
    {
        Assert.DoesNotContain("NOT MATCHED BY SOURCE", Merge(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThePageIsOrderedByTheKeySoBatchingIsDeterministic()
    {
        Assert.Contains("ORDER BY [Plant], [Line], [Code]", Merge(), StringComparison.Ordinal);
        Assert.Contains($"OFFSET {AppendMergeSql.OffsetParameter} ROWS FETCH NEXT {AppendMergeSql.BatchParameter} ROWS ONLY", Merge(), StringComparison.Ordinal);
    }

    [Fact]
    public void NeitherStatementEverSelectsEverything()
    {
        Assert.DoesNotContain("*", Merge(), StringComparison.Ordinal);
        Assert.DoesNotContain("*", AppendMergeSql.Append("dbo.Stage", "dbo.Part", Columns), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppendNamesItsColumnsOnBothSides()
    {
        Assert.Contains(
            "INSERT INTO dbo.Part ([Plant], [Line], [Code], [Descr], [Qty])",
            AppendMergeSql.Append("dbo.Stage", "dbo.Part", Columns),
            StringComparison.Ordinal);

        Assert.Contains(
            "SELECT s.[Plant], s.[Line], s.[Code], s.[Descr], s.[Qty]",
            AppendMergeSql.Append("dbo.Stage", "dbo.Part", Columns),
            StringComparison.Ordinal);
    }

    /// <summary>An empty SET list is a syntax error, not a merge that only inserts.</summary>
    [Fact]
    public void AKeyThatIsTheWholeRowProducesAMergeThatOnlyInserts()
    {
        var sql = AppendMergeSql.Merge("dbo.Stage", "dbo.Part", Keys, Keys);

        Assert.DoesNotContain("WHEN MATCHED", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN NOT MATCHED BY TARGET THEN INSERT", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AMergeWithNoKeyIsRefused() =>
        Assert.Throws<ArgumentException>(() => AppendMergeSql.Merge("dbo.Stage", "dbo.Part", Columns, []));

    /// <summary>
    /// The failure this replaces: the deployed loader throws away any configured size over
    /// ten thousand and substitutes a tenth of the total, so a job asking for batches of
    /// fifty thousand gets exactly ten batches of whatever the table happens to hold and
    /// nothing anywhere says so.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(500, 500)]
    [InlineData(10_000, 10_000)]
    [InlineData(25_000, 25_000)]
    [InlineData(1_000_000, 1_000_000)]
    public void AConfiguredBatchSizeIsUsedExactlyAsConfigured(int configured, int expected) =>
        Assert.Equal(expected, AppendMergeSql.ResolveBatchSize(configured));

    [Fact]
    public void OnlyAnUnsetBatchSizeGetsTheEngineDefault() =>
        Assert.Equal(AppendMergeSql.DefaultBatchSize, AppendMergeSql.ResolveBatchSize(0));

    [Fact]
    public void ABracketInsideAColumnNameIsEscapedRatherThanEndingTheIdentifier() =>
        Assert.Equal("[odd]]name]", AppendMergeSql.Quote("odd]name"));
}
