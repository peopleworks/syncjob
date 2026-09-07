using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SyncJob.Core.Model;
using SyncJob.Core.Run;
using SyncJob.Database;
using SyncJob.Engine;
using SyncJob.Services.Models;

namespace SyncJob.Services;

/// <summary>
/// Runs one requested synchronisation on the SyncJob.Core engine and says what it did.
/// <para>
/// This class used to carry a third copy of the pipeline, beside the CLI's two, and it
/// was the copy that held the bug. It published with
/// <c>TRUNCATE TABLE final; INSERT INTO final SELECT * FROM stage;</c>, which is three
/// failures in one statement: no column list, so the rows go in shifted the day the two
/// tables stop agreeing and for as long as the types line up; a schema-modification lock
/// held for the length of the insert, against which even a reader that asked for
/// <c>NOLOCK</c> blocks; and one check before it, <c>stageCount != rowCount</c>, which
/// asks whether the bulk copy lost rows rather than whether the load is plausible - so a
/// source that returned nothing gave <c>0 == 0</c>, passed, emptied a production table
/// and reported success.
/// </para>
/// <para>
/// All of that now belongs to <see cref="JobRunner"/>. What is left here is what belongs
/// to the service and to nothing else: reading the configuration out of the local SQLite
/// store, resolving the credentials the model refuses to hold, turning one
/// <see cref="JobRun"/> into a task result, and writing the execution history with the
/// numbers the engine actually counted.
/// </para>
/// </summary>
public sealed class SyncTaskExecutor
{
    /// <summary>
    /// The share of the destination a replace has to stage before it is allowed to
    /// publish, when nothing in the configuration would have caught the loss.
    /// <para>
    /// The service's configuration has exactly one guard setting, <c>MinRowThreshold</c>,
    /// and it defaults to zero - which is no guard at all, and is the state every
    /// deployed configuration is in. A fixed floor is also the check that cannot see the
    /// failure it exists for: a table that grew to four million rows and stages nine
    /// hundred passes any floor anyone set when it had a thousand.
    /// </para>
    /// <para>
    /// One percent, and deliberately low. Nobody typed this number, so it has to refuse
    /// only the failure it is here for: it is the difference between a table that shrank
    /// and a table that vanished, not a judgement about how much a load ought to move.
    /// An operator who wants a real floor still sets <c>MinRowThreshold</c>, and both
    /// have to pass. Against an empty or absent destination the fraction does not apply,
    /// so a first run is not refused.
    /// </para>
    /// </summary>
    public const double MinimumFractionOfDestination = 0.01;

    /// <summary>What a run is attributed to when the task does not say who asked for it.</summary>
    private const string DefaultTrigger = "SyncJobWorker";

    private readonly ILogger<SyncTaskExecutor> _logger;

    public SyncTaskExecutor(ILogger<SyncTaskExecutor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Runs the configuration the task names and returns what happened.
    /// <para>
    /// It does not throw for a run that went wrong: a failed step is a result, not an
    /// exception, so the caller gets the row counts of whatever did happen. It still
    /// catches, because everything before the run - a configuration that is not there,
    /// a password that will not decrypt, a destination that cannot be reached - is a
    /// throw, and so is the one place the engine itself still throws (see
    /// <see cref="RunAsync"/>).
    /// </para>
    /// </summary>
    public async Task<SyncTaskExecutionResult> ExecuteTaskAsync(
        SyncTaskEntity task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var executionId = Guid.NewGuid();
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var result = new SyncTaskExecutionResult
        {
            ExecutionId = executionId,
            Success = false
        };

        // Kept outside the try so that a failure after the configuration was read still
        // has somewhere to write its history row.
        string? configId = null;

        try
        {
            _logger.LogInformation(
                "Starting execution of task {TaskId} (Type: {TaskType})", task.TaskId, task.TaskType);

            var config = ConfigRepository.GetById(task.ConfigId ?? task.ProjectId)
                ?? throw new InvalidOperationException(
                    $"Configuration '{task.ConfigId ?? task.ProjectId}' not found");

            configId = config.ConfigId;

            var mappings = ColumnMappingRepository.GetByConfigId(config.ConfigId);
            if(mappings.Count == 0)
                throw new InvalidOperationException($"No column mappings found for '{config.ConfigId}'");

            var source = ConnectionRepository.GetById(config.SourceConnectionId)
                ?? throw new InvalidOperationException(
                    $"Source connection '{config.SourceConnectionId}' not found");

            var destination = ConnectionRepository.GetById(config.DestConnectionId)
                ?? throw new InvalidOperationException(
                    $"Destination connection '{config.DestConnectionId}' not found");

            var triggeredBy = task.RequestedBy ?? DefaultTrigger;

            var run = await RunAsync(config, mappings, source, destination, triggeredBy, cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            Describe(task, run);
            Fill(result, run, stopwatch.ElapsedMilliseconds);
            Record(executionId, config.ConfigId, startedAt, stopwatch.ElapsedMilliseconds, run, triggeredBy);
        }
        catch(Exception ex)
        {
            stopwatch.Stop();

            result.Success = false;
            result.Skipped = false;
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            result.ErrorMessage = ex.Message;
            result.ErrorStackTrace = ex.StackTrace;

            _logger.LogError(ex, "Task {TaskId} failed: {ErrorMessage}", task.TaskId, ex.Message);

            if(configId is not null)
                Record(executionId, configId, startedAt, stopwatch.ElapsedMilliseconds, ex, task.RequestedBy ?? DefaultTrigger);
        }

        return result;
    }

    // ========================================================================
    // THE RUN
    // ========================================================================

    /// <summary>
    /// Builds the job through the adapter the three surfaces share and runs it.
    /// <para>
    /// <see cref="JobRunner.RunAsync"/> does not throw for anything that is a run
    /// outcome, so there is no catch here - a failed step comes back as a failed step
    /// with the other steps' numbers intact. It does still throw when the destination
    /// cannot be reached at all, because taking the lease is the first thing it does and
    /// that call is not guarded; the outer catch is what covers it, and the lead has been
    /// told.
    /// </para>
    /// </summary>
    private async Task<JobRun> RunAsync(
        ConfigurationEntity config,
        List<ColumnMappingEntity> mappings,
        ConnectionEntity source,
        ConnectionEntity destination,
        string triggeredBy,
        CancellationToken cancellationToken)
    {
        var losses = new List<string>();

        var sourceConnectionString = BuildConnectionString(source);
        var destinationConnectionString = BuildConnectionString(destination);

        var cfg = ToSyncConfig(config, mappings, sourceConnectionString, destinationConnectionString);
        var section = SectionJson(config, mappings, sourceConnectionString, destinationConnectionString, losses);

        // The service has no command line, so nothing overrides the configuration. The
        // adapter takes a RunSettings because that is what the CLI hands it, and a
        // default instance says "no overrides" in the terms it already understands.
        var settings = new RunSettings();

        var (job, imported) = await CoreAdapter
            .JobAsync(config.ConfigId, section, cfg, settings, cancellationToken)
            .ConfigureAwait(false);

        Guard(job);
        Note(config, losses);
        Report(config.ConfigId, losses.Concat(imported));

        var options = Leased(CoreAdapter.Options(
            cfg, settings, triggeredBy, new RunLog(_logger, config.ConfigId)));

        return await new JobRunner().RunAsync(job, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The guard this surface sets for itself, on top of the floor the configuration
    /// carries. See <see cref="MinimumFractionOfDestination"/> for the number.
    /// <para>
    /// A replace and nothing else. A merge or an append stages the rows that changed
    /// since the last run, so comparing a night's changes against the whole destination
    /// would refuse every quiet night - the fraction is only meaningful where the staged
    /// set is supposed to be the whole table.
    /// </para>
    /// <para>
    /// Left alone where something already set it, so that an importer which one day
    /// carries a fraction of its own wins over a default nobody chose.
    /// </para>
    /// </summary>
    private static void Guard(SyncJobDefinition job)
    {
        foreach(var step in job.Steps)
        {
            if(step.Publication.Mode != PublicationMode.Replace)
                continue;

            step.Publication.Guard.MinimumFractionOfDestination ??= MinimumFractionOfDestination;
        }
    }

    /// <summary>
    /// The adapter's options with the lease switched on.
    /// <para>
    /// <see cref="JobRunOptions.UseLease"/> is false in the adapter, which is right for a
    /// one-shot CLI on one machine and wrong here. This is the surface where two hosts
    /// can genuinely overlap - two workers polling the same central queue, or one worker
    /// restarted while the last run is still going - and it is the reason
    /// <see cref="SqlJobLeaseStore"/> exists at all: it replaces a status flag that a
    /// crashed run leaves set for ever.
    /// </para>
    /// <para>
    /// Copied property by property because <see cref="JobRunOptions"/> is a class with
    /// init-only properties and not a record, so there is no <c>with</c>, and the adapter
    /// belongs to another package. That makes this a copy which silently stops carrying
    /// whatever property the Core adds next, and it is the one thing wrong with it.
    /// </para>
    /// </summary>
    private static JobRunOptions Leased(JobRunOptions options) => new()
    {
        TriggeredBy = options.TriggeredBy,
        DryRun = options.DryRun,
        ResolveConnectionString = options.ResolveConnectionString,
        Progress = options.Progress,
        ContinueOnError = options.ContinueOnError,
        UseLease = true
    };

    // ========================================================================
    // WHAT THE CONFIGURATION BECOMES
    // ========================================================================

    /// <summary>
    /// The part of the configuration the adapter reads directly: the two resolved
    /// connection strings and the copy options.
    /// <para>
    /// The rest of the job travels as JSON - see <see cref="SectionJson"/> - so this
    /// carries no incremental block. Building one would mean parsing
    /// <c>TrackingMode</c> and <c>MergeStrategy</c> into the CLI's enums, and that is
    /// where the old converter threw: the column's own default is <c>Snapshot</c>, which
    /// is not a member of <c>TrackingMode</c>, so every configuration left at the default
    /// with a tracking column set failed before a row moved.
    /// </para>
    /// </summary>
    private static SyncConfig ToSyncConfig(
        ConfigurationEntity config,
        List<ColumnMappingEntity> mappings,
        string sourceConnectionString,
        string destinationConnectionString) => new()
    {
        Source = new SourceConfig
        {
            ConnectionString = sourceConnectionString,
            Query = config.SourceQuery,
            StoredProcedure = config.SourceStoredProc
        },
        Destination = new DestConfig
        {
            ConnectionString = destinationConnectionString,
            StageTable = config.DestStageTable,
            FinalTable = config.DestFinalTable
        },
        ColumnMappings = mappings
            .Select(x => new ColumnMap { Source = x.SourceColumn, Dest = x.DestColumn })
            .ToList(),
        Options = new SyncOptions
        {
            BatchSize = config.BatchSize,
            MaxDegreeOfParallelism = config.MaxDOP,
            BulkCopyTimeoutSeconds = config.BulkCopyTimeout,
            KeepIdentity = config.KeepIdentity,
            MinRowThresholdToCommit = config.MinRowThreshold
        }
    };

    /// <summary>
    /// The configuration as the one JSON section the importer reads.
    /// <para>
    /// Written out rather than serialised from <see cref="SyncConfig"/> for one reason
    /// that matters: the importer reads <c>Mode</c> and <c>MergeStrategy</c> as strings
    /// and turns a value it does not recognise into a loss an operator can read, while
    /// going through the CLI's enums turns the same value into an exception in the middle
    /// of a run. The SQLite column holds whatever a person typed, so it goes across as
    /// written.
    /// </para>
    /// <para>
    /// Only properties the importer defines are emitted: anything else lands in its
    /// extension data and is reported as a setting SyncJob does not define, which would
    /// be this method telling on itself once a night.
    /// </para>
    /// </summary>
    private static string SectionJson(
        ConfigurationEntity config,
        List<ColumnMappingEntity> mappings,
        string sourceConnectionString,
        string destinationConnectionString,
        List<string> losses)
    {
        var section = new Dictionary<string, object?>
        {
            ["Source"] = new Dictionary<string, object?>
            {
                ["ConnectionString"] = sourceConnectionString,
                ["Query"] = config.SourceQuery,
                ["StoredProcedure"] = config.SourceStoredProc,
                ["Parameters"] = Parameters(config, losses)
            },
            ["Destination"] = new Dictionary<string, object?>
            {
                ["ConnectionString"] = destinationConnectionString,
                ["StageTable"] = config.DestStageTable,
                ["FinalTable"] = config.DestFinalTable
            },
            ["ColumnMappings"] = mappings
                .Select(x => new Dictionary<string, object?>
                {
                    ["Source"] = x.SourceColumn,
                    ["Dest"] = x.DestColumn
                })
                .ToList(),
            ["Options"] = new Dictionary<string, object?>
            {
                ["BatchSize"] = config.BatchSize,
                ["MaxDegreeOfParallelism"] = config.MaxDOP,
                ["BulkCopyTimeoutSeconds"] = config.BulkCopyTimeout,
                ["KeepIdentity"] = config.KeepIdentity,
                ["MinRowThresholdToCommit"] = config.MinRowThreshold
            }
        };

        if(IsIncremental(config))
        {
            section["Incremental"] = new Dictionary<string, object?>
            {
                ["Enabled"] = true,
                ["Mode"] = config.TrackingMode,
                ["TrackingColumn"] = config.TrackingColumn,

                // Deliberately not the service's own dbo.SyncJobTracking. See Note().
                ["TrackingTable"] = null,

                ["JobIdentifier"] = config.ConfigId,
                ["ForceFullRefresh"] = false,
                ["PrimaryKeyColumns"] = mappings
                    .Where(x => x.IsPrimaryKey)
                    .Select(x => x.DestColumn)
                    .ToList(),
                ["MergeStrategy"] = config.MergeStrategy
            };
        }

        return JsonSerializer.Serialize(section);
    }

    /// <summary>
    /// Whether the configuration asks for anything other than a full replace.
    /// <para>
    /// <c>None</c> and <c>Snapshot</c> both mean "read it all and replace it all";
    /// <c>Snapshot</c> is the column's default and is the value nearly every deployed
    /// configuration carries. Sending either across as an enabled incremental block would
    /// turn a replace into a merge on the strength of a default nobody set.
    /// </para>
    /// </summary>
    private static bool IsIncremental(ConfigurationEntity config) =>
        !string.IsNullOrWhiteSpace(config.TrackingColumn) &&
        !string.Equals(config.TrackingMode, "None", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(config.TrackingMode, "Snapshot", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The stored procedure's parameters, which the old path dropped on the floor with a
    /// <c>// TODO: Parse from config.SourceParameters if needed</c> beside it - so a
    /// procedure configured with parameters was called with none of them.
    /// </summary>
    private static Dictionary<string, string?>? Parameters(ConfigurationEntity config, List<string> losses)
    {
        if(string.IsNullOrWhiteSpace(config.SourceParameters))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(config.SourceParameters);
        }
        catch(JsonException e)
        {
            losses.Add(
                $"The stored procedure parameters stored for '{config.ConfigId}' are not readable as JSON, so the " +
                $"procedure is called without them and will use its own defaults: {e.Message}");

            return null;
        }
    }

    /// <summary>
    /// The things this surface knows are lost that the importer cannot know about.
    /// </summary>
    private static void Note(ConfigurationEntity config, List<string> losses)
    {
        if(config.MaxDOP > 1)
        {
            losses.Add(
                $"MaxDOP is {config.MaxDOP}, and the engine reads the whole source through one streaming copy " +
                "rather than splitting it into batches and writing them in parallel. The setting is carried into " +
                "the model and nothing reads it, so this run is sequential. It is not a loss of throughput in " +
                "practice - the parallel path first read every row into memory, which is why no batch size made a " +
                "table larger than RAM copyable - but the number no longer changes anything.");
        }

        if(!string.IsNullOrWhiteSpace(config.DestStageTable))
        {
            losses.Add(
                $"The staging table {config.DestStageTable} is no longer a table you maintain. It is dropped and " +
                "rebuilt from the destination's own shape on every run, which is the fix for the failure that made " +
                "this worth doing: a hand-made stage and its destination drift apart, and a publication with no " +
                "column list then moves the data across shifted.");
        }

        if(IsIncremental(config))
        {
            losses.Add(
                "The watermark is no longer kept in dbo.SyncJobTracking. That table has a row per job with a last " +
                "sync time, a row version and a run's counters in it; the engine keeps one row per job and step " +
                "with the value and the value before it, so pointing it at the old table would fail on the first " +
                "read. It uses dbo.SyncJobWatermark in the destination instead, the old table is left exactly as " +
                "it is, and the first run after this reads everything because the new table has no mark yet.");
        }

        if(!string.IsNullOrWhiteSpace(config.TrackingColumn) &&
           string.Equals(config.TrackingMode, "Snapshot", StringComparison.OrdinalIgnoreCase))
        {
            losses.Add(
                $"The tracking mode is 'Snapshot' and the tracking column is {config.TrackingColumn}. Snapshot " +
                "means read it all and replace it all, so the column is not followed and the step is a full " +
                "replace - which is what this service has always done with it. Set the mode to Timestamp or " +
                "RowVersion to have the column actually followed.");
        }
    }

    /// <summary>
    /// Builds the connection string for an endpoint, credential and all.
    /// <para>
    /// This is the half of the credential story the Core refuses to hold: the model
    /// carries a redacted connection string and the adapter's resolver hands the real one
    /// back at run time. Nothing here is logged.
    /// </para>
    /// </summary>
    private static string BuildConnectionString(ConnectionEntity conn)
    {
        if(conn.ConnectionStringEncrypted is { Length: > 0 })
        {
            try
            {
                return System.Text.Encoding.UTF8.GetString(
                    System.Security.Cryptography.ProtectedData.Unprotect(
                        conn.ConnectionStringEncrypted,
                        null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
            catch(System.Security.Cryptography.CryptographicException)
            {
                // A blob written by another user or another machine. The parts below are
                // still there, so the fall-through is a working connection often enough
                // to be worth trying before giving up.
            }
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = conn.ServerName,
            InitialCatalog = conn.DatabaseName,
            TrustServerCertificate = conn.TrustServerCertificate,
            Encrypt = conn.Encrypt
        };

        if(string.IsNullOrWhiteSpace(conn.Username))
        {
            builder.IntegratedSecurity = true;
            return builder.ToString();
        }

        builder.UserID = conn.Username;

        if(conn.PasswordEncrypted is { Length: > 0 })
        {
            try
            {
                builder.Password = System.Text.Encoding.UTF8.GetString(
                    System.Security.Cryptography.ProtectedData.Unprotect(
                        conn.PasswordEncrypted,
                        null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
            catch(System.Security.Cryptography.CryptographicException)
            {
                throw new InvalidOperationException(
                    $"Failed to decrypt password for connection '{conn.ConnectionId}'");
            }
        }

        return builder.ToString();
    }

    // ========================================================================
    // WHAT CAME BACK
    // ========================================================================

    /// <summary>
    /// Everything the file said that did not carry across, at information level.
    /// <para>
    /// Printed rather than swallowed because an operator who is not told is an operator
    /// who thinks it worked.
    /// </para>
    /// </summary>
    private void Report(string configId, IEnumerable<string> losses)
    {
        var list = losses.ToList();
        if(list.Count == 0)
            return;

        _logger.LogInformation(
            "Configuration {ConfigId} did not carry across whole; {Count} note(s) follow. The run went ahead.",
            configId, list.Count);

        foreach(var loss in list)
            _logger.LogInformation("{ConfigId}: {Loss}", configId, loss);
    }

    /// <summary>
    /// The run, step by step, with the numbers the work produced rather than the ones the
    /// intention had.
    /// <para>
    /// A refusal because another host holds the lease arrives here as a skip, and it is
    /// logged as information. A service that pages someone at 02:00 because the lease
    /// worked is a service people switch off.
    /// </para>
    /// </summary>
    private void Describe(SyncTaskEntity task, JobRun run)
    {
        foreach(var step in run.Steps)
        {
            // A result carrying the reserved id is about the run and not about a step, so
            // it is not printed as a step called "(run)".
            if(step.StepId == JobRunner.RunLevelStepId)
            {
                _logger.LogInformation("Task {TaskId}: {Message}", task.TaskId, step.Message);
                continue;
            }

            _logger.LogInformation(
                "Task {TaskId} step {Step} {Status}: {RowsRead} read, {RowsInserted} inserted, " +
                "{RowsUpdated} updated, {RowsDeleted} deleted{Watermark}",
                task.TaskId,
                step.StepName,
                step.Status,
                step.RowsRead,
                step.RowsInserted,
                step.RowsUpdated,
                step.RowsDeleted,
                step.WatermarkValue is null
                    ? string.Empty
                    : $", watermark {step.PreviousWatermarkValue ?? "(none)"} -> {step.WatermarkValue}");

            if(step.Message is { Length: > 0 } message)
                _logger.LogInformation("Task {TaskId} step {Step}: {Message}", task.TaskId, step.StepName, message);
        }

        // The outcome at the level it deserves, which is the difference between a night
        // of skips and a night of failures. A guard that refused is an error: a table
        // someone asked to be loaded is stale and the source is why. A lease another host
        // holds is information: that is the lease doing its job.
        switch(run.Status)
        {
            case RunStatus.Failed:
                _logger.LogError(
                    "Task {TaskId} did not publish and the destination still holds what it held: {Notes}",
                    task.TaskId, Notes(run));
                break;

            case RunStatus.Skipped:
                _logger.LogInformation(
                    "Task {TaskId} was skipped and nothing was written: {Notes}", task.TaskId, Notes(run));
                break;

            default:
                _logger.LogInformation(
                    "Task {TaskId} published: {RowsRead} read, {RowsInserted} inserted, {RowsUpdated} updated, " +
                    "{RowsDeleted} deleted",
                    task.TaskId, run.RowsRead, run.RowsInserted, run.RowsUpdated, run.RowsDeleted);
                break;
        }
    }

    /// <summary>
    /// The run turned into the task result the worker and the central queue read.
    /// <para>
    /// A skip is a success with something to say. It is a table someone asked to be
    /// loaded that deliberately was not - a guard that refused, a lease another host
    /// holds - which is a report rather than a phone call, but it is not silence either.
    /// </para>
    /// </summary>
    private static void Fill(SyncTaskExecutionResult result, JobRun run, long durationMs)
    {
        result.DurationMs = durationMs;

        // From the run, which took them from the copier and the publisher. The path this
        // replaces set RowsInserted to the source row count.
        result.RowsProcessed = run.RowsRead;
        result.RowsInserted = run.RowsInserted;
        result.RowsUpdated = run.RowsUpdated;
        result.RowsDeleted = run.RowsDeleted;
        result.RowsFailed = 0;

        result.Success = run.Status != RunStatus.Failed;
        result.Skipped = run.Status == RunStatus.Skipped;
        result.Notes = Notes(run);

        if(run.Status == RunStatus.Failed)
        {
            result.ErrorMessage = result.Notes
                ?? "the run failed and no step said why, which is itself worth reporting";
        }
    }

    /// <summary>What the steps had to say, in one string, or null when they all published quietly.</summary>
    private static string? Notes(JobRun run)
    {
        var messages = run.Steps
            .Select(x => x.Message)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        return messages.Count == 0 ? null : string.Join(" ", messages);
    }

    private static string Status(RunStatus status) => status switch
    {
        RunStatus.Succeeded => "Success",
        RunStatus.Skipped => "Skipped",
        _ => "Failed"
    };

    /// <summary>
    /// The local execution history, with the engine's counts.
    /// <para>
    /// Written for a failure and a skip as well as a success. The path this replaces only
    /// wrote the row when everything worked, so the local history of a job that had been
    /// failing every night for a week was empty.
    /// </para>
    /// </summary>
    private void Record(
        Guid executionId,
        string configId,
        DateTime startedAt,
        long durationMs,
        JobRun run,
        string triggeredBy) =>
        Record(new ExecutionHistoryEntity
        {
            ExecutionId = executionId.ToString("N"),
            ConfigId = configId,
            StartTime = startedAt,
            EndTime = startedAt.AddMilliseconds(durationMs),
            DurationMs = durationMs,
            Status = Status(run.Status),
            ExecutionMode = "OnDemand",
            RowsRead = run.RowsRead,
            RowsInserted = run.RowsInserted,
            RowsUpdated = run.RowsUpdated,
            RowsDeleted = run.RowsDeleted,
            ErrorMessage = run.Status == RunStatus.Succeeded ? null : Notes(run),
            HostMachine = Environment.MachineName,
            TriggeredBy = triggeredBy
        });

    /// <summary>The history row for a run that threw before it became a <see cref="JobRun"/>.</summary>
    private void Record(
        Guid executionId,
        string configId,
        DateTime startedAt,
        long durationMs,
        Exception error,
        string triggeredBy) =>
        Record(new ExecutionHistoryEntity
        {
            ExecutionId = executionId.ToString("N"),
            ConfigId = configId,
            StartTime = startedAt,
            EndTime = startedAt.AddMilliseconds(durationMs),
            DurationMs = durationMs,
            Status = "Failed",
            ExecutionMode = "OnDemand",
            ErrorMessage = error.Message,
            ErrorStackTrace = error.StackTrace,
            HostMachine = Environment.MachineName,
            TriggeredBy = triggeredBy
        });

    /// <summary>
    /// A history row that cannot be written is a warning and not a failed load: the rows
    /// that were published are published, and reporting the load as failed because the
    /// note about it did not save is how a good run gets run again.
    /// </summary>
    private void Record(ExecutionHistoryEntity execution)
    {
        try
        {
            ExecutionHistoryRepository.Create(execution);
        }
        catch(Exception e)
        {
            _logger.LogWarning(
                e,
                "The execution history row for {ConfigId} could not be written: {ErrorMessage}. " +
                "Whatever the run published is published.",
                execution.ConfigId, e.Message);
        }
    }

    /// <summary>
    /// The engine's progress through the service's own log.
    /// <para>
    /// No throttling of its own: the copy reports every ten thousand rows and the runner
    /// reports once per step, so this is already as quiet as the work is.
    /// </para>
    /// </summary>
    private sealed class RunLog(ILogger logger, string configId) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) =>
            logger.LogInformation("{ConfigId} {StepId}: {Message}", configId, value.StepId, value.Message);
    }
}
