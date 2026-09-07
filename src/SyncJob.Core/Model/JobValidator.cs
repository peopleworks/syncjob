namespace SyncJob.Core.Model;

/// <summary>
/// Says what is wrong with a job before anything touches a database.
/// <para>
/// Every rule here exists because the deployed systems fail this way silently: a second
/// sync key that makes the watermark meaningless, a variable name that eats another
/// one's, a merge with no key to merge on. None of them raise an error today; they
/// produce a wrong answer and a green log. Refusing to start is the whole point.
/// </para>
/// </summary>
public static class JobValidator
{
    /// <summary>Every problem found, worst first. Empty means the job may run.</summary>
    public static IReadOnlyList<ValidationIssue> Validate(SyncJobDefinition job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var issues = new List<ValidationIssue>();

        if(string.IsNullOrWhiteSpace(job.Id))
            issues.Add(Error(null, "the job has no id, so its runs and watermarks cannot be correlated"));

        if(job.Destination is null || string.IsNullOrWhiteSpace(job.Destination.ConnectionString))
            issues.Add(Error(null, "the job has no destination"));

        if(job.Steps.Count == 0)
            issues.Add(Warning(null, "the job has no steps, so a run would do nothing and report success"));

        foreach(var duplicate in job.Steps.GroupBy(x => x.Order).Where(x => x.Count() > 1))
        {
            issues.Add(Warning(
                null,
                $"{duplicate.Count()} steps share the order {duplicate.Key}, so their sequence is decided by " +
                "whatever order they were loaded in: " + Names(duplicate)));
        }

        foreach(var step in job.Steps)
            ValidateStep(job, step, issues);

        return issues;
    }

    private static void ValidateStep(SyncJobDefinition job, SyncStep step, List<ValidationIssue> issues)
    {
        var where = step.Name is { Length: > 0 } ? step.Name : step.Id;

        if(string.IsNullOrWhiteSpace(step.DestinationTable))
            issues.Add(Error(where, "the step names no destination table"));

        var sourceKinds = new[]
        {
            !string.IsNullOrWhiteSpace(step.Source.Sql),
            !string.IsNullOrWhiteSpace(step.Source.StoredProcedure),
            !string.IsNullOrWhiteSpace(step.Source.Table)
        }.Count(x => x);

        if(sourceKinds == 0)
            issues.Add(Error(where, "the step has no source: give it a query, a stored procedure or a table"));
        else if(sourceKinds > 1)
            issues.Add(Error(where, "the step has more than one source and there is no rule for which wins"));

        if(step.SourceEndpointId is { Length: > 0 } endpointId &&
           job.Sources.All(x => !string.Equals(x.Id, endpointId, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(Error(where, $"the step reads from '{endpointId}', which is not one of the job's sources"));
        }

        // One sync key, or none. Two is the failure that looks like it works: the
        // deployed engine joins every sync-key column into one string and takes MAX of
        // it, which answers a question nobody asked.
        var syncKeys = step.FieldMaps.Where(x => x.IsSyncKey).ToList();
        if(syncKeys.Count > 1)
        {
            issues.Add(Error(
                where,
                "the step has " + syncKeys.Count + " sync keys and a watermark can only follow one column: " +
                string.Join(", ", syncKeys.Select(x => x.Source ?? x.Target))));
        }

        if(step.Incremental?.Watermark is not null && syncKeys.Count == 0)
            issues.Add(Error(where, "the step keeps a watermark but no field map carries the sync key that fills it"));

        if(step.Publication.Mode == PublicationMode.Merge && !step.FieldMaps.Any(x => x.IsUniqueKey))
            issues.Add(Error(where, "the step merges but declares no unique key, so no row can be matched to another"));

        if(step.Incremental?.Tombstones is not null && !step.FieldMaps.Any(x => x.IsDeleteKey))
            issues.Add(Error(where, "the step reads a tombstone ledger but declares no delete key to apply it by"));

        if(step.Publication.Guard.MinimumFractionOfDestination is { } fraction && fraction is <= 0 or > 1)
            issues.Add(Error(where, $"the guard's minimum fraction is {fraction}; it has to be above 0 and at most 1"));

        if(step.Publication.Guard.OnFailure == GuardFailureAction.Force)
        {
            issues.Add(Warning(
                where,
                "the guard is set to publish even when it refuses, which is a setting for one deliberate run"));
        }

        if(step.Publication.Mode == PublicationMode.Replace &&
           step.Publication.Guard is { MinimumRows: 0, MinimumFractionOfDestination: null })
        {
            issues.Add(Warning(
                where,
                "the step replaces the whole destination with no guard at all, so a source that returns nothing " +
                "empties the table and reports success"));
        }

        if(step.BatchSize < 0)
            issues.Add(Error(where, "the batch size is negative"));

        if(step.Publication.MaxDegreeOfParallelism > 1)
        {
            // Said out loud rather than honoured or ignored. The engine streams a step's
            // rows in one pass - which is what lets it copy a table larger than memory -
            // and there is no second stream for this number to govern. An operator who
            // typed it is entitled to know it does nothing, because the alternative is
            // believing a job is four times faster than it is.
            issues.Add(Warning(
                where,
                $"the step asks for {step.Publication.MaxDegreeOfParallelism} parallel copies and the engine " +
                "copies a step in a single stream, so the setting has no effect. It is kept rather than dropped " +
                "because a job imported from a format that carries it should not lose what it said."));
        }

        ValidateVariables(step, where, issues);
    }

    private static void ValidateVariables(SyncStep step, string where, List<ValidationIssue> issues)
    {
        foreach(var duplicate in step.Variables
                    .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Where(x => x.Count() > 1))
        {
            issues.Add(Error(where, $"the variable '{duplicate.Key}' is declared {duplicate.Count()} times"));
        }

        foreach(var variable in step.Variables.Where(x => string.IsNullOrWhiteSpace(x.Name)))
            issues.Add(Error(where, "a variable has no name"));

        if(step.VariableSyntax != VariableSyntax.Legacy)
            return;

        // Legacy substitution is a textual replace, so a name that is a prefix of
        // another one destroys it: replacing @Last first turns @LastSyncValue into
        // <value>SyncValue. Substitution runs longest-first, which makes the common
        // case right, but a name that is a prefix *and* appears on its own is still
        // ambiguous, and the operator is the only one who can say which was meant.
        var names = step.Variables
            .Select(x => x.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        foreach(var name in names)
        {
            var eaten = names
                .Where(x => x.Length > name.Length && x.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if(eaten.Count > 0)
            {
                issues.Add(Warning(
                    where,
                    $"the variable '{name}' is a prefix of {string.Join(", ", eaten.Select(x => $"'{x}'"))}; " +
                    "with the legacy syntax the longer name is substituted first, but the SQL reads ambiguously - " +
                    "move the step to the delimited syntax"));
            }
        }
    }

    private static string Names(IEnumerable<SyncStep> steps) =>
        string.Join(", ", steps.Select(x => x.Name is { Length: > 0 } ? x.Name : x.Id));

    private static ValidationIssue Error(string? step, string message) =>
        new() { Severity = ValidationSeverity.Error, Step = step, Message = message };

    private static ValidationIssue Warning(string? step, string message) =>
        new() { Severity = ValidationSeverity.Warning, Step = step, Message = message };
}

public sealed class ValidationIssue
{
    public ValidationSeverity Severity { get; init; }

    /// <summary>The step this is about, or null when it is about the job.</summary>
    public string? Step { get; init; }

    public string Message { get; init; } = string.Empty;

    public override string ToString() =>
        Step is null ? $"{Severity}: {Message}" : $"{Severity} [{Step}]: {Message}";
}

public enum ValidationSeverity
{
    /// <summary>Worth saying out loud; the job may still run.</summary>
    Warning = 0,

    /// <summary>The job does not run.</summary>
    Error = 1
}
