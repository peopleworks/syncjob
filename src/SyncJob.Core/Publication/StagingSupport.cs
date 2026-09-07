using System.Text;
using Microsoft.Data.SqlClient;

namespace SyncJob.Core.Publication;

/// <summary>
/// A two-part SQL Server object name, parsed once so that everything this package
/// builds quotes it the same way.
/// <para>
/// An object name is the one thing in a statement that cannot be a parameter, so it
/// goes through here instead of being pasted in: brackets are escaped, a name that
/// arrives already bracketed is not bracketed twice, and a schema that was left off is
/// filled in rather than being resolved differently by whoever happens to be connected.
/// </para>
/// </summary>
internal readonly record struct SqlObjectName(string Schema, string Name)
{
    public const string DefaultSchema = "dbo";

    /// <summary>
    /// Splits <c>schema.table</c>, <c>[schema].[table]</c> or a bare <c>table</c>.
    /// A three-part name is refused rather than trimmed: dropping the database part
    /// quietly would publish into the connection's database instead of the named one.
    /// </summary>
    public static SqlObjectName Parse(string value, string? defaultSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var parts = Split(value);
        if(parts.Count > 2)
        {
            throw new ArgumentException(
                $"'{value}' is a {parts.Count}-part name; this engine writes into the database it is " +
                "connected to, so a table is named as 'schema.table' at most.",
                nameof(value));
        }

        var name = parts[^1].Trim();
        if(name.Length == 0)
            throw new ArgumentException($"'{value}' has no table name in it.", nameof(value));

        var schema = parts.Count == 2 ? parts[0].Trim() : string.Empty;
        if(schema.Length == 0)
            schema = string.IsNullOrWhiteSpace(defaultSchema) ? DefaultSchema : defaultSchema!;

        return new SqlObjectName(schema, name);
    }

    /// <summary>The name as it goes into a statement.</summary>
    public string Quoted => $"{Bracket(Schema)}.{Bracket(Name)}";

    /// <summary>
    /// The name as a string literal, for <c>OBJECT_ID</c> and <c>DBCC CHECKIDENT</c> -
    /// both of which take a name and neither of which accepts it as a parameter.
    /// </summary>
    public string Literal => Quoted.Replace("'", "''", StringComparison.Ordinal);

    public override string ToString() => Quoted;

    private static string Bracket(string part) =>
        $"[{part.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static List<string> Split(string value)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inBrackets = false;

        for(var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if(inBrackets)
            {
                // A closing bracket doubled inside brackets is a literal ']'.
                if(c != ']')
                    current.Append(c);
                else if(i + 1 < value.Length && value[i + 1] == ']')
                {
                    current.Append(']');
                    i++;
                }
                else
                    inBrackets = false;
            }
            else if(c == '[')
                inBrackets = true;
            else if(c == '.')
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(c);
        }

        parts.Add(current.ToString());
        return parts;
    }
}

/// <summary>
/// The small amount of ADO.NET this package needs, in one place so that every command
/// it runs carries the same timeout and the same cancellation token.
/// </summary>
internal static class PublicationSql
{
    public static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public static async Task<int> ExecuteAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, sql, commandTimeoutSeconds);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<object?> ScalarAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = Command(connection, transaction, sql, commandTimeoutSeconds);
        foreach(var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DBNull ? null : result;
    }

    /// <summary>
    /// The object's id, or null when nothing of that name is there. Used rather than a
    /// <c>COUNT</c> over <c>sys.objects</c> because a batch that names a missing table
    /// anywhere in it fails to compile, whichever branch would have run.
    /// </summary>
    public static async Task<int?> ObjectIdAsync(
        SqlConnection connection,
        SqlObjectName name,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var id = await ScalarAsync(
            connection, null, "SELECT OBJECT_ID(@name);", commandTimeoutSeconds, cancellationToken,
            ("@name", name.Quoted));

        return id is null ? null : Convert.ToInt32(id, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// An exact count. <c>COUNT_BIG</c> rather than <c>COUNT</c> because the guard's
    /// whole job is to be right about a table with more than two billion rows in it,
    /// and an exact count rather than <c>sys.dm_db_partition_stats</c> because a guard
    /// that reads an estimate is a guard that can be talked into a truncate.
    /// </summary>
    public static async Task<long> CountAsync(
        SqlConnection connection,
        SqlObjectName table,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var count = await ScalarAsync(
            connection, null, $"SELECT COUNT_BIG(*) FROM {table.Quoted};", commandTimeoutSeconds, cancellationToken);

        return Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqlCommand Command(
        SqlConnection connection, SqlTransaction? transaction, string sql, int commandTimeoutSeconds)
    {
        var command = new SqlCommand(sql, connection, transaction);

        // Zero is SQL Server's "no limit", and it is the right default here: a count
        // over a table with a hundred million rows in it legitimately takes minutes,
        // and a guard that gives up after thirty seconds fails the run for no reason.
        command.CommandTimeout = commandTimeoutSeconds;
        return command;
    }
}
