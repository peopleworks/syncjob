using SyncJob.Core.Catalog;

namespace SyncJob.Core.Copy;

/// <summary>
/// One source column and the destination column its values are written to, with the
/// ordinal the source reader returns it at.
/// </summary>
public sealed record ColumnMatch(string SourceColumn, int SourceOrdinal, string DestinationColumn);

/// <summary>
/// Lines a source result set up against a destination table by name, and refuses to
/// guess.
/// <para>
/// This is the whole of the fix that WP 1.1 exists for. The deployed service path
/// publishes with <c>INSERT INTO final SELECT * FROM stage</c> and no column list, so
/// the day someone adds a column to one side of the pair the rows go in shifted by one
/// and SQL Server says nothing, because the types still line up. Anything that falls
/// back to ordinal position when a name does not match reproduces that failure. So
/// there is no fallback: a name on one side that is not on the other is an error that
/// names the column, and every such error found is reported at once rather than one
/// per run.
/// </para>
/// </summary>
public static class ColumnMatcher
{
    /// <summary>
    /// The matches, ordered the way the destination's catalog orders its columns.
    /// <para>
    /// The order is not decoration: <c>SqlBulkCopy</c> pulls values out of the source
    /// reader in destination-column order, and a reader opened with
    /// <see cref="System.Data.CommandBehavior.SequentialAccess"/> refuses to go
    /// backwards. Returning the matches in that order lets the caller see, before a
    /// single row moves, whether the two orders agree.
    /// </para>
    /// </summary>
    /// <param name="sourceColumns">The reader's column names, in the reader's order.</param>
    /// <param name="destinationColumns">The destination's columns, in <c>column_id</c> order.</param>
    /// <param name="columnMap">
    /// Source name to destination name, for the columns whose names differ. Empty is
    /// the normal case; entries here override the by-name match for the columns they
    /// name and leave every other column matching by name.
    /// </param>
    /// <param name="keepIdentity">
    /// True when the destination's identity values are the source's. False means the
    /// destination generates them, and an identity column with no source counterpart is
    /// then expected rather than an error.
    /// </param>
    /// <param name="destinationTable">Named in the error, because an operator reading it needs to know which table.</param>
    /// <exception cref="ColumnMatchException">The two sides do not line up.</exception>
    public static IReadOnlyList<ColumnMatch> Match(
        IReadOnlyList<string> sourceColumns,
        IReadOnlyList<CatalogColumn> destinationColumns,
        IReadOnlyDictionary<string, string> columnMap,
        bool keepIdentity,
        string destinationTable)
    {
        ArgumentNullException.ThrowIfNull(sourceColumns);
        ArgumentNullException.ThrowIfNull(destinationColumns);
        ArgumentNullException.ThrowIfNull(columnMap);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationTable);

        var problems = new List<string>();

        // SQL Server's own name comparison follows the database collation, which can be
        // case sensitive. Matching case-insensitively here and then writing with the
        // destination's own spelling is the safe direction: it accepts a job whose SQL
        // says "customerid" against a column called "CustomerId", and it never invents
        // a name the destination does not have.
        var destinationByName = new Dictionary<string, CatalogColumn>(StringComparer.OrdinalIgnoreCase);
        foreach(var column in destinationColumns)
            destinationByName[column.Name] = column;

        var map = BuildMap(columnMap, problems);

        for(var i = 0; i < sourceColumns.Count; i++)
        {
            if(string.IsNullOrWhiteSpace(sourceColumns[i]))
            {
                // An expression with no alias comes back with an empty name, and a
                // column with no name can only ever be matched by position.
                problems.Add(
                    $"column {i + 1} of the source has no name, so it cannot be matched to anything: " +
                    "give the expression an alias in the query");
            }
        }

        var named = sourceColumns.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        foreach(var duplicate in named.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
        {
            problems.Add(
                $"the source returns {duplicate.Count()} columns called '{duplicate.Key}', so a match by name " +
                "is ambiguous: alias one of them in the query");
        }

        foreach(var (sourceName, targetName) in map)
        {
            if(!named.Contains(sourceName, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"the column map sends '{sourceName}' to '{targetName}', but the source returns no column " +
                    $"called '{sourceName}'");
            }

            if(!destinationByName.ContainsKey(targetName))
            {
                problems.Add(
                    $"the column map sends '{sourceName}' to '{targetName}', but {destinationTable} has no column " +
                    $"called '{targetName}'");
            }
        }

        // Destination name to the source column that fills it, so that two source
        // columns aiming at one destination column is caught rather than silently
        // letting the last one win.
        var filledBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for(var ordinal = 0; ordinal < sourceColumns.Count; ordinal++)
        {
            var sourceName = sourceColumns[ordinal];
            if(string.IsNullOrWhiteSpace(sourceName))
                continue;

            var targetName = map.TryGetValue(sourceName, out var mapped) ? mapped : sourceName;

            if(!destinationByName.TryGetValue(targetName, out var target))
            {
                // Reported against the map already when the map is what named it.
                if(!map.ContainsKey(sourceName))
                {
                    problems.Add(
                        $"the source column '{sourceName}' has no column of that name in {destinationTable}: " +
                        "add the column, drop it from the query, or map it with a column map");
                }

                continue;
            }

            if(target.IsComputed)
            {
                problems.Add(
                    $"the source column '{sourceName}' would be written to '{target.Name}', which {destinationTable} " +
                    "computes for itself and refuses to have written: drop it from the query");
                continue;
            }

            if(target.IsRowVersion)
            {
                // The server would accept this mapping and then ignore the values,
                // which is the quietest way there is to lose a column.
                problems.Add(
                    $"the source column '{sourceName}' would be written to '{target.Name}', which is a rowversion " +
                    $"that {destinationTable} stamps for itself: drop it from the query, or give the destination a " +
                    "binary(8) column if the source's version is what you want to keep");
                continue;
            }

            if(filledBy.TryGetValue(target.Name, out var already))
            {
                problems.Add(
                    $"the source columns '{already}' and '{sourceName}' would both be written to '{target.Name}'");
                continue;
            }

            filledBy[target.Name] = sourceName;
        }

        foreach(var column in destinationColumns)
        {
            if(column.IsComputed || column.IsRowVersion || filledBy.ContainsKey(column.Name))
                continue;

            if(column.IsIdentity && !keepIdentity)
                continue;

            problems.Add(
                $"the destination column '{column.Name}' has no column of that name in the source: " +
                "the copy would leave it empty, and a column left empty by accident looks exactly like " +
                "one left empty on purpose");
        }

        if(problems.Count > 0)
            throw new ColumnMatchException(destinationTable, problems);

        var sourceOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for(var ordinal = 0; ordinal < sourceColumns.Count; ordinal++)
        {
            if(!string.IsNullOrWhiteSpace(sourceColumns[ordinal]))
                sourceOrdinals[sourceColumns[ordinal]] = ordinal;
        }

        return destinationColumns
            .Where(x => filledBy.ContainsKey(x.Name))
            .Select(x => new ColumnMatch(filledBy[x.Name], sourceOrdinals[filledBy[x.Name]], x.Name))
            .ToList();
    }

    /// <summary>
    /// The caller's map, rebuilt so that its keys are matched the way column names are
    /// matched everywhere else here.
    /// </summary>
    private static Dictionary<string, string> BuildMap(
        IReadOnlyDictionary<string, string> columnMap,
        List<string> problems)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach(var (source, target) in columnMap)
        {
            if(string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                problems.Add(
                    $"the column map has an entry with a blank name ('{source}' to '{target}')");
                continue;
            }

            if(map.TryGetValue(source, out var already))
            {
                problems.Add(
                    $"the column map sends '{source}' to both '{already}' and '{target}'");
                continue;
            }

            map[source] = target;
        }

        return map;
    }
}

/// <summary>
/// The source and the destination do not line up by name.
/// <para>
/// Every problem found is carried, not just the first: an operator fixing a mapping at
/// three in the morning should learn all of it in one run rather than one column per
/// attempt.
/// </para>
/// </summary>
public sealed class ColumnMatchException : InvalidOperationException
{
    public ColumnMatchException(string destinationTable, IReadOnlyList<string> problems)
        : base(Describe(destinationTable, problems))
    {
        DestinationTable = destinationTable;
        Problems = problems;
    }

    public string DestinationTable { get; }

    public IReadOnlyList<string> Problems { get; }

    private static string Describe(string destinationTable, IReadOnlyList<string> problems) =>
        $"the source does not line up with {destinationTable}:" +
        string.Concat(problems.Select(x => Environment.NewLine + "  - " + x));
}
