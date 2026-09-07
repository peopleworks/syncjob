using SyncJob.Core.Model;
using SyncJob.Core.Run;

namespace SyncJob.Core.Tests;

/// <summary>
/// What a step's SQL says after its variables are put into it, asserted without a server.
/// <para>
/// Every one of these is a substitution that produces SQL which runs. That is what makes
/// them worth writing: the legacy syntax's prefix collision, a null that became an empty
/// string, a <c>${Name}</c> nobody declared - none of them raise an error anywhere. They
/// change which rows come back, and report success.
/// </para>
/// </summary>
public sealed class VariableResolverTests
{
    private static string Delimited(string sql, params ResolvedVariable[] values) =>
        VariableResolver.Substitute(sql, VariableSyntax.Delimited, values);

    private static string Legacy(string sql, params ResolvedVariable[] values) =>
        VariableResolver.Substitute(sql, VariableSyntax.Legacy, values);

    private static ResolvedVariable Value(string name, string? value = "42") => new(name, value, false);

    // -------------------------------------------------------------------- the delimited syntax

    [Fact]
    public void ADelimitedVariableIsReplacedWhereverItAppears()
    {
        Assert.Equal(
            "SELECT * FROM t WHERE a > 7 AND b > 7",
            Delimited("SELECT * FROM t WHERE a > ${Last} AND b > ${Last}", Value("Last", "7")));
    }

    /// <summary>
    /// The whole reason the delimited syntax exists: a name that is a prefix of another
    /// one is harmless, because the closing brace says where the name ends.
    /// </summary>
    [Fact]
    public void ANameThatIsAPrefixOfAnotherIsStillItsOwnVariable()
    {
        Assert.Equal(
            "WHERE a > 1 AND b > 2",
            Delimited("WHERE a > ${Last} AND b > ${LastSyncValue}", Value("Last", "1"), Value("LastSyncValue", "2")));
    }

    [Fact]
    public void TheNameIsMatchedWithoutRegardToCase()
    {
        Assert.Equal("x = 9", Delimited("x = ${lastsyncvalue}", Value("LastSyncValue", "9")));
    }

    /// <summary>
    /// Case-insensitive on the name and exact on the delimiters: <c>$ {Name}</c> and
    /// <c>${Name</c> are text an operator wrote, not a variable spelled loosely.
    /// </summary>
    [Theory]
    [InlineData("$ {Last}")]
    [InlineData("${Last")]
    [InlineData("{Last}")]
    public void OnlyTheExactDelimitersAreAVariable(string written)
    {
        Assert.Equal(written, Delimited(written, Value("Last", "1")));
    }

    /// <summary>
    /// Left alone because it may be an operator's literal, and reported because the same
    /// text is what a renamed variable leaves behind.
    /// </summary>
    [Fact]
    public void AVariableNobodyDeclaredSurvivesAndIsReported()
    {
        const string written = "SELECT * FROM t WHERE a > ${Last} AND b = '${Currency}'";
        var values = new[] { Value("Last", "1") };

        Assert.Equal(
            "SELECT * FROM t WHERE a > 1 AND b = '${Currency}'",
            VariableResolver.Substitute(written, VariableSyntax.Delimited, values));

        Assert.Equal(["Currency"], VariableResolver.UnresolvedNames(written, VariableSyntax.Delimited, values));
    }

    [Fact]
    public void EverythingTheSqlNamesAndTheStepDeclaresIsReportedAsResolved()
    {
        Assert.Empty(VariableResolver.UnresolvedNames(
            "a > ${Last} AND b > ${LastSyncValue}",
            VariableSyntax.Delimited,
            [Value("Last"), Value("LastSyncValue")]));
    }

    [Fact]
    public void TheSameUnresolvedNameIsReportedOnce()
    {
        Assert.Equal(
            ["Currency"],
            VariableResolver.UnresolvedNames("${Currency} ${currency}", VariableSyntax.Delimited, []));
    }

    // ------------------------------------------------------------------------ the legacy syntax

    /// <summary>
    /// The failure the deployed loader has: replacing @Last first turns @LastSyncValue
    /// into "1SyncValue", which is SQL that runs and rows that are wrong. Longest first is
    /// what makes the common case right.
    /// </summary>
    [Fact]
    public void LegacySubstitutionTakesTheLongestNameFirst()
    {
        var sql = Legacy("WHERE a > @Last AND b > @LastSyncValue", Value("Last", "1"), Value("LastSyncValue", "2"));

        Assert.Equal("WHERE a > 1 AND b > 2", sql);
        Assert.DoesNotContain("SyncValue", sql, StringComparison.Ordinal);
    }

    /// <summary>Declaration order is not the answer either way round.</summary>
    [Fact]
    public void LegacySubstitutionIsNotDecidedByDeclarationOrder()
    {
        Assert.Equal(
            "WHERE a > 1 AND b > 2",
            Legacy("WHERE a > @Last AND b > @LastSyncValue", Value("LastSyncValue", "2"), Value("Last", "1")));
    }

    /// <summary>
    /// The importer strips the leading @ off the catalog's name, so the legacy syntax puts
    /// it back - and only the form with the @ on it is a variable. The bare name in a
    /// select list is a column.
    /// </summary>
    [Fact]
    public void OnlyTheNameWithItsAtSignIsReplaced()
    {
        Assert.Equal(
            "SELECT LastSyncValue FROM t WHERE d > 5",
            Legacy("SELECT LastSyncValue FROM t WHERE d > @LastSyncValue", Value("LastSyncValue", "5")));
    }

    /// <summary>The deployed shape, substituted inside two string literals it knows nothing about.</summary>
    [Fact]
    public void TheOpenQueryShapeOfTheDeployedJobsSubstitutes()
    {
        Assert.Equal(
            "SELECT * FROM OPENQUERY([Factory1],'Select * from dbo.GetTparadasData(''2026-01-15T00:00:00'')')",
            Legacy(
                "SELECT * FROM OPENQUERY([Factory1],'Select * from dbo.GetTparadasData(''@LastSyncValue'')')",
                Value("LastSyncValue", "2026-01-15T00:00:00")));
    }

    [Fact]
    public void AnUndeclaredAtNameIsReportedForTheLegacySyntaxToo()
    {
        Assert.Equal(
            ["Currency"],
            VariableResolver.UnresolvedNames(
                "a > @LastSyncValue AND b = @Currency", VariableSyntax.Legacy, [Value("LastSyncValue")]));
    }

    /// <summary>@@ROWCOUNT belongs to the server and is nobody's unresolved variable.</summary>
    [Fact]
    public void TheServersOwnGlobalsAreNotReportedAsVariables()
    {
        Assert.Empty(VariableResolver.UnresolvedNames("SELECT @@ROWCOUNT", VariableSyntax.Legacy, []));
    }

    // ------------------------------------------------------------------------------ the values

    /// <summary>
    /// An empty string here would read as a row matching nothing; NULL reads as what it
    /// is, which is a variable that resolved to no value at all.
    /// </summary>
    [Fact]
    public void ANullValueBecomesTheSqlLiteralNull()
    {
        Assert.Equal("WHERE a > NULL", Delimited("WHERE a > ${Last}", Value("Last", null)));
        Assert.Equal("WHERE a > NULL", Legacy("WHERE a > @Last", Value("Last", null)));
    }

    /// <summary>
    /// How many times the quote would have to be doubled depends on how many string
    /// literals deep the site is, and the deployed jobs put their variables two deep. So
    /// the value is refused by name rather than pasted in at a depth guessed wrong.
    /// </summary>
    [Fact]
    public void AValueWithASingleQuoteInItIsRefusedByName()
    {
        var exception = Assert.Throws<VariableResolutionException>(() =>
            Delimited("WHERE name = '${Plant}'", Value("Plant", "O'Brien")));

        Assert.Equal("Plant", exception.VariableName);
        Assert.Contains("single quote", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing is substituted where nothing is named, in either syntax - and the legacy
    /// half is the one that could go wrong, because there the name is loose in the text
    /// and a column called after a variable is a column.
    /// </summary>
    [Fact]
    public void SqlThatNamesNoVariableComesBackUntouched()
    {
        Assert.Equal(
            "SELECT * FROM dbo.Customer",
            VariableResolver.Substitute("SELECT * FROM dbo.Customer", VariableSyntax.Delimited, []));

        Assert.Equal(
            "SELECT LastSyncValue FROM dbo.Customer",
            Legacy("SELECT LastSyncValue FROM dbo.Customer", Value("Last", "1")));
    }

    // ------------------------------------------------------------------------ the connections

    /// <summary>
    /// Both connection strings name a server that is not there, so every one of these says
    /// the same thing twice: the value was worked out without opening anything. A step with
    /// no variables must not cost two connections, and a variable that needs no query must
    /// not cost one either.
    /// </summary>
    private const string Nowhere =
        "Server=syncjob-no-such-host;Integrated Security=true;Encrypt=false;Connect Timeout=1";

    private static Task<IReadOnlyList<ResolvedVariable>> ResolveAsync(params SqlVariable[] variables) =>
        VariableResolver.ResolveAsync(
            new SyncStep { Id = "step-1", Name = "Tparadas", Variables = [.. variables] },
            Nowhere,
            Nowhere,
            CancellationToken.None);

    [Fact]
    public async Task AStepWithNoVariablesOpensNothing()
    {
        Assert.Empty(await ResolveAsync());
    }

    [Fact]
    public async Task AVariableWithNoSqlIsItsDefaultAndOpensNothing()
    {
        var value = Assert.Single(await ResolveAsync(new SqlVariable { Name = "Plant", DefaultValue = "F1" }));

        Assert.Equal("F1", value.Value);
        Assert.True(value.CameFromDefault);
    }

    [Fact]
    public async Task AVariableWithNeitherSqlNorDefaultIsRefusedByName()
    {
        var exception = await Assert.ThrowsAsync<VariableResolutionException>(() =>
            ResolveAsync(new SqlVariable { Name = "Plant" }));

        Assert.Equal("Plant", exception.VariableName);
    }

    /// <summary>A variable with no name has nothing in the SQL for its value to go into.</summary>
    [Fact]
    public async Task AVariableWithNoNameIsRefused()
    {
        var exception = await Assert.ThrowsAsync<VariableResolutionException>(() =>
            ResolveAsync(new SqlVariable { Sql = "SELECT 1" }));

        Assert.Contains("no name", exception.Message, StringComparison.Ordinal);
    }
}
