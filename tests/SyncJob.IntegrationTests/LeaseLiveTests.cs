using SyncJob.Core.Model;
using SyncJob.Core.Run;

namespace SyncJob.IntegrationTests;

/// <summary>
/// The job lease against a real server, which is the only place it can be tested at all:
/// every question this class answers is a question about what two sessions do to one row.
/// <para>
/// The failure being tested for is the deployed guard, a status column set to "in
/// progress". It is broken in both directions at once - a run killed mid-flight leaves the
/// flag set and blocks the job forever, while a job whose flag is stuck but whose steps are
/// not starts anyway. So the two tests that matter most here are the one where an expired
/// lease is taken over, and the one where two hosts ask at the same instant.
/// </para>
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LeaseLiveTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Long enough that nothing in a test expires by accident, short enough to be obviously
    /// not a real production duration.
    /// </summary>
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    [LiveFact]
    public async Task ALeaseIsTakenOnAFreeJob_AndASecondHostIsRefused()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        var first = await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234", TenMinutes, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal("run-1", first.RunId);
        Assert.Equal("host-a/1234", first.Holder);
        Assert.True(first.ExpiresAt > first.AcquiredAt);
        Assert.False(first.IsExpired(DateTimeOffset.UtcNow));

        var second = await store.TryAcquireAsync(
            connectionString, "nightly", "run-2", "host-b/5678", TenMinutes, CancellationToken.None);

        Assert.Null(second);

        // One row per job, and it still names the run that holds it.
        Assert.Equal(1, await SqlServerFixture.CountAsync(connectionString, SqlJobLeaseStore.DefaultTable));
        Assert.Equal("run-1", await ColumnAsync(connectionString, "RunId", "nightly"));
    }

    /// <summary>
    /// The case the flag it replaces cannot do at all: the holder died, its claim lapsed on
    /// its own, and the next host takes the job over without anyone editing a row by hand.
    /// </summary>
    [LiveFact]
    public async Task AnExpiredLeaseIsTakenOverByAnotherHost_AndTheRowSaysWhoItTookItFrom()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        var dead = await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234",
            TimeSpan.FromMilliseconds(400), CancellationToken.None);

        Assert.NotNull(dead);

        // host-a is killed here: it never renews and never releases.
        await Task.Delay(900);

        var taken = await store.TryAcquireAsync(
            connectionString, "nightly", "run-2", "host-b/5678", TenMinutes, CancellationToken.None);

        Assert.NotNull(taken);
        Assert.Equal("run-2", taken.RunId);
        Assert.True(taken.ExpiresAt > dead.ExpiresAt);

        // What an operator wants at three in the morning: the row says the job was taken off
        // a host that stopped renewing, and names it.
        Assert.Equal("run-1", await ColumnAsync(connectionString, "PreviousRunId", "nightly"));
        Assert.Equal("host-a/1234", await ColumnAsync(connectionString, "PreviousHolder", "nightly"));

        // And nobody released it - it lapsed. That is the difference between a run that
        // finished and a run that died, and it is the first thing to look at.
        Assert.Equal(DBNull.Value, await ColumnAsync(connectionString, "ReleasedAt", "nightly"));
    }

    [LiveFact]
    public async Task RenewalExtendsTheExpiry_AndTheOtherHostIsStillRefusedPastTheOriginalOne()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        var lease = await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234",
            TimeSpan.FromMilliseconds(1_500), CancellationToken.None);

        Assert.NotNull(lease);
        var originalExpiry = lease.ExpiresAt;

        await Task.Delay(1_000);
        await store.RenewAsync(connectionString, "nightly", lease, CancellationToken.None);

        // The caller's own copy moved: a run that renewed against a value it still believed
        // would be renewing against a lease that is not the one the server holds.
        Assert.True(lease.ExpiresAt > originalExpiry);

        // Past where the lease would have lapsed had it not been renewed.
        await Task.Delay(800);

        Assert.Null(await store.TryAcquireAsync(
            connectionString, "nightly", "run-2", "host-b/5678", TenMinutes, CancellationToken.None));

        Assert.Equal("run-1", await ColumnAsync(connectionString, "RunId", "nightly"));
        Assert.Equal(1, await ColumnAsync(connectionString, "Renewals", "nightly"));
    }

    /// <summary>
    /// A renewal is the length the lease was taken for, measured from now - not the age of
    /// the lease. Deriving it from the caller's copy would grow it at every renewal, so a
    /// five-minute lease renewed all night would hold the job for hours after the process
    /// that held it died.
    /// </summary>
    [LiveFact]
    public async Task RenewalExtendsByTheDurationTheLeaseWasTakenFor_NotByItsAge()
    {
        const int renewals = 5;

        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        var lease = await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234",
            TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(lease);

        for(var i = 0; i < renewals; i++)
        {
            await Task.Delay(400);
            await store.RenewAsync(connectionString, "nightly", lease, CancellationToken.None);
        }

        // Both instants come from the server, so this measures the store's arithmetic and
        // not the two clocks. Renewed correctly the lease ends five seconds after the last
        // renewal, about seven seconds after it was taken. Renewed by its own age it ends
        // eleven seconds after it was taken, and every renewal after that makes it worse.
        var heldFor = lease.ExpiresAt - lease.AcquiredAt;

        Assert.True(
            heldFor < TimeSpan.FromSeconds(9),
            $"after {renewals} renewals a five-second lease runs {heldFor.TotalSeconds:0.0}s past the moment it " +
            "was taken; a lease extended by its own age holds the job for hours after the process that took " +
            "it has died, and nothing else can start");

        Assert.Equal(renewals, await ColumnAsync(connectionString, "Renewals", "nightly"));
        Assert.Equal(5_000, await ColumnAsync(connectionString, "DurationMs", "nightly"));
    }

    /// <summary>
    /// An expired lease that nobody else wanted is reclaimed rather than refused: the row
    /// still names this run, and failing a live run because a renewal was late by a garbage
    /// collection is worse than letting it carry on.
    /// </summary>
    [LiveFact]
    public async Task ALeaseThatExpiredAndWasNotTakenIsRenewedByTheRunThatStillHoldsIt()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        var lease = await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234",
            TimeSpan.FromMilliseconds(400), CancellationToken.None);

        Assert.NotNull(lease);
        await Task.Delay(900);
        Assert.True(lease.IsExpired(DateTimeOffset.UtcNow));

        await store.RenewAsync(connectionString, "nightly", lease, CancellationToken.None);

        Assert.False(lease.IsExpired(DateTimeOffset.UtcNow));
        Assert.Equal("run-1", await ColumnAsync(connectionString, "RunId", "nightly"));
    }

    [LiveFact]
    public async Task ReleaseFreesTheJobImmediately()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        var lease = await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234", TenMinutes, CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Null(await store.TryAcquireAsync(
            connectionString, "nightly", "run-2", "host-b/5678", TenMinutes, CancellationToken.None));

        await store.ReleaseAsync(connectionString, "nightly", lease, CancellationToken.None);

        // Immediately: no waiting out a ten-minute lease that its holder has finished with.
        var next = await store.TryAcquireAsync(
            connectionString, "nightly", "run-2", "host-b/5678", TenMinutes, CancellationToken.None);

        Assert.NotNull(next);
        Assert.Equal("run-2", next.RunId);

        // The run that released is told, so it cannot go on believing it holds the job.
        Assert.True(lease.IsExpired(DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// A released lease is expired, not deleted. The row is the only place an operator can
    /// see whether the last run finished or died, and deleting it throws that away.
    /// </summary>
    [LiveFact]
    public async Task AReleasedLeaseLeavesTheRowSayingWhoRanAndThatTheyFinished()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        var lease = await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234", TenMinutes, CancellationToken.None);

        Assert.NotNull(lease);
        await store.ReleaseAsync(connectionString, "nightly", lease, CancellationToken.None);

        Assert.Equal(1, await SqlServerFixture.CountAsync(connectionString, SqlJobLeaseStore.DefaultTable));
        Assert.Equal("run-1", await ColumnAsync(connectionString, "RunId", "nightly"));
        Assert.NotEqual(DBNull.Value, await ColumnAsync(connectionString, "ReleasedAt", "nightly"));
    }

    /// <summary>
    /// The stolen-lease case for renewal. A run whose claim lapsed and was taken over must
    /// not be able to take it back: it would then be writing into a destination another run
    /// owns, which is the exact thing the lease exists to prevent.
    /// </summary>
    [LiveFact]
    public async Task RenewingALeaseAnotherHostTookOverThrows_AndDoesNotTakeItBack()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        var lost = await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234",
            TimeSpan.FromMilliseconds(400), CancellationToken.None);

        Assert.NotNull(lost);
        await Task.Delay(900);

        var taken = await store.TryAcquireAsync(
            connectionString, "nightly", "run-2", "host-b/5678", TenMinutes, CancellationToken.None);

        Assert.NotNull(taken);

        var thrown = await Assert.ThrowsAsync<JobLeaseLostException>(
            () => store.RenewAsync(connectionString, "nightly", lost, CancellationToken.None));

        Assert.Equal("nightly", thrown.JobId);
        Assert.Equal("run-1", thrown.RunId);

        // And the host that does hold it still does, with its expiry untouched.
        Assert.Equal("run-2", await ColumnAsync(connectionString, "RunId", "nightly"));
        Assert.Equal(0, await ColumnAsync(connectionString, "Renewals", "nightly"));
    }

    [LiveFact]
    public async Task AReleasedLeaseCannotBeRenewedBackIntoLife()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        var lease = await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234", TenMinutes, CancellationToken.None);

        Assert.NotNull(lease);
        await store.ReleaseAsync(connectionString, "nightly", lease, CancellationToken.None);

        await Assert.ThrowsAsync<JobLeaseLostException>(
            () => store.RenewAsync(connectionString, "nightly", lease, CancellationToken.None));

        // Still free: the renewal did not resurrect a claim the run had given up.
        Assert.NotNull(await store.TryAcquireAsync(
            connectionString, "nightly", "run-2", "host-b/5678", TenMinutes, CancellationToken.None));
    }

    /// <summary>
    /// The stolen-lease case for release, and the reason it is quiet where renewal throws:
    /// release runs in a <c>finally</c>, and a run that succeeded must not be reported as
    /// failed because its cleanup found the job already belonged to somebody else.
    /// </summary>
    [LiveFact]
    public async Task ReleasingALeaseAnotherHostTookOverIsQuiet_AndDoesNotFreeTheirClaim()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        var lost = await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234",
            TimeSpan.FromMilliseconds(400), CancellationToken.None);

        Assert.NotNull(lost);
        await Task.Delay(900);

        Assert.NotNull(await store.TryAcquireAsync(
            connectionString, "nightly", "run-2", "host-b/5678", TenMinutes, CancellationToken.None));

        await store.ReleaseAsync(connectionString, "nightly", lost, CancellationToken.None);

        // host-b still holds it: a third host is still refused.
        Assert.Null(await store.TryAcquireAsync(
            connectionString, "nightly", "run-3", "host-c/9012", TenMinutes, CancellationToken.None));

        Assert.Equal("run-2", await ColumnAsync(connectionString, "RunId", "nightly"));
        Assert.Equal(DBNull.Value, await ColumnAsync(connectionString, "ReleasedAt", "nightly"));
    }

    [LiveFact]
    public async Task ReleasingALeaseThatWasNeverTakenIsQuiet()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        var imaginary = new RunLease { RunId = "run-1", Holder = "host-a/1234" };

        await store.ReleaseAsync(connectionString, "nightly", imaginary, CancellationToken.None);

        // The table was created on the way past, and nothing was invented in it.
        Assert.Equal(0, await SqlServerFixture.CountAsync(connectionString, SqlJobLeaseStore.DefaultTable));
    }

    /// <summary>
    /// The connection dropped after the server committed the acquire but before the client
    /// saw the answer, so the run retries. Without this it would be locked out of its own
    /// job for a whole lease duration by its own ghost.
    /// </summary>
    [LiveFact]
    public async Task ARunThatAlreadyHoldsTheLeaseIsGivenItAgain()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        var first = await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234", TenMinutes, CancellationToken.None);

        var again = await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234", TenMinutes, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(again);
        Assert.Equal("run-1", again.RunId);

        // And it did not record itself as the host it took the job from.
        Assert.Equal(DBNull.Value, await ColumnAsync(connectionString, "PreviousRunId", "nightly"));

        // A different run is still refused.
        Assert.Null(await store.TryAcquireAsync(
            connectionString, "nightly", "run-2", "host-b/5678", TenMinutes, CancellationToken.None));
    }

    [LiveFact]
    public async Task EveryJobKeepsItsOwnLease()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        Assert.NotNull(await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234", TenMinutes, CancellationToken.None));
        Assert.NotNull(await store.TryAcquireAsync(
            connectionString, "hourly", "run-2", "host-a/1234", TenMinutes, CancellationToken.None));

        Assert.Equal(2, await SqlServerFixture.CountAsync(connectionString, SqlJobLeaseStore.DefaultTable));
        Assert.Null(await store.TryAcquireAsync(
            connectionString, "nightly", "run-3", "host-b/5678", TenMinutes, CancellationToken.None));
    }

    [LiveFact]
    public async Task ALeaseTableNamedByTheCallerIsUsedAndCreated()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore("dbo.JobLocks");

        Assert.Equal("[dbo].[JobLocks]", store.Table);
        Assert.NotNull(await store.TryAcquireAsync(
            connectionString, "nightly", "run-1", "host-a/1234", TenMinutes, CancellationToken.None));

        Assert.Equal(1, await SqlServerFixture.CountAsync(connectionString, "dbo.JobLocks"));
        Assert.Equal(-1, await SqlServerFixture.CountAsync(connectionString, SqlJobLeaseStore.DefaultTable));
    }

    /// <summary>
    /// Two hosts starting the same job together is exactly the moment the table does not
    /// exist yet, so the create races itself. It runs in rounds because one round does not
    /// reliably collide: with error 2714 left unguarded this shape fails part way through
    /// with "there is already an object named", which is what the catch is for.
    /// </summary>
    [LiveFact]
    public async Task TheTableSurvivesHostsRacingToCreateIt()
    {
        const int rounds = 12;
        const int hosts = 8;

        var connectionString = await fixture.CreateDatabaseAsync();

        // A fresh table each round: the race exists only while the table is missing, so
        // creating the same one again proves nothing after the first time.
        for(var round = 0; round < rounds; round++)
        {
            var table = $"dbo.SyncJobLease{round}";
            var store = new SqlJobLeaseStore(table);
            var gate = new TaskCompletionSource();

            var racers = Enumerable.Range(0, hosts).Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                await store.EnsureTableAsync(connectionString, CancellationToken.None);
            })).ToArray();

            gate.SetResult();
            await Task.WhenAll(racers);

            Assert.Equal(0, await SqlServerFixture.CountAsync(connectionString, table));
        }
    }

    /// <summary>
    /// The one that decides whether any of the rest is worth anything: four hosts asking for
    /// the same job in the same instant, over and over, and never more than one holder.
    /// <para>
    /// It is run many times because a race that is only run once proves nothing - the two
    /// sessions may simply not have overlapped. The failure this catches is a decision and a
    /// write that are two statements with a window between them.
    /// </para>
    /// </summary>
    [LiveFact]
    public async Task FourHostsRacingForOneJob_ExactlyOneGetsIt()
    {
        const int rounds = 25;
        const int hosts = 4;

        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();

        // The table and the connection pool are warm before the timing matters, so the race
        // is over the lease and not over who pays for the first connection.
        await store.EnsureTableAsync(connectionString, CancellationToken.None);

        var wrong = new List<string>();

        for(var round = 0; round < rounds; round++)
        {
            var jobId = $"job-{round}";
            var gate = new TaskCompletionSource();

            var attempts = Enumerable.Range(0, hosts).Select(host => Task.Run(async () =>
            {
                await gate.Task;
                return await store.TryAcquireAsync(
                    connectionString, jobId, $"run-{host}", $"host-{host}", TenMinutes, CancellationToken.None);
            })).ToArray();

            gate.SetResult();
            var results = await Task.WhenAll(attempts);

            var winners = results.Where(x => x is not null).Select(x => x!.RunId).ToList();
            if(winners.Count != 1)
            {
                wrong.Add($"{jobId}: {winners.Count} holders ({string.Join(", ", winners)})");
                continue;
            }

            var stored = (string?)await ColumnAsync(connectionString, "RunId", jobId);
            if(stored != winners[0])
                wrong.Add($"{jobId}: {winners[0]} was told it holds the lease but the row says {stored}");
        }

        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count} of {rounds} races did not end with exactly one holder, so the acquire interleaves - " +
            "two runs would load the same destination at once:" +
            Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    /// <summary>
    /// The same race against a job somebody already holds and has let lapse: the takeover
    /// path is a different branch of the statement from the insert, and it has to be just as
    /// indivisible.
    /// </summary>
    [LiveFact]
    public async Task FourHostsRacingToTakeOverOneExpiredLease_ExactlyOneGetsIt()
    {
        const int rounds = 15;
        const int hosts = 4;

        var connectionString = await fixture.CreateDatabaseAsync();
        var store = new SqlJobLeaseStore();
        var wrong = new List<string>();

        for(var round = 0; round < rounds; round++)
        {
            var jobId = $"job-{round}";

            // A host takes the job and dies without releasing it.
            Assert.NotNull(await store.TryAcquireAsync(
                connectionString, jobId, "run-dead", "host-dead",
                TimeSpan.FromMilliseconds(150), CancellationToken.None));

            await Task.Delay(250);

            var gate = new TaskCompletionSource();
            var attempts = Enumerable.Range(0, hosts).Select(host => Task.Run(async () =>
            {
                await gate.Task;
                return await store.TryAcquireAsync(
                    connectionString, jobId, $"run-{host}", $"host-{host}", TenMinutes, CancellationToken.None);
            })).ToArray();

            gate.SetResult();
            var results = await Task.WhenAll(attempts);

            var winners = results.Where(x => x is not null).Select(x => x!.RunId).ToList();
            if(winners.Count != 1)
                wrong.Add($"{jobId}: {winners.Count} holders ({string.Join(", ", winners)})");
        }

        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count} of {rounds} takeovers of an expired lease ended with something other than one " +
            "holder, so an expired lease can be taken by two hosts at once:" +
            Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    private static Task<object?> ColumnAsync(string connectionString, string column, string jobId) =>
        SqlServerFixture.ScalarAsync(
            connectionString,
            $"SELECT [{column}] FROM {SqlJobLeaseStore.DefaultTable} WHERE [JobId] = N'{jobId}';");
}
