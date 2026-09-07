using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlSchemaDiff.Models;
using SqlSchemaDiff.Services;
using SyncJob.Core.Model;

namespace SyncJob.Core.Publication;

/// <summary>
/// Publishes a <see cref="PublicationMode.Replace"/> step by exchanging the staged
/// table with the destination rather than emptying the destination and filling it.
/// <para>
/// Why an exchange at all: a truncate inside the publishing transaction takes a
/// schema-modification lock and keeps it until the transaction ends, so the insert
/// that follows blocks every reader of the destination - including a reader that asked
/// for <c>NOLOCK</c>, which acquires a schema-stability lock like everyone else. A
/// hundred and twenty-two thousand rows once meant fifteen seconds of a hung
/// dashboard. The exchange is metadata: the lock is taken and given back.
/// </para>
/// <para>
/// <b>Why <c>SWITCH</c> and not <c>sp_rename</c>.</b> Indexes, constraints and
/// permissions belong to the physical object, not to the name. A rename therefore
/// hands the destination's name to the bare staging table, and everything the
/// destination carried has to be built again on the new object: the indexes, which is
/// only work, and the <c>GRANT</c>s, which is a security decision. Re-granting means
/// scripting <c>sys.database_permissions</c> back out - every column-level grant,
/// every <c>DENY</c>, every <c>WITH GRANT OPTION</c>, under the right grantor - and
/// anything the script does not think of is a permission that silently disappears or,
/// worse, a <c>DENY</c> that does. <c>ALTER TABLE ... SWITCH</c> moves the storage and
/// leaves the object alone: the destination keeps its <c>object_id</c>, so its
/// indexes, constraints, permissions, triggers and extended properties are not
/// restored afterwards - they are never lost. That is a guarantee by construction
/// rather than by remembering, which is why it is worth the price the switch charges:
/// the staged table has to be brought up to the destination's shape first (see
/// <see cref="SwapAlignment"/>), and the exchange is refused outright in the cases
/// <see cref="SwapCapability"/> lists.
/// </para>
/// <para>
/// Both switches and the identity reseed run in one transaction, so a failure at any
/// point leaves the destination holding exactly the rows it held before.
/// </para>
/// </summary>
public sealed class SwapPublisher : IPublisher
{
    public async Task<PublishResult> PublishAsync(
        string connectionString,
        string stagingTable,
        SyncStep step,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(step);

        if(step.Publication.Mode != PublicationMode.Replace)
        {
            throw new NotSupportedException(
                $"{nameof(SwapPublisher)} publishes {PublicationMode.Replace}; " +
                $"{step.Publication.Mode} belongs to another publisher.");
        }

        var staging = SqlObjectName.Parse(stagingTable);
        var destination = SqlObjectName.Parse(step.DestinationTable);
        var timeout = step.CommandTimeoutSeconds;

        await using var connection = await PublicationSql.OpenAsync(connectionString, cancellationToken);

        var capability = await SwapCapability.ReadAsync(connection, staging, destination, timeout, cancellationToken);

        // Both counts are taken before the transaction opens. Taken inside it they
        // would hold the schema-modification lock for the length of two scans, which
        // is the whole failure this publisher exists to avoid.
        var stagedRows = await PublicationSql.CountAsync(connection, staging, timeout, cancellationToken);
        var replacedRows = await PublicationSql.CountAsync(connection, destination, timeout, cancellationToken);

        // The stamp goes on staging, before either path publishes. Every row a replace
        // writes is a new row, so there is nothing to distinguish between updated and
        // inserted, and staging is a table nobody else can see - which is what makes
        // this the cheap place to do it. The deployed system stamps its own staging
        // copy too, but after the merge has already read from it, so the value never
        // reaches the destination at all.
        await StampStagingAsync(connection, staging, step.Provenance, timeout, cancellationToken);

        if(capability.BlockedBecause is null)
            await SwapAsync(connectionString, connection, staging, destination, timeout, cancellationToken);
        else
            await ReplaceInPlaceAsync(connection, staging, destination, capability, step, timeout, cancellationToken);

        return new PublishResult(stagedRows, 0, replacedRows);
    }

    /// <summary>
    /// Writes the step's provenance value into every staged row. Does nothing when the
    /// step asks for no stamp.
    /// </summary>
    private static async Task StampStagingAsync(
        SqlConnection connection,
        SqlObjectName staging,
        ProvenanceStamp? provenance,
        int timeout,
        CancellationToken cancellationToken)
    {
        var (stamp, stampValue) = AppendMergeSql.Stamp(provenance);
        if(stamp is null)
            return;

        var sql = $"UPDATE {staging.Quoted} SET {AppendMergeSql.Quote(stamp.Column)} = {stamp.ValueSql};";

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeout };
        if(stampValue is not null)
            command.Parameters.AddWithValue(AppendMergeSql.StampParameter, stampValue);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SwapAsync(
        string connectionString,
        SqlConnection connection,
        SqlObjectName staging,
        SqlObjectName destination,
        int timeout,
        CancellationToken cancellationToken)
    {
        var snapshot = await new SqlServerSchemaExtractor().ExtractAsync(connectionString, cancellationToken);
        var model = StagingTableFactory.FindTable(snapshot, destination)
                    ?? throw new InvalidOperationException(
                        $"{destination.Quoted} disappeared between the capability check and the swap");

        foreach(var statement in SwapAlignment.Statements(model, staging))
            await PublicationSql.ExecuteAsync(connection, null, statement, timeout, cancellationToken);

        var reseed = await ReadReseedAsync(connection, staging, model, timeout, cancellationToken);

        // Where the destination's rows go. Switching them out is a metadata operation
        // and a truncate is not; more to the point, a truncate here is a statement the
        // next maintainer can add an insert after, and this one is not.
        var discard = Discard(destination);
        await PublicationSql.ExecuteAsync(
            connection,
            null,
            SqlRender.BuildTableCreateOnly(StagingTableFactory.AsStaging(model, discard)),
            timeout,
            cancellationToken);

        // Everything from here is inside the try, so that the discard is taken away
        // whether the alignment, the switches or the commit is what went wrong.
        try
        {
            foreach(var statement in SwapAlignment.Statements(model, discard))
                await PublicationSql.ExecuteAsync(connection, null, statement, timeout, cancellationToken);

            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await PublicationSql.ExecuteAsync(
                connection, transaction,
                $"ALTER TABLE {destination.Quoted} SWITCH TO {discard.Quoted};", timeout, cancellationToken);
            await PublicationSql.ExecuteAsync(
                connection, transaction,
                $"ALTER TABLE {staging.Quoted} SWITCH TO {destination.Quoted};", timeout, cancellationToken);

            // A switch leaves the destination's identity counter at its seed, so the
            // next row inserted by anything else would collide with the rows just
            // published. The truncate-and-insert this replaces did not have that
            // problem - inserting explicit identity values moves the counter - so
            // putting it back is not a nicety, it is not regressing.
            if(reseed is not null)
            {
                await PublicationSql.ExecuteAsync(
                    connection, transaction,
                    $"DBCC CHECKIDENT('{destination.Literal}', RESEED, {reseed}) WITH NO_INFOMSGS;",
                    timeout, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            // The discard holds the rows the destination used to have. Dropping it is
            // best effort: a leftover is a table an operator can find by its name, and
            // failing the publication over it would undo a swap that worked.
            try
            {
                await PublicationSql.ExecuteAsync(
                    connection, null, $"DROP TABLE IF EXISTS {discard.Quoted};", timeout, CancellationToken.None);
            }
            catch(SqlException)
            {
            }
        }
    }

    /// <summary>
    /// The publication for a destination no switch can reach: empty it and fill it
    /// again, in one transaction, <b>with an explicit column list</b>.
    /// <para>
    /// The column list is the point. The Windows service publishes with
    /// <c>INSERT INTO final SELECT * FROM stage</c>, and the day a column is added to
    /// one of the two tables the rows go in shifted - silently, for as long as the
    /// types happen to line up.
    /// </para>
    /// </summary>
    private static async Task ReplaceInPlaceAsync(
        SqlConnection connection,
        SqlObjectName staging,
        SqlObjectName destination,
        SwapCapability capability,
        SyncStep step,
        int timeout,
        CancellationToken cancellationToken)
    {
        var columns = await ReadSharedColumnsAsync(connection, staging, destination, timeout, cancellationToken);

        // IDENTITY_INSERT names a table; through a view it would have to name the base
        // table underneath, which is a resolution this publisher does not do. So an
        // identity column reaching a view is left for the destination to assign.
        var keepIdentity = step.Publication.KeepIdentity && capability.IsTable;
        if(!keepIdentity)
            columns = columns.Where(x => !x.IsIdentity).ToList();

        if(columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"{staging.Quoted} and {destination.Quoted} have no writable column in common, so there is " +
                "nothing that could be published");
        }

        var columnList = string.Join(", ", columns.Select(x => x.Quoted));
        var identityInsert = keepIdentity && columns.Any(x => x.IsIdentity);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // TRUNCATE is refused on a table another table's foreign key points at, and on
        // a view there is nothing to truncate.
        var empty = capability.IsTable && !capability.ReferencedByForeignKey
            ? $"TRUNCATE TABLE {destination.Quoted};"
            : $"DELETE FROM {destination.Quoted};";

        await PublicationSql.ExecuteAsync(connection, transaction, empty, timeout, cancellationToken);

        if(identityInsert)
        {
            await PublicationSql.ExecuteAsync(
                connection, transaction, $"SET IDENTITY_INSERT {destination.Quoted} ON;", timeout, cancellationToken);
        }

        await PublicationSql.ExecuteAsync(
            connection, transaction,
            $"INSERT INTO {destination.Quoted} ({columnList}) SELECT {columnList} FROM {staging.Quoted};",
            timeout, cancellationToken);

        if(identityInsert)
        {
            await PublicationSql.ExecuteAsync(
                connection, transaction, $"SET IDENTITY_INSERT {destination.Quoted} OFF;", timeout, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The highest identity value among the staged rows, read before the transaction:
    /// after the switch the value lives in the destination, and reading it there would
    /// mean a scan while the destination is locked.
    /// </summary>
    private static async Task<string?> ReadReseedAsync(
        SqlConnection connection,
        SqlObjectName staging,
        TableModel model,
        int timeout,
        CancellationToken cancellationToken)
    {
        var identity = model.Columns.FirstOrDefault(x => x.IsIdentity);
        if(identity is null)
            return null;

        var column = $"[{identity.Name.Replace("]", "]]", StringComparison.Ordinal)}]";
        var value = await PublicationSql.ScalarAsync(
            connection, null,
            $"SELECT CONVERT(nvarchar(64), MAX({column})) FROM {staging.Quoted};",
            timeout, cancellationToken);

        // Nothing staged, so the seed the switch leaves behind is the right one.
        var text = value as string;
        if(string.IsNullOrEmpty(text))
            return null;

        // It came from the server, but it is going into a DBCC statement that takes no
        // parameters, so it is checked rather than trusted.
        return text.All(c => char.IsAsciiDigit(c) || c is '-' or '.') ? text : null;
    }

    private static async Task<List<InsertColumn>> ReadSharedColumnsAsync(
        SqlConnection connection,
        SqlObjectName staging,
        SqlObjectName destination,
        int timeout,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.name, c.is_identity
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(@destination)
              AND c.is_computed = 0
              AND TYPE_NAME(c.system_type_id) <> 'timestamp'
              AND EXISTS (
                  SELECT 1 FROM sys.columns s
                  WHERE s.object_id = OBJECT_ID(@staging) AND s.name = c.name AND s.is_computed = 0)
            ORDER BY c.column_id;
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeout };
        command.Parameters.AddWithValue("@destination", destination.Quoted);
        command.Parameters.AddWithValue("@staging", staging.Quoted);

        var columns = new List<InsertColumn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken))
            columns.Add(new InsertColumn(reader.GetString(0), reader.GetBoolean(1)));

        return columns;
    }

    private static SqlObjectName Discard(SqlObjectName destination)
    {
        const int suffixLength = 17; // "_swapout_" plus eight hex digits
        var stem = destination.Name.Length > 128 - suffixLength
            ? destination.Name[..(128 - suffixLength)]
            : destination.Name;

        return new SqlObjectName(
            destination.Schema,
            $"{stem}_swapout_{Guid.NewGuid():N}"[..(stem.Length + suffixLength)]);
    }

    private readonly record struct InsertColumn(string Name, bool IsIdentity)
    {
        public string Quoted => $"[{Name.Replace("]", "]]", StringComparison.Ordinal)}]";
    }
}

/// <summary>
/// Whether the destination can be published by exchanging tables, and when it cannot,
/// which of SQL Server's rules says so.
/// <para>
/// Every one of these is a documented refusal of <c>ALTER TABLE ... SWITCH</c>. They
/// are checked before anything is written so that the fallback is a decision rather
/// than a rescue after a failed statement - and so that the reason can be said in
/// words rather than as error 4967.
/// </para>
/// </summary>
internal sealed record SwapCapability(bool IsTable, bool ReferencedByForeignKey, string? BlockedBecause)
{
    public static async Task<SwapCapability> ReadAsync(
        SqlConnection connection,
        SqlObjectName staging,
        SqlObjectName destination,
        int timeout,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                d.type,
                CASE WHEN EXISTS (SELECT 1 FROM sys.foreign_keys f WHERE f.referenced_object_id = d.object_id)
                     THEN 1 ELSE 0 END,
                ISNULL(t.is_memory_optimized, 0),
                ISNULL(t.temporal_type, 0),
                (SELECT COUNT(*) FROM sys.partitions p
                 WHERE p.object_id = d.object_id AND p.index_id IN (0, 1)),
                (SELECT MIN(i.data_space_id) FROM sys.indexes i
                 WHERE i.object_id = d.object_id AND i.index_id IN (0, 1)),
                (SELECT MIN(i.data_space_id) FROM sys.indexes i
                 WHERE i.object_id = OBJECT_ID(@staging) AND i.index_id IN (0, 1))
            FROM sys.objects d
            LEFT JOIN sys.tables t ON t.object_id = d.object_id
            WHERE d.object_id = OBJECT_ID(@destination);
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeout };
        command.Parameters.AddWithValue("@destination", destination.Quoted);
        command.Parameters.AddWithValue("@staging", staging.Quoted);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if(!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"the destination {destination.Quoted} does not exist; a replace has nothing to replace");
        }

        var type = reader.GetString(0).Trim();
        var isTable = string.Equals(type, "U", StringComparison.Ordinal);
        var referencedByForeignKey = reader.GetInt32(1) == 1;
        var isMemoryOptimized = reader.GetBoolean(2);
        var isTemporal = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture) != 0;
        var partitions = reader.GetInt32(4);
        var destinationSpace = reader.IsDBNull(5) ? -1 : reader.GetInt32(5);
        var stagingSpace = reader.IsDBNull(6) ? -2 : reader.GetInt32(6);

        var blocked = Blocked(
            isTable, referencedByForeignKey, isMemoryOptimized, isTemporal, partitions, destinationSpace, stagingSpace);

        return new SwapCapability(isTable, referencedByForeignKey, blocked);
    }

    private static string? Blocked(
        bool isTable,
        bool referencedByForeignKey,
        bool isMemoryOptimized,
        bool isTemporal,
        int partitions,
        int destinationSpace,
        int stagingSpace)
    {
        if(!isTable)
            return "the destination is not a table, and only a table has storage to exchange";
        if(referencedByForeignKey)
            return "another table's foreign key points at the destination, which SQL Server refuses to switch";
        if(isMemoryOptimized)
            return "the destination is memory-optimized";
        if(isTemporal)
            return "the destination is system-versioned, and its history would not follow the switch";
        if(partitions > 1)
            return "the destination is partitioned, so a switch would have to name the partition";
        if(destinationSpace != stagingSpace)
            return "staging and the destination are on different filegroups, and a switch cannot move storage between them";

        return null;
    }
}
