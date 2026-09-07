using System.Globalization;
using System.Text.Json;
using SyncJob.Core.Model;

namespace SyncJob.Core.Import;

/// <summary>
/// Reads SyncJob's SQLite configuration store - the <c>Configurations</c>,
/// <c>Connections</c> and <c>ColumnMappings</c> tables the CLI keeps in
/// <c>syncjob.db</c>.
/// <para>
/// It takes rows, not a database. The Core may not reference
/// <c>Microsoft.Data.Sqlite</c> - there is a test that fails if it ever does - because
/// the moment it does, the DataSync console and SqlArchive inherit a configuration store
/// neither of them uses. So the surface opens the file, reads the three tables and hands
/// the rows over as plain objects. That is a constraint on the API, and it is the right
/// one: an importer that cannot open a connection cannot surprise anyone by opening one.
/// </para>
/// </summary>
public sealed class SqliteJobImporter : IJobImporter
{
    private const string SourceEndpointId = "source";
    private const string DestinationEndpointId = "destination";

    private readonly List<SqliteJobRows> _rows;

    /// <param name="rows">One entry per configuration, with the rows that belong to it.</param>
    /// <param name="sourceDescription">
    /// What to call this store in an error message - the database file's name is the
    /// useful answer, and only the caller knows it.
    /// </param>
    public SqliteJobImporter(IEnumerable<SqliteJobRows> rows, string sourceDescription = "syncjob.db")
    {
        ArgumentNullException.ThrowIfNull(rows);

        _rows = rows.ToList();
        SourceDescription = sourceDescription;
    }

    public string SourceDescription { get; }

    public Task<ImportResult> ImportAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = new ImportResult();

        foreach(var rows in _rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Jobs.Add(ReadJob(rows, result.Losses));
        }

        return Task.FromResult(result);
    }

    private static SyncJobDefinition ReadJob(SqliteJobRows rows, List<string> losses)
    {
        var configuration = rows.Configuration;
        var name = string.IsNullOrWhiteSpace(configuration.DisplayName)
            ? configuration.ConfigId
            : configuration.DisplayName;

        var job = new SyncJobDefinition
        {
            Id = configuration.ConfigId,
            Name = name,
            Description = string.IsNullOrWhiteSpace(configuration.Description) ? null : configuration.Description,
            IsActive = configuration.IsActive,
            Destination = ReadEndpoint(
                configuration.ConfigId,
                DestinationEndpointId,
                configuration.DestConnectionId,
                rows.DestinationConnection,
                losses),
            Sources =
            {
                ReadEndpoint(
                    configuration.ConfigId,
                    SourceEndpointId,
                    configuration.SourceConnectionId,
                    rows.SourceConnection,
                    losses)
            }
        };

        ReadTags(configuration, job, losses);

        if(!string.IsNullOrWhiteSpace(configuration.CreatedBy))
            job.Extensions["syncjob.sqlite.createdBy"] = configuration.CreatedBy!;

        job.Steps.Add(ReadStep(rows, name, losses));

        return job;
    }

    private static void ReadTags(SqliteConfigurationRow configuration, SyncJobDefinition job, List<string> losses)
    {
        if(string.IsNullOrWhiteSpace(configuration.Tags))
            return;

        try
        {
            var tags = JsonSerializer.Deserialize<List<string>>(configuration.Tags!);
            if(tags is not null)
            {
                job.Tags.AddRange(tags.Where(x => !string.IsNullOrWhiteSpace(x)));
                return;
            }
        }
        catch(JsonException)
        {
            // Falls through to the loss below: the column is free text with a JSON
            // convention and nothing enforces it.
        }

        job.Extensions["syncjob.sqlite.tags"] = configuration.Tags!;

        losses.Add(
            $"The configuration '{configuration.ConfigId}' has a Tags column that is not a JSON array of strings. " +
            "It is kept verbatim under 'syncjob.sqlite.tags' rather than guessed at.");
    }

    private static Endpoint ReadEndpoint(
        string jobId,
        string endpointId,
        string connectionId,
        SqliteConnectionRow? row,
        List<string> losses)
    {
        if(row is null)
        {
            losses.Add(
                $"The configuration '{jobId}' names the connection '{connectionId}' for its {endpointId}, and no " +
                "such row was handed to the importer. The endpoint is imported without a connection string and the " +
                "job will not validate until one is supplied.");

            return new Endpoint { Id = endpointId, Name = connectionId };
        }

        var endpoint = new Endpoint
        {
            Id = endpointId,
            Name = string.IsNullOrWhiteSpace(row.DisplayName) ? connectionId : row.DisplayName,
            IsActive = row.IsActive
        };

        endpoint.Extensions["syncjob.sqlite.connectionId"] = row.ConnectionId;

        if(!string.IsNullOrWhiteSpace(row.ServerType) &&
           !row.ServerType.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            endpoint.Extensions["syncjob.sqlite.serverType"] = row.ServerType;

            losses.Add(
                $"The connection '{row.ConnectionId}' is of type '{row.ServerType}'. This engine speaks to SQL " +
                "Server; the connection string was composed anyway and will have to be replaced.");
        }

        if(!string.IsNullOrWhiteSpace(row.ConnectionString))
        {
            var (redacted, outcome) = EndpointSecrets.Redact(row.ConnectionString);
            endpoint.ConnectionString = redacted;

            if(outcome != EndpointSecrets.Redaction.NoSecret)
            {
                endpoint.SecretRef = EndpointSecrets.Reference(jobId, endpointId);

                losses.Add(EndpointSecrets.CredentialLoss(
                    $"The connection string the caller resolved for '{row.ConnectionId}'", endpoint.SecretRef));
            }
        }
        else
        {
            endpoint.ConnectionString = EndpointSecrets.Compose(
                row.ServerName, row.DatabaseName, row.Username, row.TrustServerCertificate, row.Encrypt);
        }

        // The Core is never handed the DPAPI blob itself, only the fact that there is
        // one, because a blob protected for one Windows account is worthless to the
        // others and decrypting it is the surface's job. What the model needs is the
        // name of the secret, not the secret.
        if(row.HasStoredPassword || row.HasStoredConnectionString)
        {
            endpoint.SecretRef ??= EndpointSecrets.Reference(jobId, endpointId);

            losses.Add(
                $"The connection '{row.ConnectionId}' keeps its " +
                (row.HasStoredConnectionString ? "whole connection string" : "password") +
                " as a DPAPI blob, which only the Windows account that wrote it can read. It was not imported: the " +
                $"endpoint points at the secret named '{endpoint.SecretRef}', which the surface has to resolve.");
        }

        return endpoint;
    }

    private static SyncStep ReadStep(SqliteJobRows rows, string name, List<string> losses)
    {
        var configuration = rows.Configuration;

        var step = new SyncStep
        {
            Id = configuration.ConfigId,
            Name = name,
            Order = 1,
            IsActive = configuration.IsActive,
            SourceEndpointId = SourceEndpointId,
            DestinationTable = configuration.DestFinalTable,
            BatchSize = Math.Max(0, configuration.BatchSize),
            CommandTimeoutSeconds = Math.Max(0, configuration.BulkCopyTimeout),
            Source = new SourceQuery
            {
                Sql = Blank(configuration.SourceQuery),
                StoredProcedure = Blank(configuration.SourceStoredProc)
            },
            Publication = new PublicationPlan
            {
                StagingTable = Blank(configuration.DestStageTable),
                KeepIdentity = configuration.KeepIdentity,
                MaxDegreeOfParallelism = Math.Max(1, configuration.MaxDOP),
                Guard = new PublicationGuard { MinimumRows = Math.Max(0, configuration.MinRowThreshold) }
            }
        };

        ReadSourceParameters(configuration, step, losses);
        ReadColumnMappings(rows, step, losses);
        ReadPublicationMode(configuration, step, losses);
        ReadTracking(configuration, step, losses);

        return step;
    }

    private static void ReadSourceParameters(SqliteConfigurationRow configuration, SyncStep step, List<string> losses)
    {
        if(string.IsNullOrWhiteSpace(configuration.SourceParameters))
            return;

        try
        {
            var parameters = JsonSerializer.Deserialize<Dictionary<string, string?>>(configuration.SourceParameters!);
            if(parameters is not null)
            {
                foreach(var parameter in parameters)
                    step.Source.Parameters[parameter.Key] = parameter.Value ?? string.Empty;

                return;
            }
        }
        catch(JsonException)
        {
            // Same as Tags: a JSON convention in a TEXT column, enforced by nothing.
        }

        step.Extensions["syncjob.sqlite.sourceParameters"] = configuration.SourceParameters!;

        losses.Add(
            $"The configuration '{configuration.ConfigId}' has a SourceParameters column that is not a JSON object " +
            "of name to value. It is kept verbatim under 'syncjob.sqlite.sourceParameters'; the stored procedure " +
            "will run without parameters until it is corrected.");
    }

    /// <summary>
    /// Turns the mapping rows into field maps, keeping only what departs from matching by
    /// name: a rename, a key, an expression.
    /// <para>
    /// This is where the two SyncJob formats stop being the same model. The SQLite store
    /// marks its key on the mapping row (<c>IsPrimaryKey</c>) and carries a per-column
    /// transform the JSON sections have no field for at all; the JSON side keeps its keys
    /// in a separate <c>PrimaryKeyColumns</c> list and carries a delete-detection block
    /// this store has no column for. Neither is a superset of the other.
    /// </para>
    /// </summary>
    private static void ReadColumnMappings(SqliteJobRows rows, SyncStep step, List<string> losses)
    {
        if(rows.ColumnMappings.Count == 0)
            return;

        var ordered = rows.ColumnMappings
            .OrderBy(x => x.Ordinal ?? int.MaxValue)
            .ThenBy(x => x.MappingId)
            .ToList();

        step.Extensions["syncjob.sqlite.columnMappings"] =
            string.Join("; ", ordered.Select(x => $"{x.SourceColumn}={x.DestColumn}"));

        var identity = 0;
        var uninterpretedTransforms = new List<string>();

        foreach(var row in ordered)
        {
            var renamed =
                !string.IsNullOrWhiteSpace(row.SourceColumn) &&
                !string.IsNullOrWhiteSpace(row.DestColumn) &&
                !string.Equals(row.SourceColumn, row.DestColumn, StringComparison.OrdinalIgnoreCase);

            var expression = Blank(row.TransformExpression);

            if(!renamed && !row.IsPrimaryKey && expression is null && Blank(row.TransformType) is null)
            {
                identity++;
                continue;
            }

            var map = new FieldMap
            {
                Source = Blank(row.SourceColumn),
                Target = renamed ? row.DestColumn : null,
                IsUniqueKey = row.IsPrimaryKey,
                Expression = expression
            };

            if(Blank(row.TransformType) is { } transform)
            {
                map.Extensions["syncjob.sqlite.transformType"] = transform;

                if(expression is null)
                    uninterpretedTransforms.Add($"{row.SourceColumn} ({transform})");
            }

            if(row.Ordinal is { } ordinal)
                map.Extensions["syncjob.sqlite.ordinal"] = ordinal.ToString(CultureInfo.InvariantCulture);

            step.FieldMaps.Add(map);
        }

        if(identity > 0)
        {
            losses.Add(
                $"The configuration '{step.Id}' has {identity} column mapping(s) that name the same column on both " +
                "sides and say nothing else. They are not imported as field maps: this model discovers both sides " +
                "and matches by name, so an identity list is the default. The list is kept under the step's " +
                "'syncjob.sqlite.columnMappings'. Where it was also being used to keep a destination column out of " +
                "the load, add a field map with IsExcluded for that column.");
        }

        if(uninterpretedTransforms.Count > 0)
        {
            losses.Add(
                $"The configuration '{step.Id}' names a TransformType with no TransformExpression on " +
                $"{string.Join(", ", uninterpretedTransforms)}. This model has no field for a named transform - it " +
                "carries the SQL expression a column is read with and nothing else - so the transform is kept under " +
                "each map's 'syncjob.sqlite.transformType' and will not be applied. Write it as an expression.");
        }
    }

    private static void ReadPublicationMode(SqliteConfigurationRow configuration, SyncStep step, List<string> losses)
    {
        var strategy = configuration.MergeStrategy?.Trim();

        if(string.IsNullOrEmpty(strategy) || strategy.Equals("Upsert", StringComparison.OrdinalIgnoreCase))
        {
            step.Publication.Mode = PublicationMode.Merge;
        }
        else if(strategy.Equals("Insert", StringComparison.OrdinalIgnoreCase))
        {
            step.Publication.Mode = PublicationMode.Append;
        }
        else if(strategy.Equals("Full", StringComparison.OrdinalIgnoreCase))
        {
            step.Publication.Mode = PublicationMode.Merge;

            losses.Add(
                $"The configuration '{configuration.ConfigId}' asks for MergeStrategy 'Full', which also meant " +
                "deleting the destination rows the source no longer returns. It imports as a merge, which inserts " +
                "and updates but deletes nothing: this model removes rows through a tombstone ledger the source " +
                "writes, not by comparing whole tables, because absence from a filtered read is not evidence of " +
                "deletion. Point the step at a ledger, or accept that deletions will no longer propagate.");
        }
        else
        {
            step.Publication.Mode = PublicationMode.Merge;
            step.Extensions["syncjob.sqlite.mergeStrategy"] = strategy;

            losses.Add(
                $"The configuration '{configuration.ConfigId}' names the merge strategy '{strategy}', which is not " +
                "one SyncJob defines. It was imported as a merge; check that this is what was meant.");
        }

        if(step.Publication.Mode == PublicationMode.Merge)
        {
            losses.Add(
                $"The configuration '{configuration.ConfigId}' is configured to merge, but SyncJob never did: " +
                "IncrementalSyncEngine.ExecuteMerge is never called from anywhere and every run replaced the " +
                "destination instead. The imported job will do what the configuration always said, which is a " +
                "change in behaviour - check the unique key before the first run.");
        }
    }

    private static void ReadTracking(SqliteConfigurationRow configuration, SyncStep step, List<string> losses)
    {
        var mode = configuration.TrackingMode?.Trim();

        if(string.IsNullOrEmpty(mode) || mode.Equals("None", StringComparison.OrdinalIgnoreCase))
            return;

        if(mode.Equals("Snapshot", StringComparison.OrdinalIgnoreCase))
        {
            step.Extensions["syncjob.sqlite.trackingMode"] = mode;

            losses.Add(
                $"The configuration '{configuration.ConfigId}' tracks changes by snapshot: the CLI keeps a row hash " +
                "per key in its own SQLite tables and compares. This model has no field for that - it follows one " +
                "column's highest value - so the step will read everything on every run until a sync-key column is " +
                "chosen. The snapshot rows in syncjob.db are not carried and can be discarded.");

            return;
        }

        if(!mode.Equals("Timestamp", StringComparison.OrdinalIgnoreCase) &&
           !mode.Equals("RowVersion", StringComparison.OrdinalIgnoreCase))
        {
            step.Extensions["syncjob.sqlite.trackingMode"] = mode;

            if(!string.IsNullOrWhiteSpace(configuration.TrackingColumn))
                step.Extensions["syncjob.sqlite.trackingColumn"] = configuration.TrackingColumn!;

            losses.Add(
                $"The configuration '{configuration.ConfigId}' tracks changes with '{mode}', which this model has " +
                "no equivalent for: it follows one column's highest value, not SQL Server's own change feeds. The " +
                "rest of the job is imported and it will read everything on every run until a sync-key column is " +
                "chosen.");

            return;
        }

        if(string.IsNullOrWhiteSpace(configuration.TrackingColumn))
        {
            losses.Add(
                $"The configuration '{configuration.ConfigId}' tracks changes in mode '{mode}' but names no " +
                "TrackingColumn, so there is nothing to follow and no watermark was imported. The step will read " +
                "everything on every run.");

            return;
        }

        SyncKey(step, configuration.TrackingColumn!);

        // StateTable stays null - the engine's own store - because this format has no
        // column for one. Its JSON sibling does (Incremental.TrackingTable), which is one
        // of the places the two diverge.
        step.Incremental = new IncrementalPlan { Watermark = new WatermarkPlan() };

        if(mode.Equals("RowVersion", StringComparison.OrdinalIgnoreCase))
            step.Extensions["syncjob.sqlite.trackingMode"] = mode;

        losses.Add(
            $"The configuration '{configuration.ConfigId}' keeps a watermark, and this store has no column naming " +
            "the table it lives in - unlike the JSON sections, which have Incremental.TrackingTable. The CLI wrote " +
            "it to 'dbo.SyncJobTracking' in the destination; the imported job uses the engine's own store instead, " +
            "so carry the existing row over or the first run reads from the beginning.");
    }

    /// <summary>
    /// Marks the tracking column as the sync key, reusing the map that already carries
    /// its key role rather than adding a second one - two maps for one column would read
    /// as two sync keys and the validator would refuse the job.
    /// </summary>
    private static void SyncKey(SyncStep step, string column)
    {
        var name = column.Trim();

        var existing = step.FieldMaps.FirstOrDefault(x =>
            string.Equals(x.Source, name, StringComparison.OrdinalIgnoreCase));

        if(existing is null)
        {
            existing = new FieldMap { Source = name };
            step.FieldMaps.Add(existing);
        }

        existing.IsSyncKey = true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// One configuration and the rows that belong to it, as the caller read them out of
/// <c>syncjob.db</c>.
/// </summary>
public sealed class SqliteJobRows
{
    public required SqliteConfigurationRow Configuration { get; init; }

    /// <summary>
    /// The <c>Connections</c> row named by <see cref="SqliteConfigurationRow.SourceConnectionId"/>,
    /// or null when the caller could not find it - which is itself worth reporting.
    /// </summary>
    public SqliteConnectionRow? SourceConnection { get; init; }

    public SqliteConnectionRow? DestinationConnection { get; init; }

    public List<SqliteColumnMappingRow> ColumnMappings { get; init; } = new();
}

/// <summary>A row of the <c>Configurations</c> table.</summary>
public sealed class SqliteConfigurationRow
{
    public required string ConfigId { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string SourceConnectionId { get; init; } = string.Empty;

    public string? SourceQuery { get; init; }

    public string? SourceStoredProc { get; init; }

    /// <summary>A JSON object of name to value, by convention only - the column is TEXT.</summary>
    public string? SourceParameters { get; init; }

    public string DestConnectionId { get; init; } = string.Empty;

    public string? DestStageTable { get; init; }

    public string DestFinalTable { get; init; } = string.Empty;

    public int BatchSize { get; init; }

    public int MaxDOP { get; init; } = 1;

    public int BulkCopyTimeout { get; init; }

    public bool KeepIdentity { get; init; } = true;

    public int MinRowThreshold { get; init; }

    public bool IsActive { get; init; } = true;

    /// <summary>None, Snapshot, Timestamp, RowVersion, ChangeTracking or ChangeDataCapture.</summary>
    public string? TrackingMode { get; init; }

    public string? TrackingColumn { get; init; }

    /// <summary>Insert, Upsert or Full.</summary>
    public string? MergeStrategy { get; init; }

    public string? CreatedBy { get; init; }

    /// <summary>A JSON array of strings, by convention only.</summary>
    public string? Tags { get; init; }
}

/// <summary>
/// A row of the <c>Connections</c> table, without its secrets.
/// <para>
/// <c>PasswordEncrypted</c> and <c>ConnectionStringEncrypted</c> are deliberately absent:
/// they are DPAPI blobs that only the Windows account which wrote them can read, so
/// decrypting them belongs to the surface and never to the Core. What the importer needs
/// is the knowledge that a secret exists, so it can point the endpoint at one - and that
/// is what the two flags carry. A caller that has already resolved a full connection
/// string may pass it in <see cref="ConnectionString"/> instead, and any password left in
/// it is stripped on the way through.
/// </para>
/// </summary>
public sealed class SqliteConnectionRow
{
    public required string ConnectionId { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string ServerType { get; init; } = "SqlServer";

    public string ServerName { get; init; } = string.Empty;

    public string DatabaseName { get; init; } = string.Empty;

    public string? Username { get; init; }

    /// <summary>An already-resolved connection string, when the caller has one.</summary>
    public string? ConnectionString { get; init; }

    public bool HasStoredPassword { get; init; }

    public bool HasStoredConnectionString { get; init; }

    public bool TrustServerCertificate { get; init; }

    public bool Encrypt { get; init; } = true;

    public bool IsActive { get; init; } = true;
}

/// <summary>A row of the <c>ColumnMappings</c> table.</summary>
public sealed class SqliteColumnMappingRow
{
    public int MappingId { get; init; }

    public string SourceColumn { get; init; } = string.Empty;

    public string DestColumn { get; init; } = string.Empty;

    public bool IsPrimaryKey { get; init; }

    /// <summary>A named transform. This model has no field for one; see the importer.</summary>
    public string? TransformType { get; init; }

    public string? TransformExpression { get; init; }

    public int? Ordinal { get; init; }
}
