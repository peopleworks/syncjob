using SyncJob.Core.Model;
using SyncJob.Core.Run;

namespace SyncJob.Core.Tests;

/// <summary>
/// The little of the lease that can be judged without a server: the name of the table it
/// writes to, and the arguments it refuses before it opens a connection.
/// <para>
/// Everything else about a lease is a question about what two sessions do to one row, and
/// lives in <c>LeaseLiveTests</c>. What is here is the part a caller hits first - and a
/// store that only discovers a bad duration after a round trip is a store that fails in the
/// dark.
/// </para>
/// </summary>
public sealed class LeaseTests
{
    /// <summary>Valid in shape, and pointed at nothing: nothing here should get as far as connecting.</summary>
    private const string Nowhere = "Server=(local);Database=nowhere;Integrated Security=true";

    [Fact]
    public void TheDefaultTableIsInTheDboSchemaAndQuotedOnce()
    {
        Assert.Equal("dbo.SyncJobLease", SqlJobLeaseStore.DefaultTable);
        Assert.Equal("[dbo].[SyncJobLease]", new SqlJobLeaseStore().Table);
        Assert.Equal("[dbo].[SyncJobLease]", new SqlJobLeaseStore("   ").Table);
    }

    [Theory]
    [InlineData("JobLocks", "[dbo].[JobLocks]")]
    [InlineData("ops.JobLocks", "[ops].[JobLocks]")]
    [InlineData("[ops].[Job Locks]", "[ops].[Job Locks]")]
    public void ATableNamedByTheCallerIsFilledInAndQuotedTheSameWayEverythingElseIs(string given, string expected) =>
        Assert.Equal(expected, new SqlJobLeaseStore(given).Table);

    /// <summary>
    /// A three-part name would put the lease in a different database from the data it
    /// describes, which is the whole reason it lives in the destination.
    /// </summary>
    [Fact]
    public void AThreePartTableNameIsRefused() =>
        Assert.Throws<ArgumentException>(() => new SqlJobLeaseStore("other.dbo.JobLocks"));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ALeaseThatLastsNoTimeIsRefusedBeforeAnythingIsWritten(int seconds)
    {
        var store = new SqlJobLeaseStore();

        var thrown = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.TryAcquireAsync(
            Nowhere, "nightly", "run-1", "host-a", TimeSpan.FromSeconds(seconds), CancellationToken.None));

        Assert.Equal("duration", thrown.ParamName);
    }

    /// <summary>
    /// The expiry is computed by <c>DATEADD</c>, which counts in <c>int</c>, so a duration
    /// is bounded by what an <c>int</c> of milliseconds holds. Refusing it here is the
    /// difference between an argument exception and an arithmetic overflow inside the
    /// statement that was supposed to be taking the lease.
    /// </summary>
    [Fact]
    public async Task ALeaseLongerThanDateaddCanExpressIsRefused()
    {
        var store = new SqlJobLeaseStore();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.TryAcquireAsync(
            Nowhere, "nightly", "run-1", "host-a", TimeSpan.FromDays(30), CancellationToken.None));
    }

    [Theory]
    [InlineData("", "nightly", "run-1", "host-a")]
    [InlineData(Nowhere, "", "run-1", "host-a")]
    [InlineData(Nowhere, "nightly", "", "host-a")]
    [InlineData(Nowhere, "nightly", "run-1", "")]
    public async Task AnAcquireWithSomethingMissingIsRefusedBeforeItConnects(
        string connectionString, string jobId, string runId, string holder) =>
        await Assert.ThrowsAsync<ArgumentException>(() => new SqlJobLeaseStore().TryAcquireAsync(
            connectionString, jobId, runId, holder, TimeSpan.FromMinutes(5), CancellationToken.None));

    [Fact]
    public async Task RenewingAndReleasingNothingAtAllIsRefused()
    {
        var store = new SqlJobLeaseStore();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.RenewAsync(Nowhere, "nightly", null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.ReleaseAsync(Nowhere, "nightly", null!, CancellationToken.None));
    }

    /// <summary>
    /// The store is the implementation of the one abstraction in the Core that had none, so
    /// the runner can hold it as the interface.
    /// </summary>
    [Fact]
    public void TheStoreIsAJobLeaseStore() =>
        Assert.IsAssignableFrom<IJobLeaseStore>(new SqlJobLeaseStore());

    /// <summary>
    /// <see cref="RunLease.IsExpired"/> reads the way the store's SQL does - at the instant
    /// it expires, it is expired - so a release that sets the expiry to now is immediately
    /// visible as free rather than free a tick later.
    /// </summary>
    [Fact]
    public void ALeaseIsExpiredAtTheInstantItExpires()
    {
        var now = DateTimeOffset.UtcNow;
        var lease = new RunLease { AcquiredAt = now.AddMinutes(-5), ExpiresAt = now };

        Assert.True(lease.IsExpired(now));
        Assert.False(lease.IsExpired(now.AddTicks(-1)));
    }
}
