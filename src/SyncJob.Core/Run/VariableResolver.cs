using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SyncJob.Core.Model;
using SyncJob.Core.Publication;

namespace SyncJob.Core.Run;

/// <summary>
/// The value a variable resolved to, and where it came from.
/// <para>
/// <see cref="CameFromDefault"/> is not decoration: a watermark that fell back to its
/// default is a step about to read from the beginning of time, and the difference
/// between that and a watermark the destination actually knew about is the difference
/// between a slow night and a wrong one.
/// </para>
/// </summary>
public sealed record ResolvedVariable(string Name, string? Value, bool CameFromDefault);

/// <summary>
/// A variable that could not be turned into a value, named.
/// <para>
/// Named, because the alternative the deployed loader takes is to carry on with an
/// empty one: the step's SQL then asks the source for everything newer than nothing,
/// the whole table comes across, and the run reports success.
/// </para>
/// </summary>
public sealed class VariableResolutionException : InvalidOperationException
{
    public VariableResolutionException(string variableName, string message)
        : base(message) => VariableName = variableName;

    public VariableResolutionException(string variableName, string message, Exception innerException)
        : base(message, innerException) => VariableName = variableName;

    /// <summary>The variable that failed, so a caller can say which one without reading the message.</summary>
    public string VariableName { get; }
}

/// <summary>
/// Runs a step's variables and puts what they returned into its SQL.
/// <para>
/// This is the mechanism that closes the incremental loop of the deployed system, and
/// until now the one part of it nothing in this engine read: the importers carry
/// <see cref="SqlVariable"/> across faithfully and the value was then dropped on the
/// floor, so an imported job either read everything every night or sent the source a
/// query with a literal <c>@LastSyncValue</c> still in it.
/// </para>
/// </summary>
public static class VariableResolver
{
    /// <summary>
    /// Runs each of the step's variables and returns them in declaration order.
    /// <para>
    /// Each one is resolved on its own. The deployed loader reads every variable into one
    /// table variable and never clears it between them, so from the second variable on
    /// the value it takes is whatever the previous read left behind - a defect that only
    /// shows itself when a variable's SQL returns no row, which is exactly the moment the
    /// value matters most.
    /// </para>
    /// <para>
    /// At most one connection to each side, opened only when a variable actually asks for
    /// that side: a step with no variables opens nothing, and a step whose variables all
    /// read the destination never touches the source.
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<ResolvedVariable>> ResolveAsync(
        SyncStep step,
        string sourceConnectionString,
        string destinationConnectionString,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);

        if(step.Variables.Count == 0)
            return [];

        SqlConnection? source = null;
        SqlConnection? destination = null;

        try
        {
            var values = new List<ResolvedVariable>(step.Variables.Count);

            foreach(var variable in step.Variables)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = variable.Name?.Trim() ?? string.Empty;
                if(name.Length == 0)
                {
                    throw new VariableResolutionException(
                        string.Empty,
                        $"the step '{Describe(step)}' declares a variable with no name, so there is nothing in " +
                        "its SQL for a value to be put into");
                }

                if(string.IsNullOrWhiteSpace(variable.Sql))
                {
                    values.Add(Defaulted(step, variable, name, "has no SQL to read a value with"));
                    continue;
                }

                var side = variable.RunAgainstDestination ? "destination" : "source";

                var connection = variable.RunAgainstDestination
                    ? destination ??= await PublicationSql
                        .OpenAsync(destinationConnectionString, cancellationToken).ConfigureAwait(false)
                    : source ??= await PublicationSql
                        .OpenAsync(sourceConnectionString, cancellationToken).ConfigureAwait(false);

                object? scalar;
                try
                {
                    // The first column of the first row, and nothing said about the rest.
                    // A variable's SQL legitimately reads "SELECT value FROM ... ORDER BY
                    // ...", which is what TOP 1 semantics mean here; only an empty result
                    // is special, because only an empty result has no value in it at all.
                    scalar = await PublicationSql.ScalarAsync(
                            connection, null, variable.Sql, step.CommandTimeoutSeconds, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch(SqlException exception)
                {
                    throw new VariableResolutionException(
                        name,
                        $"the variable '{name}' of step '{Describe(step)}' could not be read from the {side}: " +
                        exception.Message,
                        exception);
                }

                // ScalarAsync answers null both for "no row" and for "one row holding
                // NULL", and the two mean the same thing here: the query knows of no
                // value, so the default is what the step runs with.
                if(scalar is null)
                {
                    values.Add(Defaulted(step, variable, name, $"returned no value from the {side}"));
                    continue;
                }

                values.Add(new ResolvedVariable(name, SqlValueText.Format(scalar), false));
            }

            return values;
        }
        finally
        {
            if(source is not null)
                await source.DisposeAsync().ConfigureAwait(false);

            if(destination is not null)
                await destination.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Puts the resolved values into a piece of SQL. Pure text: no database, no I/O.
    /// <para>
    /// Textual rather than parameterised, and there is no parameterised version of it.
    /// The deployed jobs name their variables inside an <c>OPENQUERY</c> string literal -
    /// <c>OPENQUERY([Factory1],'select * from dbo.GetData(''@LastSyncValue'')')</c> -
    /// where a parameter cannot go, because that string is not parsed here at all: it
    /// travels to the linked server and is parsed there. So this is the one place in the
    /// engine where a value that came out of a database is concatenated into SQL. What
    /// makes that defensible is where the value comes from - the operator's own SQL, run
    /// against the operator's own database, out of a configuration only an operator can
    /// edit. It is not user input, and nothing on this path turns it into user input.
    /// </para>
    /// <para>
    /// Single quotes are not doubled, and that is a decision rather than an omission.
    /// Doubling is right only when the substitution site is exactly one string literal
    /// deep. In the line above the site is two deep - the value sits inside <c>''..''</c>
    /// inside <c>'..'</c> - so a quote there would have to be quadrupled; in
    /// <c>WHERE Id > ${LastId}</c> it is nested none deep and must not be touched at all.
    /// Nothing here can tell those apart without parsing SQL that is not necessarily even
    /// T-SQL, since the OPENQUERY string is parsed by whatever the linked server runs. A
    /// value carrying a single quote is therefore refused by name rather than pasted in
    /// at a depth guessed wrong: at one depth that is a syntax error, at another it is an
    /// injection, and neither is a thing to find out at three in the morning. An operator
    /// whose value legitimately contains a quote shapes it in the variable's own SQL,
    /// which is the only code that knows what the value is being pasted into.
    /// </para>
    /// <para>
    /// A null value becomes the SQL literal <c>NULL</c> rather than an empty string.
    /// Inside a string literal that is four characters and reads as obviously wrong,
    /// which beats an empty string that reads as a row matching nothing.
    /// </para>
    /// </summary>
    public static string Substitute(string sql, VariableSyntax syntax, IReadOnlyList<ResolvedVariable> values)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(values);

        if(sql.Length == 0 || values.Count == 0)
            return sql;

        return syntax == VariableSyntax.Legacy ? Legacy(sql, values) : Delimited(sql, values);
    }

    /// <summary>
    /// The variables the SQL names and this list cannot fill.
    /// <para>
    /// A <c>${Name}</c> nobody declared is left where it is by <see cref="Substitute"/>,
    /// because it may be an operator's own literal. That is the right thing to do and the
    /// wrong thing to do silently: the same text is what a renamed or deleted variable
    /// leaves behind. So it is reported here instead, and the runner decides.
    /// </para>
    /// <para>
    /// Exact for the delimited syntax, and a hint for the legacy one: there an undeclared
    /// <c>@Name</c> cannot be told apart from a local the operator's own batch declares
    /// or a parameter the linked server expects, so what comes back is something to say
    /// out loud rather than something to refuse a run over. The answer is the same
    /// whether this is called before substitution or after it.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> UnresolvedNames(
        string sql, VariableSyntax syntax, IReadOnlyList<ResolvedVariable> values)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(values);

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach(var name in Names(sql, syntax))
        {
            if(Find(values, name) is null && seen.Add(name.Trim()))
                found.Add(name);
        }

        return found;
    }

    /// <summary>
    /// True when at least one of the declared variables is actually named in the SQL.
    /// <para>
    /// What <see cref="SourceSql"/> asks before it lets an incremental step run a query
    /// it has no way to filter: a step that declares a watermark variable and never
    /// mentions it reads the whole table every night, which is the failure that looks
    /// exactly like a working job until someone counts the rows.
    /// </para>
    /// </summary>
    internal static bool Mentions(string sql, VariableSyntax syntax, IReadOnlyList<ResolvedVariable> values)
    {
        if(string.IsNullOrEmpty(sql) || values.Count == 0)
            return false;

        return Names(sql, syntax).Any(x => Find(values, x) is not null);
    }

    private static ResolvedVariable Defaulted(SyncStep step, SqlVariable variable, string name, string what)
    {
        if(variable.DefaultValue is null)
        {
            throw new VariableResolutionException(
                name,
                $"the variable '{name}' of step '{Describe(step)}' {what} and has no default value. Carrying on " +
                "with an empty one would put the whole table through the step and report success; give the " +
                "variable a default that says where a first run starts.");
        }

        return new ResolvedVariable(name, variable.DefaultValue, true);
    }

    /// <summary>
    /// <c>${Name}</c>: case-insensitive on the name, exact on the delimiters. Delimited
    /// on both sides is what makes one variable's name safely a prefix of another's,
    /// which is the whole reason this syntax exists.
    /// </summary>
    private static string Delimited(string sql, IReadOnlyList<ResolvedVariable> values)
    {
        var result = new StringBuilder(sql.Length);
        var index = 0;

        while(index < sql.Length)
        {
            var start = sql.IndexOf("${", index, StringComparison.Ordinal);
            if(start < 0)
                break;

            var end = sql.IndexOf('}', start + 2);
            if(end < 0)
                break;

            result.Append(sql, index, start - index);

            var value = Find(values, sql[(start + 2)..end]);

            // Left exactly as written when nothing declares it: it may be an operator's
            // own literal, and UnresolvedNames is where the runner hears about it.
            result.Append(value is null ? sql[start..(end + 1)] : Literal(value));
            index = end + 1;
        }

        return result.Append(sql, index, sql.Length - index).ToString();
    }

    /// <summary>
    /// The deployed form, where the name appears bare in the SQL with a leading <c>@</c>
    /// on it. The importer strips that <c>@</c> when it reads the catalog, so it is put
    /// back here.
    /// <para>
    /// Longest name first. Replacing <c>@Last</c> before <c>@LastSyncValue</c> turns the
    /// second into the first one's value with <c>SyncValue</c> stuck on the end -
    /// silently, and into SQL that still runs. The validator warns about the collision;
    /// this ordering is what makes the common case right anyway.
    /// </para>
    /// <para>
    /// Only <c>@Name</c> is replaced, never the bare name on its own. A step whose SQL
    /// reads <c>SELECT LastSyncValue FROM t WHERE d > @LastSyncValue</c> has the name in
    /// it twice and only one of them is the variable; replacing both would quietly
    /// rewrite the select list.
    /// </para>
    /// </summary>
    private static string Legacy(string sql, IReadOnlyList<ResolvedVariable> values)
    {
        var result = sql;

        foreach(var value in values
                    .Where(x => x.Name.Length > 0)
                    .OrderByDescending(x => x.Name.Length)
                    .ThenBy(x => x.Name, StringComparer.Ordinal))
        {
            result = result.Replace(Token(value.Name), Literal(value), StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static IEnumerable<string> Names(string sql, VariableSyntax syntax) =>
        syntax == VariableSyntax.Legacy ? LegacyNames(sql) : DelimitedNames(sql);

    private static string Token(string name) => name.StartsWith('@') ? name : "@" + name;

    private static ResolvedVariable? Find(IReadOnlyList<ResolvedVariable> values, string name) =>
        values.FirstOrDefault(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string Literal(ResolvedVariable value)
    {
        if(value.Value is null)
            return "NULL";

        if(value.Value.Contains('\'', StringComparison.Ordinal))
        {
            throw new VariableResolutionException(
                value.Name,
                $"the variable '{value.Name}' resolved to a value with a single quote in it, and variables are " +
                "substituted as text because the deployed jobs name them inside OPENQUERY string literals where " +
                "a parameter cannot go. How many times that quote would have to be doubled depends on how many " +
                "string literals deep the variable sits in the step's SQL, which nothing here can know. Shape the " +
                "value in the variable's own SQL, where the depth is known.");
        }

        return value.Value;
    }

    private static string Describe(SyncStep step) =>
        step.Name is { Length: > 0 } ? step.Name : step.Id;

    private static IEnumerable<string> DelimitedNames(string sql)
    {
        var index = 0;

        while(index < sql.Length)
        {
            var start = sql.IndexOf("${", index, StringComparison.Ordinal);
            if(start < 0)
                yield break;

            var end = sql.IndexOf('}', start + 2);
            if(end < 0)
                yield break;

            yield return sql[(start + 2)..end];
            index = end + 1;
        }
    }

    private static IEnumerable<string> LegacyNames(string sql)
    {
        for(var i = 0; i < sql.Length; i++)
        {
            if(sql[i] != '@')
                continue;

            // @@ROWCOUNT and its family belong to the server, not to anybody's step.
            if(i + 1 < sql.Length && sql[i + 1] == '@')
            {
                i++;
                continue;
            }

            var start = i + 1;
            var end = start;
            while(end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] is '_' or '#' or '$'))
                end++;

            if(end > start)
                yield return sql[start..end];

            i = end - 1;
        }
    }
}

/// <summary>
/// A value read out of one database, written as text that means the same thing to
/// another one.
/// <para>
/// Invariant, and ISO 8601 with the <c>T</c> in it for anything carrying a date. Both
/// halves are load-bearing. The current culture would put a Spanish decimal comma or a
/// British day-first date into a WHERE clause and let the server read it as something
/// else. And a column of the old <c>datetime</c> type reads the space-separated form
/// according to the session's DATEFORMAT, so <c>'2026-01-15 00:00:00'</c> raises
/// "out-of-range value" the moment a session runs dmy - which is exactly what
/// <c>SyncJob.Cli/IncrementalSync.cs</c> builds today - while
/// <c>'2026-01-15T00:00:00'</c> is read the same way under every language setting.
/// </para>
/// <para>
/// The fractional digits are trimmed rather than padded, and that is not tidiness.
/// <c>datetime</c> - which is what the deployed schemas are full of - refuses any string
/// with seven fractional digits in it, all zeros included: <c>'2026-01-15T10:00:00'</c>
/// converts and <c>'2026-01-15T10:00:00.0000000'</c> raises "conversion failed when
/// converting date and/or time from character string", so the round-trip format would
/// break every step keyed on one. Trimming keeps every digit a value actually has, and a
/// value with seven of them can only have come from a <c>datetime2</c>, which accepts
/// them back.
/// </para>
/// </summary>
internal static class SqlValueText
{
    /// <summary>
    /// ISO 8601 with the fraction only where there is one, and the kind only where the
    /// value carried one.
    /// </summary>
    public const string DateTimeFormat = "yyyy-MM-ddTHH:mm:ss.FFFFFFFK";

    private const string OffsetFormat = "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz";

    public static string Format(object value) => value switch
    {
        string text => text,
        DateTime moment => moment.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString(OffsetFormat, CultureInfo.InvariantCulture),
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),

        // A rowversion arrives as bytes and goes back as the literal SQL Server writes,
        // because that string is also what the watermark store keeps between runs.
        byte[] bytes => "0x" + Convert.ToHexString(bytes),

        // Not "True": SQL Server converts '1' to a bit and refuses 'True'.
        bool flag => flag ? "1" : "0",
        Guid id => id.ToString("D", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
