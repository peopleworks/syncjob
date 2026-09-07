using SyncJob.Core;
using SyncJob.Core.Import;
using SyncJob.Core.Model;
using SyncJob.Core.Run;

namespace SyncJob.Engine;

/// <summary>
/// Turns a loaded <see cref="SyncConfig"/> into what the engine takes: a
/// <see cref="SyncJobDefinition"/> and a <see cref="JobRunOptions"/>.
/// <para>
/// One place, because there are three surfaces and the whole point of the Core is that
/// they stop disagreeing. The CLI, the Windows service and the central command all come
/// through here.
/// </para>
/// <para>
/// The model is built by <see cref="AppSettingsJobImporter"/> rather than by hand, so
/// the same reader that imports a file for inspection is the one that runs it - a
/// second mapping written here would be a second thing to keep in step, and the
/// importer already knows things this would have to learn, such as which source wins
/// when a section carries both a query and a procedure.
/// </para>
/// </summary>
internal static class CoreAdapter
{
    /// <summary>
    /// The endpoint ids the importer assigns. Named here because the credential lookup
    /// below has to match them and a typo would be a run that cannot connect.
    /// </summary>
    internal const string SourceEndpointId = "source";

    internal const string DestinationEndpointId = "destination";

    /// <summary>
    /// The job the engine will run, plus everything that could not be carried across.
    /// <para>
    /// <paramref name="sectionJson"/> is the raw JSON of the section and
    /// <paramref name="cfg"/> is the same section already loaded and decrypted. Both are
    /// needed and they are not redundant: the importer reads the JSON and deliberately
    /// strips credentials out of the model, while the run needs the credential back -
    /// see <see cref="Options"/>.
    /// </para>
    /// </summary>
    public static async Task<(SyncJobDefinition Job, IReadOnlyList<string> Losses)> JobAsync(
        string sectionName,
        string sectionJson,
        SyncConfig cfg,
        RunSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(settings);

        var wrapped = $"{{ {System.Text.Json.JsonSerializer.Serialize(sectionName)}: {sectionJson} }}";

        var imported = await new AppSettingsJobImporter(wrapped, $"the section '{sectionName}'")
            .ImportAsync(cancellationToken)
            .ConfigureAwait(false);

        if(imported.Jobs.Count == 0)
        {
            throw new InvalidOperationException(
                $"the section '{sectionName}' does not describe a job the engine can run" +
                (imported.Losses.Count == 0 ? "." : ": " + string.Join(" ", imported.Losses)));
        }

        var job = imported.Jobs[0];
        var losses = new List<string>(imported.Losses);

        ApplyOverrides(job, cfg, settings, losses);

        return (job, losses);
    }

    /// <summary>
    /// How the run behaves, and where its credentials come from.
    /// <para>
    /// <see cref="JobRunOptions.ResolveConnectionString"/> is the whole reason the Core
    /// can refuse to hold a password. The model carries a redacted connection string and
    /// a <see cref="Endpoint.SecretRef"/> naming what completes it; this hands back the
    /// real one, which the surface already has because <c>LoadConfig</c> decrypted the
    /// <c>enc:</c> form through DPAPI on the way in. The engine never reads the secret
    /// store and never logs what comes back.
    /// </para>
    /// </summary>
    public static JobRunOptions Options(
        SyncConfig cfg,
        RunSettings settings,
        string triggeredBy,
        IProgress<RunProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(settings);

        var source = cfg.Source?.ConnectionString ?? string.Empty;
        var destination = cfg.Destination?.ConnectionString ?? string.Empty;

        return new JobRunOptions
        {
            TriggeredBy = triggeredBy,
            DryRun = settings.DryRun,
            Progress = progress,
            ContinueOnError = settings.ContinueOnError,

            // A single-step job on one machine: the lease costs a table and two writes
            // and buys nothing the process itself does not already guarantee. The
            // service, which is the surface where two hosts can genuinely overlap, turns
            // it on.
            UseLease = false,

            ResolveConnectionString = (endpoint, _) => Task.FromResult(
                endpoint.Id == DestinationEndpointId ? destination : source)
        };
    }

    /// <summary>
    /// The command line, applied over what the file said.
    /// <para>
    /// Every flag that used to mean something still means it. The mapping is written out
    /// rather than inferred because a flag that quietly stops working is worse than one
    /// that was removed: the operator keeps typing it and keeps believing it.
    /// </para>
    /// </summary>
    private static void ApplyOverrides(
        SyncJobDefinition job, SyncConfig cfg, RunSettings settings, List<string> losses)
    {
        var step = job.Steps.Count > 0 ? job.Steps[0] : null;
        if(step is null)
            return;

        if(settings.Append)
            step.Publication.Mode = PublicationMode.Append;

        // --min-commit, --skip-commit and --force-commit are the guard, which is what
        // PuedeCommitear was: a floor, and what to do when the load is under it.
        var floor = settings.MinCommit ?? cfg.Options?.MinRowThresholdToCommit ?? 0;
        step.Publication.Guard.MinimumRows = Math.Max(0, floor);

        if(settings.ForceCommit)
            step.Publication.Guard.OnFailure = GuardFailureAction.Force;
        else if(settings.SkipCommit)
            step.Publication.Guard.OnFailure = GuardFailureAction.Skip;

        if(settings.BatchSize is > 0)
            step.BatchSize = settings.BatchSize.Value;
        else if(cfg.Options?.BatchSize is > 0)
            step.BatchSize = cfg.Options.BatchSize;

        if(settings.MaxDop is > 0)
            step.Publication.MaxDegreeOfParallelism = Math.Max(1, settings.MaxDop.Value);
        else if(cfg.Options?.MaxDegreeOfParallelism is > 0)
            step.Publication.MaxDegreeOfParallelism = cfg.Options.MaxDegreeOfParallelism;

        if(cfg.Options is not null)
            step.Publication.KeepIdentity = cfg.Options.KeepIdentity;

        if(cfg.Options?.BulkCopyTimeoutSeconds is > 0)
            step.CommandTimeoutSeconds = cfg.Options.BulkCopyTimeoutSeconds;

        if(settings.FullRefresh && step.Incremental is not null)
            step.Incremental.ForceFullRead = true;

        // --sp and --sp-param replace the source outright, the way ApplyOverrides did:
        // a procedure and a query together is the ambiguity the importer refuses.
        if(!string.IsNullOrWhiteSpace(settings.StoredProcedure))
        {
            step.Source.Sql = null;
            step.Source.StoredProcedure = settings.StoredProcedure;
        }

        if(settings.SpParams is { Length: > 0 })
        {
            step.Source.Parameters.Clear();
            foreach(var (name, value) in Program.ParseNameValuePairs(settings.SpParams))
                step.Source.Parameters[name] = value;
        }

        if(settings.Top is > 0)
            ApplyTop(step, settings.Top.Value, losses);

        // --direct said "load the final table without a stage". The engine always
        // stages, and this is the one flag whose meaning genuinely changed, so it is
        // said out loud rather than silently ignored.
        if(settings.Direct)
        {
            losses.Add(
                "--direct loaded the destination without a staging table. The engine always stages now, and the " +
                "reason is the failure --direct could not avoid: with nothing staged there is nothing for the row " +
                "guard to compare, so a source that comes back empty is discovered after the destination has " +
                "already been emptied. The rows still end up in the same table; only the order changed.");
        }
    }

    /// <summary>
    /// <c>--top</c>, which exists for trying a job out against a real source.
    /// </summary>
    private static void ApplyTop(SyncStep step, int top, List<string> losses)
    {
        if(!string.IsNullOrWhiteSpace(step.Source.StoredProcedure))
        {
            losses.Add(
                $"--top {top} does not apply to a stored procedure: only the procedure can limit what it returns. " +
                "Give it a @Top parameter and pass it with --sp-param.");

            return;
        }

        if(!string.IsNullOrWhiteSpace(step.Source.Sql))
        {
            // The derived table is how the CLI has always done it, and it is safe here
            // because the query is going to the source as written either way. It is not
            // how the watermark is applied - see SourceSql, which refuses to wrap a
            // query it did not build, because over a linked server wrapping moves every
            // row across before filtering any of them.
            step.Source.Sql = $"SELECT TOP {top} * FROM ( {step.Source.Sql} ) AS _src_";
            return;
        }

        if(!string.IsNullOrWhiteSpace(step.Source.Table))
            step.Source.Sql = $"SELECT TOP {top} * FROM {step.Source.Table}";
    }
}
