using Microsoft.Data.SqlClient;
using SyncJob.Core.Catalog;
using SyncJob.Core.Model;

namespace SyncJob.Core.Publication;

/// <summary>
/// The columns an append or a merge actually moves, and the key it matches on.
/// </summary>
internal sealed class PublicationColumnSet
{
    /// <summary>Every column that travels from staging, in the destination's own order.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The columns a merge matches on. Empty for an append.</summary>
    public required IReadOnlyList<string> KeyColumns { get; init; }

    /// <summary>
    /// True when an identity column is among <see cref="Columns"/>, so the statement has
    /// to be wrapped in <c>SET IDENTITY_INSERT</c> to be allowed to supply its values.
    /// </summary>
    public bool NeedsIdentityInsert { get; init; }
}

/// <summary>
/// Works out which columns a publication may move, by reading both catalogs and matching
/// them by name.
/// <para>
/// Both sides, never one. The failure this exists to prevent is the one SyncJob's service
/// path still has: publish with <c>SELECT *</c> and the day someone adds a column to one
/// of the two tables the data goes in shifted, with no error, for as long as the types
/// happen to line up. A name present on one side and missing on the other is therefore
/// refused here rather than guessed at by position.
/// </para>
/// <para>
/// Table names are used as the operator wrote them - <c>dbo.Customer</c>, not
/// <c>[dbo].[Customer]</c> - because that is how every job definition in the deployed
/// systems spells them, and bracketing a two-part name as a single identifier would break
/// every one of them. Column names, which are discovered rather than configured, are
/// bracketed.
/// </para>
/// </summary>
internal static class AppendMergeColumns
{
    public static async Task<PublicationColumnSet> ResolveAsync(
        SqlConnection connection,
        string stagingTable,
        SyncStep step,
        bool requireUniqueKey,
        CancellationToken cancellationToken)
    {
        var destinationTable = step.DestinationTable;

        // Both shapes from one round trip, so they describe the same instant: the
        // whole point of the comparison below is that the two tables agree, and reading
        // them separately would let a schema change land between the two reads and be
        // compared against a table as it no longer is.
        var (destination, staging) = await TableCatalog.ReadPairAsync(
            connection, destinationTable, stagingTable, step.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        if(destination.Count == 0)
            throw new InvalidOperationException($"the destination table {destinationTable} has no columns, or is not there");

        if(staging.Count == 0)
            throw new InvalidOperationException($"the staging table {stagingTable} has no columns, or is not there");

        var stagingNames = staging.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var excluded = step.FieldMaps
            .Where(x => x.IsExcluded)
            .Select(x => x.Target ?? x.Source)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The provenance column is filled in by the publisher itself, so it is not
        // expected in staging and must not be required to be there.
        if(step.Provenance is { } stamp && !string.IsNullOrWhiteSpace(stamp.Column))
            excluded.Add(stamp.Column);

        var keys = step.FieldMaps
            .Where(x => x.IsUniqueKey)
            .Select(x => x.Target ?? x.Source)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();

        if(requireUniqueKey && keys.Count == 0)
        {
            throw new InvalidOperationException(
                $"the step '{Describe(step)}' merges but declares no unique key, so no row could be matched to another");
        }

        var keyNames = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keepIdentity = step.Publication.KeepIdentity;
        var columns = new List<string>();
        var needsIdentityInsert = false;

        foreach(var column in destination)
        {
            // A computed column has no value of its own to write and a rowversion is
            // stamped by the server, so naming either one in an INSERT is an error rather
            // than a preference. Neither is ever offered to the operator to exclude by hand.
            if(column.IsComputed || column.IsRowVersion)
                continue;

            if(excluded.Contains(column.Name))
            {
                if(keyNames.Contains(column.Name))
                {
                    throw new InvalidOperationException(
                        $"the step '{Describe(step)}' matches on {column.Name} and also excludes it, so there is nothing left to match on");
                }

                continue;
            }

            if(column.IsIdentity)
            {
                // Refused rather than resolved one way or the other. Matching on an
                // identity column needs the source's values for it and KeepIdentity says
                // they must not travel; honouring either setting means silently
                // overriding the other, and the operator who reads the job back would
                // never see which one lost.
                if(keyNames.Contains(column.Name) && !keepIdentity)
                {
                    throw new InvalidOperationException(
                        $"the step '{Describe(step)}' merges on the identity column {column.Name} but does not keep identity " +
                        "values, so no staged row could ever match a destination row");
                }

                if(!keepIdentity)
                    continue;

                needsIdentityInsert = true;
            }

            if(!stagingNames.Contains(column.Name))
            {
                throw new InvalidOperationException(
                    $"the destination {destinationTable} has a column {column.Name} that the staging table {stagingTable} " +
                    "does not, so the two have drifted apart; exclude the column with a field map or fix the staging table - " +
                    "the one thing that must not happen is publishing by position");
            }

            columns.Add(column.Name);
        }

        if(columns.Count == 0)
            throw new InvalidOperationException($"the step '{Describe(step)}' has no columns left to publish once its exclusions are applied");

        var resolved = columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var key in keys.Where(x => !resolved.Contains(x)))
            throw new InvalidOperationException($"the step '{Describe(step)}' matches on {key}, which the destination {destinationTable} does not have");

        return new PublicationColumnSet
        {
            Columns = columns,

            // Taken from the destination's own spelling rather than from the field map's,
            // so the ON clause, the SET list and the column list cannot disagree about case.
            KeyColumns = columns.Where(x => keyNames.Contains(x)).ToList(),
            NeedsIdentityInsert = needsIdentityInsert
        };
    }

    /// <summary>
    /// Turns <c>IDENTITY_INSERT</c> on or off for the step's destination, when the
    /// resolved column set needs it.
    /// <para>
    /// Its own statement rather than a prefix on the publication: the setting belongs to
    /// the session, and batching it with the statement that does the work makes the row
    /// count that comes back depend on how the driver aggregates statements that have no
    /// row count of their own.
    /// </para>
    /// </summary>
    public static async Task SetIdentityInsertAsync(
        SqlConnection connection,
        SyncStep step,
        bool needed,
        bool on,
        CancellationToken cancellationToken)
    {
        if(!needed)
            return;

        var sql = $"SET IDENTITY_INSERT {step.DestinationTable} {(on ? "ON" : "OFF")};";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = step.CommandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Describe(SyncStep step) =>
        step.Name is { Length: > 0 } ? step.Name : step.Id;
}
