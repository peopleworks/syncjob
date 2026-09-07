using SyncJob.Core.Incremental;

namespace SyncJob.Core.Tests;

/// <summary>
/// The legacy tombstone key, split the way the ledger wrote it.
/// <para>
/// The deployed implementation splits it with <c>PARSENAME</c>, which was written for
/// taking apart <c>server.database.schema.object</c>: it counts from the right and gives up
/// past four parts. Asking it for parts one, two and three of a three-part key returns the
/// key's <b>last</b> three in the wrong order for two of them, and for anything longer it
/// returns nulls - the join then matches no row, nothing is deleted, and the run reports
/// success. These tests are the difference.
/// </para>
/// </summary>
public sealed class TombstoneKeyTests
{
    private const string Separator = ";";

    [Fact]
    public void TwoPartsSplitIntoTwo() =>
        Assert.Equal(["PLANT1", "SKU-9"], TombstoneKey.SplitLegacy("PLANT1;SKU-9", Separator));

    /// <summary>
    /// Three parts, in the order the source wrote them. This is the case the deployed
    /// implementation silently misses, and the order matters as much as the count: read
    /// from the right, the plant would end up in the column the SKU belongs in.
    /// </summary>
    [Fact]
    public void ThreePartsSplitIntoThreeInTheOrderTheyWereWritten()
    {
        var parts = TombstoneKey.SplitLegacy("PLANT1;LINE-A;SKU-9", Separator);

        Assert.Equal(3, parts.Count);
        Assert.Equal("PLANT1", parts[0]);
        Assert.Equal("LINE-A", parts[1]);
        Assert.Equal("SKU-9", parts[2]);
    }

    [Fact]
    public void FivePartsSplitIntoFive() =>
        Assert.Equal(["a", "b", "c", "d", "e"], TombstoneKey.SplitLegacy("a;b;c;d;e", Separator));

    /// <summary>Past the point where <c>PARSENAME</c> stops answering at all.</summary>
    [Fact]
    public void SevenPartsSplitIntoSeven() =>
        Assert.Equal(["a", "b", "c", "d", "e", "f", "g"], TombstoneKey.SplitLegacy("a;b;c;d;e;f;g", Separator));

    [Fact]
    public void AnEmptyPartStaysAPart() =>
        Assert.Equal(["a", "", "c"], TombstoneKey.SplitLegacy("a;;c", Separator));

    [Fact]
    public void AMultiCharacterSeparatorSplitsOnTheWholeThing() =>
        Assert.Equal(["a", "b", "c"], TombstoneKey.SplitLegacy("a||b||c", "||"));

    /// <summary>
    /// No separator means no split, which would turn every composite key into a single part
    /// that matches nothing - the silent miss again, by a different route.
    /// </summary>
    [Fact]
    public void AnEmptySeparatorIsRefused() =>
        Assert.Throws<ArgumentException>(() => TombstoneKey.SplitLegacy("a;b", string.Empty));

    [Fact]
    public void AKeyThatMatchesTheDeleteKeyIsRead()
    {
        var parts = TombstoneKey.TryReadLegacy(42, "PLANT1;LINE-A;SKU-9", Separator, expectedParts: 3, out var problem);

        Assert.Null(problem);
        Assert.Equal(["PLANT1", "LINE-A", "SKU-9"], parts);
    }

    [Theory]
    [InlineData("PLANT1;SKU-9", 3, 2)]
    [InlineData("a;b;c;d", 3, 4)]
    [InlineData("just-one", 3, 1)]
    public void APartCountThatDisagreesWithTheDeleteKeyIsReportedRatherThanUsed(
        string keyValue,
        int expectedParts,
        int actualParts)
    {
        var parts = TombstoneKey.TryReadLegacy(7, keyValue, Separator, expectedParts, out var problem);

        Assert.Null(parts);
        Assert.NotNull(problem);
        Assert.Equal(7, problem.LedgerId);
        Assert.Equal(actualParts, problem.Parts);
        Assert.Equal(expectedParts, problem.ExpectedParts);

        // The ledger id and the value it held, because the operator has to be able to go and
        // find the row: a report that says only "some keys were wrong" is not one.
        Assert.Contains(keyValue, problem.ToString(), StringComparison.Ordinal);
        Assert.Contains("7", problem.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A key part that legitimately contains the separator cannot be represented in this
    /// format at all - which is what <c>TombstoneKeyFormat.Columns</c> is for. What matters
    /// is that it is reported rather than deleting the wrong row or nothing at all.
    /// </summary>
    [Fact]
    public void AValueContainingTheSeparatorIsReportedRatherThanSilentlyMisread()
    {
        var parts = TombstoneKey.TryReadLegacy(11, "PLANT1;LINE;A;SKU-9", Separator, expectedParts: 3, out var problem);

        Assert.Null(parts);
        Assert.NotNull(problem);
        Assert.Equal(4, problem.Parts);
    }

    [Fact]
    public void ANullKeyValueIsReported()
    {
        var parts = TombstoneKey.TryReadLegacy(3, null, Separator, expectedParts: 3, out var problem);

        Assert.Null(parts);
        Assert.NotNull(problem);
        Assert.Equal(0, problem.Parts);
    }

    [Fact]
    public void TheExceptionCarriesEveryProblemAndNamesTheRows()
    {
        var problems = new List<TombstoneKeyProblem>
        {
            new(1, "a;b", 2, 3),
            new(2, "a;b;c;d", 4, 3)
        };

        var exception = new TombstoneKeyFormatException(problems);

        Assert.Equal(2, exception.Problems.Count);
        Assert.Contains("ledger row 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("ledger row 2", exception.Message, StringComparison.Ordinal);
    }
}
