namespace SyncJob.Core.Model;

/// <summary>
/// One execution of one job, with an identity of its own.
/// <para>
/// DataSync has no such thing: it writes a row per run into one table and rows per step
/// into another, with nothing linking them, so reconstructing what happened means
/// joining on overlapping time ranges and hoping two runs did not overlap. It also
/// counts the rows it moved and then only prints them. Both are fixed here by giving a
/// run an id that every step result carries.
/// </para>
/// </summary>
public sealed class JobRun
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");

    public string JobId { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? FinishedAt { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Running;

    /// <summary>What asked for this run: a schedule, an operator, a service, a test.</summary>
    public string? TriggeredBy { get; set; }

    /// <summary>The machine that is running it, which is half of the lease's answer to "who".</summary>
    public string? Host { get; set; }

    /// <summary>
    /// True when nothing was written: every decision is made and reported, the
    /// destination is not touched.
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>The claim this run holds on the job while it runs.</summary>
    public RunLease? Lease { get; set; }

    public List<StepResult> Steps { get; set; } = new();

    /// <summary>Rows read across every step.</summary>
    public long RowsRead => Steps.Sum(x => x.RowsRead);

    public long RowsInserted => Steps.Sum(x => x.RowsInserted);

    public long RowsUpdated => Steps.Sum(x => x.RowsUpdated);

    public long RowsDeleted => Steps.Sum(x => x.RowsDeleted);
}

/// <summary>
/// A claim on a job, held for a bounded time by a named holder.
/// <para>
/// It replaces the deployed guard, which is a status column set to "in progress". That
/// has no owner, no expiry and no way back: a run killed mid-flight leaves the flag set
/// and every later run refuses - and, because the check is an <c>AND</c> of two
/// conditions, a job whose flag is stuck while no step is flagged starts anyway. A
/// lease says who holds it and until when, so a dead holder's claim lapses on its own
/// and a live one can say it is still there.
/// </para>
/// </summary>
public sealed class RunLease
{
    /// <summary>The run that holds the claim.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Machine and process, so a stuck lease can be traced to something real.</summary>
    public string Holder { get; set; } = string.Empty;

    public DateTimeOffset AcquiredAt { get; set; }

    /// <summary>
    /// When the claim lapses unless renewed. A long run renews it as it goes, so the
    /// duration bounds how long a crash blocks the job, not how long a job may take.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;
}

/// <summary>What one step did, and what it cost.</summary>
public sealed class StepResult
{
    /// <summary>The run this belongs to. The link DataSync's model does not have.</summary>
    public string RunId { get; set; } = string.Empty;

    public string StepId { get; set; } = string.Empty;

    public string StepName { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Running;

    public long RowsRead { get; set; }

    public long RowsInserted { get; set; }

    public long RowsUpdated { get; set; }

    public long RowsDeleted { get; set; }

    /// <summary>The watermark this step left behind, when it moved one.</summary>
    public string? WatermarkValue { get; set; }

    /// <summary>The watermark it started from, so a replay does not need guesswork.</summary>
    public string? PreviousWatermarkValue { get; set; }

    /// <summary>
    /// Why the destination was not written, when it was not: a guard that refused, a
    /// source that failed, a step that was skipped. Null when the step published.
    /// </summary>
    public string? Message { get; set; }

    public TimeSpan? Duration => FinishedAt - StartedAt;
}

public enum RunStatus
{
    NotStarted = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,

    /// <summary>
    /// The step decided not to write and said why - a guard that refused with
    /// <see cref="GuardFailureAction.Skip"/>, or a source with nothing new. Distinct
    /// from failure on purpose: a night of skips is a report, a night of failures is a
    /// phone call.
    /// </summary>
    Skipped = 4
}
