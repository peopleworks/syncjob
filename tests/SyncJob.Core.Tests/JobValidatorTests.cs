using SyncJob.Core.Model;

namespace SyncJob.Core.Tests;

/// <summary>
/// Each of these is a way the deployed systems fail quietly today. The point of the
/// validator is that the job refuses to start instead.
/// </summary>
public sealed class JobValidatorTests
{
    [Fact]
    public void AWellFormedJob_HasNothingToSay()
    {
        Assert.Empty(JobValidator.Validate(Job()));
    }

    // ------------------------------------------------------------- the watermark

    /// <summary>
    /// The deployed engine joins every sync-key column into one comma-separated string
    /// and then takes MAX() of that string. With one column it works; with two it
    /// returns the largest piece of text, which is not the largest value of anything.
    /// </summary>
    [Fact]
    public void TwoSyncKeys_AreRefused_BecauseAWatermarkCanOnlyFollowOneColumn()
    {
        var job = Job();
        job.Steps[0].FieldMaps.Add(new FieldMap { Source = "ChangedAt", IsSyncKey = true });
        job.Steps[0].FieldMaps.Add(new FieldMap { Source = "Version", IsSyncKey = true });

        var issue = Assert.Single(Errors(job));
        Assert.Contains("2 sync keys", issue.Message, StringComparison.Ordinal);
        Assert.Contains("ChangedAt", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OneSyncKey_IsFine()
    {
        var job = Job();
        job.Steps[0].FieldMaps.Add(new FieldMap { Source = "ChangedAt", IsSyncKey = true });
        job.Steps[0].Incremental = new IncrementalPlan { Watermark = new WatermarkPlan() };

        Assert.Empty(Errors(job));
    }

    [Fact]
    public void AWatermarkWithNoSyncKey_HasNothingToFillIt()
    {
        var job = Job();
        job.Steps[0].Incremental = new IncrementalPlan { Watermark = new WatermarkPlan() };

        Assert.Contains(Errors(job), x => x.Message.Contains("no field map carries the sync key", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------- the keys

    [Fact]
    public void AMergeWithNoUniqueKey_HasNothingToMatchOn()
    {
        var job = Job();
        job.Steps[0].Publication.Mode = PublicationMode.Merge;

        Assert.Contains(Errors(job), x => x.Message.Contains("declares no unique key", StringComparison.Ordinal));
    }

    [Fact]
    public void ATombstoneLedgerWithNoDeleteKey_CannotApplyAnything()
    {
        var job = Job();
        job.Steps[0].Incremental = new IncrementalPlan
        {
            Tombstones = new TombstoneLedger { Table = "dbo.Deleted" }
        };

        Assert.Contains(Errors(job), x => x.Message.Contains("no delete key", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the guard

    /// <summary>
    /// The failure the guard exists for: a source that returns nothing, followed by a
    /// truncate, is a job that empties a production table and reports success. It is
    /// only a warning because a table that is legitimately allowed to be empty exists
    /// too - but nobody gets to configure it by accident.
    /// </summary>
    [Fact]
    public void ReplacingAWholeTableWithNoGuard_IsWorthSayingOutLoud()
    {
        var job = Job();
        job.Steps[0].Publication.Guard = new PublicationGuard();

        Assert.Contains(
            JobValidator.Validate(job),
            x => x.Severity == ValidationSeverity.Warning &&
                 x.Message.Contains("empties the table and reports success", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.5d)]
    [InlineData(1.5d)]
    public void AFractionOutsideItsRange_IsRefused(double fraction)
    {
        var job = Job();
        job.Steps[0].Publication.Guard.MinimumFractionOfDestination = fraction;

        Assert.Contains(Errors(job), x => x.Message.Contains("minimum fraction", StringComparison.Ordinal));
    }

    [Fact]
    public void ForcingPastTheGuard_IsAWarningEveryTimeItRuns()
    {
        var job = Job();
        job.Steps[0].Publication.Guard.OnFailure = GuardFailureAction.Force;

        Assert.Contains(
            JobValidator.Validate(job),
            x => x.Severity == ValidationSeverity.Warning &&
                 x.Message.Contains("one deliberate run", StringComparison.Ordinal));
    }

    // --------------------------------------------------------------- the variables

    /// <summary>
    /// The bug in the deployed loader: substitution is a plain textual replace, so
    /// replacing <c>@Last</c> before <c>@LastSyncValue</c> turns the second into
    /// <c>&lt;value&gt;SyncValue</c> and the query reads from the wrong place.
    /// </summary>
    [Fact]
    public void AVariableNameThatIsAPrefixOfAnother_IsFlaggedUnderTheLegacySyntax()
    {
        var job = Job();
        job.Steps[0].VariableSyntax = VariableSyntax.Legacy;
        job.Steps[0].Variables.Add(new SqlVariable { Name = "@Last", Sql = "SELECT 1" });
        job.Steps[0].Variables.Add(new SqlVariable { Name = "@LastSyncValue", Sql = "SELECT 2" });

        Assert.Contains(
            JobValidator.Validate(job),
            x => x.Message.Contains("is a prefix of", StringComparison.Ordinal) &&
                 x.Message.Contains("'@LastSyncValue'", StringComparison.Ordinal));
    }

    /// <summary>The delimited syntax makes the collision impossible, so it is not raised.</summary>
    [Fact]
    public void TheSameTwoNames_AreFineOnceTheyAreDelimited()
    {
        var job = Job();
        job.Steps[0].VariableSyntax = VariableSyntax.Delimited;
        job.Steps[0].Variables.Add(new SqlVariable { Name = "Last", Sql = "SELECT 1" });
        job.Steps[0].Variables.Add(new SqlVariable { Name = "LastSyncValue", Sql = "SELECT 2" });

        Assert.Empty(JobValidator.Validate(job));
    }

    [Fact]
    public void ADuplicatedVariableName_IsRefused()
    {
        var job = Job();
        job.Steps[0].Variables.Add(new SqlVariable { Name = "Cutoff", Sql = "SELECT 1" });
        job.Steps[0].Variables.Add(new SqlVariable { Name = "cutoff", Sql = "SELECT 2" });

        Assert.Contains(Errors(job), x => x.Message.Contains("declared 2 times", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the source

    [Fact]
    public void AStepWithNoSource_IsRefused()
    {
        var job = Job();
        job.Steps[0].Source = new SourceQuery();

        Assert.Contains(Errors(job), x => x.Message.Contains("has no source", StringComparison.Ordinal));
    }

    [Fact]
    public void AStepWithTwoSources_IsRefusedRatherThanRanked()
    {
        var job = Job();
        job.Steps[0].Source.StoredProcedure = "dbo.GetCustomers";

        Assert.Contains(Errors(job), x => x.Message.Contains("more than one source", StringComparison.Ordinal));
    }

    [Fact]
    public void AStepPointingAtAnEndpointTheJobDoesNotHave_IsRefused()
    {
        var job = Job();
        job.Steps[0].SourceEndpointId = "plant-9";

        Assert.Contains(Errors(job), x => x.Message.Contains("not one of the job's sources", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------- the job

    [Fact]
    public void AJobWithNoDestination_IsRefused()
    {
        var job = Job();
        job.Destination = null;

        Assert.Contains(Errors(job), x => x.Message.Contains("no destination", StringComparison.Ordinal));
    }

    [Fact]
    public void StepsSharingAnOrder_AreFlagged_BecauseTheirSequenceIsWhateverLoadedThem()
    {
        var job = Job();
        job.Steps.Add(Step("second"));
        job.Steps[1].Order = job.Steps[0].Order;

        Assert.Contains(
            JobValidator.Validate(job),
            x => x.Message.Contains("share the order", StringComparison.Ordinal));
    }

    [Fact]
    public void ActiveSteps_AreInOrderAndSkipTheInactiveOnes()
    {
        var job = Job();
        job.Steps.Add(Step("third", order: 3));
        job.Steps.Add(Step("second", order: 2));
        job.Steps.Add(Step("off", order: 0));
        job.Steps[^1].IsActive = false;

        Assert.Equal(["first", "second", "third"], job.ActiveSteps.Select(x => x.Name));
    }

    // ---------------------------------------------------------------- the builders

    private static IEnumerable<ValidationIssue> Errors(SyncJobDefinition job) =>
        JobValidator.Validate(job).Where(x => x.Severity == ValidationSeverity.Error);

    private static SyncJobDefinition Job() => new()
    {
        Id = "customers",
        Name = "Customers",
        Destination = new Endpoint { Id = "central", ConnectionString = "Server=.;Database=Central" },
        Sources = { new Endpoint { Id = "plant-1", ConnectionString = "Server=.;Database=Plant1" } },
        Steps = { Step("first") }
    };

    private static SyncStep Step(string name, double order = 1) => new()
    {
        Id = name,
        Name = name,
        Order = order,
        SourceEndpointId = "plant-1",
        Source = new SourceQuery { Table = "dbo.Customer" },
        DestinationTable = "dbo.Customer",
        Publication = new PublicationPlan
        {
            Mode = PublicationMode.Replace,
            Guard = new PublicationGuard { MinimumRows = 1 }
        }
    };
}
