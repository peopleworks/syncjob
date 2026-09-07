using SyncJob.Core.Model;

namespace SyncJob.Core.Publication;

/// <summary>
/// A value the publisher writes itself, into a column staging does not carry - a
/// provenance stamp. <see cref="ValueSql"/> is either a parameter marker or the
/// operator's own expression; nothing else ever reaches it.
/// </summary>
public sealed record PublicationStamp(string Column, string ValueSql);

/// <summary>
/// The statements <see cref="AppendPublisher"/> and <see cref="MergePublisher"/> send.
/// <para>
/// Built as text, separately from anything that opens a connection, so that what the
/// server is asked to do can be asserted on in a test with no server - which is where the
/// two mistakes worth catching live: a <c>SELECT *</c>, and a key column in the
/// <c>UPDATE SET</c>.
/// </para>
/// </summary>
public static class AppendMergeSql
{
    /// <summary>The parameter carrying a batch's starting offset.</summary>
    public const string OffsetParameter = "@__offset";

    /// <summary>The parameter carrying a batch's size.</summary>
    public const string BatchParameter = "@__batch";

    /// <summary>The parameter carrying a provenance stamp, when it is a literal.</summary>
    public const string StampParameter = "@__stamp";

    /// <summary>
    /// Rows per <c>MERGE</c> when the step does not say. Two thousand keeps one batch
    /// under the five-thousand-lock threshold at which SQL Server escalates to a table
    /// lock - with room for the page locks each row also takes - so a merge into a busy
    /// destination does not stop everyone else reading it.
    /// </summary>
    public const int DefaultBatchSize = 2_000;

    /// <summary>
    /// The batch size to actually use: the operator's, whenever there is one.
    /// <para>
    /// The deployed loader replaces any configured size with a tenth of the total once a
    /// set passes ten thousand rows, so a job that asks for batches of fifty thousand
    /// silently gets exactly ten batches of whatever the table happens to hold, and
    /// nothing anywhere says so. A number an operator typed is honoured here or reported
    /// as invalid by <c>JobValidator</c>; it is never quietly replaced.
    /// </para>
    /// </summary>
    public static int ResolveBatchSize(int configured) =>
        configured > 0 ? configured : DefaultBatchSize;

    /// <summary>
    /// <c>INSERT INTO destination (columns) SELECT (the same columns) FROM staging</c>.
    /// </summary>
    public static string Append(
        string stagingTable,
        string destinationTable,
        IReadOnlyList<string> columns,
        PublicationStamp? stamp = null)
    {
        ArgumentNullException.ThrowIfNull(columns);

        if(columns.Count == 0)
            throw new ArgumentException("an append needs at least one column", nameof(columns));

        var target = columns.Select(Quote).ToList();
        var source = columns.Select(x => "s." + Quote(x)).ToList();

        if(stamp is not null)
        {
            target.Add(Quote(stamp.Column));
            source.Add(stamp.ValueSql);
        }

        return $"""
            INSERT INTO {destinationTable} ({string.Join(", ", target)})
            SELECT {string.Join(", ", source)}
            FROM {stagingTable} AS s;
            """;
    }

    /// <summary>
    /// One batch of a merge: the page of staging between <see cref="OffsetParameter"/> and
    /// <see cref="BatchParameter"/>, matched on <paramref name="keyColumns"/>.
    /// </summary>
    /// <remarks>
    /// Three things about this statement are load-bearing.
    /// <list type="bullet">
    /// <item><description>
    /// <c>WHEN MATCHED AND EXISTS (SELECT source EXCEPT SELECT target)</c> rather than a
    /// bare <c>WHEN MATCHED</c>. The bare form writes every matched row whether or not
    /// anything about it changed, so a second run of the same merge rewrites the whole
    /// table: every rowversion moves, every update trigger fires, and the log grows by the
    /// size of the destination for no change at all. <c>EXCEPT</c> rather than a chain of
    /// <c>&lt;&gt;</c> because it compares two nulls as equal, which is what "did this row
    /// change" means and what <c>&lt;&gt;</c> answers wrongly.
    /// </description></item>
    /// <item><description>
    /// There is no <c>WHEN NOT MATCHED BY SOURCE</c>. Absence from a filtered, paged read
    /// is not evidence of deletion - it is evidence of a filter and a page - which is why
    /// deletions come from the tombstone ledger instead. With batching it would also be
    /// catastrophic: each batch would delete everything outside its own page.
    /// </description></item>
    /// <item><description>
    /// <c>WITH (HOLDLOCK)</c> on the target. Without it two concurrent merges can both
    /// find a key missing and both insert it, which a unique key turns into a failed run
    /// and no unique key turns into a duplicate row.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static string Merge(
        string stagingTable,
        string destinationTable,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> keyColumns,
        PublicationStamp? stamp = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(keyColumns);

        if(keyColumns.Count == 0)
            throw new ArgumentException("a merge needs a key to match on", nameof(keyColumns));

        var keys = keyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updatable = columns.Where(x => !keys.Contains(x)).ToList();

        var insertColumns = columns.Select(Quote).ToList();
        var insertValues = columns.Select(x => "s." + Quote(x)).ToList();

        if(stamp is not null)
        {
            insertColumns.Add(Quote(stamp.Column));
            insertValues.Add(stamp.ValueSql);
        }

        var on = string.Join(" AND ", keyColumns.Select(x => $"t.{Quote(x)} = s.{Quote(x)}"));
        var order = string.Join(", ", keyColumns.Select(Quote));

        // WHEN MATCHED is left out entirely when the key is the whole row: there would be
        // nothing to set, and an empty SET list is a syntax error rather than a no-op.
        var matched = updatable.Count == 0
            ? string.Empty
            : $"""

                WHEN MATCHED AND EXISTS (
                    SELECT {string.Join(", ", updatable.Select(x => "s." + Quote(x)))}
                    EXCEPT
                    SELECT {string.Join(", ", updatable.Select(x => "t." + Quote(x)))}
                ) THEN UPDATE SET
                    {string.Join(",\n    ", updatable.Select(x => $"t.{Quote(x)} = s.{Quote(x)}"))}
                """;

        return $"""
            MERGE INTO {destinationTable} WITH (HOLDLOCK) AS t
            USING (
                SELECT {string.Join(", ", columns.Select(Quote))}
                FROM {stagingTable}
                ORDER BY {order}
                OFFSET {OffsetParameter} ROWS FETCH NEXT {BatchParameter} ROWS ONLY
            ) AS s
                ON {on}{matched}
            WHEN NOT MATCHED BY TARGET THEN INSERT ({string.Join(", ", insertColumns)})
                VALUES ({string.Join(", ", insertValues)})
            OUTPUT $action;
            """;
    }

    /// <summary>
    /// The stamp a step asks for, as a fragment and the parameter value that goes with it,
    /// or nothing when the step asks for none.
    /// <para>
    /// Only <see cref="ProvenanceValueKind.Expression"/> is concatenated into the
    /// statement, and it is concatenated because that is what it is for: the operator's own
    /// <c>SYSUTCDATETIME()</c>, running with the operator's rights. A literal is a
    /// parameter, always.
    /// </para>
    /// </summary>
    public static (PublicationStamp? Fragment, object? ParameterValue) Stamp(ProvenanceStamp? provenance)
    {
        if(provenance is null || string.IsNullOrWhiteSpace(provenance.Column))
            return (null, null);

        switch(provenance.Kind)
        {
            case ProvenanceValueKind.Expression:
                return (new PublicationStamp(provenance.Column, provenance.Value), null);

            case ProvenanceValueKind.Number:
                if(!decimal.TryParse(provenance.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var number))
                {
                    throw new InvalidOperationException(
                        $"the provenance stamp on {provenance.Column} is declared a number but its value is '{provenance.Value}'");
                }

                return (new PublicationStamp(provenance.Column, StampParameter), number);

            default:
                return (new PublicationStamp(provenance.Column, StampParameter), provenance.Value);
        }
    }

    /// <summary>
    /// Brackets one identifier. A closing bracket inside a name is doubled, which is the
    /// only escape SQL Server has here.
    /// </summary>
    public static string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
