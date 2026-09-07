using System.Globalization;
using SyncJob.Core.Model;

namespace SyncJob.Core.Publication;

/// <summary>
/// Counts what was staged, counts what is already there, and decides whether the one
/// may replace the other.
/// <para>
/// This is the check SyncJob's CLI has had since 2.3 and its Windows service has never
/// had. Without it a source that comes back empty - a view that broke, a filter that
/// matched nothing, a linked server that returned no rows rather than failing -
/// followed by a truncate, is a job that empties a production table and reports
/// success.
/// </para>
/// </summary>
public sealed class PublicationGuardEvaluator : IPublicationGuard
{
    /// <summary>
    /// What <see cref="GuardVerdict.DestinationRows"/> holds when the destination is
    /// not there at all, which is a first run rather than a failure.
    /// </summary>
    public const long DestinationAbsent = -1;

    /// <summary>Seconds a count may take; zero means no limit. See <see cref="PublicationSql"/>.</summary>
    public int CommandTimeoutSeconds { get; init; }

    public async Task<GuardVerdict> EvaluateAsync(
        string connectionString,
        string stagingTable,
        string destinationTable,
        PublicationGuard guard,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(guard);

        var staging = SqlObjectName.Parse(stagingTable);
        var destination = SqlObjectName.Parse(destinationTable);

        await using var connection = await PublicationSql.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);

        var stagedRows = await PublicationSql.CountAsync(connection, staging, CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        var destinationRows = await PublicationSql.ObjectIdAsync(
            connection, destination, CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false) is null
            ? DestinationAbsent
            : await PublicationSql.CountAsync(connection, destination, CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        return Decide(stagedRows, destinationRows, guard);
    }

    /// <summary>
    /// The decision itself, with the counting done. Separate from the connection so
    /// that the arithmetic - which is the part that empties a production table when it
    /// is wrong - can be tested without a server.
    /// </summary>
    /// <param name="stagedRows">How many rows the copy put into the staging table.</param>
    /// <param name="destinationRows">
    /// <see cref="DestinationAbsent"/> when the destination does not exist. The
    /// fraction is then not applied: there is nothing to compare against, and refusing
    /// every first run is not a safety feature.
    /// </param>
    /// <param name="guard">The step's thresholds. Both may be set; both have to pass.</param>
    public static GuardVerdict Decide(long stagedRows, long destinationRows, PublicationGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);

        var refusals = new List<string>();

        if(guard.MinimumRows > 0 && stagedRows < guard.MinimumRows)
        {
            refusals.Add(
                $"the row floor refused it: {Number(stagedRows)} staged, {Number(guard.MinimumRows)} required");
        }

        var fraction = guard.MinimumFractionOfDestination;
        var fractionApplies = fraction.HasValue && destinationRows > 0;

        if(fractionApplies)
        {
            var required = (long)Math.Ceiling(destinationRows * fraction!.Value);
            if(stagedRows < required)
            {
                refusals.Add(
                    $"the fraction refused it: {Number(stagedRows)} staged, {Number(required)} required " +
                    $"({Percentage(fraction.Value)} of the {Number(destinationRows)} already there)");
            }
        }

        if(refusals.Count > 0)
            return new GuardVerdict(false, stagedRows, destinationRows, string.Join("; ", refusals));

        return new GuardVerdict(true, stagedRows, destinationRows, Passed(stagedRows, destinationRows, guard, fractionApplies));
    }

    private static string Passed(long stagedRows, long destinationRows, PublicationGuard guard, bool fractionApplies)
    {
        var against = destinationRows == DestinationAbsent
            ? "the destination does not exist yet, so this is a first run"
            : $"{Number(destinationRows)} already there";

        var rules = new List<string>();
        if(guard.MinimumRows > 0)
            rules.Add($"floor {Number(guard.MinimumRows)}");
        if(guard.MinimumFractionOfDestination.HasValue)
        {
            rules.Add(fractionApplies
                ? $"fraction {Percentage(guard.MinimumFractionOfDestination.Value)}"
                : $"fraction {Percentage(guard.MinimumFractionOfDestination.Value)} not applicable");
        }

        // Saying so is the point: a guard with nothing set reads as a passing guard in
        // a log, and is the state the service shipped in.
        var applied = rules.Count == 0
            ? "no guard is configured, so nothing was checked"
            : $"{string.Join(" and ", rules)} met";

        return $"{Number(stagedRows)} staged against {against}; {applied}";
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percentage(double fraction) =>
        (fraction * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
}
