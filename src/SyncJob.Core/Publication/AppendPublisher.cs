using Microsoft.Data.SqlClient;
using SyncJob.Core.Model;

namespace SyncJob.Core.Publication;

/// <summary>
/// Adds the staged rows to the destination and touches nothing that was already there.
/// <para>
/// The whole statement is an explicit column list on both sides. SyncJob's service path
/// publishes with <c>INSERT INTO final SELECT * FROM stage</c>, and the day someone adds a
/// column to one of the two tables that insert starts shifting the data one column across
/// without failing, for as long as the types line up. Its CLI path fixed this and wrote
/// down why; the service never did. Here there is no form of the statement that could do
/// it - the column list is discovered from both catalogs and matched by name.
/// </para>
/// </summary>
public sealed class AppendPublisher : IPublisher
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
        if(step.Publication.Mode != PublicationMode.Append)
            throw new ArgumentException($"this publisher appends, and the step '{step.Name}' is configured to {step.Publication.Mode}", nameof(step));

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var columns = await AppendMergeColumns.ResolveAsync(connection, stagingTable, step, requireUniqueKey: false, cancellationToken);
        var (stamp, stampValue) = AppendMergeSql.Stamp(step.Provenance);

        var sql = AppendMergeSql.Append(stagingTable, step.DestinationTable, columns.Columns, stamp);

        await AppendMergeColumns.SetIdentityInsertAsync(connection, step, columns.NeedsIdentityInsert, on: true, cancellationToken);

        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = step.CommandTimeoutSeconds };
            if(stampValue is not null)
                command.Parameters.AddWithValue(AppendMergeSql.StampParameter, stampValue);

            var inserted = await command.ExecuteNonQueryAsync(cancellationToken);

            // Nothing is updated and nothing is deleted, by definition: that is what makes
            // this the mode it is safe to point at a table something else also writes to.
            return new PublishResult(inserted, 0, 0);
        }
        finally
        {
            // CancellationToken.None: a cancelled run still has to put the session back
            // the way it found it, and the connection is about to be returned to a pool.
            await AppendMergeColumns.SetIdentityInsertAsync(connection, step, columns.NeedsIdentityInsert, on: false, CancellationToken.None);
        }
    }
}
