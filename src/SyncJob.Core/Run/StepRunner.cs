using System.Globalization;
using Microsoft.Data.SqlClient;
using SyncJob.Core.Copy;
using SyncJob.Core.Incremental;
using SyncJob.Core.Model;
using SyncJob.Core.Publication;

namespace SyncJob.Core.Run;

/// <summary>
/// Runs one step from its variables to its watermark, and reports what it did with
/// numbers that came from the work rather than from the intention.
/// <para>
/// Until now this engine has been a box of parts with no assembly: a streaming copier, a
/// staging factory, a guard, three publishers, a watermark store and a tombstone applier,
/// and nothing anywhere that ran a step. So all three deployed surfaces kept their own
/// copy of the pipeline - the CLI, the Windows service and the central command - each
/// reading the whole source into a list, each batching its own way, and one of the three
/// with no guard at all. This is the one implementation they are to be pointed at.
/// </para>
/// <para>
/// Two orderings here are load-bearing and neither is obvious.
/// </para>
/// <para>
/// <b>The watermark is written after the publication and read before it.</b> Written
/// after, because a watermark advanced past a publication that then failed makes the next
/// run skip the rows that never arrived - silently, and for ever. Read before, because
/// <see cref="SwapPublisher"/> exchanges the staged table's storage with the
/// destination's: after a replace the staging table is empty, and a maximum read from it
/// then is null. The value is therefore taken out of staging the moment the copy ends and
/// only committed once the destination has actually been written.
/// </para>
/// <para>
/// <b>The tombstone ledger is applied after the publication.</b>
/// <see cref="SqlTombstoneApplier"/> explains why: the deletions are in the destination
/// and the mark-processed is in the source, the two cannot be one transaction without
/// MSDTC, and ordering them is the answer. Applying the ledger first would delete rows a
/// failed publication then never replaces.
/// </para>
/// </summary>
public sealed class StepRunner
{
    private readonly ITableCopier _copier;
    private readonly IStagingTableFactory _staging;
    private readonly IPublicationGuard _guard;
    private readonly IWatermarkStore? _watermarks;
    private readonly ITombstoneApplier _tombstones;

    public StepRunner(
        ITableCopier? copier = null,
        IStagingTableFactory? stagingFactory = null,
        IPublicationGuard? guard = null,
        IWatermarkStore? watermarks = null,
        ITombstoneApplier? tombstones = null)
    {
        _copier = copier ?? new SqlTableCopier();
        _staging = stagingFactory ?? new StagingTableFactory();
        _guard = guard ?? new PublicationGuardEvaluator();

        // Kept null rather than defaulted, because the store's table is a per-step
        // setting that arrives through its constructor and not through the interface:
        // a caller that honours WatermarkPlan.StateTable builds one store per table, and
        // that is what happens per step below. A store supplied here overrides all of it.
        _watermarks = watermarks;

        _tombstones = tombstones ?? new SqlTombstoneApplier();
    }

    /// <summary>
    /// Which publisher a mode is published by.
    /// <para>
    /// A property rather than a constructor parameter because the constructor is a
    /// contract shared with two other packages. It exists at all because without it the
    /// only way to assert that a step's row counts come from the publisher rather than
    /// from the copy is to have a server, and that assertion is the whole point of the
    /// counts: the deployed path sets <c>RowsInserted</c> to the source row count with a
    /// <c>// TODO: Obtener metrics reales del MERGE</c> beside it.
    /// </para>
    /// </summary>
    public Func<PublicationMode, IPublisher> Publishers { get; init; } = DefaultPublisher;

    /// <summary>
    /// Where the step's next watermark is read from once the rows are staged. See
    /// <see cref="IStagedWatermarkReader"/> for why it is staging and not the source.
    /// </summary>
    public IStagedWatermarkReader StagedWatermarks { get; init; } = new SqlStagedWatermarkReader();

    /// <summary>
    /// Copies the step's source into a staging table, decides whether it may reach the
    /// destination, publishes it if it may, and says what happened either way.
    /// <para>
    /// It does not throw. A run of thirty steps whose seventh fails must not lose the
    /// other twenty-nine results, so a failure - including a cancellation - comes back as
    /// a <see cref="StepResult"/> with <see cref="RunStatus.Failed"/> and a
    /// <see cref="StepResult.Message"/> that says what to do about it. The only exception
    /// is an argument that is null, which is a wiring mistake rather than a run.
    /// </para>
    /// </summary>
    public async Task<StepResult> RunAsync(
        SyncJobDefinition job,
        SyncStep step,
        string sourceConnectionString,
        string destinationConnectionString,
        string runId,
        JobRunOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(options);

        var result = new StepResult
        {
            RunId = runId,
            StepId = step.Id,
            StepName = string.IsNullOrWhiteSpace(step.Name) ? step.Id : step.Name,
            StartedAt = DateTimeOffset.UtcNow
        };

        string? staging = null;

        try
        {
            // The watermark first, and the variables after it. A step whose source is SQL
            // the operator wrote takes its watermark through a variable that reads the
            // watermark table - that is the deployed shape, and SourceSql refuses the
            // step without it. Resolving variables first means that variable runs before
            // anything has created the table it reads, so the first run of every such
            // step would fail on "Invalid object name" and the second would work.
            var watermark = await ReadWatermarkAsync(job, step, destinationConnectionString, cancellationToken).ConfigureAwait(false);
            result.PreviousWatermarkValue = watermark?.Value ?? step.Incremental?.Watermark?.InitialValue;

            var variables = await VariableResolver
                .ResolveAsync(step, sourceConnectionString, destinationConnectionString, cancellationToken)
                .ConfigureAwait(false);

            var source = SourceSql.Build(step, variables, watermark);

            // Every mode stages, Append included, and that is a decision rather than a
            // symmetry. Appending straight into the destination would put the guard after
            // the rows instead of before them - there would be nothing left to refuse -
            // and a copy that is cancelled or fails half way commits the batches it
            // already sent, which in the destination is a partial load nobody asked for
            // and in staging is a table that gets dropped.
            //
            // A dry run ignores a staging table the step named for itself and takes a
            // generated one: an operator inspecting tonight's load while tonight's load
            // is running must not drop the table the real run is filling.
            staging = await _staging.CreateAsync(
                destinationConnectionString,
                step.DestinationTable,
                options.DryRun ? null : step.Publication.StagingTable,
                cancellationToken).ConfigureAwait(false);

            var copy = await _copier
                .CopyAsync(BuildRequest(step, source, sourceConnectionString, destinationConnectionString, options), staging, cancellationToken)
                .ConfigureAwait(false);

            result.RowsRead = copy.RowsCopied;

            var next = await ReadNextWatermarkAsync(step, destinationConnectionString, staging, cancellationToken).ConfigureAwait(false);

            var verdict = await _guard
                .EvaluateAsync(destinationConnectionString, staging, step.DestinationTable, step.Publication.Guard, cancellationToken)
                .ConfigureAwait(false);

            var overridden = !verdict.MayPublish && step.Publication.Guard.OnFailure == GuardFailureAction.Force;

            if(!verdict.MayPublish && !overridden)
            {
                var skipped = step.Publication.Guard.OnFailure == GuardFailureAction.Skip;
                result.Status = skipped ? RunStatus.Skipped : RunStatus.Failed;
                result.Message =
                    $"the guard refused to publish into {step.DestinationTable}, so it still holds what it held " +
                    $"before: {verdict.Reason}. " +
                    (skipped
                        ? "The step is set to skip, so the run carried on; the destination is now one load stale."
                        : "Check the source before forcing it: the reason above is the arithmetic, not the cause.");

                return result;
            }

            if(options.DryRun)
            {
                result.Status = RunStatus.Succeeded;
                result.Message = DryRunMessage(step, verdict, overridden, next);
                return result;
            }

            var publisher = Publishers(step.Publication.Mode);
            var published = await publisher
                .PublishAsync(destinationConnectionString, staging, step, cancellationToken)
                .ConfigureAwait(false);

            // From the publisher, never from the copy. A replace reports what it put in
            // and what it took out, an append reports its insert count and a merge counts
            // the rows the MERGE itself said it changed - which is why an unchanged row
            // that was staged shows up in none of them.
            result.RowsInserted = published.Inserted;
            result.RowsUpdated = published.Updated;
            result.RowsDeleted = published.Deleted;

            var notes = new List<string>();
            if(overridden)
            {
                notes.Add(
                    $"the guard refused and the step is set to publish anyway, so it was overridden and " +
                    $"{step.DestinationTable} was written: {verdict.Reason}.");
            }

            if(step.Incremental?.Tombstones is { } ledger)
            {
                var tombstones = await _tombstones
                    .ApplyAsync(sourceConnectionString, destinationConnectionString, step, ledger, cancellationToken)
                    .ConfigureAwait(false);

                result.RowsDeleted += tombstones.Deleted;

                if(!tombstones.IsComplete)
                    notes.Add(Describe(tombstones, ledger));
            }

            if(next is not null)
            {
                var store = Watermarks(step);
                await store
                    .WriteAsync(destinationConnectionString, job.Id, step.Id, next, cancellationToken)
                    .ConfigureAwait(false);

                result.WatermarkValue = next;
            }

            // Cleared here and only here. The flag is a repair an operator switched on for
            // one run, and a repair run that failed has not had its one run yet - leaving
            // it set is what lets the retry still read everything.
            if(step.Incremental is { ForceFullRead: true } incremental)
                incremental.ForceFullRead = false;

            result.Status = RunStatus.Succeeded;
            result.Message = notes.Count == 0 ? null : string.Join(" ", notes);

            return result;
        }
        catch(CopyCanceledException e)
        {
            // The rows it already wrote are committed in staging, and staging is dropped
            // below - so they go with it. The destination was never reached, because
            // publication is the step after this one.
            result.Status = RunStatus.Failed;
            result.RowsRead = e.RowsWritten;
            result.Message =
                $"the copy was cancelled after {Number(e.RowsWritten)} rows had reached staging. Those rows go " +
                $"with the staging table, {step.DestinationTable} was not written and the watermark did not move, " +
                "so running the step again repeats exactly this work and nothing else.";

            return result;
        }
        catch(OperationCanceledException)
        {
            result.Status = RunStatus.Failed;
            result.Message =
                $"the step was cancelled. Whether {step.DestinationTable} was written depends on where it stopped: " +
                "a replace and an append are one transaction each and are all or nothing, a merge commits each " +
                "batch, and the watermark is only written after a publication that finished - so it did not move.";

            return result;
        }
        catch(Exception e)
        {
            result.Status = RunStatus.Failed;
            result.Message = $"the step failed and {step.DestinationTable} may not have been written: {e.Message}";

            return result;
        }
        finally
        {
            // Whatever happened, including a throw. The deployed system leaves its
            // staging tables behind and they accumulate until someone notices the
            // database has three hundred of them. CancellationToken.None: a cancelled
            // run still has to clear up after itself.
            if(staging is not null)
            {
                try
                {
                    await _staging.DropAsync(destinationConnectionString, staging, CancellationToken.None).ConfigureAwait(false);
                }
                catch(Exception e)
                {
                    result.Message = Append(
                        result.Message,
                        $"the staging table {staging} could not be dropped and is still there: {e.Message}");
                }
            }

            result.FinishedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// The publisher each mode is published by. A mode with no publisher is a model that
    /// has grown a case this class has not, which is worth stopping for rather than
    /// defaulting to whichever publisher happens to be first.
    /// </summary>
    public static IPublisher DefaultPublisher(PublicationMode mode) => mode switch
    {
        PublicationMode.Replace => new SwapPublisher(),
        PublicationMode.Append => new AppendPublisher(),
        PublicationMode.Merge => new MergePublisher(),
        _ => throw new NotSupportedException($"there is no publisher for {mode}")
    };

    /// <summary>
    /// What the run would have done, with the counting already done rather than guessed.
    /// <para>
    /// A dry run that says "OK" is worth nothing; one that says "1,240 rows staged and
    /// the guard would have allowed them" is worth having, and the only way to say the
    /// second is to have actually staged them.
    /// </para>
    /// </summary>
    private static string DryRunMessage(SyncStep step, GuardVerdict verdict, bool overridden, string? next)
    {
        var what = overridden
            ? $"the guard would have refused and the step would have published anyway: {verdict.Reason}"
            : $"the guard would have allowed it: {verdict.Reason}";

        var watermark = next is null
            ? string.Empty
            : $" The watermark would move to {next}.";

        return
            $"dry run: {Number(verdict.StagedRows)} rows were staged from the real source and {what}. " +
            $"A real run would {Describe(step.Publication.Mode)} {step.DestinationTable}.{watermark} " +
            "Nothing was written: the staging table has been dropped and the destination and the watermark are " +
            "as they were.";
    }

    private static string Describe(PublicationMode mode) => mode switch
    {
        PublicationMode.Replace => "replace",
        PublicationMode.Append => "append to",
        PublicationMode.Merge => "merge into",
        _ => "publish into"
    };

    /// <summary>
    /// The ledger rows that could not be applied, by their own ids.
    /// <para>
    /// A number alone has nowhere to put them, which is the whole reason
    /// <see cref="TombstoneResult.Unapplied"/> is a list. The deployed applier splits its
    /// keys with <c>PARSENAME</c>, matches nothing, deletes nothing and reports success;
    /// the one thing this must not do is arrive at the same place by dropping the list on
    /// the floor.
    /// </para>
    /// </summary>
    private static string Describe(TombstoneResult tombstones, TombstoneLedger ledger)
    {
        const int listed = 10;

        var problems = tombstones.Unapplied.Take(listed).Select(x => x.ToString());
        var more = tombstones.Unapplied.Count > listed
            ? $" and {Number(tombstones.Unapplied.Count - listed)} more"
            : string.Empty;

        return
            $"{Number(tombstones.Unapplied.Count)} deletion(s) recorded in {ledger.Table} could not be applied " +
            "because their keys do not have the shape the step's delete key implies, so those rows are still in " +
            $"the destination and still in the ledger: {string.Join("; ", problems)}{more}.";
    }

    private static string Append(string? message, string note) =>
        string.IsNullOrEmpty(message) ? note : message + " " + note;

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The step's watermark store: the one supplied to the constructor, or one built for
    /// the table this step's plan names.
    /// </summary>
    private IWatermarkStore Watermarks(SyncStep step) =>
        _watermarks ?? new SqlWatermarkStore(step.Incremental?.Watermark?.StateTable);

    /// <summary>
    /// Where the step is resuming from, or null when it reads everything -
    /// <see cref="IncrementalPlan.ForceFullRead"/> included, which is what an operator
    /// switches on to repair a table.
    /// </summary>
    private async Task<Watermark?> ReadWatermarkAsync(
        SyncJobDefinition job,
        SyncStep step,
        string destinationConnectionString,
        CancellationToken cancellationToken)
    {
        if(step.Incremental is not { Watermark: not null } incremental || incremental.ForceFullRead)
            return null;

        var store = Watermarks(step);

        return await store
            .ReadAsync(destinationConnectionString, job.Id, step.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The value the step would resume from next time, taken out of the staged rows
    /// before anything is published. Null when the step keeps no watermark, and null when
    /// the copy staged no rows - a run that found nothing new must not move the mark.
    /// </summary>
    private async Task<string?> ReadNextWatermarkAsync(
        SyncStep step,
        string destinationConnectionString,
        string stagingTable,
        CancellationToken cancellationToken)
    {
        if(step.Incremental?.Watermark is null)
            return null;

        if(SourceSql.SyncKeyColumn(step) is not { Length: > 0 } expression)
            return null;

        return await StagedWatermarks
            .ReadAsync(destinationConnectionString, stagingTable, expression, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The copy, assembled from the step. The column map carries only the columns whose
    /// names actually differ: everything else is matched against the destination's own
    /// catalog by name, which is what stops a publication going in shifted the day
    /// someone adds a column to one side.
    /// </summary>
    private static CopyRequest BuildRequest(
        SyncStep step,
        ResolvedSource source,
        string sourceConnectionString,
        string destinationConnectionString,
        JobRunOptions options)
    {
        var request = new CopyRequest
        {
            SourceConnectionString = sourceConnectionString,
            DestinationConnectionString = destinationConnectionString,
            Sql = source.Sql,
            IsStoredProcedure = source.IsStoredProcedure,
            BatchSize = step.BatchSize,
            CommandTimeoutSeconds = step.CommandTimeoutSeconds,
            KeepIdentity = step.Publication.KeepIdentity,
            Progress = options.Progress is null ? null : new CopyProgressRelay(options.Progress, step)
        };

        foreach(var (name, value) in source.Parameters)
            request.Parameters[name] = value;

        foreach(var map in step.FieldMaps)
        {
            if(map.IsExcluded || map.Source is not { Length: > 0 } from || map.Target is not { Length: > 0 } to)
                continue;

            if(!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                request.ColumnMap[from] = to;
        }

        return request;
    }

    /// <summary>
    /// Turns the copier's progress into the run's, and refuses to let a caller's progress
    /// sink stop a copy that is working: this is called once every ten thousand rows from
    /// inside <c>SqlBulkCopy</c>'s own callback.
    /// </summary>
    private sealed class CopyProgressRelay(IProgress<RunProgress> progress, SyncStep step) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value)
        {
            try
            {
                progress.Report(new RunProgress(step.Id, $"{Number(value.RowsCopied)} rows staged for {step.DestinationTable}", value.RowsCopied));
            }
            catch(Exception)
            {
                // A progress sink that throws is the caller's problem and not the copy's.
            }
        }
    }
}

/// <summary>
/// Reads the largest value the step's sync key reached among the rows that were just
/// staged.
/// <para>
/// Staged, and this is the whole reason it exists as its own thing. Reading the maximum
/// back from the <b>source</b> would take in rows committed since the copy started, and
/// the watermark would then stand past rows that never travelled - which are the rows the
/// next run would never ask for again. Reading it from the <b>destination</b> would take
/// in rows this step did not write, which is every row of a merge's destination that some
/// other step or the application itself put there. The staged set is exactly what is about
/// to be published, so its maximum is exactly the point the next run may start from.
/// </para>
/// </summary>
public interface IStagedWatermarkReader
{
    /// <summary>
    /// The maximum of <paramref name="expression"/> over <paramref name="stagingTable"/>
    /// as text, or null when the table is empty.
    /// </summary>
    Task<string?> ReadAsync(
        string connectionString,
        string stagingTable,
        string expression,
        CancellationToken cancellationToken);
}

/// <summary>
/// The staged maximum, from the server, as a string that survives the round trip back
/// into the next run's query.
/// </summary>
public sealed class SqlStagedWatermarkReader : IStagedWatermarkReader
{
    /// <summary>Seconds the read may take; zero means no limit.</summary>
    public int CommandTimeoutSeconds { get; init; }

    public async Task<string?> ReadAsync(
        string connectionString,
        string stagingTable,
        string expression,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // The expression is the operator's own SQL, arriving from the step's sync key the
        // same way the step's SELECT does. There is no parameterised form of a column
        // name, which is why this is the one thing in the statement that is not one.
        await using var command = new SqlCommand($"SELECT MAX({expression}) FROM {stagingTable};", connection)
        {
            CommandTimeout = CommandTimeoutSeconds
        };

        return Format(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// A watermark is kept as text, so the format has to be one the next run's predicate
    /// can compare against and one that does not depend on the machine's locale. A date
    /// rendered with <c>ToString()</c> on a Spanish server and read back on an English
    /// one is a watermark that moves by ten months.
    /// <para>
    /// It is <see cref="SqlValueText"/>'s rendering and not one of its own, because the
    /// value written here is the value <c>SourceSql</c> puts back into the next run's
    /// predicate: two renderings of the same instant are a watermark that does not
    /// compare against the column it came from. This side originally used the round-trip
    /// <c>"O"</c> format, which always writes seven fractional digits - and a
    /// <c>datetime</c> column, which the deployed schemas are full of, refuses any string
    /// with seven of them even when they are all zeros. WP 1.5b found that against a real
    /// server; the trimmed format is the one that survives both column types.
    /// </para>
    /// </summary>
    public static string? Format(object? value) =>
        value is null or DBNull ? null : SqlValueText.Format(value);
}
