using System.Globalization;
using System.Text;
using SyncJob.Core.Model;

namespace SyncJob.Core.Run;

/// <summary>
/// What a step actually sends to its source, once everything is resolved.
/// <para>
/// <see cref="Sql"/> holds the procedure's name when <see cref="IsStoredProcedure"/> is
/// true, because that is the shape <c>CopyRequest</c> takes: one string and a flag that
/// says how the command reads it.
/// </para>
/// </summary>
public sealed record ResolvedSource(
    string Sql,
    bool IsStoredProcedure,
    IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// Turns a step's <see cref="SourceQuery"/> into the one command it sends: variables
/// substituted, and the watermark applied in whichever of the three ways the shape of
/// the source allows.
/// <para>
/// <c>Sql</c>, <c>StoredProcedure</c> and <c>Table</c> plus <c>Where</c> are three
/// shapes that until now no code turned into a command, so a step's source was a
/// description of an intention rather than something anything could run.
/// </para>
/// </summary>
public static class SourceSql
{
    /// <summary>
    /// The parameter a built watermark predicate binds to. Named unlike anything an
    /// operator writes, because it shares a namespace with whatever their own
    /// <c>Where</c> already declares.
    /// </summary>
    public const string WatermarkParameter = "@__syncjob_watermark";

    /// <summary>
    /// The variable the engine fills in with the step's boundary, so that SQL the
    /// operator wrote can name it: <c>${Watermark}</c>, or <c>@Watermark</c> under the
    /// legacy syntax. Reserved - a step may not declare one of its own by this name, and
    /// <c>JobValidator</c> refuses it.
    /// </summary>
    public const string ReservedWatermarkName = "Watermark";

    /// <summary>
    /// The SELECT - or the procedure name - a step reads, with its variables substituted
    /// and its watermark applied. <paramref name="watermark"/> is null on a first run or
    /// a full read.
    /// <para>
    /// One source wins in the order procedure, query, table, so a step carrying two of
    /// them behaves the same way twice rather than differently on two machines. Two is a
    /// validation error and this is not where it is caught.
    /// </para>
    /// </summary>
    public static ResolvedSource Build(
        SyncStep step, IReadOnlyList<ResolvedVariable> variables, Watermark? watermark)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(variables);

        // ForceFullRead is the operator saying "read everything this once", so it takes
        // the whole watermark question off the table rather than only the predicate.
        var plan = step.Incremental is { ForceFullRead: false } incremental ? incremental.Watermark : null;
        var boundary = Boundary(plan, watermark);

        if(plan is not null)
            variables = WithWatermark(step, variables, boundary);

        if(!string.IsNullOrWhiteSpace(step.Source.StoredProcedure))
            return FromProcedure(step, variables, plan, boundary);

        if(!string.IsNullOrWhiteSpace(step.Source.Sql))
            return FromQuery(step, variables, plan);

        if(!string.IsNullOrWhiteSpace(step.Source.Table))
            return FromTable(step, variables, plan, boundary);

        throw new InvalidOperationException(
            $"the step '{Describe(step)}' has no source to read: give it a query, a stored procedure or a table");
    }

    /// <summary>
    /// The variables the step declared, plus the one the engine supplies.
    /// <para>
    /// A query the operator wrote cannot have a predicate appended to it - see
    /// <see cref="FromQuery"/> - so the boundary has to reach it through a variable. It
    /// could be one the operator declares, reading the watermark table by hand, and the
    /// deployed system does exactly that. This is the same thing without the round trip
    /// and without the operator having to write the job's own id into a string literal:
    /// the runner has already read the watermark by the time this is called, so
    /// <c>${Watermark}</c> is simply that value.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ResolvedVariable> WithWatermark(
        SyncStep step, IReadOnlyList<ResolvedVariable> variables, string? boundary)
    {
        if(boundary is null)
        {
            // Substituting NULL would turn `col > ${Watermark}` into a predicate that
            // matches nothing at all, so a first run would read zero rows and report
            // success - which is the silent-empty-load failure seen from a new angle.
            // WatermarkPlan.InitialValue exists precisely to say where a first run
            // starts.
            if(VariableResolver.Mentions(step.Source.Sql ?? string.Empty, step.VariableSyntax, [Reserved(null)]))
            {
                throw new InvalidOperationException(
                    $"the step '{Describe(step)}' names {ReservedWatermarkName} in its SQL and has no watermark " +
                    "yet, and its watermark plan sets no InitialValue. There is nothing to compare against, and " +
                    "substituting NULL would make the predicate match no rows at all and call it a successful " +
                    "run. Give the plan an InitialValue old enough to mean everything.");
            }

            return variables;
        }

        return [Reserved(boundary), .. variables];
    }

    private static ResolvedVariable Reserved(string? boundary) =>
        new(ReservedWatermarkName, boundary, CameFromDefault: false);

    /// <summary>
    /// The column whose maximum is the step's next watermark, or null when the step
    /// keeps none.
    /// <para>
    /// Spelled the way the <b>destination</b> spells it - <c>Target</c> before
    /// <c>Source</c> - because the runner reads the new value back out of the staging
    /// table, and staging is created in the shape of the destination. That is the
    /// opposite spelling from the predicate <see cref="Build"/> applies, which is sent to
    /// the source and therefore uses <c>FieldMap.Source</c>. The two are easy to confuse
    /// and a column named differently on the two sides is exactly where confusing them
    /// stops being harmless.
    /// </para>
    /// </summary>
    public static string? SyncKeyColumn(SyncStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var map = step.FieldMaps.FirstOrDefault(x => x.IsSyncKey);

        return map is null ? null : Blank(map.Target) ?? Blank(map.Source);
    }

    /// <summary>
    /// A stored procedure takes its watermark as a parameter, which is the one of the
    /// three shapes where no SQL has to be assembled at all.
    /// </summary>
    private static ResolvedSource FromProcedure(
        SyncStep step, IReadOnlyList<ResolvedVariable> variables, WatermarkPlan? plan, string? boundary)
    {
        var name = step.Source.StoredProcedure!.Trim();

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach(var (key, value) in step.Source.Parameters)
        {
            parameters[Parameter(key)] = value is null
                ? null
                : VariableResolver.Substitute(value, step.VariableSyntax, variables);
        }

        if(plan is not null)
        {
            var column = SyncKeySource(step);
            var parameter = Parameter(column);

            if(!parameters.ContainsKey(parameter))
            {
                throw new InvalidOperationException(
                    $"the step '{Describe(step)}' keeps a watermark on '{column}' and reads from the procedure " +
                    $"{name}, which is passed no parameter called {parameter}, so every run would ask it for " +
                    "everything. Fix one of three: pass the procedure a parameter named after the sync key, or " +
                    "name the sync key after the parameter the procedure already takes, or take the watermark " +
                    "plan off the step and let it read everything on purpose.");
            }

            // Left as the operator configured it on a first run: their own value is what
            // says where a step with no history yet starts.
            if(boundary is not null)
                parameters[parameter] = BoundaryValue(step, plan, column, boundary);
        }

        return new ResolvedSource(name, true, parameters);
    }

    /// <summary>
    /// SQL the operator wrote takes its watermark through a variable, because there is no
    /// safe way to append a predicate to it.
    /// <para>
    /// The query may end in an <c>ORDER BY</c>, may be grouped, or may be an
    /// <c>OPENQUERY</c> string parsed on another server, and wrapping it in a derived
    /// table - which is what <c>SyncJob.Cli/IncrementalSync.cs</c> does today - is worse
    /// than not filtering at all over a linked server: every row crosses the link and is
    /// then thrown away here. So the watermark reaches this shape the way the deployed
    /// system already does it, through a variable, and a step that keeps a watermark and
    /// names no variable in its SQL is refused rather than quietly reading everything.
    /// </para>
    /// </summary>
    private static ResolvedSource FromQuery(
        SyncStep step, IReadOnlyList<ResolvedVariable> variables, WatermarkPlan? plan)
    {
        var written = step.Source.Sql!;

        if(plan is not null)
        {
            if(!VariableResolver.Mentions(written, step.VariableSyntax, variables))
            {
                var example = step.VariableSyntax == VariableSyntax.Legacy ? "@LastSyncValue" : "${LastSyncValue}";

                throw new InvalidOperationException(
                    $"the step '{Describe(step)}' keeps a watermark and reads from SQL the operator wrote, and " +
                    "none of its variables are named in that SQL, so every run would read everything. A predicate " +
                    "cannot be appended to a query this engine did not build - it may end in an ORDER BY, it may " +
                    "be grouped, and if it is an OPENQUERY string then wrapping it moves every row across the " +
                    "linked server before filtering any of them. Fix one of three: declare a variable that reads " +
                    $"the watermark and name it in the SQL ({example}), or give the step a table and a where " +
                    "clause so the engine can build the predicate itself, or take the watermark plan off the step " +
                    "and let it read everything on purpose.");
            }

            if(plan.Overlap != TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"the step '{Describe(step)}' sets a watermark overlap of {plan.Overlap} and reads from SQL " +
                    "the operator wrote. An overlap moves the boundary this engine builds, and here it builds " +
                    "none: the boundary is whatever the step's variable selects. Take the overlap off the step " +
                    "and put it in that variable's own SQL, where it is a DATEADD on the value being read.");
            }
        }

        return new ResolvedSource(
            VariableResolver.Substitute(written, step.VariableSyntax, variables),
            false,
            EmptyParameters);
    }

    /// <summary>
    /// A table is the shape where the engine writes the SQL, so it is the shape where it
    /// can also write the predicate.
    /// <para>
    /// The table name is used as the operator wrote it - <c>dbo.Customer</c>, not
    /// <c>[dbo].[Customer]</c> - for the reason <c>AppendMergeColumns</c> gives at the top
    /// of its file: that is how every job definition in the deployed systems spells one.
    /// The sync-key column is discovered rather than typed into a statement by hand, so it
    /// is bracketed.
    /// </para>
    /// </summary>
    private static ResolvedSource FromTable(
        SyncStep step, IReadOnlyList<ResolvedVariable> variables, WatermarkPlan? plan, string? boundary)
    {
        var sql = new StringBuilder("SELECT * FROM ").Append(step.Source.Table!.Trim());
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var where = string.IsNullOrWhiteSpace(step.Source.Where)
            ? null
            : Predicate(VariableResolver.Substitute(step.Source.Where!, step.VariableSyntax, variables));

        string? watermark = null;

        if(plan is not null)
        {
            var column = SyncKeySource(step);

            if(boundary is not null)
            {
                parameters[WatermarkParameter] = BoundaryValue(step, plan, column, boundary);
                watermark = $"{Bracket(column)} > {WatermarkParameter}";
            }
        }

        if(where is not null && watermark is not null)
        {
            // The operator's clause is parenthesised before anything is added to it: a
            // where of "A OR B" with a watermark appended by AND reads as "A OR (B AND
            // watermark)", which quietly brings back every row A matches.
            sql.Append(" WHERE (").Append(where).Append(") AND ").Append(watermark);
        }
        else if(where is not null)
        {
            sql.Append(" WHERE ").Append(where);
        }
        else if(watermark is not null)
        {
            sql.Append(" WHERE ").Append(watermark);
        }

        return new ResolvedSource(sql.ToString(), false, parameters);
    }

    /// <summary>
    /// The value the boundary starts from: what the last run reached, or - on a first run,
    /// where there is nothing to have reached - what the plan says a first run starts at.
    /// Ignoring the initial value would make every first run read the whole table, which
    /// is the one thing an operator sets it to prevent.
    /// </summary>
    private static string? Boundary(WatermarkPlan? plan, Watermark? watermark)
    {
        if(plan is null)
            return null;

        return Blank(watermark?.Value) ?? Blank(plan.InitialValue);
    }

    /// <summary>
    /// The boundary as it is sent, with the overlap taken off it.
    /// <para>
    /// A rowversion goes as bytes rather than as its <c>0x...</c> text: SQL Server refuses
    /// to compare a rowversion column with an nvarchar parameter outright - "implicit
    /// conversion from data type nvarchar to timestamp is not allowed" - and a procedure
    /// parameter declared binary refuses the same way. Everything else goes as text, which
    /// the server converts to the column's own type.
    /// </para>
    /// <para>
    /// Text the last run left is sent exactly as it was left. Only a boundary this method
    /// moves is written again, and then in the one form every date type on the server
    /// accepts. Re-rendering the rest would mean deciding that a value nobody said was a
    /// date is one, and a version string of "1-2-3" parses as a date perfectly well.
    /// </para>
    /// </summary>
    private static object BoundaryValue(SyncStep step, WatermarkPlan plan, string column, string boundary)
    {
        var value = boundary;

        if(plan.Overlap != TimeSpan.Zero)
        {
            if(plan.Overlap < TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"the step '{Describe(step)}' sets a watermark overlap of {plan.Overlap}, which is negative. " +
                    "An overlap reads back a little before the boundary to catch rows committed out of order; a " +
                    "negative one moves the boundary forward instead, and the rows it steps over are never read " +
                    "by any run.");
            }

            if(!TryMoment(value, out var moment))
            {
                throw new InvalidOperationException(
                    $"the step '{Describe(step)}' sets a watermark overlap of {plan.Overlap} and its sync key " +
                    $"'{column}' last reached '{value}', which is not a date or a time. An overlap moves a " +
                    "boundary back by a length of time, and there is no length of time to take off a rowversion " +
                    "or a counter: follow a datetime column, or set the overlap to zero.");
            }

            value = (moment - plan.Overlap).ToString(SqlValueText.DateTimeFormat, CultureInfo.InvariantCulture);
        }

        return IsBinaryLiteral(value) ? Convert.FromHexString(value.AsSpan(2)) : value;
    }

    /// <summary>
    /// A date or a time, or nothing.
    /// <para>
    /// A separator is required before the value is even offered to the parser, and the
    /// case that needs it is a decimal: <c>DateTime.TryParse("1.5")</c> is true and
    /// answers the fifth of January of the current year, so a step keyed on a numeric
    /// version would have its boundary silently moved to a date nobody chose. A
    /// rowversion and a counter are refused by the parser on their own.
    /// </para>
    /// </summary>
    private static bool TryMoment(string value, out DateTime moment)
    {
        moment = default;

        return value.AsSpan().ContainsAny('-', ':', '/') &&
               DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out moment);
    }

    /// <summary>The column the predicate names, spelled the way the source spells it.</summary>
    private static string SyncKeySource(SyncStep step)
    {
        var maps = step.FieldMaps.Where(x => x.IsSyncKey).ToList();

        if(maps.Count == 0)
        {
            throw new InvalidOperationException(
                $"the step '{Describe(step)}' keeps a watermark but no field map carries the sync key that fills " +
                "it, so there is no column to read from where the last run stopped");
        }

        if(maps.Count > 1)
        {
            throw new InvalidOperationException(
                $"the step '{Describe(step)}' marks {maps.Count} columns as the sync key and a watermark can only " +
                "follow one: " + string.Join(", ", maps.Select(x => x.Source ?? x.Target)));
        }

        return Blank(maps[0].Source) ?? Blank(maps[0].Target) ??
               throw new InvalidOperationException(
                   $"the step '{Describe(step)}' has a sync key field map that names no column on either side");
    }

    /// <summary>
    /// An operator's where clause, with the keyword taken off the front when they wrote
    /// one. Both spellings are in the deployed configurations and only one of them can be
    /// appended to a SELECT.
    /// </summary>
    private static string Predicate(string where)
    {
        var trimmed = where.Trim();

        return trimmed.StartsWith("where", StringComparison.OrdinalIgnoreCase) &&
               trimmed.Length > 5 && char.IsWhiteSpace(trimmed[5])
            ? trimmed[6..].Trim()
            : trimmed;
    }

    private static string Bracket(string column) =>
        column.StartsWith('[') && column.EndsWith(']')
            ? column
            : "[" + column.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string Parameter(string name) => name.StartsWith('@') ? name : "@" + name;

    private static bool IsBinaryLiteral(string value) =>
        value.Length > 2 &&
        value.Length % 2 == 0 &&
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        !value.AsSpan(2).ContainsAnyExcept("0123456789abcdefABCDEF");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Describe(SyncStep step) => step.Name is { Length: > 0 } ? step.Name : step.Id;

    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
}
