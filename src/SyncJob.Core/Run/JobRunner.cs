using System.Globalization;
using SyncJob.Core.Model;

namespace SyncJob.Core.Run;

/// <summary>
/// Everything a run needs that is not in the job definition.
/// </summary>
public sealed class JobRunOptions
{
    /// <summary>What asked for the run: "schedule", "operator", "service", a test's name.</summary>
    public string TriggeredBy { get; init; } = "unspecified";

    /// <summary>Decisions are made and reported; the destination is not written to.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Resolves an endpoint's <see cref="Endpoint.SecretRef"/> into a complete connection
    /// string. The Core never reads a secret store; the surface that owns one supplies
    /// this, and what comes back has a password in it and is never put in a message.
    /// </summary>
    public Func<Endpoint, CancellationToken, Task<string>>? ResolveConnectionString { get; init; }

    /// <summary>Told about every step as it starts and finishes, and about copy progress.</summary>
    public IProgress<RunProgress>? Progress { get; init; }

    /// <summary>
    /// Take the job's lease for the duration of the run. False for a surface that has its
    /// own mutual exclusion, and for a test.
    /// </summary>
    public bool UseLease { get; init; } = true;

    /// <summary>Carry on to the next step when one fails, rather than stopping the run.</summary>
    public bool ContinueOnError { get; init; }
}

/// <summary>Where a run has got to, for a caller that is watching one.</summary>
public sealed record RunProgress(string StepId, string Message, long RowsSoFar);

/// <summary>
/// Runs a job's steps in order, under a lease, and returns one <see cref="JobRun"/> that
/// says what every one of them did.
/// <para>
/// It does not throw for anything that is a normal night. A job whose lease is held by a
/// live run comes back as a run that says so, because the caller is a scheduler and two
/// hosts racing for the same job is the situation the lease exists for rather than an
/// error. A job that does not validate comes back the same way, before a single
/// connection is opened.
/// </para>
/// </summary>
public sealed class JobRunner
{
    /// <summary>
    /// The step id carried by a result that is about the run rather than about a step -
    /// a lease that was held, a job that did not validate.
    /// <para>
    /// <see cref="JobRun"/> has nowhere else to put it: it has no message and no issue
    /// list, and "the run refused to start, and here is why" has to reach a person. A
    /// reserved id in <see cref="JobRun.Steps"/> is the closest the model currently comes,
    /// and it costs nothing - the row counts on it are zero.
    /// </para>
    /// </summary>
    public const string RunLevelStepId = "(run)";

    /// <summary>
    /// The shortest gap between two lease renewals, whatever the lease duration works out
    /// to. A job configured with a lease of nothing would otherwise renew in a tight loop
    /// against the destination.
    /// </summary>
    public static readonly TimeSpan MinimumRenewalInterval = TimeSpan.FromMilliseconds(100);

    private readonly IJobLeaseStore _leases;
    private readonly StepRunner _steps;

    public JobRunner(IJobLeaseStore? leaseStore = null, StepRunner? stepRunner = null)
    {
        _leases = leaseStore ?? new SqlJobLeaseStore();
        _steps = stepRunner ?? new StepRunner();
    }

    /// <summary>
    /// How often a run renews its lease.
    /// <para>
    /// A third of the duration, so two renewals in a row may fail outright before the
    /// claim lapses. Renewing at half would leave one failure between a working run and
    /// another host taking its job; renewing every few seconds would put a write on the
    /// destination all night for a claim that is good for two hours.
    /// </para>
    /// </summary>
    public static TimeSpan RenewalInterval(TimeSpan leaseDuration)
    {
        var interval = leaseDuration / 3;

        return interval < MinimumRenewalInterval ? MinimumRenewalInterval : interval;
    }

    /// <summary>
    /// Validates, takes the lease, runs every active step and gives the lease back.
    /// <para>
    /// A cancelled run returns rather than throwing, for the same reason a step does: the
    /// results of the steps that did run are the most valuable thing the run produced and
    /// an <see cref="OperationCanceledException"/> would throw them away. The run's status
    /// is <see cref="RunStatus.Failed"/> and the last step says it was cancelled.
    /// </para>
    /// </summary>
    public async Task<JobRun> RunAsync(SyncJobDefinition job, JobRunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(options);

        var run = new JobRun
        {
            JobId = job.Id,
            StartedAt = DateTimeOffset.UtcNow,
            TriggeredBy = options.TriggeredBy,
            Host = Environment.MachineName,
            DryRun = options.DryRun
        };

        // Before the lease and before any connection, and all of them at once. Every rule
        // in the validator is a failure the deployed systems have shipped silently - a
        // second sync key that makes the watermark meaningless, a merge with no key to
        // merge on - and an operator fixing them deserves the whole list rather than one
        // per attempt. Taking a lease first would also block the job for the lease
        // duration on a job that could never have run.
        var errors = JobValidator.Validate(job).Where(x => x.Severity == ValidationSeverity.Error).ToList();
        if(errors.Count > 0)
        {
            return Refuse(
                run,
                RunStatus.Failed,
                $"the job does not validate, so nothing ran and nothing was connected to. {errors.Count} error(s): " +
                string.Join("; ", errors.Select(x => x.ToString())));
        }

        if(!job.IsActive)
            return Refuse(run, RunStatus.Skipped, "the job is switched off, so none of its steps ran.");

        // The validator has already refused a job without one.
        var destination = job.Destination!;

        if(!destination.IsActive)
        {
            return Refuse(
                run,
                RunStatus.Skipped,
                $"the job's destination '{Describe(destination)}' is switched off, so none of its steps ran.");
        }

        string destinationConnectionString;
        try
        {
            destinationConnectionString = await ResolveAsync(destination, options, cancellationToken).ConfigureAwait(false);
        }
        catch(Exception e)
        {
            return Refuse(
                run,
                RunStatus.Failed,
                $"the connection string for the destination '{Describe(destination)}' could not be resolved from " +
                $"its secret reference, so nothing ran: {e.Message}");
        }

        var lease = await AcquireAsync(job, run, options, destinationConnectionString, cancellationToken).ConfigureAwait(false);
        if(options.UseLease && !options.DryRun && lease is null)
        {
            return Refuse(
                run,
                RunStatus.Skipped,
                "another run holds this job's lease and it has not lapsed, so this one did not start. That is what " +
                "the lease is for; nothing is wrong and nothing needs clearing - a run that dies leaves a claim " +
                "that expires on its own.");
        }

        run.Lease = lease;

        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewing = lease is null
            ? Task.CompletedTask
            : RenewAsync(destinationConnectionString, job, lease, options, renewal.Token);

        try
        {
            await RunStepsAsync(job, run, options, destinationConnectionString, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await renewal.CancelAsync().ConfigureAwait(false);

            // Joined rather than abandoned: a renewal already in flight when the lease is
            // released would put the claim back seconds after the run gave it up, and the
            // next host would then wait out a whole lease duration for nothing.
            await Task.WhenAll(renewing).ConfigureAwait(false);

            if(lease is not null)
                await ReleaseAsync(destinationConnectionString, job, run, lease, options).ConfigureAwait(false);

            run.FinishedAt = DateTimeOffset.UtcNow;
            run.Status = Summarise(run.Steps);
        }

        return run;
    }

    /// <summary>
    /// The run's status, which is the worst of its steps'.
    /// <para>
    /// A run of two successes and one skip is <see cref="RunStatus.Skipped"/>, and that is
    /// the decision worth defending. A skip means a table someone asked to be loaded was
    /// deliberately not loaded; a run that reports success while that is true tells the
    /// same lie the guard exists to stop, and the truth is one click away in the steps
    /// where nobody clicks. It is still not a phone call - that is the whole reason
    /// <see cref="RunStatus.Skipped"/> is a status of its own and not a kind of failure -
    /// so a night of skips reads as a report and a single failure reads as a failure.
    /// </para>
    /// <para>
    /// A run with no steps succeeded: there was nothing to do, and the validator has
    /// already said out loud that a job with no steps reports success for doing nothing.
    /// </para>
    /// </summary>
    public static RunStatus Summarise(IReadOnlyList<StepResult> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if(steps.Any(x => x.Status == RunStatus.Failed))
            return RunStatus.Failed;

        return steps.Any(x => x.Status == RunStatus.Skipped) ? RunStatus.Skipped : RunStatus.Succeeded;
    }

    private async Task RunStepsAsync(
        SyncJobDefinition job,
        JobRun run,
        JobRunOptions options,
        string destinationConnectionString,
        CancellationToken cancellationToken)
    {
        // By endpoint rather than by its id, because an id is optional and two endpoints
        // may share a blank one. A run of thirty steps against one source asks the
        // surface's secret store once.
        var resolved = new Dictionary<Endpoint, string>();

        foreach(var step in job.ActiveSteps)
        {
            var endpoint = SourceEndpoint(job, step, out var problem);
            if(endpoint is null)
            {
                run.Steps.Add(Refused(run, step, RunStatus.Failed, problem!));
                if(!options.ContinueOnError)
                    return;

                continue;
            }

            // The third of the three levels the model says are honoured: the job, the
            // step, and the endpoint. A step reading from an endpoint someone switched
            // off is skipped and not failed - switching one off is a decision.
            if(!endpoint.IsActive)
            {
                run.Steps.Add(Refused(
                    run,
                    step,
                    RunStatus.Skipped,
                    $"the source endpoint '{Describe(endpoint)}' is switched off, so the step did not run and " +
                    $"{step.DestinationTable} is one load stale."));

                continue;
            }

            if(!resolved.TryGetValue(endpoint, out var sourceConnectionString))
            {
                try
                {
                    sourceConnectionString = await ResolveAsync(endpoint, options, cancellationToken).ConfigureAwait(false);
                    resolved[endpoint] = sourceConnectionString;
                }
                catch(Exception e)
                {
                    run.Steps.Add(Refused(
                        run,
                        step,
                        RunStatus.Failed,
                        $"the connection string for the source '{Describe(endpoint)}' could not be resolved from " +
                        $"its secret reference: {e.Message}"));

                    if(!options.ContinueOnError)
                        return;

                    continue;
                }
            }

            Report(options, step.Id, $"{Name(step)} started");

            var result = await _steps
                .RunAsync(job, step, sourceConnectionString, destinationConnectionString, run.RunId, options, cancellationToken)
                .ConfigureAwait(false);

            run.Steps.Add(result);

            Report(
                options,
                step.Id,
                $"{Name(step)} {result.Status.ToString().ToLowerInvariant()}: {Counts(result)}",
                result.RowsRead);

            // A cancelled run stops whatever ContinueOnError says. The next step would be
            // handed a token that is already cancelled and would fail on its first await,
            // which turns one cancellation into a run full of failures that all read like
            // separate problems.
            if(cancellationToken.IsCancellationRequested)
                return;

            if(result.Status == RunStatus.Failed && !options.ContinueOnError)
                return;
        }
    }

    /// <summary>
    /// The endpoint a step reads from, or null with the reason it could not be decided.
    /// <para>
    /// A step that names none and a job with exactly one source is the ordinary case and
    /// is not ambiguous. A job with several is: picking the first would be a guess, and a
    /// guess here reads the wrong database and publishes what it finds.
    /// </para>
    /// </summary>
    private static Endpoint? SourceEndpoint(SyncJobDefinition job, SyncStep step, out string? problem)
    {
        problem = null;

        if(step.SourceEndpointId is { Length: > 0 } id)
        {
            var named = job.Sources.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if(named is not null)
                return named;

            problem = $"the step reads from '{id}', which is not one of the job's sources, so it did not run.";
            return null;
        }

        if(job.Sources.Count == 1)
            return job.Sources[0];

        problem = job.Sources.Count == 0
            ? "the step names no source endpoint and the job has none, so there is nothing to read from."
            : $"the step names no source endpoint and the job has {job.Sources.Count} of them, so which one it " +
              "reads is a guess: name one on the step.";

        return null;
    }

    private async Task<RunLease?> AcquireAsync(
        SyncJobDefinition job,
        JobRun run,
        JobRunOptions options,
        string destinationConnectionString,
        CancellationToken cancellationToken)
    {
        // A dry run takes no lease, deliberately. It is the one kind of run an operator
        // starts by hand while the schedule is live, and a dry run that holds the job's
        // claim is a dry run that stops tonight's real load from happening. Nothing it
        // does needs mutual exclusion: it writes to a staging table it named itself and
        // drops it again.
        if(!options.UseLease || options.DryRun)
            return null;

        // Through the concrete type, because IJobLeaseStore has no EnsureTableAsync: the
        // table has to exist before the first acquire and only the SQL store has one.
        if(_leases is SqlJobLeaseStore store)
            await store.EnsureTableAsync(destinationConnectionString, cancellationToken).ConfigureAwait(false);

        return await _leases
            .TryAcquireAsync(destinationConnectionString, job.Id, run.RunId, Holder(), job.LeaseDuration, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Says the run is still alive until it is not.
    /// <para>
    /// A background loop rather than a hook on step boundaries, because a job of one step
    /// that takes three hours has no boundaries: it would lose its own lease to the next
    /// host half way through, and then two runs would be writing the same table. What it
    /// costs is one small write on the destination every third of the lease duration for
    /// as long as the run lasts.
    /// </para>
    /// <para>
    /// A renewal that fails is reported and tried again rather than being fatal.
    /// <see cref="IJobLeaseStore.RenewAsync"/> returns nothing, so this cannot tell a
    /// lease another host has taken over from a connection that blipped - and abandoning a
    /// run half way through a publication on the strength of a guess is worse than the
    /// overlap it would be avoiding.
    /// </para>
    /// </summary>
    private async Task RenewAsync(
        string destinationConnectionString,
        SyncJobDefinition job,
        RunLease lease,
        JobRunOptions options,
        CancellationToken cancellationToken)
    {
        var interval = RenewalInterval(job.LeaseDuration);
        using var timer = new PeriodicTimer(interval);

        try
        {
            while(await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await _leases
                        .RenewAsync(destinationConnectionString, job.Id, lease, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch(Exception e)
                {
                    Report(
                        options,
                        RunLevelStepId,
                        $"the lease on '{job.Id}' could not be renewed and will be tried again in {interval}; it " +
                        $"lapses at {lease.ExpiresAt:O} unless one of the attempts gets through: {e.Message}");
                }
            }
        }
        catch(OperationCanceledException)
        {
            // The run finished and the loop is torn down with it.
        }
    }

    /// <summary>
    /// Gives the claim back, and says so in the run when it could not.
    /// <para>
    /// A lease that is not released is not a lost run - everything published is published -
    /// but it does block the next one until it lapses, and that is worth an operator
    /// knowing before they go looking for what is wrong with the schedule.
    /// </para>
    /// </summary>
    private async Task ReleaseAsync(
        string destinationConnectionString,
        SyncJobDefinition job,
        JobRun run,
        RunLease lease,
        JobRunOptions options)
    {
        try
        {
            // CancellationToken.None: a cancelled run must still give the job back, or
            // the cancellation costs the next run its slot as well.
            await _leases.ReleaseAsync(destinationConnectionString, job.Id, lease, CancellationToken.None).ConfigureAwait(false);
        }
        catch(Exception e)
        {
            var message =
                $"the run finished but its lease could not be released, so the job is claimed until it lapses at " +
                $"{lease.ExpiresAt:O}: {e.Message}";

            // Succeeded, not Failed: everything this run published is published, and
            // summarising it as a failure would page someone about a claim that expires
            // on its own.
            run.Steps.Add(new StepResult
            {
                RunId = run.RunId,
                StepId = RunLevelStepId,
                StepName = "the run itself",
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
                Status = RunStatus.Succeeded,
                Message = message
            });

            Report(options, RunLevelStepId, message);
        }
    }

    private static async Task<string> ResolveAsync(
        Endpoint endpoint,
        JobRunOptions options,
        CancellationToken cancellationToken)
    {
        if(options.ResolveConnectionString is null)
            return endpoint.ConnectionString;

        return await options.ResolveConnectionString(endpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A run that never started, with the reason where a person will find it.</summary>
    private static JobRun Refuse(JobRun run, RunStatus status, string message)
    {
        var now = DateTimeOffset.UtcNow;

        run.Steps.Add(new StepResult
        {
            RunId = run.RunId,
            StepId = RunLevelStepId,
            StepName = "the run itself",
            StartedAt = run.StartedAt,
            FinishedAt = now,
            Status = status,
            Message = message
        });

        run.FinishedAt = now;
        run.Status = status;

        return run;
    }

    /// <summary>A step that never reached the step runner, and why.</summary>
    private static StepResult Refused(JobRun run, SyncStep step, RunStatus status, string message)
    {
        var now = DateTimeOffset.UtcNow;

        return new StepResult
        {
            RunId = run.RunId,
            StepId = step.Id,
            StepName = Name(step),
            StartedAt = now,
            FinishedAt = now,
            Status = status,
            Message = message
        };
    }

    /// <summary>
    /// A progress report is a courtesy to the caller and never the reason a run stops, so
    /// a sink that throws is swallowed here rather than unwinding a publication.
    /// </summary>
    private static void Report(JobRunOptions options, string stepId, string message, long rows = 0)
    {
        if(options.Progress is null)
            return;

        try
        {
            options.Progress.Report(new RunProgress(stepId, message, rows));
        }
        catch(Exception)
        {
            // The caller's sink is the caller's problem.
        }
    }

    private static string Counts(StepResult result) =>
        $"{Number(result.RowsRead)} read, {Number(result.RowsInserted)} inserted, " +
        $"{Number(result.RowsUpdated)} updated, {Number(result.RowsDeleted)} deleted";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Name(SyncStep step) =>
        string.IsNullOrWhiteSpace(step.Name) ? step.Id : step.Name;

    /// <summary>
    /// An endpoint by a name a person recognises. Never its connection string: a resolved
    /// one has a password in it, and this text ends up in a run history.
    /// </summary>
    private static string Describe(Endpoint endpoint) =>
        string.IsNullOrWhiteSpace(endpoint.Name) ? endpoint.Id : endpoint.Name;

    /// <summary>Machine and process, so a stuck lease can be traced to something real.</summary>
    private static string Holder() =>
        $"{Environment.MachineName}/{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}";
}
