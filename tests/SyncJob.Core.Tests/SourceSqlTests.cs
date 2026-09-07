using SyncJob.Core;
using SyncJob.Core.Model;
using SyncJob.Core.Run;

namespace SyncJob.Core.Tests;

/// <summary>
/// The command a step sends its source, asserted without a server.
/// <para>
/// The failures here are all the same shape: a step that reads everything every night
/// while reporting success. An overlap ignored because the key is a rowversion, a
/// watermark that never reached an OPENQUERY string, a procedure called without the
/// parameter that bounds it - none of them is an error anywhere, and the row counts are
/// the only place they show.
/// </para>
/// </summary>
public sealed class SourceSqlTests
{
    private static readonly Watermark Reached = new("2026-01-15T10:00:00.0000000", null, DateTimeOffset.UtcNow);

    private static SyncStep Step(SourceQuery source, params FieldMap[] maps) => new()
    {
        Id = "step-1",
        Name = "Tparadas",
        DestinationTable = "dbo.Tparadas",
        Source = source,
        FieldMaps = [.. maps]
    };

    private static SyncStep Incremental(SourceQuery source, TimeSpan overlap = default, string? initial = null)
    {
        var step = Step(source, new FieldMap { Source = "FechaCambio", Target = "ChangedAt", IsSyncKey = true });
        step.Incremental = new IncrementalPlan
        {
            Watermark = new WatermarkPlan { Overlap = overlap, InitialValue = initial }
        };

        return step;
    }

    // ------------------------------------------------------------------------ the three shapes

    [Fact]
    public void ATableBecomesASelectOverIt()
    {
        var source = SourceSql.Build(Step(new SourceQuery { Table = "dbo.Customer" }), [], null);

        Assert.Equal("SELECT * FROM dbo.Customer", source.Sql);
        Assert.False(source.IsStoredProcedure);
        Assert.Empty(source.Parameters);
    }

    /// <summary>
    /// The name as the operator wrote it, for the reason AppendMergeColumns gives: that is
    /// how every job definition in the deployed systems spells one.
    /// </summary>
    [Fact]
    public void TheTableNameIsUsedAsTheOperatorWroteIt()
    {
        Assert.Equal(
            "SELECT * FROM [Otra Base].dbo.Customer",
            SourceSql.Build(Step(new SourceQuery { Table = "[Otra Base].dbo.Customer" }), [], null).Sql);
    }

    [Fact]
    public void AWhereOnATableIsAppendedToIt()
    {
        Assert.Equal(
            "SELECT * FROM dbo.Customer WHERE Active = 1",
            SourceSql.Build(Step(new SourceQuery { Table = "dbo.Customer", Where = "Active = 1" }), [], null).Sql);
    }

    /// <summary>Both spellings are in the deployed configurations and only one can be appended.</summary>
    [Fact]
    public void AWhereThatAlreadySaysWhereIsNotSaidTwice()
    {
        Assert.Equal(
            "SELECT * FROM dbo.Customer WHERE Active = 1",
            SourceSql.Build(Step(new SourceQuery { Table = "dbo.Customer", Where = " WHERE Active = 1" }), [], null).Sql);
    }

    [Fact]
    public void AQueryIsSentAsWrittenWithItsVariablesInIt()
    {
        var source = SourceSql.Build(
            Step(new SourceQuery { Sql = "SELECT * FROM t WHERE d > ${Last}" }),
            [new ResolvedVariable("Last", "5", false)],
            null);

        Assert.Equal("SELECT * FROM t WHERE d > 5", source.Sql);
        Assert.False(source.IsStoredProcedure);
    }

    [Fact]
    public void AProcedureIsSentByNameWithItsParametersSubstituted()
    {
        var step = Step(new SourceQuery
        {
            StoredProcedure = "dbo.ReadChanges",
            Parameters = { ["Plant"] = "${Plant}", ["@Since"] = "1900-01-01" }
        });

        var source = SourceSql.Build(step, [new ResolvedVariable("Plant", "F1", false)], null);

        Assert.Equal("dbo.ReadChanges", source.Sql);
        Assert.True(source.IsStoredProcedure);
        Assert.Equal("F1", source.Parameters["@Plant"]);
        Assert.Equal("1900-01-01", source.Parameters["@Since"]);
    }

    [Fact]
    public void AStepWithNoSourceAtAllIsRefused()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => SourceSql.Build(Step(new SourceQuery()), [], null));

        Assert.Contains("no source to read", exception.Message, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------------- the watermark

    [Fact]
    public void ATableStepReadsOnlyPastTheWatermark()
    {
        var source = SourceSql.Build(Incremental(new SourceQuery { Table = "dbo.Tparadas" }), [], Reached);

        Assert.Equal($"SELECT * FROM dbo.Tparadas WHERE [FechaCambio] > {SourceSql.WatermarkParameter}", source.Sql);
        Assert.Equal(Reached.Value, source.Parameters[SourceSql.WatermarkParameter]);
    }

    /// <summary>
    /// The predicate goes to the source, so it is spelled the way the source spells the
    /// column - the opposite of what the runner reads the new value back with.
    /// </summary>
    [Fact]
    public void ThePredicateNamesTheColumnTheWayTheSourceDoesAndTheWatermarkTheWayTheDestinationDoes()
    {
        var step = Incremental(new SourceQuery { Table = "dbo.Tparadas" });

        Assert.Contains("[FechaCambio] >", SourceSql.Build(step, [], Reached).Sql, StringComparison.Ordinal);
        Assert.Equal("ChangedAt", SourceSql.SyncKeyColumn(step));
    }

    [Fact]
    public void AStepThatKeepsNoWatermarkHasNoSyncKeyColumn()
    {
        Assert.Null(SourceSql.SyncKeyColumn(Step(new SourceQuery { Table = "dbo.Customer" })));
    }

    /// <summary>
    /// "A OR B" with a watermark appended by AND reads as "A OR (B AND watermark)", which
    /// quietly brings back every row A matches, every night.
    /// </summary>
    [Fact]
    public void TheOperatorsWhereIsParenthesisedBeforeTheWatermarkIsAddedToIt()
    {
        var step = Incremental(new SourceQuery { Table = "dbo.Tparadas", Where = "Plant = 1 OR Plant = 2" });

        Assert.Equal(
            "SELECT * FROM dbo.Tparadas WHERE (Plant = 1 OR Plant = 2) AND " +
            $"[FechaCambio] > {SourceSql.WatermarkParameter}",
            SourceSql.Build(step, [], Reached).Sql);
    }

    [Fact]
    public void AForcedFullReadDropsThePredicateAndNotTheWhere()
    {
        var step = Incremental(new SourceQuery { Table = "dbo.Tparadas", Where = "Active = 1" });
        step.Incremental!.ForceFullRead = true;

        var source = SourceSql.Build(step, [], Reached);

        Assert.Equal("SELECT * FROM dbo.Tparadas WHERE Active = 1", source.Sql);
        Assert.Empty(source.Parameters);
    }

    /// <summary>
    /// The value an operator sets so that a first run does not read the whole table is the
    /// one thing that makes a first run different from no watermark at all.
    /// </summary>
    [Fact]
    public void AFirstRunStartsAtThePlansInitialValue()
    {
        var step = Incremental(new SourceQuery { Table = "dbo.Tparadas" }, initial: "2026-01-01T00:00:00");

        Assert.Equal("2026-01-01T00:00:00", SourceSql.Build(step, [], null).Parameters[SourceSql.WatermarkParameter]);
    }

    [Fact]
    public void AFirstRunWithNoInitialValueReadsEverything()
    {
        var source = SourceSql.Build(Incremental(new SourceQuery { Table = "dbo.Tparadas" }), [], null);

        Assert.Equal("SELECT * FROM dbo.Tparadas", source.Sql);
        Assert.Empty(source.Parameters);
    }

    [Fact]
    public void AStepThatKeepsAWatermarkAndMarksNoSyncKeyIsRefused()
    {
        var step = Step(new SourceQuery { Table = "dbo.Tparadas" });
        step.Incremental = new IncrementalPlan { Watermark = new WatermarkPlan() };

        var exception = Assert.Throws<InvalidOperationException>(() => SourceSql.Build(step, [], Reached));

        Assert.Contains("no field map carries the sync key", exception.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------- the overlap

    [Fact]
    public void AnOverlapMovesTheBoundaryBack()
    {
        var step = Incremental(new SourceQuery { Table = "dbo.Tparadas" }, TimeSpan.FromMinutes(30));

        // Trimmed rather than padded to seven digits: the old datetime type refuses a
        // seven-digit fraction outright, all zeros included.
        Assert.Equal(
            "2026-01-15T09:30:00",
            SourceSql.Build(step, [], Reached).Parameters[SourceSql.WatermarkParameter]);
    }

    /// <summary>
    /// An overlap is a length of time and there is no length of time to take off a
    /// rowversion. Ignoring it would leave an operator believing rows are being re-read
    /// that never are.
    /// <para>
    /// The decimal is the one that has to be refused rather than parsed:
    /// <c>DateTime.TryParse("1.5")</c> is true and answers the fifth of January, so a
    /// numeric version key would otherwise be shifted to a date nobody chose.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("0x0000000000000BB8")]
    [InlineData("184023")]
    [InlineData("1.5")]
    public void AnOverlapOnAKeyThatIsNotADateIsRefused(string reached)
    {
        var step = Incremental(new SourceQuery { Table = "dbo.Tparadas" }, TimeSpan.FromMinutes(30));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SourceSql.Build(step, [], new Watermark(reached, null, DateTimeOffset.UtcNow)));

        Assert.Contains("not a date or a time", exception.Message, StringComparison.Ordinal);
        Assert.Contains(reached, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Backwards is the whole point of an overlap; forwards steps over rows nothing reads.</summary>
    [Fact]
    public void ANegativeOverlapIsRefused()
    {
        var step = Incremental(new SourceQuery { Table = "dbo.Tparadas" }, TimeSpan.FromMinutes(-30));

        var exception = Assert.Throws<InvalidOperationException>(() => SourceSql.Build(step, [], Reached));

        Assert.Contains("never read by any run", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// SQL Server refuses to compare a rowversion column with an nvarchar parameter
    /// outright, so the value goes as the bytes it is.
    /// </summary>
    [Fact]
    public void ARowversionWatermarkIsSentAsBytesRatherThanAsItsText()
    {
        var step = Incremental(new SourceQuery { Table = "dbo.Tparadas" });

        var value = SourceSql
            .Build(step, [], new Watermark("0x0000000000000BB8", null, DateTimeOffset.UtcNow))
            .Parameters[SourceSql.WatermarkParameter];

        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0x0B, 0xB8 }, Assert.IsType<byte[]>(value));
    }

    // ------------------------------------------------------- the watermark a query cannot take

    /// <summary>
    /// The one the deployed system solved with a variable, and the reason it had to: a
    /// predicate cannot be appended to an OPENQUERY string, and wrapping the query in a
    /// derived table moves every row across the link before filtering any of them.
    /// </summary>
    [Fact]
    public void AQueryThatKeepsAWatermarkAndNamesNoVariableIsRefusedWithAllThreeFixes()
    {
        var step = Incremental(new SourceQuery { Sql = "SELECT * FROM OPENQUERY([F1],'select * from paradas')" });

        var exception = Assert.Throws<InvalidOperationException>(() => SourceSql.Build(step, [], Reached));

        Assert.Contains("declare a variable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("table and a where", exception.Message, StringComparison.Ordinal);
        Assert.Contains("take the watermark plan off", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Declared and never named in the SQL is the same failure as not declared at all: the
    /// value is read and then goes nowhere.
    /// </summary>
    [Fact]
    public void AQueryWhoseVariableIsDeclaredButNeverNamedIsRefusedToo()
    {
        var step = Incremental(new SourceQuery { Sql = "SELECT * FROM paradas" });

        Assert.Throws<InvalidOperationException>(() =>
            SourceSql.Build(step, [new ResolvedVariable("LastSyncValue", "5", false)], Reached));
    }

    [Fact]
    public void AQueryThatNamesItsVariableCarriesTheWatermarkThroughIt()
    {
        var step = Incremental(new SourceQuery
        {
            Sql = "SELECT * FROM OPENQUERY([F1],'select * from f(''@LastSyncValue'')')"
        });
        step.VariableSyntax = VariableSyntax.Legacy;

        var source = SourceSql.Build(step, [new ResolvedVariable("LastSyncValue", "2026-01-15T10:00:00", false)], Reached);

        Assert.Equal("SELECT * FROM OPENQUERY([F1],'select * from f(''2026-01-15T10:00:00'')')", source.Sql);
    }

    /// <summary>
    /// The overlap belongs to the boundary this engine builds, and for a query it builds
    /// none. Accepting it would be a setting that does nothing.
    /// </summary>
    [Fact]
    public void AnOverlapOnAQueryStepIsRefused()
    {
        var step = Incremental(
            new SourceQuery { Sql = "SELECT * FROM t WHERE d > ${LastSyncValue}" }, TimeSpan.FromMinutes(30));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SourceSql.Build(step, [new ResolvedVariable("LastSyncValue", "5", false)], Reached));

        Assert.Contains("DATEADD", exception.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------- the watermark a procedure takes

    [Fact]
    public void AProcedureTakesTheWatermarkThroughTheParameterNamedAfterTheSyncKey()
    {
        var step = Incremental(new SourceQuery
        {
            StoredProcedure = "dbo.ReadChanges",
            Parameters = { ["FechaCambio"] = "1900-01-01" }
        });

        Assert.Equal(Reached.Value, SourceSql.Build(step, [], Reached).Parameters["@FechaCambio"]);
    }

    [Fact]
    public void AProcedureWithNoParameterForTheWatermarkIsRefused()
    {
        var step = Incremental(new SourceQuery
        {
            StoredProcedure = "dbo.ReadChanges",
            Parameters = { ["Plant"] = "F1" }
        });

        var exception = Assert.Throws<InvalidOperationException>(() => SourceSql.Build(step, [], Reached));

        Assert.Contains("@FechaCambio", exception.Message, StringComparison.Ordinal);
        Assert.Contains("name the sync key after the parameter", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// On a first run the operator's own value stands: it is what says where a step with
    /// no history yet starts.
    /// </summary>
    [Fact]
    public void AProcedureOnAFirstRunKeepsTheParameterTheOperatorConfigured()
    {
        var step = Incremental(new SourceQuery
        {
            StoredProcedure = "dbo.ReadChanges",
            Parameters = { ["FechaCambio"] = "1900-01-01" }
        });

        Assert.Equal("1900-01-01", SourceSql.Build(step, [], null).Parameters["@FechaCambio"]);
    }
}
