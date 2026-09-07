using SyncJob.Core.Model;
using SyncJob.Core.Publication;

namespace SyncJob.Core.Tests;

/// <summary>
/// The guard's arithmetic, without a server.
/// <para>
/// This is the check that stands between an empty source and a production table. The
/// counting is trivial and the decision is not, so the decision is a function of three
/// numbers and is tested as one - including the reason it gives, which is what an
/// operator reads at three in the morning before deciding whether to force it.
/// </para>
/// </summary>
public sealed class GuardTests
{
    private const long DestinationAbsent = PublicationGuardEvaluator.DestinationAbsent;

    [Theory]
    [InlineData(0, 1000, false)]
    [InlineData(999, 1000, false)]
    [InlineData(1000, 1000, true)]
    [InlineData(1001, 1000, true)]
    public void TheFloorRefusesWhatIsBelowIt(long staged, int floor, bool mayPublish)
    {
        var verdict = PublicationGuardEvaluator.Decide(staged, 5_000, new PublicationGuard { MinimumRows = floor });

        Assert.Equal(mayPublish, verdict.MayPublish);
    }

    /// <summary>
    /// The failure the fraction exists for: a table that grew to four million rows and
    /// stages nine hundred passes any floor anyone set when it had a thousand.
    /// </summary>
    [Fact]
    public void TheFractionCatchesACollapseThatAFloorDoesNot()
    {
        var guard = new PublicationGuard { MinimumRows = 1_000, MinimumFractionOfDestination = 0.5 };

        var verdict = PublicationGuardEvaluator.Decide(900_000, 4_000_000, guard);

        Assert.False(verdict.MayPublish);
        Assert.Contains("fraction", verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(499, false)]
    [InlineData(500, true)]
    [InlineData(4_000, true)]
    public void TheFractionComparesAgainstWhatIsAlreadyThere(long staged, bool mayPublish)
    {
        var guard = new PublicationGuard { MinimumFractionOfDestination = 0.5 };

        var verdict = PublicationGuardEvaluator.Decide(staged, 1_000, guard);

        Assert.Equal(mayPublish, verdict.MayPublish);
    }

    /// <summary>
    /// A count that does not divide evenly rounds the requirement up, so "half" of an
    /// odd number is not quietly satisfied by one row short of it.
    /// </summary>
    [Fact]
    public void TheRequiredFractionIsRoundedUp()
    {
        var guard = new PublicationGuard { MinimumFractionOfDestination = 0.5 };

        Assert.False(PublicationGuardEvaluator.Decide(500, 1_001, guard).MayPublish);
        Assert.True(PublicationGuardEvaluator.Decide(501, 1_001, guard).MayPublish);
    }

    [Fact]
    public void BothRulesHaveToPass()
    {
        var guard = new PublicationGuard { MinimumRows = 1_000, MinimumFractionOfDestination = 0.5 };

        Assert.True(PublicationGuardEvaluator.Decide(1_200, 2_000, guard).MayPublish);
        Assert.False(PublicationGuardEvaluator.Decide(999, 1_000, guard).MayPublish);   // the floor alone
        Assert.False(PublicationGuardEvaluator.Decide(1_200, 4_000, guard).MayPublish); // the fraction alone
    }

    [Fact]
    public void WhenBothRulesRefuse_BothAreSaid()
    {
        var guard = new PublicationGuard { MinimumRows = 1_000, MinimumFractionOfDestination = 0.5 };

        var verdict = PublicationGuardEvaluator.Decide(10, 4_000, guard);

        Assert.False(verdict.MayPublish);
        Assert.Contains("floor", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("fraction", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A destination that is not there yet is a first run, not a refusal: there is
    /// nothing to compare against, and refusing every first run is not a safety feature.
    /// </summary>
    [Fact]
    public void AFirstRunPassesTheFraction()
    {
        var guard = new PublicationGuard { MinimumRows = 1, MinimumFractionOfDestination = 0.9 };

        var verdict = PublicationGuardEvaluator.Decide(3, DestinationAbsent, guard);

        Assert.True(verdict.MayPublish);
        Assert.Equal(DestinationAbsent, verdict.DestinationRows);
        Assert.Contains("first run", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>An empty destination is the same case: half of nothing refuses nothing.</summary>
    [Fact]
    public void AnEmptyDestinationDoesNotTriggerTheFraction()
    {
        var guard = new PublicationGuard { MinimumFractionOfDestination = 0.5 };

        Assert.True(PublicationGuardEvaluator.Decide(0, 0, guard).MayPublish);
    }

    [Fact]
    public void ZeroAndNullSwitchTheRulesOff()
    {
        var verdict = PublicationGuardEvaluator.Decide(0, 4_000_000, new PublicationGuard());

        // Not a bug - it is the Windows service's configuration, and the reason has to
        // say that nothing was checked rather than read as a guard that approved it.
        Assert.True(verdict.MayPublish);
        Assert.Contains("no guard is configured", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReasonNamesBothNumbersWhenTheFloorRefuses()
    {
        var verdict = PublicationGuardEvaluator.Decide(12, 4_000, new PublicationGuard { MinimumRows = 1_000 });

        Assert.Contains("12", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("1,000", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReasonNamesBothNumbersWhenTheFractionRefuses()
    {
        var guard = new PublicationGuard { MinimumFractionOfDestination = 0.5 };

        var verdict = PublicationGuardEvaluator.Decide(900, 4_000_000, guard);

        Assert.Contains("900", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("2,000,000", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("4,000,000", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("50%", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerdictCarriesTheCountsItDecidedOn()
    {
        var verdict = PublicationGuardEvaluator.Decide(1_234, 5_678, new PublicationGuard { MinimumRows = 1 });

        Assert.Equal(1_234, verdict.StagedRows);
        Assert.Equal(5_678, verdict.DestinationRows);
    }

    /// <summary>
    /// A fraction above one reads as "the load must have grown", which is a legitimate
    /// thing to ask of a table that only ever gets bigger.
    /// </summary>
    [Fact]
    public void AFractionAboveOneDemandsGrowth()
    {
        var guard = new PublicationGuard { MinimumFractionOfDestination = 1.1 };

        Assert.False(PublicationGuardEvaluator.Decide(1_000, 1_000, guard).MayPublish);
        Assert.True(PublicationGuardEvaluator.Decide(1_100, 1_000, guard).MayPublish);
    }
}
