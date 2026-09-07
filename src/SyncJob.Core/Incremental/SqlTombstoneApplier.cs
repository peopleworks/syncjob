using System.Globalization;
using Microsoft.Data.SqlClient;
using SyncJob.Core.Model;

namespace SyncJob.Core.Incremental;

/// <summary>
/// What the ledger's own columns are called.
/// <para>
/// The ledger is written by the source application, so its shape is a contract with
/// software we do not own, and <c>TombstoneLedger</c> has no field for any of these names.
/// They are settings here rather than constants for that reason: the defaults are the
/// deployed spelling, and a source that spells them differently is configured, not
/// patched.
/// </para>
/// </summary>
public sealed record TombstoneLedgerColumns
{
    /// <summary>The ledger's own increasing id. It is what "applied this far" means.</summary>
    public string Id { get; init; } = "Id";

    /// <summary>The key's parts joined by a separator. Only <c>TombstoneKeyFormat.Legacy</c> reads it.</summary>
    public string KeyValue { get; init; } = "KeyValue";

    /// <summary>
    /// The flag set when a deletion has been applied. Required when the ledger says
    /// <c>MarkProcessed</c>; ignored otherwise.
    /// </summary>
    public string? Processed { get; init; } = "Processed";

    /// <summary>
    /// The column naming which table a deletion belongs to, for a ledger shared by several
    /// steps. Null - the default - means the ledger is this step's own and every row in it
    /// is this step's, which is the only shape the model can currently describe.
    /// </summary>
    public string? TableName { get; init; }
}

/// <summary>
/// Reads what the source recorded as deleted and deletes it from the destination.
/// <para>
/// The key is read left to right with no limit on its parts - see <see cref="TombstoneKey"/>
/// for the deployed alternative and what it silently costs - and a key that does not match
/// the step's delete key is reported instead of being joined on and missed.
/// </para>
/// <para>
/// The two writes cannot be one transaction, because they are in two databases: the
/// deletions are in the destination and the mark is in the source. Rather than reach for a
/// distributed transaction, they are ordered so that the only surviving failure is a
/// harmless one. The source's ledger rows are locked first and stay locked; the destination's
/// deletions commit; only then does the mark commit. A failure before the destination
/// commits marks nothing, and a failure after it re-applies deletions that are already
/// applied - and deleting a row that is not there deletes nothing. What cannot happen is a
/// deletion marked processed and never applied.
/// </para>
/// </summary>
public sealed class SqlTombstoneApplier : ITombstoneApplier
{
    /// <summary>Where the destination records what it has applied when it may not write to the source.</summary>
    public const string DefaultHighWaterTable = "dbo.SyncJobTombstoneHighWater";

    // 2100 parameters is the hard limit on one command, and the delete spends one per key
    // part per row. Staying well under it leaves room for the statement's own parameters
    // and keeps the plan cache from filling with a shape per batch size.
    private const int MaxParametersPerCommand = 2_000;

    private readonly TombstoneLedgerColumns _columns;
    private readonly string _highWaterTable;

    public SqlTombstoneApplier(TombstoneLedgerColumns? ledgerColumns = null, string? highWaterTable = null)
    {
        _columns = ledgerColumns ?? new TombstoneLedgerColumns();
        _highWaterTable = string.IsNullOrWhiteSpace(highWaterTable) ? DefaultHighWaterTable : highWaterTable;
    }

    /// <summary>
    /// Applies every deletion whose key has the shape the step's delete key implies, and
    /// returns the rest in <see cref="TombstoneResult.Unapplied"/> rather than throwing.
    /// <para>
    /// A malformed key is a fact about the source's data, not a failure of the run, and the
    /// other deletions are still correct. What must not happen is the deployed behaviour,
    /// where nothing is deleted and nothing is said - so an unapplied row is always named,
    /// with its ledger id, and it is the caller's job to put that somewhere a person reads.
    /// </para>
    /// </summary>
    public async Task<TombstoneResult> ApplyAsync(
        string sourceConnectionString,
        string destinationConnectionString,
        SyncStep step,
        TombstoneLedger ledger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(ledger);

        if(string.IsNullOrWhiteSpace(ledger.Table))
            throw new InvalidOperationException($"the step '{Describe(step)}' reads a tombstone ledger that names no table");

        var keyColumns = step.FieldMaps
            .Where(x => x.IsDeleteKey)
            .Select(x => x.Target ?? x.Source)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();

        if(keyColumns.Count == 0)
            throw new InvalidOperationException($"the step '{Describe(step)}' reads a tombstone ledger but declares no delete key to apply it by");

        if(ledger.MarkProcessed && string.IsNullOrWhiteSpace(_columns.Processed))
            throw new InvalidOperationException($"the step '{Describe(step)}' marks its ledger processed but no processed column is configured");

        await using var destination = new SqlConnection(destinationConnectionString);
        await destination.OpenAsync(cancellationToken);

        var appliedThrough = 0L;
        if(!ledger.MarkProcessed)
        {
            await EnsureHighWaterTableAsync(destination, cancellationToken);
            appliedThrough = await ReadHighWaterAsync(destination, step, ledger, cancellationToken);
        }

        await using var source = new SqlConnection(sourceConnectionString);
        await source.OpenAsync(cancellationToken);

        // The source transaction opens before the ledger is read and stays open across the
        // destination's work, so nothing else can take the same rows in the meantime. It is
        // held longer than a transaction normally would be, and that is the price of never
        // marking a deletion that was not applied.
        await using var sourceTransaction = (SqlTransaction)await source.BeginTransactionAsync(cancellationToken);

        var (entries, problems) = await ReadLedgerAsync(
            source, sourceTransaction, step, ledger, keyColumns, appliedThrough, cancellationToken);

        if(entries.Count == 0)
        {
            await sourceTransaction.RollbackAsync(cancellationToken);

            return new TombstoneResult(0, problems);
        }

        var deleted = await ApplyToDestinationAsync(
            destination, step, ledger, keyColumns, entries, problems, appliedThrough, cancellationToken);

        if(ledger.MarkProcessed)
        {
            await MarkProcessedAsync(source, sourceTransaction, ledger, entries, cancellationToken);
            await sourceTransaction.CommitAsync(cancellationToken);
        }
        else
        {
            await sourceTransaction.RollbackAsync(cancellationToken);
        }

        return new TombstoneResult(deleted, problems);
    }

    private async Task<long> ApplyToDestinationAsync(
        SqlConnection destination,
        SyncStep step,
        TombstoneLedger ledger,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<LedgerEntry> entries,
        IReadOnlyList<TombstoneKeyProblem> problems,
        long appliedThrough,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await destination.BeginTransactionAsync(cancellationToken);

        // The same key can be recorded deleted more than once - a row deleted, restored and
        // deleted again. Joining on the duplicates would ask the destination to delete a row
        // it has already deleted in the same statement, so they are collapsed here where it
        // is free.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tuples = new List<object[]>();
        foreach(var entry in entries)
        {
            if(seen.Add(string.Join('\u001f', entry.Parts.Select(Text))))
                tuples.Add(entry.Parts);
        }

        var perBatch = Math.Max(1, MaxParametersPerCommand / keyColumns.Count);
        long deleted = 0;

        for(var offset = 0; offset < tuples.Count; offset += perBatch)
        {
            var batch = tuples.GetRange(offset, Math.Min(perBatch, tuples.Count - offset));
            deleted += await DeleteAsync(destination, transaction, step, keyColumns, batch, cancellationToken);
        }

        if(!ledger.MarkProcessed)
        {
            // The high-water mark and the deletions are in the same database, so these two
            // genuinely are one transaction: a run that deletes and then fails to record how
            // far it got would delete the same rows again forever.
            //
            // It stops short of the first malformed row rather than jumping over it. The run
            // is about to fail loudly; when the operator fixes that ledger row, the next run
            // has to be able to reach it.
            var reached = entries.Max(x => x.Id);
            if(problems.Count > 0)
                reached = Math.Min(reached, problems.Min(x => x.LedgerId) - 1);

            if(reached > appliedThrough)
                await WriteHighWaterAsync(destination, transaction, step, ledger, reached, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private async Task<(List<LedgerEntry> Entries, List<TombstoneKeyProblem> Problems)> ReadLedgerAsync(
        SqlConnection source,
        SqlTransaction transaction,
        SyncStep step,
        TombstoneLedger ledger,
        IReadOnlyList<string> keyColumns,
        long appliedThrough,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> selected = ledger.KeyFormat == TombstoneKeyFormat.Legacy
            ? new[] { _columns.KeyValue }
            : keyColumns;

        var filters = new List<string>();

        // UPDLOCK only where the rows are about to be updated. Where the source is
        // read-only to us there is nothing to protect and no reason to take a lock on
        // somebody else's table.
        var hint = ledger.MarkProcessed ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;

        if(ledger.MarkProcessed)
            filters.Add($"{Quote(_columns.Processed!)} = 0");
        else
            filters.Add($"{Quote(_columns.Id)} > @appliedThrough");

        if(!string.IsNullOrWhiteSpace(_columns.TableName))
            filters.Add($"{Quote(_columns.TableName!)} = @destinationTable");

        var sql = $"""
            SELECT {Quote(_columns.Id)}, {string.Join(", ", selected.Select(Quote))}
            FROM {ledger.Table}{hint}
            WHERE {string.Join(" AND ", filters)}
            ORDER BY {Quote(_columns.Id)};
            """;

        await using var command = new SqlCommand(sql, source, transaction) { CommandTimeout = step.CommandTimeoutSeconds };
        command.Parameters.AddWithValue("@appliedThrough", appliedThrough);
        command.Parameters.AddWithValue("@destinationTable", step.DestinationTable);

        var entries = new List<LedgerEntry>();
        var problems = new List<TombstoneKeyProblem>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken))
        {
            var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);

            if(ledger.KeyFormat == TombstoneKeyFormat.Legacy)
            {
                var keyValue = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetValue(1).ToString();
                var parts = TombstoneKey.TryReadLegacy(id, keyValue, ledger.LegacySeparator, keyColumns.Count, out var problem);

                if(parts is null)
                    problems.Add(problem!);
                else
                    entries.Add(new LedgerEntry(id, [.. parts.Cast<object>()]));

                continue;
            }

            var values = new object[keyColumns.Count];
            var nulls = 0;
            for(var i = 0; i < keyColumns.Count; i++)
            {
                values[i] = reader.GetValue(i + 1);
                if(values[i] is DBNull)
                    nulls++;
            }

            // A null part joins to nothing, which is the same silent miss by another route.
            if(nulls > 0)
                problems.Add(new TombstoneKeyProblem(id, string.Join(ledger.LegacySeparator, values.Select(Text)), keyColumns.Count - nulls, keyColumns.Count));
            else
                entries.Add(new LedgerEntry(id, values));
        }

        return (entries, problems);
    }

    private static async Task<int> DeleteAsync(
        SqlConnection destination,
        SqlTransaction transaction,
        SyncStep step,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<object[]> tuples,
        CancellationToken cancellationToken)
    {
        var rows = new List<string>(tuples.Count);
        for(var i = 0; i < tuples.Count; i++)
            rows.Add("(" + string.Join(", ", keyColumns.Select((_, k) => $"@k{i}_{k}")) + ")");

        // The keys arrive as the ledger stored them, usually as text, and are compared
        // against destination columns that are usually not. That is deliberate rather than
        // careless: SQL Server converts the side with the lower type precedence, which is
        // the parameter, so the destination column is never wrapped in a conversion and its
        // index is still usable. A value the column's type cannot hold raises an error,
        // which is the right answer for a ledger row nobody can act on.
        var sql = $"""
            DELETE d
            FROM {step.DestinationTable} AS d
            INNER JOIN (VALUES {string.Join(", ", rows)}) AS v ({string.Join(", ", keyColumns.Select(Quote))})
                ON {string.Join(" AND ", keyColumns.Select(x => $"d.{Quote(x)} = v.{Quote(x)}"))};
            """;

        await using var command = new SqlCommand(sql, destination, transaction) { CommandTimeout = step.CommandTimeoutSeconds };
        for(var i = 0; i < tuples.Count; i++)
        {
            for(var k = 0; k < keyColumns.Count; k++)
                command.Parameters.AddWithValue($"@k{i}_{k}", tuples[i][k]);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MarkProcessedAsync(
        SqlConnection source,
        SqlTransaction transaction,
        TombstoneLedger ledger,
        IReadOnlyList<LedgerEntry> entries,
        CancellationToken cancellationToken)
    {
        for(var offset = 0; offset < entries.Count; offset += MaxParametersPerCommand)
        {
            var batch = entries.Skip(offset).Take(MaxParametersPerCommand).ToList();
            var markers = batch.Select((_, i) => $"@i{i}").ToList();

            var sql = $"""
                UPDATE {ledger.Table}
                SET {Quote(_columns.Processed!)} = 1
                WHERE {Quote(_columns.Id)} IN ({string.Join(", ", markers)});
                """;

            await using var command = new SqlCommand(sql, source, transaction);
            for(var i = 0; i < batch.Count; i++)
                command.Parameters.AddWithValue(markers[i], batch[i].Id);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Its own batch: a batch that names a table which does not exist yet fails to compile
    /// as a whole, whichever branch of the <c>IF</c> would have run.
    /// </summary>
    private async Task EnsureHighWaterTableAsync(SqlConnection destination, CancellationToken cancellationToken)
    {
        var sql = $"""
            IF OBJECT_ID(N'{_highWaterTable}', N'U') IS NULL
                CREATE TABLE {_highWaterTable} (
                    [StepId]           nvarchar(200)     NOT NULL,
                    [Ledger]           nvarchar(400)     NOT NULL,
                    [AppliedThroughId] bigint            NOT NULL,
                    [UpdatedAt]        datetimeoffset(7) NOT NULL,
                    PRIMARY KEY ([StepId], [Ledger])
                );
            """;

        try
        {
            await using var command = new SqlCommand(sql, destination);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch(SqlException e) when(e.Number == 2714)
        {
            // Already there, because another run created it a moment ago.
        }
    }

    private async Task<long> ReadHighWaterAsync(
        SqlConnection destination,
        SyncStep step,
        TombstoneLedger ledger,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT [AppliedThroughId] FROM {_highWaterTable} WHERE [StepId] = @step AND [Ledger] = @ledger;";

        await using var command = new SqlCommand(sql, destination);
        command.Parameters.AddWithValue("@step", step.Id);
        command.Parameters.AddWithValue("@ledger", ledger.Table);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task WriteHighWaterAsync(
        SqlConnection destination,
        SqlTransaction transaction,
        SyncStep step,
        TombstoneLedger ledger,
        long appliedThrough,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            MERGE {_highWaterTable} WITH (HOLDLOCK) AS t
            USING (SELECT @step AS [StepId], @ledger AS [Ledger]) AS s
                ON t.[StepId] = s.[StepId] AND t.[Ledger] = s.[Ledger]
            WHEN MATCHED THEN UPDATE SET
                t.[AppliedThroughId] = @appliedThrough,
                t.[UpdatedAt] = SYSDATETIMEOFFSET()
            WHEN NOT MATCHED BY TARGET THEN
                INSERT ([StepId], [Ledger], [AppliedThroughId], [UpdatedAt])
                VALUES (@step, @ledger, @appliedThrough, SYSDATETIMEOFFSET());
            """;

        await using var command = new SqlCommand(sql, destination, transaction);
        command.Parameters.AddWithValue("@step", step.Id);
        command.Parameters.AddWithValue("@ledger", ledger.Table);
        command.Parameters.AddWithValue("@appliedThrough", appliedThrough);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Text(object value) =>
        value is DBNull or null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Describe(SyncStep step) =>
        step.Name is { Length: > 0 } ? step.Name : step.Id;

    /// <summary>
    /// Column names, unlike table names, are configuration rather than something an
    /// operator spells with a schema, so they are bracketed.
    /// </summary>
    private static string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private sealed record LedgerEntry(long Id, object[] Parts);
}
