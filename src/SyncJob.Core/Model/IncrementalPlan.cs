namespace SyncJob.Core.Model;

/// <summary>
/// What makes the next run read less than everything: a watermark that says where the
/// last one got to, and a ledger that says what was deleted since.
/// </summary>
public sealed class IncrementalPlan
{
    public WatermarkPlan? Watermark { get; set; }

    public TombstoneLedger? Tombstones { get; set; }

    /// <summary>
    /// Ignore the watermark for one run and read everything. An operator sets this to
    /// repair a table; the engine clears it once the run finishes, so it cannot be left
    /// on by accident.
    /// </summary>
    public bool ForceFullRead { get; set; }
}

/// <summary>
/// The high-water mark of a step: the largest value the sync-key column had when the
/// last run finished, kept so the next run can ask for more than that.
/// <para>
/// The previous value is kept beside it, because the operation an operator actually
/// performs when something goes wrong is "run it again from where it was before", and
/// without the previous value that means guessing.
/// </para>
/// </summary>
public sealed class WatermarkPlan
{
    /// <summary>
    /// Where the watermark is read and written. Null means the engine's own store,
    /// which is the destination database. DataSync keeps its <c>SyncHistory</c> table
    /// in the destination for the same reason: a watermark that lives away from the
    /// data it describes can survive a restore of that data and then lie.
    /// </summary>
    public string? StateTable { get; set; }

    /// <summary>
    /// The value a step starts from when no watermark exists yet. A first run of a
    /// date-keyed step usually wants a date old enough to mean "everything".
    /// </summary>
    public string? InitialValue { get; set; }

    /// <summary>
    /// Read back rows a little before the watermark, to catch rows committed out of
    /// order around the boundary of the last read. Zero trusts the source's clock
    /// completely, which is rarely warranted when the key is a datetime and always
    /// warranted when it is a rowversion.
    /// </summary>
    public TimeSpan Overlap { get; set; }
}

/// <summary>
/// The table where the source records what it deleted, so the destination can delete it
/// too without either side comparing whole tables.
/// <para>
/// The ledger is written by the source application, not by this engine, so its shape is
/// a contract with software we do not own. Both shapes are therefore read: the one
/// already deployed, and one without its limits for whatever is deployed next.
/// </para>
/// </summary>
public sealed class TombstoneLedger
{
    /// <summary>The table on the source that holds the deletions.</summary>
    public string Table { get; set; } = string.Empty;

    public TombstoneKeyFormat KeyFormat { get; set; } = TombstoneKeyFormat.Legacy;

    /// <summary>
    /// The separator between the parts of a composite key in the legacy format.
    /// Ignored by <see cref="TombstoneKeyFormat.Columns"/>.
    /// </summary>
    public string LegacySeparator { get; set; } = ";";

    /// <summary>
    /// Whether the engine writes back to the ledger to say a deletion was applied.
    /// <para>
    /// True is the deployed behaviour and the one that keeps the ledger from growing
    /// forever. It requires write access to the source, which is not always granted -
    /// where it is not, the destination keeps its own record of what it has applied.
    /// </para>
    /// </summary>
    public bool MarkProcessed { get; set; } = true;
}

public enum TombstoneKeyFormat
{
    /// <summary>
    /// The shape already in production: a <c>KeyValue</c> column holding the key's
    /// parts joined by a separator.
    /// <para>
    /// It is read exactly as written, but not with the deployed engine's method. That
    /// splits the string with <c>PARSENAME</c>, which counts parts from the right and
    /// stops at five: a three-part key leaves the first two slots null, the join
    /// matches nothing, and nothing is deleted and nothing is reported. Here the split
    /// counts from the left, has no limit, and a key whose part count does not match
    /// the step's delete key is a reported error rather than a silent miss.
    /// </para>
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// One column per key part, named as the destination names them. No separator, no
    /// part limit, no ambiguity when a key value legitimately contains the separator -
    /// which the legacy format cannot represent at all.
    /// </summary>
    Columns = 1
}
