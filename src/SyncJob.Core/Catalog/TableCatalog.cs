using Microsoft.Data.SqlClient;

namespace SyncJob.Core.Catalog;

/// <summary>
/// A column as the server describes it, which is the only description this engine
/// trusts.
/// <para>
/// Every column list in the deployed systems is either configured by hand or produced
/// by <c>SELECT *</c>. The first drifts from the table it describes; the second cannot
/// tell that the two sides stopped agreeing. Both end the same way - the data goes
/// across shifted, with no error - so the shape is read from the catalog, at the moment
/// it is needed, on the connection that is about to use it.
/// </para>
/// </summary>
/// <param name="Name">The column's name, spelled as the catalog spells it.</param>
/// <param name="IsComputed">
/// True for a column the server calculates. It cannot be written at all - a bulk copy
/// that maps anything to one is refused by the server - so it takes no part in the
/// match in either direction.
/// </param>
/// <param name="IsIdentity">
/// True for a column the destination can generate for itself. Whether it still needs a
/// source column depends on <c>KeepIdentity</c>: see <c>ColumnMatcher.Match</c>.
/// </param>
/// <param name="IsRowVersion">
/// True for a <c>rowversion</c>. The destination stamps its own on every row it writes
/// and there is no way to ask it not to, so a source column aimed at one would be read,
/// carried across the wire and then thrown away by the server without a word.
/// </param>
/// <param name="IsGeneratedAlways">
/// True for a period column of a system-versioned table - the row-start and row-end
/// times the server maintains. Unlike a computed column it holds real data of an
/// ordinary type, so nothing about it looks unwritable until an insert is refused with
/// "Cannot insert an explicit value into a GENERATED ALWAYS column".
/// </param>
public sealed record CatalogColumn(
    string Name,
    bool IsComputed,
    bool IsIdentity,
    bool IsRowVersion = false,
    bool IsGeneratedAlways = false);

/// <summary>
/// Reads a table's columns from <c>sys.columns</c>, in <c>column_id</c> order.
/// <para>
/// One reader, because there were three: the copy engine, the append and merge
/// publishers, and the swap publisher each had their own query and their own record for
/// the answer. They agreed today. Three copies of a catalog query agree until the first
/// time one of them learns something - that a rowversion still reports its type as
/// <c>timestamp</c>, say - and then the engine has two answers to the same question and
/// no way to tell which caller got which.
/// </para>
/// </summary>
public static class TableCatalog
{
    // A rowversion column still reports its type as "timestamp", the name it had before
    // the standard took that word for something else, and TYPE_NAME on the system type
    // sees through an alias type where a join to sys.types on user_type_id would report
    // the alias's own name instead.
    private const string Projection = """
        SELECT s.ord, c.name, c.is_computed, c.is_identity,
               CASE WHEN TYPE_NAME(c.system_type_id) = 'timestamp' THEN 1 ELSE 0 END,
               CASE WHEN c.generated_always_type <> 0 THEN 1 ELSE 0 END
        FROM (VALUES {0}) AS s(ord, object_id)
        JOIN sys.columns AS c ON c.object_id = s.object_id
        ORDER BY s.ord, c.column_id;
        """;

    /// <summary>
    /// One table's columns, or an empty list when the connection cannot see it. The
    /// caller decides what an empty list means: for a destination it is an error, and
    /// for a table being probed it is an answer.
    /// </summary>
    public static async Task<IReadOnlyList<CatalogColumn>> ReadAsync(
        SqlConnection connection,
        string table,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        var sql = string.Format(
            System.Globalization.CultureInfo.InvariantCulture, Projection, "(0, OBJECT_ID(@t0))");

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
        AddTable(command, 0, table);

        var lists = await ReadListsAsync(command, count: 1, cancellationToken).ConfigureAwait(false);
        return lists[0];
    }

    /// <summary>
    /// Two tables' columns in one round trip, in the order they were asked for.
    /// <para>
    /// One trip rather than two because every caller that wants two wants them to
    /// describe the same instant: staging and its destination are compared column by
    /// column, and a schema change landing between two separate reads would produce a
    /// comparison of two tables as they never simultaneously were.
    /// </para>
    /// </summary>
    public static async Task<(IReadOnlyList<CatalogColumn> First, IReadOnlyList<CatalogColumn> Second)> ReadPairAsync(
        SqlConnection connection,
        string first,
        string second,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(first);
        ArgumentException.ThrowIfNullOrWhiteSpace(second);

        var sql = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            Projection,
            "(0, OBJECT_ID(@t0)), (1, OBJECT_ID(@t1))");

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = commandTimeoutSeconds };
        AddTable(command, 0, first);
        AddTable(command, 1, second);

        var lists = await ReadListsAsync(command, count: 2, cancellationToken).ConfigureAwait(false);
        return (lists[0], lists[1]);
    }

    private static void AddTable(SqlCommand command, int ordinal, string table) =>

        // Sized rather than inferred: OBJECT_ID takes nvarchar(776), which is what a
        // three-part name with every part at its maximum and bracketed comes to, and an
        // inferred parameter would be sized from whichever name happened to be passed
        // first and force a recompile for the next one.
        command.Parameters.Add(
            $"@t{ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            System.Data.SqlDbType.NVarChar,
            776).Value = table;

    private static async Task<List<CatalogColumn>[]> ReadListsAsync(
        SqlCommand command,
        int count,
        CancellationToken cancellationToken)
    {
        var lists = new List<CatalogColumn>[count];
        for(var i = 0; i < count; i++)
            lists[i] = new List<CatalogColumn>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while(await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lists[reader.GetInt32(0)].Add(new CatalogColumn(
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetInt32(4) == 1,
                reader.GetInt32(5) == 1));
        }

        return lists;
    }
}
