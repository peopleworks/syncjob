using Microsoft.Data.SqlClient;
using SyncJob.Core.Model;
using SyncJob.Core.Publication;

namespace SyncJob.Core.Run;

/// <summary>
/// Keeps one row per job in a table in the destination database saying who holds the job
/// and until when.
/// <para>
/// It replaces the deployed guard, a status column set to "in progress", which is broken
/// in both directions at once: the flag has no owner and no expiry, so a run killed
/// mid-flight leaves it set and every later run refuses forever - and because the
/// deployed check is an <c>AND</c> of the job's flag and a step's flag, a job whose flag
/// is stuck while no step is flagged starts anyway. A lease has an owner and an expiry,
/// so a dead holder's claim lapses on its own and a live one renews.
/// </para>
/// <para>
/// In the destination database for the same reason <see cref="Incremental.SqlWatermarkStore"/>
/// keeps the watermark there: state that lives away from the data it describes survives a
/// restore of that data and then lies about it.
/// </para>
/// <para>
/// Every instant in the row and every comparison against one is the server's clock, never
/// the client's. Two hosts whose clocks are three minutes apart is not a hypothetical, and
/// an expiry decided on the client would mean two different things to them - which is a
/// second holder. The row is therefore written and judged entirely inside SQL Server, and
/// the only thing the client contributes is how long the lease should last.
/// </para>
/// </summary>
public sealed class SqlJobLeaseStore : IJobLeaseStore
{
    /// <summary>Where the leases go when the caller does not name a table.</summary>
    public const string DefaultTable = "dbo.SyncJobLease";

    /// <summary>"There is already an object named ...": two hosts created the table together.</summary>
    private const int ObjectAlreadyExists = 2714;

    /// <summary>Violation of PRIMARY KEY / of UNIQUE index: another session inserted this job first.</summary>
    private const int DuplicateKey = 2627;
    private const int DuplicateIndexKey = 2601;

    /// <summary>Chosen as the deadlock victim.</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>
    /// Not the zero the publication path uses. Zero means "no limit", which is the honest
    /// default for a count over a hundred million rows and the wrong one here: every
    /// statement in this file touches exactly one row, so an acquire that has been blocked
    /// for thirty seconds is not slow, it is stuck, and a service should be told rather
    /// than left waiting.
    /// </summary>
    private const int CommandTimeoutSeconds = 30;

    /// <summary>
    /// <c>DATEADD</c> counts in <c>int</c>, so a duration is expressed in milliseconds and
    /// bounded by what an <c>int</c> of them holds - about twenty-four days. Anything near
    /// that is a bug in the caller, not a lease.
    /// </summary>
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly SqlObjectName _table;

    /// <summary>
    /// The table comes in through the constructor rather than through
    /// <see cref="IJobLeaseStore"/> because the interface carries a job and no plan, and a
    /// deployment that wants its leases somewhere else constructs one store for them.
    /// </summary>
    public SqlJobLeaseStore(string? table = null) =>
        _table = SqlObjectName.Parse(string.IsNullOrWhiteSpace(table) ? DefaultTable : table!);

    /// <summary>The table this store writes to, quoted as it goes into a statement.</summary>
    public string Table => _table.Quoted;

    /// <summary>
    /// Creates the lease table if it is not there.
    /// <para>
    /// Two hosts racing to create it is the ordinary case, not the exotic one: that race
    /// happens precisely when both are trying to start the same job for the first time. The
    /// loser gets error 2714 and has exactly what it wanted anyway.
    /// </para>
    /// </summary>
    public async Task EnsureReadyAsync(string connectionString, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = await PublicationSql.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);
        await EnsureTableAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the lease when the job is free or the held lease has expired, and returns null
    /// when someone else holds one that has not. Taking over an expired lease is the whole
    /// difference between this and the flag it replaces.
    /// <para>
    /// The decision and the write are one <c>MERGE</c>, and that is the reason they cannot
    /// interleave. Anything in two statements has a window between them - the trap is
    /// <c>IF NOT EXISTS ... INSERT</c>, where a second session inserts between the check and
    /// the insert and one of the two gets a duplicate key instead of a clean answer.
    /// <c>WITH (HOLDLOCK)</c> makes the match a serializable range lock on the primary key,
    /// so a session that finds no row holds the key range while it inserts; a second session
    /// looking for the same key blocks on that range rather than deciding against a row that
    /// is about to exist, and when it proceeds it reads the committed row and takes the
    /// matched branch. The hint is not decoration: with it removed, four hosts racing for
    /// one job fail with a primary-key violation, which is the same trap in a different
    /// costume. <c>OUTPUT</c> is how the caller learns which happened: a
    /// <c>MERGE</c> emits an output row only for an action it actually took, so "no rows" is
    /// exactly "a live lease is held by someone else" - without a second <c>SELECT</c> that
    /// would race with the write it is describing.
    /// </para>
    /// <para>
    /// A run that already holds the lease is given it again rather than refused. The case is
    /// a connection dropped after the server committed the acquire but before the client saw
    /// the answer: the run retries, and without this it would be blocked for a whole lease
    /// duration by its own ghost.
    /// </para>
    /// </summary>
    public async Task<RunLease?> TryAcquireAsync(
        string connectionString,
        string jobId,
        string runId,
        string holder,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);

        var milliseconds = DurationMilliseconds(duration);

        await using var connection = await PublicationSql.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);
        await EnsureTableAsync(connection, cancellationToken).ConfigureAwait(false);

        // @now is read once, on the server, and used for every instant this statement
        // writes and every comparison it makes. SYSDATETIMEOFFSET() called four times in
        // one statement is four calls, and a row whose AcquiredAt is later than its own
        // RenewedAt is a row an operator cannot read at three in the morning.
        var sql = $"""
            DECLARE @now datetimeoffset(7) = SYSDATETIMEOFFSET();

            MERGE {_table.Quoted} WITH (HOLDLOCK) AS t
            USING (SELECT @job AS [JobId]) AS s
                ON t.[JobId] = s.[JobId]
            WHEN MATCHED AND (t.[ExpiresAt] <= @now OR t.[RunId] = @run) THEN UPDATE SET
                t.[PreviousRunId]  = CASE WHEN t.[RunId] = @run THEN t.[PreviousRunId]  ELSE t.[RunId]  END,
                t.[PreviousHolder] = CASE WHEN t.[RunId] = @run THEN t.[PreviousHolder] ELSE t.[Holder] END,
                t.[RunId]          = @run,
                t.[Holder]         = @holder,
                t.[AcquiredAt]     = @now,
                t.[RenewedAt]      = @now,
                t.[ExpiresAt]      = DATEADD(millisecond, @duration, @now),
                t.[DurationMs]     = @duration,
                t.[Renewals]       = 0,
                t.[ReleasedAt]     = NULL
            WHEN NOT MATCHED BY TARGET THEN
                INSERT ([JobId], [RunId], [Holder], [AcquiredAt], [RenewedAt], [ExpiresAt], [DurationMs], [Renewals])
                VALUES (@job, @run, @holder, @now, @now, DATEADD(millisecond, @duration, @now), @duration, 0)
            OUTPUT inserted.[RunId], inserted.[Holder], inserted.[AcquiredAt], inserted.[ExpiresAt];
            """;

        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = CommandTimeoutSeconds };
            command.Parameters.AddWithValue("@job", jobId);
            command.Parameters.AddWithValue("@run", runId);
            command.Parameters.AddWithValue("@holder", holder);
            command.Parameters.AddWithValue("@duration", milliseconds);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if(!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            return new RunLease
            {
                RunId = reader.GetString(0),
                Holder = reader.GetString(1),
                AcquiredAt = reader.GetDateTimeOffset(2),
                ExpiresAt = reader.GetDateTimeOffset(3)
            };
        }
        catch(SqlException e) when(e.Number is DuplicateKey or DuplicateIndexKey)
        {
            // The range lock makes this unreachable - a race test that rethrows here still
            // passes - and it is caught anyway because losing a race is a normal outcome of
            // this method, and a caller that gets an exception where it expected null is a
            // service that dies at midnight. A
            // duplicate key on this table has exactly one meaning: another session got the
            // job in first, which is the answer null already carries.
            return null;
        }
        catch(SqlException e) when(e.Number == DeadlockVictim)
        {
            // Same reasoning, one step further out. A deadlock victim is rolled back, so it
            // demonstrably did not take the lease, and "did not take it" is the safe
            // direction to fail in: the run does not start. The alternatives are to crash
            // the caller or to retry straight back into the contention.
            return null;
        }
    }

    /// <summary>
    /// Extends the lease from now, and updates <paramref name="lease"/> so the caller's copy
    /// of <see cref="RunLease.ExpiresAt"/> is the one the server actually holds - otherwise
    /// the run goes on renewing against a value it can no longer trust.
    /// <para>
    /// Keyed on the job <b>and</b> the run, so a lease that expired and was taken over by
    /// another host cannot be taken back by the run that lost it: the row's <c>RunId</c> is
    /// someone else's, nothing matches, and this throws. A lease that expired and was
    /// <i>not</i> taken over is reclaimed rather than refused - the row still names this
    /// run, nobody else wanted it, and failing a live run because a renewal was late by a
    /// garbage collection is a worse outcome than letting it carry on.
    /// </para>
    /// <para>
    /// The new expiry is <c>DurationMs</c> from now, read from the row rather than derived
    /// from the lease the caller holds. Deriving it as <c>ExpiresAt - AcquiredAt</c> would
    /// grow by the age of the lease at every renewal, so a five-minute lease renewed all
    /// night would end the night holding the job for hours after the process died.
    /// </para>
    /// </summary>
    /// <returns>
    /// False when this run no longer holds the lease: another host took it over, or the run
    /// released it and is now trying to resurrect a claim it gave up. False rather than an
    /// exception because a renewal that fails on a blinking network is a different fact from
    /// one that fails because the claim is gone, and only the caller can tell what to do
    /// about each - see <see cref="IJobLeaseStore.RenewAsync"/>.
    /// </returns>
    public async Task<bool> RenewAsync(
        string connectionString,
        string jobId,
        RunLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(lease);

        await using var connection = await PublicationSql.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);
        await EnsureTableAsync(connection, cancellationToken).ConfigureAwait(false);

        // ReleasedAt IS NULL: a run that released the job and then renews is not renewing,
        // it is resurrecting a claim it gave up, and the next host to ask would be refused
        // by a run that had already finished.
        var sql = $"""
            DECLARE @now datetimeoffset(7) = SYSDATETIMEOFFSET();

            UPDATE {_table.Quoted} SET
                [ExpiresAt] = DATEADD(millisecond, [DurationMs], @now),
                [RenewedAt] = @now,
                [Renewals]  = [Renewals] + 1
            OUTPUT inserted.[ExpiresAt]
            WHERE [JobId] = @job AND [RunId] = @run AND [ReleasedAt] IS NULL;
            """;

        var expiresAt = await PublicationSql.ScalarAsync(
            connection, null, sql, CommandTimeoutSeconds, cancellationToken,
            ("@job", jobId), ("@run", lease.RunId)).ConfigureAwait(false);

        if(expiresAt is null)
            return false;

        lease.ExpiresAt = (DateTimeOffset)expiresAt;
        return true;
    }

    /// <summary>
    /// Gives the job back, and the row keeps saying who had it. The lease is expired rather
    /// than deleted so that the answer to "what happened at two o'clock" survives the run
    /// that happened at two o'clock, and so that <c>ReleasedAt</c> can tell an operator the
    /// one thing they actually want to know: whether the last run finished or died.
    /// <para>
    /// Keyed on the job and the run, so a run whose lease was taken over cannot release the
    /// claim the host that took it now holds.
    /// </para>
    /// <para>
    /// Releasing a lease this run no longer holds returns quietly, where renewing throws.
    /// The asymmetry is deliberate: release is what runs in a <c>finally</c>, its goal is
    /// that the job is free, and when the row says someone else holds it that goal is
    /// already met. Throwing there would turn a run that succeeded into a run that failed
    /// in its own cleanup.
    /// </para>
    /// </summary>
    public async Task ReleaseAsync(
        string connectionString,
        string jobId,
        RunLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(lease);

        await using var connection = await PublicationSql.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);
        await EnsureTableAsync(connection, cancellationToken).ConfigureAwait(false);

        var sql = $"""
            DECLARE @now datetimeoffset(7) = SYSDATETIMEOFFSET();

            UPDATE {_table.Quoted} SET
                [ExpiresAt]  = @now,
                [ReleasedAt] = @now
            OUTPUT inserted.[ExpiresAt]
            WHERE [JobId] = @job AND [RunId] = @run AND [ReleasedAt] IS NULL;
            """;

        var expiresAt = await PublicationSql.ScalarAsync(
            connection, null, sql, CommandTimeoutSeconds, cancellationToken,
            ("@job", jobId), ("@run", lease.RunId)).ConfigureAwait(false);

        // The caller's copy is told the truth as well, so a run that releases and then asks
        // IsExpired is not answered by a value that stopped being true on the server.
        if(expiresAt is not null)
            lease.ExpiresAt = (DateTimeOffset)expiresAt;
    }

    private static int DurationMilliseconds(TimeSpan duration)
    {
        if(duration <= TimeSpan.Zero || duration > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                $"a lease lasts for more than nothing and less than {MaximumDuration.TotalDays:0} days; a run " +
                "that needs longer than that renews rather than asking for it all at once");
        }

        return (int)duration.TotalMilliseconds;
    }

    /// <summary>
    /// Its own batch, never folded into the statement that follows it: a batch that names a
    /// table which does not exist yet fails to compile as a whole, whichever branch of the
    /// <c>IF</c> would have run.
    /// <para>
    /// Called before every operation rather than only by the runner, so a surface that
    /// forgot to call <see cref="EnsureReadyAsync(string, CancellationToken)"/> gets a
    /// working lease instead of an "invalid object name", and so that renewing against a
    /// table that is not there reports a lost lease rather than a SQL error.
    /// </para>
    /// </summary>
    private async Task EnsureTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        // RenewedAt, Renewals and ReleasedAt are not decoration. ExpiresAt alone cannot tell
        // an operator whether the holder is alive: a lease acquired for an hour and dead
        // since the first minute looks exactly like one that is being renewed every ten
        // seconds. PreviousRunId and PreviousHolder record the takeover, so a job that keeps
        // being stolen from a host that keeps dying says so in the row rather than only in
        // whatever logs that host still has.
        var sql = $"""
            IF OBJECT_ID(N'{_table.Literal}', N'U') IS NULL
                CREATE TABLE {_table.Quoted} (
                    [JobId]          nvarchar(200)     NOT NULL,
                    [RunId]          nvarchar(200)     NOT NULL,
                    [Holder]         nvarchar(400)     NOT NULL,
                    [AcquiredAt]     datetimeoffset(7) NOT NULL,
                    [RenewedAt]      datetimeoffset(7) NOT NULL,
                    [ExpiresAt]      datetimeoffset(7) NOT NULL,
                    [DurationMs]     int               NOT NULL,
                    [Renewals]       int               NOT NULL,
                    [ReleasedAt]     datetimeoffset(7)     NULL,
                    [PreviousRunId]  nvarchar(200)         NULL,
                    [PreviousHolder] nvarchar(400)         NULL,
                    PRIMARY KEY ([JobId])
                );
            """;

        try
        {
            await PublicationSql.ExecuteAsync(
                connection, null, sql, CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch(SqlException e) when(e.Number == ObjectAlreadyExists)
        {
            // Two hosts starting the same job together both found the table missing and both
            // tried to create it. The one that lost has exactly what it wanted.
        }
    }
}
