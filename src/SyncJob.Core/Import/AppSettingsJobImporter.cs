using System.Text.Json;
using System.Text.Json.Serialization;
using SyncJob.Core.Model;

namespace SyncJob.Core.Import;

/// <summary>
/// Reads SyncJob's JSON configuration - one file holding many named sections, each of
/// which is one job with one step.
/// <para>
/// The file is handed in as text rather than as a path, because the Core does not decide
/// where a surface keeps its configuration and does not want a file system in its
/// contract. The CLI reads the file; this turns it into jobs.
/// </para>
/// </summary>
public sealed class AppSettingsJobImporter : IJobImporter
{
    private const string SourceEndpointId = "source";
    private const string DestinationEndpointId = "destination";

    private static readonly JsonSerializerOptions ReaderOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _json;

    /// <param name="json">The whole configuration file.</param>
    /// <param name="sourceDescription">
    /// What to call this file in an error message. The file's own name is the useful
    /// answer, and only the caller knows it.
    /// </param>
    public AppSettingsJobImporter(string json, string sourceDescription = "appsettings section")
    {
        ArgumentNullException.ThrowIfNull(json);

        _json = json;
        SourceDescription = sourceDescription;
    }

    public string SourceDescription { get; }

    public Task<ImportResult> ImportAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = new ImportResult();

        using var document = JsonDocument.Parse(_json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if(document.RootElement.ValueKind != JsonValueKind.Object)
        {
            result.Losses.Add(
                $"{SourceDescription}: the file's root is a {document.RootElement.ValueKind}, not an object of named " +
                "sections, so there is nothing to import from it.");

            return Task.FromResult(result);
        }

        foreach(var section in document.RootElement.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if(section.Value.ValueKind != JsonValueKind.Object)
                continue;

            var dto = section.Value.Deserialize<SectionDto>(ReaderOptions);
            if(dto is null || !dto.LooksLikeAJob)
                continue;

            if(dto.Source is null || dto.Destination is null)
            {
                result.Losses.Add(
                    $"The section '{section.Name}' has some of a job's shape but no " +
                    $"{(dto.Source is null ? "Source" : "Destination")}, so it was not imported.");

                continue;
            }

            result.Jobs.Add(ReadJob(section.Name, dto, result.Losses));
        }

        return Task.FromResult(result);
    }

    private static SyncJobDefinition ReadJob(string sectionName, SectionDto dto, List<string> losses)
    {
        // The section name is what an operator types on the command line, but the
        // watermark was keyed by JobIdentifier where one was given. Identity follows the
        // watermark, so a section that gets renamed keeps its history.
        var id = string.IsNullOrWhiteSpace(dto.Incremental?.JobIdentifier)
            ? sectionName
            : dto.Incremental!.JobIdentifier!.Trim();

        var job = new SyncJobDefinition
        {
            Id = id,
            Name = sectionName,
            Destination = ReadEndpoint(
                id, DestinationEndpointId, $"{sectionName} destination", dto.Destination!.ConnectionString, losses),
            Sources =
            {
                ReadEndpoint(id, SourceEndpointId, $"{sectionName} source", dto.Source!.ConnectionString, losses)
            }
        };

        if(!string.Equals(id, sectionName, StringComparison.Ordinal))
            job.Extensions["syncjob.json.section"] = sectionName;

        RecordUnknownProperties(
            dto.Unknown, $"the section '{sectionName}'", "syncjob.json.unknown", job.Extensions, losses);

        job.Steps.Add(ReadStep(sectionName, dto, losses));

        return job;
    }

    private static Endpoint ReadEndpoint(
        string jobId,
        string endpointId,
        string name,
        string? connectionString,
        List<string> losses)
    {
        var (redacted, outcome) = EndpointSecrets.Redact(connectionString);
        var endpoint = new Endpoint
        {
            Id = endpointId,
            Name = name,
            ConnectionString = redacted
        };

        if(outcome == EndpointSecrets.Redaction.NoSecret)
            return endpoint;

        endpoint.SecretRef = EndpointSecrets.Reference(jobId, endpointId);

        losses.Add(outcome == EndpointSecrets.Redaction.SecretRemoved
            ? EndpointSecrets.CredentialLoss($"The connection string for {name}", endpoint.SecretRef)
            : $"The connection string for {name} names a password but could not be parsed, so none of it was " +
              $"imported rather than risk carrying part of the secret. Re-enter the whole connection string, with " +
              $"the credential as the secret named '{endpoint.SecretRef}'.");

        return endpoint;
    }

    private static SyncStep ReadStep(string sectionName, SectionDto dto, List<string> losses)
    {
        var step = new SyncStep
        {
            Id = sectionName,
            Name = sectionName,
            Order = 1,
            SourceEndpointId = SourceEndpointId,
            DestinationTable = dto.Destination?.FinalTable ?? string.Empty,
            Source = new SourceQuery
            {
                Sql = Blank(dto.Source?.Query),
                StoredProcedure = Blank(dto.Source?.StoredProcedure)
            }
        };

        foreach(var parameter in dto.Source?.Parameters ?? [])
            step.Source.Parameters[parameter.Key] = parameter.Value ?? string.Empty;

        // The two paths in the CLI disagree about which wins, and neither says so: the
        // pre-flight probe runs the procedure (Program.cs:1103) and the copy runs the
        // query (Program.cs:1462). A section carrying both was therefore measured
        // against one source and loaded from the other.
        if(step.Source.Sql is not null && step.Source.StoredProcedure is not null)
        {
            losses.Add(
                $"The section '{sectionName}' gives both a Query and a StoredProcedure. SyncJob's pre-flight probe " +
                "runs the procedure while its copy runs the query, so the two disagreed silently. Both are imported " +
                "and the job will not validate until one of them is removed.");
        }

        if(dto.Options is { } options)
        {
            step.BatchSize = Math.Max(0, options.BatchSize);
            step.CommandTimeoutSeconds = Math.Max(0, options.BulkCopyTimeoutSeconds);
            step.Publication.KeepIdentity = options.KeepIdentity;
            step.Publication.MaxDegreeOfParallelism = Math.Max(1, options.MaxDegreeOfParallelism);
            step.Publication.Guard.MinimumRows = Math.Max(0, options.MinRowThresholdToCommit);

            RecordUnknownProperties(
                options.Unknown,
                $"the Options of '{sectionName}'",
                "syncjob.json.options.unknown",
                step.Extensions,
                losses);
        }

        step.Publication.StagingTable = Blank(dto.Destination?.StageTable);

        ReadColumnMappings(sectionName, dto, step, losses);
        ReadIncremental(sectionName, dto, step, losses);

        return step;
    }

    /// <summary>
    /// Turns the JSON column list into field maps - which means turning almost all of it
    /// into nothing.
    /// <para>
    /// SyncJob's list is load-bearing today: it is both the bulk-copy mapping and the
    /// explicit destination column list that stops a publication going in shifted
    /// (Program.cs:1878). This model gets that same protection from discovering both
    /// sides and matching by name, so an entry naming the same column twice says nothing
    /// the default does not already say. What is kept is what departs: a rename.
    /// </para>
    /// </summary>
    private static void ReadColumnMappings(string sectionName, SectionDto dto, SyncStep step, List<string> losses)
    {
        List<ColumnMapDto> mappings = dto.ColumnMappings ?? [];
        if(mappings.Count == 0)
            return;

        step.Extensions["syncjob.json.columnMappings"] =
            string.Join("; ", mappings.Select(x => $"{x.Source}={x.Dest}"));

        var identity = 0;

        foreach(var mapping in mappings)
        {
            if(string.IsNullOrWhiteSpace(mapping.Dest))
                continue;

            if(string.IsNullOrWhiteSpace(mapping.Source) ||
               string.Equals(mapping.Source, mapping.Dest, StringComparison.OrdinalIgnoreCase))
            {
                identity++;
                continue;
            }

            step.FieldMaps.Add(new FieldMap { Source = mapping.Source, Target = mapping.Dest });
        }

        if(identity == 0)
            return;

        losses.Add(
            $"The section '{sectionName}' lists {identity} column mapping(s) that name the same column on both " +
            "sides. They are not imported as field maps: this model discovers both sides and matches by name, so " +
            "an identity list is the default. The list is kept under the step's 'syncjob.json.columnMappings'. " +
            "Where it was also being used to keep a destination column out of the load, add a field map with " +
            "IsExcluded for that column.");
    }

    private static void ReadIncremental(string sectionName, SectionDto dto, SyncStep step, List<string> losses)
    {
        if(dto.Incremental is not { } incremental)
            return;

        RecordUnknownProperties(
            incremental.Unknown,
            $"the Incremental block of '{sectionName}'",
            "syncjob.json.incremental.unknown",
            step.Extensions,
            losses);

        if(!incremental.Enabled)
        {
            if(incremental.CarriesSettings)
            {
                step.Extensions["syncjob.json.incremental.disabled"] =
                    $"Mode={incremental.Mode}; TrackingColumn={incremental.TrackingColumn}; " +
                    $"MergeStrategy={incremental.MergeStrategy}";

                losses.Add(
                    $"The section '{sectionName}' carries an incremental configuration with Enabled=false. It runs " +
                    "as a full replace, so none of it is imported as behaviour; the settings are kept under the " +
                    "step's 'syncjob.json.incremental.disabled'.");
            }

            return;
        }

        ReadPublicationMode(sectionName, incremental, step, losses);
        ReadKeys(incremental, step);
        ReadTracking(sectionName, incremental, step, losses);
        ReadDeleteDetection(sectionName, incremental, step, losses);
    }

    private static void ReadPublicationMode(
        string sectionName,
        IncrementalDto incremental,
        SyncStep step,
        List<string> losses)
    {
        var strategy = incremental.MergeStrategy?.Trim();

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
                $"The section '{sectionName}' asks for MergeStrategy 'Full', which also meant deleting the " +
                "destination rows that the source no longer returns. It imports as a merge, which inserts and " +
                "updates but deletes nothing: this model removes rows through a tombstone ledger the source writes, " +
                "not by comparing whole tables, because absence from a filtered read is not evidence of deletion. " +
                "Point the step at a ledger, or accept that deletions will no longer propagate.");
        }
        else
        {
            step.Publication.Mode = PublicationMode.Merge;
            step.Extensions["syncjob.json.mergeStrategy"] = strategy;

            losses.Add(
                $"The section '{sectionName}' names the merge strategy '{strategy}', which is not one SyncJob " +
                "defines. It was imported as a merge; check that this is what was meant.");
        }

        // Both the merge and the delete detection are dead code in the CLI:
        // IncrementalSyncEngine.ExecuteMerge and ExecuteDeleteDetection are written,
        // documented and never called from anywhere. The configuration was therefore a
        // statement of intent, and this engine will now act on it.
        if(step.Publication.Mode == PublicationMode.Merge)
        {
            losses.Add(
                $"The section '{sectionName}' is configured to merge, but SyncJob never did: " +
                "IncrementalSyncEngine.ExecuteMerge is never called from anywhere and every run replaced the " +
                "destination instead. The imported job will do what the configuration always said, which is a " +
                "change in behaviour - check the unique key before the first run.");
        }
    }

    private static void ReadKeys(IncrementalDto incremental, SyncStep step)
    {
        foreach(var column in incremental.PrimaryKeyColumns ?? [])
        {
            if(string.IsNullOrWhiteSpace(column))
                continue;

            Key(step, column).IsUniqueKey = true;
        }
    }

    private static void ReadTracking(
        string sectionName,
        IncrementalDto incremental,
        SyncStep step,
        List<string> losses)
    {
        var mode = incremental.Mode?.Trim();
        var isColumnTracking =
            string.IsNullOrEmpty(mode) ||
            mode.Equals("Timestamp", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("RowVersion", StringComparison.OrdinalIgnoreCase);

        if(!isColumnTracking)
        {
            step.Extensions["syncjob.json.incremental.mode"] = mode!;

            if(!string.IsNullOrWhiteSpace(incremental.TrackingColumn))
                step.Extensions["syncjob.json.incremental.trackingColumn"] = incremental.TrackingColumn!;

            losses.Add(
                $"The section '{sectionName}' tracks changes with '{mode}', which this model has no equivalent " +
                "for: it follows one column's highest value, not SQL Server's own change feeds. The rest of the " +
                "job is imported and it will read everything on every run until a sync-key column is chosen.");

            return;
        }

        if(string.IsNullOrWhiteSpace(incremental.TrackingColumn))
        {
            losses.Add(
                $"The section '{sectionName}' is incremental in mode '{mode ?? "Timestamp"}' but names no " +
                "TrackingColumn, so there is nothing to follow and no watermark was imported. The step will read " +
                "everything on every run.");

            return;
        }

        Key(step, incremental.TrackingColumn!).IsSyncKey = true;

        step.Incremental = new IncrementalPlan
        {
            ForceFullRead = incremental.ForceFullRefresh,
            Watermark = new WatermarkPlan { StateTable = Blank(incremental.TrackingTable) }
        };

        if(mode is not null && mode.Equals("RowVersion", StringComparison.OrdinalIgnoreCase))
            step.Extensions["syncjob.json.incremental.mode"] = mode;
    }

    private static void ReadDeleteDetection(
        string sectionName,
        IncrementalDto incremental,
        SyncStep step,
        List<string> losses)
    {
        if(incremental.DeleteDetection is not { Enabled: true } detection)
            return;

        var mode = detection.Mode?.Trim() ?? "SoftDelete";

        step.Extensions["syncjob.json.deleteDetection"] =
            $"Mode={mode}; SoftDeleteColumn={detection.SoftDeleteColumn}; " +
            $"SoftDeleteValue={detection.SoftDeleteValue}; UseComparison={detection.UseComparison}";

        losses.Add(
            $"The section '{sectionName}' detects deletions with '{mode}', which this model has no field for: it " +
            "applies deletions from a ledger the source writes, and it has no way to read a soft-delete flag, no " +
            "change feed, and no whole-table comparison. The settings are kept under the step's " +
            "'syncjob.json.deleteDetection'. Nothing was lost in practice - " +
            "IncrementalSyncEngine.ExecuteDeleteDetection is never called from anywhere, so no deletion has ever " +
            "been applied by this path.");
    }

    /// <summary>
    /// The field map for a column, made once and then decorated. A column that is both
    /// the primary key and the tracking column is one map with two roles, not two maps -
    /// two would read as two sync keys and the validator would refuse the job.
    /// </summary>
    private static FieldMap Key(SyncStep step, string column)
    {
        var name = column.Trim();

        var existing = step.FieldMaps.FirstOrDefault(x =>
            string.Equals(x.Source, name, StringComparison.OrdinalIgnoreCase));

        if(existing is not null)
            return existing;

        var map = new FieldMap { Source = name };
        step.FieldMaps.Add(map);

        return map;
    }

    private static void RecordUnknownProperties(
        Dictionary<string, JsonElement>? unknown,
        string where,
        string extensionKey,
        Dictionary<string, string> extensions,
        List<string> losses)
    {
        if(unknown is null || unknown.Count == 0)
            return;

        var names = unknown.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

        extensions[extensionKey] = string.Join(", ", names);

        losses.Add(
            $"{where} carries {names.Count} setting(s) SyncJob does not define: {string.Join(", ", names)}. " +
            $"They are kept under '{extensionKey}' so nothing is lost, but nothing reads them.");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ------------------------------------------------------------------- the JSON shape
    //
    // These mirror SyncConfig and IncrementalConfig in the CLI. The enums are read as
    // strings on purpose: an unrecognised value becomes a loss an operator can read
    // rather than an exception in the middle of an import of forty sections.

    private sealed class SectionDto
    {
        public SourceDto? Source { get; set; }

        public DestinationDto? Destination { get; set; }

        public List<ColumnMapDto>? ColumnMappings { get; set; }

        public OptionsDto? Options { get; set; }

        public IncrementalDto? Incremental { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Unknown { get; set; }

        /// <summary>
        /// An appsettings file holds Logging, Serilog and whatever else beside the jobs.
        /// A section with none of a job's parts is not a job that failed to import, so it
        /// is passed over in silence rather than reported as a loss.
        /// </summary>
        internal bool LooksLikeAJob =>
            Source is not null || Destination is not null || ColumnMappings is not null ||
            Options is not null || Incremental is not null;
    }

    private sealed class SourceDto
    {
        public string? ConnectionString { get; set; }

        public string? Query { get; set; }

        public string? StoredProcedure { get; set; }

        public Dictionary<string, string?>? Parameters { get; set; }
    }

    private sealed class DestinationDto
    {
        public string? ConnectionString { get; set; }

        public string? StageTable { get; set; }

        public string? FinalTable { get; set; }
    }

    private sealed class ColumnMapDto
    {
        public string? Source { get; set; }

        public string? Dest { get; set; }
    }

    private sealed class OptionsDto
    {
        public int BatchSize { get; set; }

        public int MaxDegreeOfParallelism { get; set; }

        public int BulkCopyTimeoutSeconds { get; set; }

        public bool KeepIdentity { get; set; }

        public int MinRowThresholdToCommit { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Unknown { get; set; }
    }

    private sealed class IncrementalDto
    {
        public bool Enabled { get; set; }

        public string? Mode { get; set; }

        public string? TrackingColumn { get; set; }

        public string? TrackingTable { get; set; }

        public string? JobIdentifier { get; set; }

        public DeleteDetectionDto? DeleteDetection { get; set; }

        public bool ForceFullRefresh { get; set; }

        public List<string>? PrimaryKeyColumns { get; set; }

        public string? MergeStrategy { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Unknown { get; set; }

        internal bool CarriesSettings =>
            !string.IsNullOrWhiteSpace(Mode) ||
            !string.IsNullOrWhiteSpace(TrackingColumn) ||
            !string.IsNullOrWhiteSpace(MergeStrategy) ||
            PrimaryKeyColumns is { Count: > 0 } ||
            DeleteDetection is { Enabled: true };
    }

    private sealed class DeleteDetectionDto
    {
        public bool Enabled { get; set; }

        public string? Mode { get; set; }

        public string? SoftDeleteColumn { get; set; }

        public string? SoftDeleteValue { get; set; }

        public bool UseComparison { get; set; }
    }
}
