using SyncJob.Core.Copy;
using SyncJob.Core.Incremental;
using SyncJob.Core.Model;
using SyncJob.Core.Run;

namespace SyncJob.Core.Tests;

/// <summary>
/// The runner's decisions, with every part it assembles replaced by a fake and no server
/// anywhere.
/// <para>
/// This is where most of the value is, because almost everything the runner gets wrong is
/// an ordering or a status rather than a statement: a watermark written before the
/// publication that then failed, a guard's refusal reported as a success, a staging table
/// left behind by a step that threw. None of those need a database to be wrong, and none
/// of them fail a smoke test.
/// </para>
/// </summary>
public sealed class RunnerTests
{
    private const string SourceConnection = "Server=nowhere;Database=source";
    private const string DestinationConnection = "Server=nowhere;Database=destination";

    /// <summary>
    /// The whole pipeline in the order it has to happen in, asserted as a sequence.
    /// <para>
    /// Two of these positions are the ones that matter. The watermark is read out of
    /// staging <b>before</b> the publication, because a replace exchanges the staged
    /// table's storage with the destination's and leaves staging empty; and it is written
    /// <b>after</b> it, because a watermark that moved past a publication which then
    /// failed makes every later run skip the rows that never arrived.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OneStepRunsItsPartsInTheOrderThatMakesTheWatermarkHonest()
    {
        var log = new List<string>();
        var step = Step();
        step.Incremental = new IncrementalPlan { Watermark = new WatermarkPlan(), Tombstones = new TombstoneLedger { Table = "dbo.Ledger" } };
        step.FieldMaps.Add(new FieldMap { Source = "ChangedAt", Target = "ChangedAt", IsSyncKey = true });
        step.FieldMaps.Add(new FieldMap { Source = "Id", Target = "Id", IsDeleteKey = true });

        var result = await Runner(log).RunAsync(Job(step), step, SourceConnection, DestinationConnection, "run", Options(), default);

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(
            ["watermark-read", "staging-create", "copy", "staged-watermark", "guard", "publish", "tombstones", "watermark-write", "staging-drop"],
            log);
    }

    /// <summary>
    /// The counts come from the publisher and from the tombstone applier. The deployed
    /// path sets <c>RowsInserted</c> to the number of rows it read, with a
    /// <c>// TODO: Obtener metrics reales del MERGE</c> beside it, so a merge that changed
    /// nothing reports that it inserted everything.
    /// </summary>
    [Fact]
    public async Task TheRowCountsComeFromThePublisherAndNotFromTheCopy()
    {
        var step = Step(PublicationMode.Merge);
        step.Incremental = new IncrementalPlan { Tombstones = new TombstoneLedger { Table = "dbo.Ledger" } };

        var runner = Runner(
            copy: new CopyResult(1_000, TimeSpan.FromSeconds(1)),
            publish: new PublishResult(7, 11, 0),
            tombstones: new TombstoneResult(3, []));

        var result = await runner.RunAsync(Job(step), step, SourceConnection, DestinationConnection, "run", Options(), default);

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(1_000, result.RowsRead);
        Assert.Equal(7, result.RowsInserted);
        Assert.Equal(11, result.RowsUpdated);

        // The publisher's deletions and the ledger's, added: a merge deletes nothing of
        // its own, and the rows the ledger removed are still rows that left.
        Assert.Equal(3, result.RowsDeleted);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task AGuardThatRefusesWithAbort_FailsTheStepAndPublishesNothing()
    {
        var log = new List<string>();
        var step = Step();
        step.Publication.Guard.OnFailure = GuardFailureAction.Abort;

        var result = await Runner(log, verdict: Refused).RunAsync(
            Job(step), step, SourceConnection, DestinationConnection, "run", Options(), default);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.DoesNotContain("publish", log);
        Assert.Contains("staging-drop", log);

        Assert.NotNull(result.Message);
        Assert.Contains("the guard refused", result.Message, StringComparison.Ordinal);

        // The reason itself, not just the fact: it is what an operator reads before
        // deciding whether to force it.
        Assert.Contains("nothing was staged", result.Message, StringComparison.Ordinal);
        Assert.Contains(step.DestinationTable, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGuardThatRefusesWithSkip_SkipsTheStepAndSaysTheDestinationIsStale()
    {
        var step = Step();
        step.Publication.Guard.OnFailure = GuardFailureAction.Skip;

        var result = await Runner(verdict: Refused).RunAsync(
            Job(step), step, SourceConnection, DestinationConnection, "run", Options(), default);

        // Distinct from Failed on purpose: a night of skips is a report and a night of
        // failures is a phone call.
        Assert.Equal(RunStatus.Skipped, result.Status);
        Assert.Contains("stale", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGuardThatRefusesWithForce_PublishesAndRecordsThatItWasOverridden()
    {
        var log = new List<string>();
        var step = Step();
        step.Publication.Guard.OnFailure = GuardFailureAction.Force;

        var result = await Runner(log, verdict: Refused).RunAsync(
            Job(step), step, SourceConnection, DestinationConnection, "run", Options(), default);

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Contains("publish", log);

        // Recorded, or the one deliberate run that overrode a guard is indistinguishable
        // from every run that did not.
        Assert.Contains("overridden", result.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The deployed system leaves its staging tables behind, and they accumulate until
    /// someone notices the database has three hundred of them.
    /// </summary>
    [Fact]
    public async Task AStepThatThrowsMidCopy_StillDropsItsStagingTable()
    {
        var log = new List<string>();
        var step = Step();

        var runner = Runner(log, copier: new FakeCopier(log, _ => throw new InvalidOperationException("the source view is broken")));

        var result = await runner.RunAsync(Job(step), step, SourceConnection, DestinationConnection, "run", Options(), default);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(["staging-create", "copy", "staging-drop"], log);
        Assert.Contains("the source view is broken", result.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordering the whole incremental story rests on. A watermark advanced past a
    /// publication that then failed makes the next run ask for rows after a point the
    /// destination never reached, so the rows in between are skipped silently and for
    /// ever - and nothing later ever notices, because the run after that reports success.
    /// </summary>
    [Fact]
    public async Task TheWatermarkIsNotWrittenWhenThePublicationFailed()
    {
        var watermarks = new FakeWatermarks();
        var step = Step();
        step.Incremental = new IncrementalPlan { Watermark = new WatermarkPlan() };
        step.FieldMaps.Add(new FieldMap { Source = "ChangedAt", Target = "ChangedAt", IsSyncKey = true });

        var runner = new StepRunner(
            copier: new FakeCopier(null, _ => Task.FromResult(new CopyResult(500, TimeSpan.Zero))),
            stagingFactory: new FakeStaging(null),
            guard: new FakeGuard(null, _ => Allowed),
            watermarks: watermarks,
            tombstones: new FakeTombstones(null, new TombstoneResult(0, [])))
        {
            Publishers = _ => new FakePublisher(null, _ => throw new InvalidOperationException("the switch was refused")),
            StagedWatermarks = new FakeStagedWatermark(null, "2026-09-07T00:00:00.0000000")
        };

        var result = await runner.RunAsync(Job(step), step, SourceConnection, DestinationConnection, "run", Options(), default);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Empty(watermarks.Written);
        Assert.Null(result.WatermarkValue);
    }

    /// <summary>
    /// A ledger row whose key cannot be used is a fact about the source's data rather than
    /// a failure of the run, so the other deletions still apply - but it has to reach a
    /// person, with its ledger id, or the run has arrived at exactly the deployed
    /// behaviour: nothing deleted, nothing said.
    /// </summary>
    [Fact]
    public async Task UnappliedTombstoneRowsReachTheStepResult()
    {
        var step = Step(PublicationMode.Merge);
        step.Incremental = new IncrementalPlan { Tombstones = new TombstoneLedger { Table = "dbo.DeletedRows" } };

        var unapplied = new TombstoneResult(4, [new TombstoneKeyProblem(41, "P1", 1, 3), new TombstoneKeyProblem(97, null, 0, 3)]);

        // A merge deletes nothing of its own, so every deletion counted here came from
        // the ledger.
        var result = await Runner(publish: new PublishResult(2, 1, 0), tombstones: unapplied).RunAsync(
            Job(step), step, SourceConnection, DestinationConnection, "run", Options(), default);

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(4, result.RowsDeleted);

        Assert.NotNull(result.Message);
        Assert.Contains("dbo.DeletedRows", result.Message, StringComparison.Ordinal);
        Assert.Contains("41", result.Message, StringComparison.Ordinal);
        Assert.Contains("97", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cancelled copy is not a copy that did nothing: it runs outside a transaction and
    /// the batches it already sent are committed. They are committed into staging, which
    /// is dropped - so the answer to "what do I do about them" is "nothing", and the step
    /// says so rather than leaving an operator to work it out.
    /// </summary>
    [Fact]
    public async Task ACancelledCopyReportsWhatItWroteAndStillDropsStaging()
    {
        var log = new List<string>();
        var step = Step();

        var runner = Runner(log, copier: new FakeCopier(
            log,
            _ => throw new CopyCanceledException("dbo.Destination_stg_00000000", 12_500, new CancellationToken(true), null)));

        var result = await runner.RunAsync(Job(step), step, SourceConnection, DestinationConnection, "run", Options(), default);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(12_500, result.RowsRead);
        Assert.Contains("staging-drop", log);
        Assert.Contains("12,500", result.Message!, StringComparison.Ordinal);
        Assert.Contains("the watermark did not move", result.Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dry run that reports "OK" is worth nothing. This one reports the number the guard
    /// actually counted, which is only possible because the rows really were staged.
    /// </summary>
    [Fact]
    public async Task ADryRunStagesForRealAndPublishesNothing()
    {
        var log = new List<string>();
        var step = Step();
        step.Publication.StagingTable = "dbo.TheOperatorsOwnStagingTable";

        var staging = new FakeStaging(log);
        var runner = Runner(log, staging: staging, verdict: new GuardVerdict(true, 1_240, 1_200, "1,240 staged against 1,200 already there"));

        var result = await runner.RunAsync(
            Job(step), step, SourceConnection, DestinationConnection, "run", Options(dryRun: true), default);

        Assert.Equal(RunStatus.Succeeded, result.Status);
        Assert.Equal(["staging-create", "copy", "guard", "staging-drop"], log);
        Assert.Contains("1,240", result.Message!, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", result.Message!, StringComparison.Ordinal);

        // And it staged into a table it named itself. An operator inspecting tonight's
        // load while tonight's load is running must not drop the table the real run is
        // filling.
        Assert.Null(staging.RequestedName);
    }

    [Fact]
    public async Task ADryRunWhoseGuardWouldRefuseSaysSoRatherThanReportingSuccess()
    {
        var step = Step();
        step.Publication.Guard.OnFailure = GuardFailureAction.Abort;

        var result = await Runner(verdict: Refused).RunAsync(
            Job(step), step, SourceConnection, DestinationConnection, "run", Options(dryRun: true), default);

        Assert.Equal(RunStatus.Failed, result.Status);
    }

    [Fact]
    public async Task TheStepSaysWhereItResumedFromSoARepairDoesNotNeedGuesswork()
    {
        var step = Step();
        step.Incremental = new IncrementalPlan { Watermark = new WatermarkPlan() };
        step.FieldMaps.Add(new FieldMap { Source = "ChangedAt", Target = "ChangedAt", IsSyncKey = true });

        var watermarks = new FakeWatermarks { Current = new Watermark("2026-09-01", "2026-08-31", DateTimeOffset.UtcNow) };

        var result = await Runner(watermarks: watermarks, staged: "2026-09-07").RunAsync(
            Job(step), step, SourceConnection, DestinationConnection, "run", Options(), default);

        Assert.Equal("2026-09-01", result.PreviousWatermarkValue);
        Assert.Equal("2026-09-07", result.WatermarkValue);
        Assert.Equal([("job", "s1", "2026-09-07")], watermarks.Written);
    }

    /// <summary>
    /// The flag is a repair an operator switched on for one run, and a repair run that
    /// failed has not had its one run yet - so it is cleared on success and left alone
    /// otherwise.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ForceFullReadIsClearedOnlyByARunThatWorked(bool succeeds, bool stillSet)
    {
        var step = Step();
        step.Incremental = new IncrementalPlan { Watermark = new WatermarkPlan(), ForceFullRead = true };

        var runner = succeeds
            ? Runner()
            : Runner(copier: new FakeCopier(null, _ => throw new InvalidOperationException("no")));

        await runner.RunAsync(Job(step), step, SourceConnection, DestinationConnection, "run", Options(), default);

        Assert.Equal(stillSet, step.Incremental.ForceFullRead);
    }

    // ---------------------------------------------------------------- the run

    /// <summary>
    /// The validator's rules exist because the deployed systems fail all of them silently.
    /// Refusing to start is the point, and refusing before anything is connected to is
    /// what makes it safe to call the runner with a job that came out of an importer.
    /// </summary>
    [Fact]
    public async Task AJobThatFailsValidationNeverOpensAConnection()
    {
        var job = Job(Step());
        job.Steps[0].DestinationTable = string.Empty;
        job.Steps[0].Source = new SourceQuery();

        var leases = new FakeLeases();
        var runner = new JobRunner(leases, ThrowingStepRunner());

        var run = await runner.RunAsync(job, new JobRunOptions
        {
            ResolveConnectionString = (_, _) => throw new InvalidOperationException("the secret store was asked")
        }, default);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(0, leases.Acquires);

        var refusal = Assert.Single(run.Steps);
        Assert.Equal(JobRunner.RunLevelStepId, refusal.StepId);
        Assert.Contains("does not validate", refusal.Message!, StringComparison.Ordinal);

        // Every one of them at once: an operator fixing a job at three in the morning
        // should learn all of it in one run rather than one problem per attempt.
        Assert.Contains("no destination table", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("no source", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The caller is a scheduler and two hosts reaching for the same job is the situation
    /// the lease exists for, not an error. Throwing would make a normal Tuesday look like
    /// a fault in every monitor that watches this.
    /// </summary>
    [Fact]
    public async Task AJobWhoseLeaseIsHeldComesBackAsARunThatSaysSoRatherThanThrowing()
    {
        var leases = new FakeLeases { Grants = false };
        var run = await new JobRunner(leases, ThrowingStepRunner()).RunAsync(
            Job(Step()), new JobRunOptions { UseLease = true }, default);

        Assert.Equal(RunStatus.Skipped, run.Status);
        Assert.Null(run.Lease);
        Assert.Equal(0, leases.Releases);

        var refusal = Assert.Single(run.Steps);
        Assert.Contains("another run holds this job's lease", refusal.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunTakesItsLeaseAndGivesItBack()
    {
        var leases = new FakeLeases();

        var run = await new JobRunner(leases, Runner()).RunAsync(
            Job(Step()), new JobRunOptions { UseLease = true }, default);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.NotNull(run.Lease);
        Assert.Equal(1, leases.Releases);
        Assert.Equal(run.RunId, run.Lease.RunId);
    }

    /// <summary>
    /// A run longer than its lease that renews only at step boundaries loses its claim
    /// during a single long step, and then two hosts write the same table. So renewal is a
    /// background loop, and this proves the loop actually ticks during one step.
    /// </summary>
    [Fact]
    public async Task ALongStepRenewsTheLeaseWhileItIsStillRunning()
    {
        var leases = new FakeLeases();
        var job = Job(Step());
        job.LeaseDuration = TimeSpan.FromMilliseconds(400);

        var slow = Runner(copier: new FakeCopier(null, async _ =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600)).ConfigureAwait(false);
            return new CopyResult(10, TimeSpan.Zero);
        }));

        var run = await new JobRunner(leases, slow).RunAsync(job, new JobRunOptions { UseLease = true }, default);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.True(leases.Renewals >= 2, $"the lease was renewed {leases.Renewals} time(s) during a step that outlived it");

        // And the loop stops with the run: a renewal in flight after the release would
        // put the claim back seconds after the run gave it up.
        var settled = leases.Renewals;
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        Assert.Equal(settled, leases.Renewals);
    }

    /// <summary>
    /// A run whose lease is taken over by another host stops where it is.
    /// <para>
    /// This could not be expressed until <c>RenewAsync</c> returned something. While it
    /// returned <c>Task</c>, a renewal that found the claim gone looked exactly like a
    /// renewal that hit a bad connection, and the runner - correctly, on that information -
    /// carried on. Which meant the one case the lease exists to prevent was the one case
    /// it could not react to: two runs writing the same tables, one of them convinced it
    /// still held the job.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ARunWhoseLeaseIsTakenOverStopsWhereItIs()
    {
        var leases = new FakeLeases { RenewalsBeforeLosingTheLease = 1 };
        var job = Job(Step());
        job.LeaseDuration = TimeSpan.FromMilliseconds(300);

        var slow = Runner(copier: new FakeCopier(null, async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return new CopyResult(10, TimeSpan.Zero);
        }));

        var run = await new JobRunner(leases, slow).RunAsync(job, new JobRunOptions { UseLease = true }, default);

        Assert.Equal(RunStatus.Failed, run.Status);

        var runLevel = Assert.Single(run.Steps, x => x.StepId == JobRunner.RunLevelStepId);
        Assert.Contains("took over this job's lease", runLevel.Message!, StringComparison.Ordinal);
        Assert.Contains("two runs writing the same tables", runLevel.Message, StringComparison.Ordinal);

        // It stopped rather than running the five seconds out.
        Assert.True(
            run.Steps.Any(x => x.StepId != JobRunner.RunLevelStepId && x.Status == RunStatus.Failed),
            "the step should have been stopped, not left to finish under a lease this run no longer held");
    }

    [Theory]
    [InlineData(180, 60)]
    [InlineData(3, 1)]
    [InlineData(0, 0.1)]
    public void TheRenewalIntervalIsAThirdOfTheLeaseWithAFloorUnderIt(double leaseSeconds, double intervalSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(intervalSeconds),
            JobRunner.RenewalInterval(TimeSpan.FromSeconds(leaseSeconds)));
    }

    /// <summary>
    /// A run of two successes and one skip is Skipped, and that is the decision worth
    /// defending: a table someone asked to be loaded was deliberately not loaded, and a
    /// run that reports success while that is true tells the same lie the guard exists to
    /// stop. It is still not a failure, which is the whole reason the two are different.
    /// </summary>
    [Theory]
    [InlineData(RunStatus.Succeeded, RunStatus.Succeeded, RunStatus.Succeeded)]
    [InlineData(RunStatus.Succeeded, RunStatus.Skipped, RunStatus.Skipped)]
    [InlineData(RunStatus.Skipped, RunStatus.Failed, RunStatus.Failed)]
    [InlineData(RunStatus.Failed, RunStatus.Succeeded, RunStatus.Failed)]
    public void TheRunsStatusIsTheWorstOfItsSteps(RunStatus first, RunStatus second, RunStatus expected)
    {
        var steps = new List<StepResult> { new() { Status = first }, new() { Status = second } };

        Assert.Equal(expected, JobRunner.Summarise(steps));
    }

    [Fact]
    public void ARunWithNoStepsSucceeded()
    {
        Assert.Equal(RunStatus.Succeeded, JobRunner.Summarise([]));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task ContinueOnErrorDecidesWhetherAFailedStepStopsTheRun(bool carryOn, int expectedSteps)
    {
        var job = Job(Step(id: "first"), Step(id: "second"));
        job.Steps[1].Order = 2;
        job.Steps[1].Source = new SourceQuery { Table = "dbo.SecondSource" };

        var runner = Runner(copier: new FakeCopier(null, request =>
            request.Sql.Contains("dbo.Source", StringComparison.Ordinal)
                ? throw new InvalidOperationException("the first step's source is broken")
                : Task.FromResult(new CopyResult(5, TimeSpan.Zero))));

        var run = await new JobRunner(new FakeLeases(), runner).RunAsync(
            job, new JobRunOptions { UseLease = false, ContinueOnError = carryOn }, default);

        Assert.Equal(expectedSteps, run.Steps.Count);
        Assert.Equal(RunStatus.Failed, run.Status);
    }

    /// <summary>
    /// A guard that said skip must never stop a run: that is the difference between one
    /// stale table and a stopped night, and it is the reason the action exists.
    /// </summary>
    [Fact]
    public async Task AStepTheGuardSkippedNeverStopsTheRun()
    {
        var job = Job(Step(id: "first"), Step(id: "second"));
        job.Steps[0].Publication.Guard.OnFailure = GuardFailureAction.Skip;
        job.Steps[0].DestinationTable = "dbo.First";
        job.Steps[1].Order = 2;
        job.Steps[1].DestinationTable = "dbo.Second";

        var runner = Runner(guard: new FakeGuard(null, destination =>
            destination == "dbo.First" ? Refused : Allowed));

        var run = await new JobRunner(new FakeLeases(), runner).RunAsync(
            job, new JobRunOptions { UseLease = false, ContinueOnError = false }, default);

        Assert.Equal([RunStatus.Skipped, RunStatus.Succeeded], run.Steps.Select(x => x.Status));
        Assert.Equal(RunStatus.Skipped, run.Status);
    }

    /// <summary>
    /// A job that is switched off is skipped rather than removed, at three levels: the
    /// job, the step and the endpoint. All three are honoured, and the endpoint is the one
    /// nothing else in the engine checks.
    /// </summary>
    [Fact]
    public async Task AStepReadingFromASwitchedOffEndpointIsSkippedAndTheRunCarriesOn()
    {
        var job = Job(Step(id: "first"), Step(id: "second"));
        job.Sources.Add(new Endpoint { Id = "cold", Name = "the retired plant", IsActive = false, ConnectionString = "Server=nowhere" });
        job.Steps[0].SourceEndpointId = "cold";
        job.Steps[1].Order = 2;

        var run = await new JobRunner(new FakeLeases(), Runner()).RunAsync(
            job, new JobRunOptions { UseLease = false }, default);

        Assert.Equal(RunStatus.Skipped, run.Steps[0].Status);
        Assert.Contains("the retired plant", run.Steps[0].Message!, StringComparison.Ordinal);
        Assert.Equal(RunStatus.Succeeded, run.Steps[1].Status);
    }

    [Fact]
    public async Task ASwitchedOffJobRunsNothingAndSaysWhy()
    {
        var job = Job(Step());
        job.IsActive = false;

        var run = await new JobRunner(new FakeLeases(), ThrowingStepRunner()).RunAsync(
            job, new JobRunOptions { UseLease = false }, default);

        Assert.Equal(RunStatus.Skipped, run.Status);
        Assert.Contains("switched off", Assert.Single(run.Steps).Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Picking the first of several sources would be a guess, and a guess here reads the
    /// wrong database and publishes what it finds into a real table.
    /// </summary>
    [Fact]
    public async Task AStepThatNamesNoSourceWhenTheJobHasSeveralIsRefusedRatherThanGuessed()
    {
        var job = Job(Step());
        job.Sources.Add(new Endpoint { Id = "other", Name = "the other one", ConnectionString = "Server=nowhere" });
        job.Steps[0].SourceEndpointId = null;

        var run = await new JobRunner(new FakeLeases(), ThrowingStepRunner()).RunAsync(
            job, new JobRunOptions { UseLease = false }, default);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("is a guess", Assert.Single(run.Steps).Message!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The secret store is asked once per endpoint and not once per step: a run of thirty
    /// steps against one source is thirty DPAPI or vault round trips otherwise.
    /// </summary>
    [Fact]
    public async Task EachEndpointsSecretIsResolvedOncePerRun()
    {
        var job = Job(Step(id: "first"), Step(id: "second"), Step(id: "third"));
        job.Steps[1].Order = 2;
        job.Steps[2].Order = 3;

        var asked = new List<string>();

        var run = await new JobRunner(new FakeLeases(), Runner()).RunAsync(job, new JobRunOptions
        {
            UseLease = false,
            ResolveConnectionString = (endpoint, _) =>
            {
                asked.Add(endpoint.Id);
                return Task.FromResult(endpoint.ConnectionString + ";Password=secret");
            }
        }, default);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(["dst", "src"], asked);
    }

    /// <summary>
    /// A dry run is the one kind of run an operator starts by hand while the schedule is
    /// live. Holding the job's claim would make it stop tonight's real load.
    /// </summary>
    [Fact]
    public async Task ADryRunTakesNoLeaseSoItCannotStopTheRealRun()
    {
        var leases = new FakeLeases();

        var run = await new JobRunner(leases, Runner()).RunAsync(
            Job(Step()), new JobRunOptions { UseLease = true, DryRun = true }, default);

        Assert.True(run.DryRun);
        Assert.Equal(0, leases.Acquires);
        Assert.Null(run.Lease);
    }

    [Fact]
    public async Task TheRunCarriesWhoAskedForItAndWhatRanIt()
    {
        var run = await new JobRunner(new FakeLeases(), Runner()).RunAsync(
            Job(Step()), new JobRunOptions { UseLease = false, TriggeredBy = "schedule" }, default);

        Assert.Equal("schedule", run.TriggeredBy);
        Assert.Equal(Environment.MachineName, run.Host);
        Assert.NotNull(run.FinishedAt);
        Assert.All(run.Steps, x => Assert.Equal(run.RunId, x.RunId));
    }

    /// <summary>
    /// A progress sink is a courtesy to the caller. One that throws must not unwind a
    /// publication that worked.
    /// </summary>
    [Fact]
    public async Task AProgressSinkThatThrowsDoesNotFailTheRun()
    {
        var run = await new JobRunner(new FakeLeases(), Runner()).RunAsync(Job(Step()), new JobRunOptions
        {
            UseLease = false,
            Progress = new ThrowingProgress()
        }, default);

        Assert.Equal(RunStatus.Succeeded, run.Status);
    }

    // ------------------------------------------------------- watermark format

    /// <summary>
    /// A watermark is kept as text. A date rendered by a Spanish server's default format
    /// and read back by an English one is a watermark that moves by ten months, which is a
    /// failure that only ever happens in production.
    /// </summary>
    [Fact]
    public void AWatermarkIsFormattedSoItSurvivesTheRoundTrip()
    {
        Assert.Equal("2026-09-07T13:45:30.1234567", SqlStagedWatermarkReader.Format(new DateTime(2026, 9, 7, 13, 45, 30, 123).AddTicks(4567)));
        Assert.Equal("0x0000000000BEEF01", SqlStagedWatermarkReader.Format(new byte[] { 0, 0, 0, 0, 0, 0xBE, 0xEF, 0x01 }));
        Assert.Equal("1234567890123", SqlStagedWatermarkReader.Format(1234567890123L));
        Assert.Equal("1.5", SqlStagedWatermarkReader.Format(1.5m));
        Assert.Null(SqlStagedWatermarkReader.Format(DBNull.Value));
        Assert.Null(SqlStagedWatermarkReader.Format(null));
    }

    // ------------------------------------------------------------------ setup

    private static readonly GuardVerdict Allowed = new(true, 500, 400, "500 staged against 400 already there");

    private static readonly GuardVerdict Refused =
        new(false, 0, 400, "the row floor refused it: nothing was staged, 1 required");

    private static SyncJobDefinition Job(params SyncStep[] steps) => new()
    {
        Id = "job",
        Name = "a job",
        Destination = new Endpoint { Id = "dst", Name = "the warehouse", ConnectionString = DestinationConnection },
        Sources = { new Endpoint { Id = "src", Name = "the plant", ConnectionString = SourceConnection } },
        Steps = [.. steps]
    };

    private static SyncStep Step(PublicationMode mode = PublicationMode.Replace, string id = "s1") => new()
    {
        Id = id,
        Name = id,
        Order = 1,
        SourceEndpointId = "src",
        Source = new SourceQuery { Table = "dbo.Source" },
        DestinationTable = "dbo.Destination",
        FieldMaps = mode == PublicationMode.Merge ? [new FieldMap { Source = "Id", Target = "Id", IsUniqueKey = true }] : [],
        Publication = new PublicationPlan { Mode = mode, Guard = new PublicationGuard { MinimumRows = 1 } }
    };

    private static JobRunOptions Options(bool dryRun = false) =>
        new() { UseLease = false, DryRun = dryRun, TriggeredBy = nameof(RunnerTests) };

    private static StepRunner Runner(
        List<string>? log = null,
        ITableCopier? copier = null,
        FakeStaging? staging = null,
        IPublicationGuard? guard = null,
        IWatermarkStore? watermarks = null,
        CopyResult? copy = null,
        PublishResult? publish = null,
        TombstoneResult? tombstones = null,
        GuardVerdict? verdict = null,
        string? staged = null) =>
        new(
            copier ?? new FakeCopier(log, _ => Task.FromResult(copy ?? new CopyResult(500, TimeSpan.Zero))),
            staging ?? new FakeStaging(log),
            guard ?? new FakeGuard(log, _ => verdict ?? Allowed),
            watermarks ?? new FakeWatermarks(log),
            new FakeTombstones(log, tombstones ?? new TombstoneResult(0, [])))
        {
            Publishers = _ => new FakePublisher(log, _ => publish ?? new PublishResult(500, 0, 400)),
            StagedWatermarks = new FakeStagedWatermark(log, staged ?? "2026-09-07")
        };

    /// <summary>A step runner whose every part fails the test if the run reaches it.</summary>
    private static StepRunner ThrowingStepRunner() =>
        new(new FakeCopier(null, _ => throw new InvalidOperationException("the run reached the source")))
        {
            Publishers = _ => throw new InvalidOperationException("the run reached the destination")
        };

    private sealed class FakeCopier(List<string>? log, Func<CopyRequest, Task<CopyResult>> copy) : ITableCopier
    {
        public Task<CopyResult> CopyAsync(CopyRequest request, string targetTable, CancellationToken cancellationToken)
        {
            log?.Add("copy");

            // WaitAsync so the fake honours the token the way SqlTableCopier does. Without
            // it a copy is uninterruptible here and nothing that cancels a run mid-copy -
            // a lost lease, the caller's own token - can be tested at all.
            return copy(request).WaitAsync(cancellationToken);
        }
    }

    private sealed class FakeStaging(List<string>? log) : IStagingTableFactory
    {
        public string? RequestedName { get; private set; }

        public Task<string> CreateAsync(string connectionString, string destinationTable, string? requestedName, CancellationToken cancellationToken)
        {
            log?.Add("staging-create");
            RequestedName = requestedName;

            return Task.FromResult(requestedName ?? destinationTable + "_stg_00000000");
        }

        public Task DropAsync(string connectionString, string stagingTable, CancellationToken cancellationToken)
        {
            log?.Add("staging-drop");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGuard(List<string>? log, Func<string, GuardVerdict> decide) : IPublicationGuard
    {
        public Task<GuardVerdict> EvaluateAsync(
            string connectionString,
            string stagingTable,
            string destinationTable,
            PublicationGuard guard,
            CancellationToken cancellationToken)
        {
            log?.Add("guard");
            return Task.FromResult(decide(destinationTable));
        }
    }

    private sealed class FakePublisher(List<string>? log, Func<SyncStep, PublishResult> publish) : IPublisher
    {
        public Task<PublishResult> PublishAsync(string connectionString, string stagingTable, SyncStep step, CancellationToken cancellationToken)
        {
            log?.Add("publish");
            return Task.FromResult(publish(step));
        }
    }

    private sealed class FakeWatermarks(List<string>? log = null) : IWatermarkStore
    {
        public Watermark? Current { get; init; }

        public List<(string Job, string Step, string Value)> Written { get; } = [];

        public Task<Watermark?> ReadAsync(string connectionString, string jobId, string stepId, CancellationToken cancellationToken)
        {
            log?.Add("watermark-read");
            return Task.FromResult(Current);
        }

        public Task WriteAsync(string connectionString, string jobId, string stepId, string value, CancellationToken cancellationToken)
        {
            log?.Add("watermark-write");
            Written.Add((jobId, stepId, value));

            return Task.CompletedTask;
        }
    }

    private sealed class FakeStagedWatermark(List<string>? log, string? value) : IStagedWatermarkReader
    {
        public Task<string?> ReadAsync(string connectionString, string stagingTable, string expression, CancellationToken cancellationToken)
        {
            log?.Add("staged-watermark");
            return Task.FromResult(value);
        }
    }

    private sealed class FakeTombstones(List<string>? log, TombstoneResult result) : ITombstoneApplier
    {
        public Task<TombstoneResult> ApplyAsync(
            string sourceConnectionString,
            string destinationConnectionString,
            SyncStep step,
            TombstoneLedger ledger,
            CancellationToken cancellationToken)
        {
            log?.Add("tombstones");
            return Task.FromResult(result);
        }
    }

    private sealed class FakeLeases : IJobLeaseStore
    {
        public bool Grants { get; init; } = true;

        public int Acquires { get; private set; }

        public int Renewals => _renewals;

        public int Releases { get; private set; }

        private int _renewals;

        public Task<RunLease?> TryAcquireAsync(
            string connectionString,
            string jobId,
            string runId,
            string holder,
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            Acquires++;

            var lease = Grants
                ? new RunLease { RunId = runId, Holder = holder, AcquiredAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow + duration }
                : null;

            return Task.FromResult(lease);
        }

        /// <summary>How many renewals happen before the lease is reported gone. Never, by default.</summary>
        public int RenewalsBeforeLosingTheLease { get; init; } = int.MaxValue;

        public Task<bool> RenewAsync(string connectionString, string jobId, RunLease lease, CancellationToken cancellationToken)
        {
            var renewals = Interlocked.Increment(ref _renewals);
            return Task.FromResult(renewals <= RenewalsBeforeLosingTheLease);
        }

        public Task EnsureReadyAsync(string connectionString, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReleaseAsync(string connectionString, string jobId, RunLease lease, CancellationToken cancellationToken)
        {
            Releases++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingProgress : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => throw new InvalidOperationException("the console went away");
    }
}
