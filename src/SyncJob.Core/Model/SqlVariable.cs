namespace SyncJob.Core.Model;

/// <summary>
/// A value worked out before a step's source query runs, and put into it.
/// <para>
/// This is the mechanism that closes the incremental loop in the deployed system: a
/// variable reads the watermark table, the step's SQL names the variable inside an
/// <c>OPENQUERY</c> string, and the next run therefore asks the source for rows newer
/// than the last one it got.
/// </para>
/// </summary>
public sealed class SqlVariable
{
    /// <summary>
    /// The name, without delimiters. <c>LastSyncValue</c>, not <c>${LastSyncValue}</c>
    /// and not <c>@LastSyncValue</c>.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// SQL whose first column of its first row is the value. It runs against the step's
    /// source endpoint unless <see cref="RunAgainstDestination"/> says otherwise - a
    /// watermark lookup usually reads the destination, because that is where the
    /// watermark lives.
    /// </summary>
    public string Sql { get; set; } = string.Empty;

    public bool RunAgainstDestination { get; set; }

    /// <summary>
    /// The value to use when the query returns nothing, rather than failing. This is
    /// what the deployed jobs achieve with a <c>UNION</c> and a sentinel row ordered
    /// last; saying it here is the same thing without the trick.
    /// </summary>
    public string? DefaultValue { get; set; }
}

/// <summary>
/// How a variable is written inside a step's SQL.
/// </summary>
public enum VariableSyntax
{
    /// <summary>
    /// <c>${Name}</c>. Delimited on both sides, so one variable's name can never be a
    /// prefix of another's. The default for anything written from now on.
    /// </summary>
    Delimited = 0,

    /// <summary>
    /// The deployed form: the variable's name appears bare in the SQL - by convention
    /// with a leading <c>@</c> - and is replaced textually.
    /// <para>
    /// Two things go wrong with it, and both are handled rather than reproduced. A name
    /// that is a prefix of another silently clobbers it, so substitution runs longest
    /// name first and a genuine prefix collision is reported at validation time instead
    /// of at three in the morning. And in the deployed loader the table variable that
    /// receives each value is never cleared between variables, so from the second
    /// variable on the value read is whatever a multi-row read returns first.
    /// </para>
    /// </summary>
    Legacy = 1
}
