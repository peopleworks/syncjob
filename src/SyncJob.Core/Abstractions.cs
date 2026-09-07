using SyncJob.Core.Incremental;
using SyncJob.Core.Model;

namespace SyncJob.Core;

/// <summary>
/// Reads a step's source and writes the rows into a table on the destination.
/// <para>
/// The whole contract is "stream": an implementation may not hold the result set in
/// memory. All three of the copy paths this engine replaces load the entire source into
/// a <c>List&lt;object[]&gt;</c> before writing a single row, which is why a table
/// larger than the machine's RAM cannot be copied today at any batch size.
/// </para>
/// </summary>
public interface ITableCopier
{
    /// <summary>
    /// Copies into <paramref name="targetTable"/>, which already exists and is empty.
    /// Returns what it moved.
    /// </summary>
    Task<CopyResult> CopyAsync(CopyRequest request, string targetTable, CancellationToken cancellationToken);
}

/// <summary>Everything one copy needs to know, resolved: no variables left in the SQL.</summary>
public sealed class CopyRequest
{
    public required string SourceConnectionString { get; init; }

    public required string DestinationConnectionString { get; init; }

    /// <summary>The SELECT to run, with every variable already substituted.</summary>
    public required string Sql { get; init; }

    public Dictionary<string, object?> Parameters { get; init; } = new();

    /// <summary>True when <see cref="Sql"/> names a stored procedure rather than a query.</summary>
    public bool IsStoredProcedure { get; init; }

    /// <summary>
    /// Source column to destination column. Empty means "match by name", which is the
    /// normal case - see <see cref="FieldMap"/> for why a map is the exception.
    /// </summary>
    public Dictionary<string, string> ColumnMap { get; init; } = new();

    public int BatchSize { get; init; }

    public int CommandTimeoutSeconds { get; init; }

    public bool KeepIdentity { get; init; }

    /// <summary>Called as rows go by. Never null-checked at the call site; use <see cref="IProgress{T}"/>.</summary>
    public IProgress<CopyProgress>? Progress { get; init; }
}

public sealed record CopyProgress(long RowsCopied, TimeSpan Elapsed);

public sealed record CopyResult(long RowsCopied, TimeSpan Elapsed);

/// <summary>
/// Creates the table a copy stages into, in the shape of the destination it will
/// replace, and takes it away afterwards.
/// <para>
/// The shape comes from the destination's own catalog rather than from a table someone
/// created by hand years ago. That is the whole fix: a staging table maintained
/// separately drifts from its destination, and a publication that trusts them to match
/// moves the data across shifted, silently.
/// </para>
/// </summary>
public interface IStagingTableFactory
{
    /// <summary>
    /// Creates a table shaped like <paramref name="destinationTable"/> and returns its
    /// name. Indexes and constraints are deliberately not copied: rows load faster
    /// without them, and <see cref="IPublisher"/> is responsible for what the
    /// destination ends up with.
    /// </summary>
    Task<string> CreateAsync(
        string connectionString,
        string destinationTable,
        string? requestedName,
        CancellationToken cancellationToken);

    Task DropAsync(string connectionString, string stagingTable, CancellationToken cancellationToken);
}

/// <summary>
/// Decides whether what was staged is allowed to reach the destination.
/// </summary>
public interface IPublicationGuard
{
    Task<GuardVerdict> EvaluateAsync(
        string connectionString,
        string stagingTable,
        string destinationTable,
        PublicationGuard guard,
        CancellationToken cancellationToken);
}

/// <summary>
/// Why the guard said what it said. The reason is not decoration: it is what an
/// operator reads at three in the morning to decide whether to force it.
/// </summary>
public sealed record GuardVerdict(bool MayPublish, long StagedRows, long DestinationRows, string Reason);

/// <summary>
/// Moves the staged rows into the destination, in whichever way the step asked for.
/// </summary>
public interface IPublisher
{
    Task<PublishResult> PublishAsync(
        string connectionString,
        string stagingTable,
        SyncStep step,
        CancellationToken cancellationToken);
}

public sealed record PublishResult(long Inserted, long Updated, long Deleted);

/// <summary>
/// Where a step's watermark is kept between runs, and what it was before.
/// </summary>
public interface IWatermarkStore
{
    Task<Watermark?> ReadAsync(string connectionString, string jobId, string stepId, CancellationToken cancellationToken);

    /// <summary>
    /// Records <paramref name="value"/> as the new watermark and keeps what was there
    /// as the previous one, in one operation, so a reader never sees the two agree.
    /// </summary>
    Task WriteAsync(
        string connectionString,
        string jobId,
        string stepId,
        string value,
        CancellationToken cancellationToken);
}

public sealed record Watermark(string Value, string? PreviousValue, DateTimeOffset UpdatedAt);

/// <summary>
/// Applies the deletions a source recorded, and tells the source they were applied.
/// </summary>
public interface ITombstoneApplier
{
    /// <summary>
    /// Applies every well-formed deletion and reports the ones it could not.
    /// <para>
    /// A ledger row whose key does not have the shape the step's delete key implies is
    /// a fact about the source's data, not a failure of the run: the other deletions
    /// are still correct, and refusing them all would leave the destination further
    /// from the truth than applying them. So they come back in
    /// <see cref="TombstoneResult.Unapplied"/> rather than as an exception - a number
    /// alone had nowhere to put them.
    /// </para>
    /// </summary>
    Task<TombstoneResult> ApplyAsync(
        string sourceConnectionString,
        string destinationConnectionString,
        SyncStep step,
        TombstoneLedger ledger,
        CancellationToken cancellationToken);
}

/// <summary>
/// What the applier deleted, and what it would not.
/// <para>
/// The deployed implementation cannot report the second half at all: it splits a key
/// with <c>PARSENAME</c>, which counts parts from the right and stops at five, so a
/// short key matches nothing, nothing is deleted, and the run reports success.
/// </para>
/// </summary>
/// <param name="Deleted">Rows removed from the destination.</param>
/// <param name="Unapplied">Ledger rows whose key could not be used, each with its id.</param>
public sealed record TombstoneResult(long Deleted, IReadOnlyList<TombstoneKeyProblem> Unapplied)
{
    public bool IsComplete => Unapplied.Count == 0;
}

/// <summary>
/// Holds a job's lease while a run has it, and lets a long run say it is still alive.
/// </summary>
public interface IJobLeaseStore
{
    /// <summary>
    /// Takes the lease, or returns null when someone else holds one that has not
    /// expired. An expired lease is taken over, which is the difference between this
    /// and a status flag that a crash leaves set forever.
    /// </summary>
    Task<RunLease?> TryAcquireAsync(
        string connectionString,
        string jobId,
        string runId,
        string holder,
        TimeSpan duration,
        CancellationToken cancellationToken);

    Task RenewAsync(string connectionString, string jobId, RunLease lease, CancellationToken cancellationToken);

    Task ReleaseAsync(string connectionString, string jobId, RunLease lease, CancellationToken cancellationToken);
}

/// <summary>
/// Turns a job definition into a job that can run: variables resolved, watermark read,
/// SQL assembled.
/// </summary>
public interface IJobImporter
{
    /// <summary>A name a human recognises, for error messages: "appsettings section", "syncjob.db".</summary>
    string SourceDescription { get; }

    /// <summary>Every job this source holds, and everything that could not be carried across.</summary>
    Task<ImportResult> ImportAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What an importer produced. <see cref="Losses"/> is the honest part: a format that
/// carries something this model has no field for says so here rather than dropping it
/// quietly.
/// </summary>
public sealed class ImportResult
{
    public List<SyncJobDefinition> Jobs { get; init; } = new();

    public List<string> Losses { get; init; } = new();
}
