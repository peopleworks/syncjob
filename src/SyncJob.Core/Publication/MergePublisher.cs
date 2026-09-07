using Microsoft.Data.SqlClient;
using SyncJob.Core.Model;

namespace SyncJob.Core.Publication;

/// <summary>
/// Matches the staged rows against the destination on the step's unique key: inserts what
/// is missing, updates what differs, and leaves alone both what is the same and what is not
/// in the staged set at all.
/// <para>
/// In SyncJob this mode is configured, documented and never called - the code that would
/// run it has no caller. So this is written rather than ported, and three things the
/// deployed and abandoned versions get wrong are fixed by construction: the merge is
/// idempotent, the batch size is the operator's, and a row missing from the staged set is
/// never taken for a deleted row. Deletions come from the tombstone ledger, because absence
/// from a filtered read is evidence of a filter, not of a delete.
/// </para>
/// </summary>
public sealed class MergePublisher : IPublisher
{
    public async Task<PublishResult> PublishAsync(
        string connectionString,
        string stagingTable,
        SyncStep step,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);

        // A publisher that runs against a step configured for something else is a wiring
        // mistake, and a wiring mistake that publishes is worse than one that stops.
        if(step.Publication.Mode != PublicationMode.Merge)
            throw new ArgumentException($"this publisher merges, and the step '{step.Name}' is configured to {step.Publication.Mode}", nameof(step));

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var columns = await AppendMergeColumns.ResolveAsync(connection, stagingTable, step, requireUniqueKey: true, cancellationToken).ConfigureAwait(false);
        var (stamp, stampValue) = AppendMergeSql.Stamp(step.Provenance);

        var sql = AppendMergeSql.Merge(stagingTable, step.DestinationTable, columns.Columns, columns.KeyColumns, stamp);
        var batchSize = AppendMergeSql.ResolveBatchSize(step.BatchSize);
        var staged = await CountStagedAsync(connection, stagingTable, step, cancellationToken).ConfigureAwait(false);

        await AppendMergeColumns.SetIdentityInsertAsync(connection, step, columns.NeedsIdentityInsert, on: true, cancellationToken).ConfigureAwait(false);

        long inserted = 0;
        long updated = 0;

        try
        {
            // Paging over staging rather than over the destination, which is what makes
            // the pages stable: the merge writes to the destination and never to the table
            // it is reading, so OFFSET/FETCH over an ORDER BY on the key visits every row
            // exactly once. Each batch is its own implicit transaction - that is the point
            // of batching, and one transaction spanning the whole set would hold locks on
            // the destination for the length of the load.
            for(long offset = 0; offset < staged; offset += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var command = new SqlCommand(sql, connection) { CommandTimeout = step.CommandTimeoutSeconds };
                command.Parameters.AddWithValue(AppendMergeSql.OffsetParameter, offset);
                command.Parameters.AddWithValue(AppendMergeSql.BatchParameter, batchSize);
                if(stampValue is not null)
                    command.Parameters.AddWithValue(AppendMergeSql.StampParameter, stampValue);

                // Read rather than counted server-side: OUTPUT $action streams one row per
                // row the merge actually changed, so nothing is buffered anywhere and a
                // matched row that did not differ produces nothing at all - which is what
                // makes the counts this returns worth reading.
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while(await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if(string.Equals(reader.GetString(0), "INSERT", StringComparison.Ordinal))
                        inserted++;
                    else
                        updated++;
                }
            }
        }
        finally
        {
            // CancellationToken.None: a cancelled run still has to put the session back
            // the way it found it, and the connection is about to be returned to a pool.
            await AppendMergeColumns.SetIdentityInsertAsync(connection, step, columns.NeedsIdentityInsert, on: false, CancellationToken.None).ConfigureAwait(false);
        }

        // Never anything but zero. A merge does not delete: see the class remarks, and
        // ITombstoneApplier for where deletions actually come from.
        return new PublishResult(inserted, updated, 0);
    }

    private static async Task<long> CountStagedAsync(
        SqlConnection connection,
        string stagingTable,
        SyncStep step,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {stagingTable};", connection)
        {
            CommandTimeout = step.CommandTimeoutSeconds
        };

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
