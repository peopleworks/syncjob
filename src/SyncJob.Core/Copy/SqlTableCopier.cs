using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SyncJob.Core.Catalog;

namespace SyncJob.Core.Copy;

/// <summary>
/// Copies a result set from one SQL Server to another by handing the source's reader
/// straight to <c>SqlBulkCopy</c> on the destination.
/// <para>
/// Nothing accumulates between the two. The three copy paths this replaces all read the
/// whole source into a <c>List&lt;object[]&gt;</c> and only then start writing, which
/// is why a table larger than the machine's memory cannot be copied today at any batch
/// size - the batch size only governs what is written, never what is held. Here the
/// only thing alive at once is whatever the driver has in flight, so a copy of ten rows
/// and a copy of a hundred million cost the same resident memory.
/// </para>
/// </summary>
public sealed class SqlTableCopier : ITableCopier
{
    /// <summary>
    /// How often a copy in flight reports progress.
    /// <para>
    /// Often enough that a long copy visibly moves and an operator can tell a slow job
    /// from a stuck one, rare enough that the callback is never what the copy is
    /// waiting on.
    /// </para>
    /// </summary>
    public const int ProgressEveryRows = 10_000;

    public async Task<CopyResult> CopyAsync(CopyRequest request, string targetTable, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTable);

        var stopwatch = Stopwatch.StartNew();

        // The catalog read and the bulk copy share one connection to the destination:
        // the shape that is checked is then the shape that is written to.
        await using var destination = new SqlConnection(request.DestinationConnectionString);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);

        // From the catalog rather than from anything the job carries: a column list
        // written down in a configuration drifts from the table it describes, and the
        // copy that trusts it is the one that writes the rows in shifted. The read and
        // the bulk copy share this connection, so the shape that is checked is the
        // shape that is written to.
        var destinationColumns = await TableCatalog.ReadAsync(
            destination, targetTable, request.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        if(destinationColumns.Count == 0)
        {
            throw new InvalidOperationException(
                $"the destination table {targetTable} does not exist on the destination, or the connection " +
                "cannot see it");
        }

        await using var source = new SqlConnection(request.SourceConnectionString);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = CreateSourceCommand(request, source);

        // SequentialAccess is what lets a varbinary(max) travel without being assembled
        // in memory first; the reader hands the driver the bytes as they arrive.
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);

        if(reader.FieldCount == 0)
        {
            throw new InvalidOperationException(
                $"the source returned no result set, so there is nothing to copy into {targetTable}" +
                (request.IsStoredProcedure ? ": a procedure that only returns a count has no rows to give" : string.Empty));
        }

        var schema = await reader.GetColumnSchemaAsync(cancellationToken).ConfigureAwait(false);
        var sourceColumns = schema.Select(x => x.ColumnName ?? string.Empty).ToList();

        // A user-defined type - geography, geometry, hierarchyid - is the one kind of
        // column whose CLR type the driver cannot name without Microsoft.SqlServer.Types
        // deployed, and it reports that as a null DataType rather than by throwing.
        // See RowBufferingReader for what is done about it.
        var userDefined = schema.Select(x => x.DataType is null).ToArray();

        var matches = ColumnMatcher.Match(
            sourceColumns,
            destinationColumns,
            request.ColumnMap,
            request.KeepIdentity,
            targetTable,
            request.ColumnsNotFromSource);

        var progress = request.Progress ?? NullProgress.Instance;

        var options = SqlBulkCopyOptions.TableLock;
        if(request.KeepIdentity)
            options |= SqlBulkCopyOptions.KeepIdentity;

        using var bulk = new SqlBulkCopy(destination, options, externalTransaction: null)
        {
            // Passed through as the caller wrote it: SqlBulkCopy takes a one-, two- or
            // three-part name, bracketed or not, and quotes it itself. A name parser
            // here would only be one more place for a quoting bug to live.
            DestinationTableName = targetTable,
            EnableStreaming = true,
            BatchSize = request.BatchSize,
            BulkCopyTimeout = request.CommandTimeoutSeconds,
            NotifyAfter = ProgressEveryRows
        };

        bulk.SqlRowsCopied += (_, e) => progress.Report(new CopyProgress(e.RowsCopied, stopwatch.Elapsed));

        foreach(var match in matches)
            bulk.ColumnMappings.Add(match.SourceColumn, match.DestinationColumn);

        var rowSource = ChooseRowSource(reader, matches, userDefined);

        try
        {
            await bulk.WriteToServerAsync(rowSource, cancellationToken).ConfigureAwait(false);
        }
        catch(Exception exception) when(cancellationToken.IsCancellationRequested)
        {
            // The rows already sent are committed - a copy outside a transaction commits
            // as it goes - so the caller is told how many rather than left to guess
            // whether the destination is empty.
            progress.Report(new CopyProgress(bulk.RowsCopied64, stopwatch.Elapsed));

            throw new CopyCanceledException(targetTable, bulk.RowsCopied64, cancellationToken, exception);
        }

        var copied = bulk.RowsCopied64;

        // Reported on the way out as well as during, so that a caller watching progress
        // always ends on the real total rather than on the last multiple of
        // ProgressEveryRows.
        progress.Report(new CopyProgress(copied, stopwatch.Elapsed));

        return new CopyResult(copied, stopwatch.Elapsed);
    }

    /// <summary>
    /// The reader itself where it can be used as it is, and a one-row buffer over it
    /// where it cannot.
    /// <para>
    /// <c>SqlBulkCopy</c> reads the source in the destination's column order. While that
    /// order runs forwards through the source's, the raw reader is handed over
    /// untouched and the copy costs nothing per row. It is only when the two orders
    /// disagree - or when a column is a type the driver cannot materialise without a
    /// spatial assembly - that a row has to be buffered.
    /// </para>
    /// </summary>
    private static DbDataReader ChooseRowSource(
        SqlDataReader reader,
        IReadOnlyList<ColumnMatch> matches,
        bool[] userDefined)
    {
        var readsForwards = true;
        for(var i = 1; i < matches.Count; i++)
        {
            if(matches[i].SourceOrdinal <= matches[i - 1].SourceOrdinal)
            {
                readsForwards = false;
                break;
            }
        }

        return readsForwards && !userDefined.Any(x => x)
            ? reader
            : new RowBufferingReader(reader, userDefined);
    }

    private static SqlCommand CreateSourceCommand(CopyRequest request, SqlConnection connection)
    {
        var command = new SqlCommand(request.Sql, connection)
        {
            CommandType = request.IsStoredProcedure ? CommandType.StoredProcedure : CommandType.Text,

            // Zero is no limit on both sides of this assignment, which is the one thing
            // a copy of a very large table needs it to be.
            CommandTimeout = request.CommandTimeoutSeconds
        };

        foreach(var (name, value) in request.Parameters)
            command.Parameters.AddWithValue(name.StartsWith('@') ? name : "@" + name, value ?? DBNull.Value);

        return command;
    }

    /// <summary>
    /// Somewhere to report to when the caller did not ask for progress, so that the
    /// copy reports unconditionally instead of testing for null on every batch.
    /// </summary>
    private sealed class NullProgress : IProgress<CopyProgress>
    {
        internal static readonly NullProgress Instance = new();

        public void Report(CopyProgress value)
        {
        }
    }
}

/// <summary>
/// A copy that was stopped part way, carrying what had already been written.
/// <para>
/// A cancelled copy is not a copy that did nothing. It runs outside a transaction, so
/// whatever batches were sent before the cancellation are committed and still in the
/// destination table. The caller is the one who has to clean that up, and it can only
/// do so if it is told - which is the difference between this and an ordinary
/// <see cref="OperationCanceledException"/>, whose standard handling still catches it.
/// </para>
/// </summary>
public sealed class CopyCanceledException : OperationCanceledException
{
    public CopyCanceledException(
        string targetTable,
        long rowsWritten,
        CancellationToken cancellationToken,
        Exception? innerException)
        : base(
            $"the copy into {targetTable} was cancelled after {rowsWritten:N0} rows had been written; " +
            "those rows are committed and are still there",
            innerException,
            cancellationToken)
    {
        TargetTable = targetTable;
        RowsWritten = rowsWritten;
    }

    public string TargetTable { get; }

    /// <summary>How many rows had reached the destination when the copy stopped.</summary>
    public long RowsWritten { get; }
}
