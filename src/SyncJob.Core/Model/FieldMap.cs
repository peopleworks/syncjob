namespace SyncJob.Core.Model;

/// <summary>
/// One column's departure from the default.
/// <para>
/// The default is that the destination's columns are discovered from the catalog and
/// matched to the source's by name. That is what DataSync's engine actually does - it
/// builds its column list from <c>INFORMATION_SCHEMA</c> at run time and never reads
/// the <c>TargetField</c> its own field maps carry - and it is what DBFSync does by
/// inferring everything from the DBF header. Neither has ever needed a row per column.
/// </para>
/// <para>
/// So a field map is written only for what departs: a column whose names differ on the
/// two sides, a column that carries a key role, a column that needs an expression, a
/// column that must not travel. A step with no field maps is the normal case, and a job
/// imported from DBFSync has none at all.
/// </para>
/// <para>
/// The cost of this default is the failure SyncJob's own comment warns about: publish
/// with <c>SELECT *</c> and the day someone adds a column to one side of the pair, the
/// data goes in shifted and SQL Server does not complain. Discovery is therefore
/// discovery of <b>both</b> sides, matched by name, with a name on one side and not the
/// other being an error - never a positional guess.
/// </para>
/// </summary>
public sealed class FieldMap
{
    /// <summary>
    /// The column as the source calls it. Null when the map exists only to say
    /// something about a destination column - an exclusion, or a provenance target.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// The column as the destination calls it. Null when the map exists only to say
    /// something about a source column.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>
    /// Part of the key that decides whether a destination row is the same row as a
    /// source row. Drives the MERGE's ON clause, is excluded from its UPDATE SET, and
    /// orders the batches so paging is deterministic.
    /// </summary>
    public bool IsUniqueKey { get; set; }

    /// <summary>
    /// The column whose highest value becomes the watermark after a successful load.
    /// <para>
    /// Exactly one column per step may carry this. DataSync aggregates every SyncKey
    /// column into one comma-separated string and then takes <c>MAX()</c> of it, which
    /// is only meaningful for a single column; the second one silently produces
    /// nonsense. Here a second one is a validation error.
    /// </para>
    /// </summary>
    public bool IsSyncKey { get; set; }

    /// <summary>
    /// Part of the key the tombstone ledger uses to find the destination row to delete.
    /// Often the same columns as <see cref="IsUniqueKey"/>, not necessarily.
    /// </summary>
    public bool IsDeleteKey { get; set; }

    /// <summary>
    /// When true the column does not travel, whatever discovery found. This is how a
    /// destination-managed column - an identity, a computed column, an audit stamp - is
    /// kept out of the way without listing every other column.
    /// </summary>
    public bool IsExcluded { get; set; }

    /// <summary>
    /// A SQL expression evaluated on the source in place of the raw column, or null for
    /// the column itself. It is written into the SELECT, so it is the operator's SQL and
    /// runs with the operator's rights - not a place to accept a value from elsewhere.
    /// </summary>
    public string? Expression { get; set; }

    public Dictionary<string, string> Extensions { get; set; } = new();
}

/// <summary>
/// A column the engine stamps on the rows it writes, after they land.
/// <para>
/// Needed when several plants feed one central database and a row has to say which
/// plant it came from. DataSync models this with three fields on a step and applies it
/// with an UPDATE - except that the UPDATE targets its own staging copy after the MERGE
/// has already run, so on the evidence of the script the stamp never reaches the
/// destination at all; and its type is stored as an integer while the script compares
/// it against lower-case strings. Both are why this is modelled again rather than
/// ported.
/// </para>
/// </summary>
public sealed class ProvenanceStamp
{
    /// <summary>The destination column that receives the value.</summary>
    public string Column { get; set; } = string.Empty;

    public ProvenanceValueKind Kind { get; set; }

    /// <summary>
    /// A SQL expression, a number or a literal string, according to
    /// <see cref="Kind"/>. Only <see cref="ProvenanceValueKind.Expression"/> reaches the
    /// server as SQL; the other two are parameters.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}

public enum ProvenanceValueKind
{
    /// <summary>A literal, sent as a parameter. The safe default.</summary>
    Text = 0,

    /// <summary>A number, sent as a parameter.</summary>
    Number = 1,

    /// <summary>
    /// SQL evaluated on the destination, such as <c>SYSUTCDATETIME()</c>. This is the
    /// one kind that is concatenated into a statement, so it is the operator's own SQL
    /// and is recorded as such.
    /// </summary>
    Expression = 2
}
