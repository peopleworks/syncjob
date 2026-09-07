using Microsoft.Data.SqlClient;

namespace SyncJob.Core.Incremental;

/// <summary>
/// Keeps each step's watermark in a table in the destination database, beside the data it
/// describes.
/// <para>
/// In the destination rather than anywhere else for the reason <c>WatermarkPlan</c> gives:
/// a watermark kept away from the rows it describes survives a restore of those rows and
/// then lies about them, and the next run reads from a point the data no longer reaches.
/// </para>
/// <para>
/// The previous value is not decoration. What an operator actually does when a load goes
/// wrong is re-run it from where it was before, and the only alternative to keeping that
/// value is guessing it.
/// </para>
/// </summary>
public sealed class SqlWatermarkStore : IWatermarkStore
{
    /// <summary>Where the watermarks go when the step's plan does not name a table.</summary>
    public const string DefaultTable = "dbo.SyncJobWatermark";

    private readonly string _table;

    /// <summary>
    /// The table comes in here rather than through <see cref="IWatermarkStore"/> because
    /// the interface carries a job and a step and no plan, and <c>WatermarkPlan.StateTable</c>
    /// is a per-step setting: a caller that honours it constructs one store per table.
    /// </summary>
    public SqlWatermarkStore(string? stateTable = null) =>
        _table = string.IsNullOrWhiteSpace(stateTable) ? DefaultTable : stateTable;

    public async Task<Watermark?> ReadAsync(
        string connectionString,
        string jobId,
        string stepId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureTableAsync(connection, cancellationToken).ConfigureAwait(false);

        // One statement, so the value and the previous value are the ones that were there
        // together. Two reads could land either side of a write and report a pair that
        // never existed - a value with its own self as its predecessor.
        var sql = $"SELECT [Value], [PreviousValue], [UpdatedAt] FROM {_table} WHERE [JobId] = @job AND [StepId] = @step;";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@step", stepId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if(!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new Watermark(
            reader.GetString(0),
            await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1),
            reader.GetDateTimeOffset(2));
    }

    public async Task WriteAsync(
        string connectionString,
        string jobId,
        string stepId,
        string value,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureTableAsync(connection, cancellationToken).ConfigureAwait(false);

        // One statement, and the shift happens inside it: in an UPDATE every right-hand
        // side is evaluated against the row as it was, so [PreviousValue] = [Value] takes
        // the old value even though [Value] is being assigned in the same SET.
        //
        // WHEN MATCHED AND [Value] <> @value, not a bare WHEN MATCHED. A run that finds
        // nothing new writes the same watermark again, and an unguarded shift would put
        // that value into PreviousValue as well - quietly destroying the one thing an
        // operator needs to re-run from after a bad load. It also keeps UpdatedAt meaning
        // what its name says: when the watermark moved, not when a run last finished.
        var sql = $"""
            MERGE {_table} WITH (HOLDLOCK) AS t
            USING (SELECT @job AS [JobId], @step AS [StepId]) AS s
                ON t.[JobId] = s.[JobId] AND t.[StepId] = s.[StepId]
            WHEN MATCHED AND t.[Value] <> @value THEN UPDATE SET
                t.[PreviousValue] = t.[Value],
                t.[Value] = @value,
                t.[UpdatedAt] = SYSDATETIMEOFFSET()
            WHEN NOT MATCHED BY TARGET THEN
                INSERT ([JobId], [StepId], [Value], [PreviousValue], [UpdatedAt])
                VALUES (@job, @step, @value, NULL, SYSDATETIMEOFFSET());
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@job", jobId);
        command.Parameters.AddWithValue("@step", stepId);
        command.Parameters.AddWithValue("@value", value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Its own batch, never folded into the read or the write: a batch that names a table
    /// which does not exist yet fails to compile as a whole, whichever branch of the
    /// <c>IF</c> would have run.
    /// </summary>
    private async Task EnsureTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var sql = $"""
            IF OBJECT_ID(N'{_table}', N'U') IS NULL
                CREATE TABLE {_table} (
                    [JobId]         nvarchar(200)    NOT NULL,
                    [StepId]        nvarchar(200)    NOT NULL,
                    [Value]         nvarchar(400)    NOT NULL,
                    [PreviousValue] nvarchar(400)        NULL,
                    [UpdatedAt]     datetimeoffset(7) NOT NULL,
                    PRIMARY KEY ([JobId], [StepId])
                );
            """;

        try
        {
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch(SqlException e) when(e.Number == 2714)
        {
            // 2714 is "there is already an object named". Two runs of the same job starting
            // together can both find the table missing and both try to create it; the one
            // that loses has exactly what it wanted anyway.
        }
    }
}
