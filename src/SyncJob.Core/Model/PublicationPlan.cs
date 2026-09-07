namespace SyncJob.Core.Model;

/// <summary>
/// How a step's rows reach the destination, and what has to be true before they are
/// allowed to.
/// </summary>
public sealed class PublicationPlan
{
    public PublicationMode Mode { get; set; } = PublicationMode.Replace;

    /// <summary>
    /// The staging table, or null to let the engine name one and drop it afterwards.
    /// <para>
    /// SyncJob today requires the staging table to exist already and to have been
    /// created by hand in the same shape as its destination. That is the origin of its
    /// worst failure: the two drift apart, and a publication with no column list moves
    /// the data across shifted. Staging is created from the destination's own shape
    /// instead, so the two cannot disagree.
    /// </para>
    /// </summary>
    public string? StagingTable { get; set; }

    /// <summary>What must hold before staging is allowed to replace anything.</summary>
    public PublicationGuard Guard { get; set; } = new();

    /// <summary>
    /// Whether the destination's identity values are the source's. When false the
    /// destination assigns its own.
    /// </summary>
    public bool KeepIdentity { get; set; } = true;

    /// <summary>
    /// How many copy operations may run at once within one step. One means sequential,
    /// which is the honest default: parallelism here buys throughput on a wide table
    /// and buys deadlocks on a narrow one.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 1;

    public Dictionary<string, string> Extensions { get; set; } = new();
}

public enum PublicationMode
{
    /// <summary>
    /// The destination ends up holding exactly what the source returned. Published by
    /// swapping the staged table into place, which is a metadata operation, rather than
    /// by truncating and inserting, which holds a schema-modification lock for the
    /// length of the insert and blocks even readers.
    /// </summary>
    Replace = 0,

    /// <summary>Rows are added; nothing existing is touched.</summary>
    Append = 1,

    /// <summary>
    /// Rows are matched on the unique key: new ones inserted, changed ones updated,
    /// and - when the step also has a tombstone ledger - deleted ones removed. Absent
    /// rows are otherwise left alone, because absence from a filtered read is not
    /// evidence of deletion.
    /// </summary>
    Merge = 2
}

/// <summary>
/// The check that stands between a load and the destination.
/// <para>
/// It exists because of the failure it prevents: a source query that returns nothing -
/// a view that broke, a filter that matched nothing, a linked server that came back
/// empty rather than failing - followed by a truncate, is a job that deletes a
/// production table and reports success. SyncJob's CLI has guarded against this since
/// 2.3; its Windows service path never has.
/// </para>
/// </summary>
public sealed class PublicationGuard
{
    /// <summary>
    /// Fewer rows than this and the load is not published. Zero disables the floor, and
    /// is the right setting only for a table that is legitimately allowed to be empty.
    /// </summary>
    public int MinimumRows { get; set; }

    /// <summary>
    /// How far below the destination's current count the staged count may fall, as a
    /// fraction. 0.5 means "refuse if we staged less than half of what is already
    /// there". Null disables the comparison, which is what a first run needs.
    /// <para>
    /// This is the check a fixed floor cannot make: a table that grew to four million
    /// rows and stages nine hundred passes any floor anyone set when it had a thousand.
    /// </para>
    /// </summary>
    public double? MinimumFractionOfDestination { get; set; }

    /// <summary>What to do when the guard refuses.</summary>
    public GuardFailureAction OnFailure { get; set; } = GuardFailureAction.Abort;
}

public enum GuardFailureAction
{
    /// <summary>Fail the step, leave the destination untouched, and say why.</summary>
    Abort = 0,

    /// <summary>
    /// Leave the destination untouched, record the refusal, and let the run carry on to
    /// the next step. For a job where one stale table is better than a stopped night.
    /// </summary>
    Skip = 1,

    /// <summary>
    /// Publish anyway and record that the guard was overridden. Only ever set for a
    /// single deliberate run, never left on.
    /// </summary>
    Force = 2
}
