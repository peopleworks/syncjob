using SyncJob.Core.Copy;

namespace SyncJob.Core.Tests;

/// <summary>
/// What a copy decides before it opens a connection: which source column goes into
/// which destination column, and when it refuses to decide at all.
/// <para>
/// Every one of these is the same failure seen from a different side. The deployed
/// service path publishes with <c>SELECT *</c> and no column list, so a column added to
/// one of the two tables shifts every value after it into its neighbour, and SQL Server
/// reports success as long as the types still fit. Anything that answers "the names do
/// not match, use position instead" rebuilds that failure. So the assertion these tests
/// really make is that nothing is ever matched by position - a name missing from either
/// side stops the copy and says which name.
/// </para>
/// </summary>
public sealed class CopyRequestTests
{
    private static readonly Dictionary<string, string> NoMap = new();

    private static DestinationColumn Plain(string name) => new(name, IsComputed: false, IsIdentity: false);

    private static IReadOnlyList<ColumnMatch> Match(
        string[] source,
        DestinationColumn[] destination,
        Dictionary<string, string>? map = null,
        bool keepIdentity = true) =>
        ColumnMatcher.Match(source, destination, map ?? NoMap, keepIdentity, "dbo.Customer");

    [Fact]
    public void ColumnsWithTheSameNamesOnBothSides_Match()
    {
        var matches = Match(
            ["Id", "Name", "Amount"],
            [Plain("Id"), Plain("Name"), Plain("Amount")]);

        Assert.Equal(
            [("Id", "Id"), ("Name", "Name"), ("Amount", "Amount")],
            matches.Select(x => (x.SourceColumn, x.DestinationColumn)));
    }

    /// <summary>
    /// The case the whole package exists for. Positions disagree on every column, names
    /// agree on every column, and the answer is the names - which is also why the
    /// matches come back in the destination's order with the source ordinal each one
    /// came from.
    /// </summary>
    [Fact]
    public void ColumnsInADifferentOrder_MatchByNameAndNotByPosition()
    {
        var matches = Match(
            ["Amount", "Id", "Name"],
            [Plain("Id"), Plain("Name"), Plain("Amount")]);

        Assert.Equal(
            [("Id", 1), ("Name", 2), ("Amount", 0)],
            matches.Select(x => (x.DestinationColumn, x.SourceOrdinal)));
    }

    [Fact]
    public void ASourceColumnTheDestinationDoesNotHave_IsRefusedByName()
    {
        var error = Assert.Throws<ColumnMatchException>(() => Match(
            ["Id", "Name", "Discount"],
            [Plain("Id"), Plain("Name")]));

        Assert.Contains("'Discount'", error.Message, StringComparison.Ordinal);
        Assert.Contains("dbo.Customer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADestinationColumnTheSourceDoesNotHave_IsRefusedByName()
    {
        var error = Assert.Throws<ColumnMatchException>(() => Match(
            ["Id", "Name"],
            [Plain("Id"), Plain("Name"), Plain("Region")]));

        Assert.Contains("'Region'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both directions in one run. An operator who has to fix two columns should learn
    /// that on the first attempt, not on the second.
    /// </summary>
    [Fact]
    public void EveryMismatch_IsReportedAtOnce()
    {
        var error = Assert.Throws<ColumnMatchException>(() => Match(
            ["Id", "Discount"],
            [Plain("Id"), Plain("Region")]));

        Assert.Equal(2, error.Problems.Count);
        Assert.Contains(error.Problems, x => x.Contains("'Discount'", StringComparison.Ordinal));
        Assert.Contains(error.Problems, x => x.Contains("'Region'", StringComparison.Ordinal));
    }

    [Fact]
    public void AColumnMap_RenamesTheColumnItNamesAndLeavesTheRestMatchingByName()
    {
        var matches = Match(
            ["Id", "Nombre", "Amount"],
            [Plain("Id"), Plain("Name"), Plain("Amount")],
            map: new Dictionary<string, string> { ["Nombre"] = "Name" });

        Assert.Equal(
            [("Id", "Id"), ("Nombre", "Name"), ("Amount", "Amount")],
            matches.Select(x => (x.SourceColumn, x.DestinationColumn)));
    }

    [Fact]
    public void AColumnMapNamingASourceColumnThatIsNotThere_IsRefusedByName()
    {
        var error = Assert.Throws<ColumnMatchException>(() => Match(
            ["Id", "Name"],
            [Plain("Id"), Plain("Name")],
            map: new Dictionary<string, string> { ["Nombre"] = "Name" }));

        Assert.Contains("'Nombre'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AColumnMapNamingADestinationColumnThatIsNotThere_IsRefusedByName()
    {
        var error = Assert.Throws<ColumnMatchException>(() => Match(
            ["Id", "Name"],
            [Plain("Id"), Plain("Name")],
            map: new Dictionary<string, string> { ["Name"] = "FullName" }));

        Assert.Contains("'FullName'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoSourceColumnsAimedAtOneDestinationColumn_AreRefused()
    {
        var error = Assert.Throws<ColumnMatchException>(() => Match(
            ["Id", "Name", "Nombre"],
            [Plain("Id"), Plain("Name")],
            map: new Dictionary<string, string> { ["Nombre"] = "Name" }));

        Assert.Contains(error.Problems, x =>
            x.Contains("'Name'", StringComparison.Ordinal) && x.Contains("'Nombre'", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>SELECT Price * Quantity FROM ...</c> comes back with an empty column name, and
    /// a column with no name can only ever be matched by position.
    /// </summary>
    [Fact]
    public void AnExpressionWithNoAlias_IsRefused()
    {
        var error = Assert.Throws<ColumnMatchException>(() => Match(
            ["Id", "", "Amount"],
            [Plain("Id"), Plain("Name"), Plain("Amount")]));

        Assert.Contains(error.Problems, x => x.Contains("column 2 of the source has no name", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoSourceColumnsWithTheSameName_AreRefusedAsAmbiguous()
    {
        var error = Assert.Throws<ColumnMatchException>(() => Match(
            ["Id", "Name", "name"],
            [Plain("Id"), Plain("Name")]));

        Assert.Contains(error.Problems, x => x.Contains("ambiguous", StringComparison.Ordinal));
    }

    /// <summary>
    /// A job whose SQL spells a column differently from the catalog is a job that works;
    /// the destination's own spelling is what gets written.
    /// </summary>
    [Fact]
    public void NamesThatDifferOnlyInCase_Match()
    {
        var matches = Match(["id", "NAME"], [Plain("Id"), Plain("Name")]);

        Assert.Equal(["Id", "Name"], matches.Select(x => x.DestinationColumn));
    }

    [Fact]
    public void AComputedDestinationColumn_NeedsNoSourceColumn()
    {
        var matches = Match(
            ["Price", "Quantity"],
            [Plain("Price"), Plain("Quantity"), new DestinationColumn("Total", IsComputed: true, IsIdentity: false)]);

        Assert.Equal(["Price", "Quantity"], matches.Select(x => x.DestinationColumn));
    }

    [Fact]
    public void ASourceColumnAimedAtAComputedDestinationColumn_IsRefusedByName()
    {
        var error = Assert.Throws<ColumnMatchException>(() => Match(
            ["Price", "Quantity", "Total"],
            [Plain("Price"), Plain("Quantity"), new DestinationColumn("Total", IsComputed: true, IsIdentity: false)]));

        Assert.Contains(error.Problems, x => x.Contains("'Total'", StringComparison.Ordinal));
    }

    /// <summary>
    /// The two halves of <c>KeepIdentity</c>. When the destination keeps the source's
    /// identity values it has to be given them; when it generates its own, a source that
    /// does not supply them is what is expected rather than a mistake.
    /// </summary>
    [Fact]
    public void AnIdentityColumnWithNoSourceColumn_IsRefusedOnlyWhenTheSourcesIdentityIsKept()
    {
        var identity = new[] { new DestinationColumn("Id", IsComputed: false, IsIdentity: true), Plain("Name") };

        var error = Assert.Throws<ColumnMatchException>(() => Match(["Name"], identity, keepIdentity: true));
        Assert.Contains("'Id'", error.Message, StringComparison.Ordinal);

        var matches = Match(["Name"], identity, keepIdentity: false);
        Assert.Equal(["Name"], matches.Select(x => x.DestinationColumn));
    }
}
