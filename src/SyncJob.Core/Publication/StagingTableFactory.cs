using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SqlSchemaDiff.Models;
using SqlSchemaDiff.Services;

namespace SyncJob.Core.Publication;

/// <summary>
/// Creates the staging table by reading the destination's own catalog entry, and takes
/// away only the tables it named itself.
/// <para>
/// SyncJob today requires the staging table to exist already, hand-created in the same
/// shape as its destination. The two drift - a column added to one and not the other -
/// and the publication that follows moves the data across shifted. Nothing here trusts
/// a table that is already sitting there: the shape is read from the destination on
/// every run, and a leftover staging table of the same name is dropped and rebuilt
/// rather than reused, because a table a crashed run left behind is not evidence of
/// anything.
/// </para>
/// </summary>
public sealed partial class StagingTableFactory : IStagingTableFactory
{
    /// <summary>
    /// The names this instance generated, so that <see cref="DropAsync"/> can tell a
    /// table it is responsible for from one the caller named and owns.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _generated = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Seconds a single statement may take; zero means no limit, which is the honest
    /// default for a <c>SELECT INTO</c> against a wide destination.
    /// </summary>
    public int CommandTimeoutSeconds { get; init; }

    /// <summary>
    /// Creates a table with the destination's columns and nothing else: no keys, no
    /// indexes, no checks. Rows load faster into a bare heap, and it is
    /// <see cref="IPublisher"/> that decides what the destination ends up carrying.
    /// </summary>
    public async Task<string> CreateAsync(
        string connectionString,
        string destinationTable,
        string? requestedName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var destination = SqlObjectName.Parse(destinationTable);
        var generated = string.IsNullOrWhiteSpace(requestedName);
        var staging = generated
            ? GenerateName(destination)
            : SqlObjectName.Parse(requestedName!, destination.Schema);

        await using var connection = await PublicationSql.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);

        if(await PublicationSql.ObjectIdAsync(connection, destination, CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException(
                $"the destination {destination.Quoted} does not exist, and the staging table takes its shape " +
                "from it - so there is nothing to stage into");
        }

        var createSql = await BuildCreateAsync(connectionString, destination, staging, cancellationToken).ConfigureAwait(false);

        await PublicationSql.ExecuteAsync(
            connection, null, $"DROP TABLE IF EXISTS {staging.Quoted};", CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        await PublicationSql.ExecuteAsync(connection, null, createSql, CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        if(generated)
            _generated.TryAdd(staging.Quoted, 0);

        return staging.Quoted;
    }

    /// <summary>
    /// Drops a staging table this engine named. A table the caller named is left
    /// alone - it is theirs, and it may well be the one thing an operator has to look
    /// at after a failed run.
    /// <para>
    /// A generated name is also recognised by its shape, not only by this instance's
    /// record of it, so that a later run can clear up after a process that was killed
    /// before it got here.
    /// </para>
    /// </summary>
    public async Task DropAsync(string connectionString, string stagingTable, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var staging = SqlObjectName.Parse(stagingTable);
        var wasGenerated = _generated.TryRemove(staging.Quoted, out _) || GeneratedName().IsMatch(staging.Name);
        if(!wasGenerated)
            return;

        await using var connection = await PublicationSql.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);
        await PublicationSql.ExecuteAsync(
            connection, null, $"DROP TABLE IF EXISTS {staging.Quoted};", CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The destination's shape as a bare <c>CREATE TABLE</c>, from SQLDiff's own
    /// extractor and renderer.
    /// <para>
    /// The extractor reads the whole database's metadata in a fixed number of round
    /// trips; there is no per-table entry point, so a step pays one metadata read for
    /// its staging table. That is the price of not maintaining a second schema reader.
    /// </para>
    /// </summary>
    private async Task<string> BuildCreateAsync(
        string connectionString,
        SqlObjectName destination,
        SqlObjectName staging,
        CancellationToken cancellationToken)
    {
        var snapshot = await new SqlServerSchemaExtractor().ExtractAsync(connectionString, cancellationToken).ConfigureAwait(false);
        var model = FindTable(snapshot, destination);

        // A destination that is not a table - a view over one, which this engine is
        // allowed to publish into - has no TableModel to render, so the server derives
        // the shape instead. Deliberately not hand-built from sys.columns: that is the
        // schema reader this package exists not to own a second copy of.
        if(model is null)
            return $"SELECT TOP (0) * INTO {staging.Quoted} FROM {destination.Quoted};";

        return SqlRender.BuildTableCreateOnly(AsStaging(model, staging));
    }

    /// <summary>
    /// The destination's table model under the staging name, stripped of everything
    /// that is not a column.
    /// <para>
    /// Staging is an ordinary heap whatever the destination is. Until SqlSchemaDiff
    /// 1.7.0 that was true by accident - the renderer did not know about system
    /// versioning or memory-optimized tables and dropped both on the floor. 1.7.0
    /// renders them faithfully, which is right for a schema diff and wrong here: a
    /// staging table declared <c>MEMORY_OPTIMIZED</c> needs a filegroup the database
    /// may not have, and one carrying <c>GENERATED ALWAYS</c> period columns cannot be
    /// bulk copied into at all.
    /// </para>
    /// </summary>
    internal static TableModel AsStaging(TableModel destination, SqlObjectName staging)
    {
        var model = destination.Clone();
        model.Schema = staging.Schema;
        model.Name = staging.Name;

        // Clone() shares the lists with the original, so these are replaced rather
        // than cleared - clearing them would empty the destination's model too.
        model.KeyConstraints = new List<KeyConstraintModel>();
        model.ForeignKeys = new List<ForeignKeyModel>();
        model.CheckConstraints = new List<CheckConstraintModel>();
        model.Indexes = new List<IndexModel>();

        model.TemporalType = null;
        model.HistoryTableSchema = null;
        model.HistoryTableName = null;
        model.PeriodStartColumn = null;
        model.PeriodEndColumn = null;
        model.IsMemoryOptimized = false;
        model.Durability = null;

        model.Columns = destination.Columns

            // The period columns go rather than being un-flagged. Kept as ordinary
            // datetime2 columns they would be two columns staging has and the source
            // does not, and the copy would refuse the step for a column nobody asked
            // for. The destination still maintains its own.
            .Where(column => column.GeneratedAlwaysType == 0)
            .Select(column =>
            {
                if(column.DefaultDefinition is null)
                    return column;

                // A default constraint is an object in the schema, so carrying the
                // destination's name for it collides on the first CREATE. The
                // definition is kept and the name is not: SQL Server generates one.
                var copy = column.Clone();
                copy.DefaultIsSystemNamed = true;
                return copy;
            })
            .ToList();

        return model;
    }

    internal static TableModel? FindTable(DatabaseSnapshot snapshot, SqlObjectName table) =>
        snapshot.Objects.FirstOrDefault(x =>
            x.Type == DbObjectType.Table
            && string.Equals(x.Schema, table.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, table.Name, StringComparison.OrdinalIgnoreCase))?.Table;

    /// <summary>
    /// <c>&lt;destination&gt;_stg_&lt;8 hex&gt;</c>, in the destination's schema. The
    /// suffix is what lets two runs of the same step overlap without one dropping the
    /// other's table; the prefix is what makes a leftover greppable.
    /// </summary>
    private static SqlObjectName GenerateName(SqlObjectName destination)
    {
        const int suffixLength = 13; // "_stg_" plus eight hex digits
        var stem = destination.Name.Length > 128 - suffixLength
            ? destination.Name[..(128 - suffixLength)]
            : destination.Name;

        return new SqlObjectName(destination.Schema, $"{stem}_stg_{Guid.NewGuid():N}"[..(stem.Length + suffixLength)]);
    }

    [GeneratedRegex(@"_stg_[0-9a-f]{8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedName();
}
