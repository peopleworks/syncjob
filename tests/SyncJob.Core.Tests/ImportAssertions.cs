using SyncJob.Core.Model;

namespace SyncJob.Core.Tests;

/// <summary>
/// The two things every import has to be able to say for itself: the job it produced runs,
/// and everything it could not carry has a sentence naming it.
/// </summary>
internal static class ImportAssertions
{
    /// <summary>
    /// A warning is allowed - a step that replaces with no guard is worth saying out loud
    /// and is still a job. An error is not: the job would not start.
    /// </summary>
    internal static void RunsCleanly(SyncJobDefinition job) =>
        Assert.DoesNotContain(JobValidator.Validate(job), x => x.Severity == ValidationSeverity.Error);

    internal static void Names(IEnumerable<string> losses, params string[] phrases) =>
        Assert.Contains(losses, loss => phrases.All(x => loss.Contains(x, StringComparison.Ordinal)));
}
