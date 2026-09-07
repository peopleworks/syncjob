namespace SyncJob.Core.Incremental;

/// <summary>
/// A ledger row whose key cannot be used, and why. It is reported rather than skipped
/// because a deletion that is silently not applied is a row that lives on in the
/// destination forever, and nobody finds out from the log.
/// </summary>
/// <param name="LedgerId">The ledger row's own id, so an operator can go and look at it.</param>
/// <param name="KeyValue">What the ledger actually held.</param>
/// <param name="Parts">How many parts it split into.</param>
/// <param name="ExpectedParts">How many the step's delete key has.</param>
public sealed record TombstoneKeyProblem(long LedgerId, string? KeyValue, int Parts, int ExpectedParts)
{
    public override string ToString() =>
        $"ledger row {LedgerId} holds a key of {Parts} part(s) and the delete key has {ExpectedParts}: '{KeyValue}'";
}

/// <summary>
/// Raised when a ledger held keys that could not be applied. Thrown after the well-formed
/// deletions have been committed, so a malformed row costs the run its green tick and not
/// the work that was already correct.
/// </summary>
public sealed class TombstoneKeyFormatException : InvalidOperationException
{
    public TombstoneKeyFormatException(IReadOnlyList<TombstoneKeyProblem> problems)
        : base(BuildMessage(problems)) =>
        Problems = problems;

    public TombstoneKeyFormatException()
        : base("a tombstone ledger held keys that could not be applied") =>
        Problems = [];

    public TombstoneKeyFormatException(string message)
        : base(message) =>
        Problems = [];

    public TombstoneKeyFormatException(string message, Exception innerException)
        : base(message, innerException) =>
        Problems = [];

    public IReadOnlyList<TombstoneKeyProblem> Problems { get; }

    private static string BuildMessage(IReadOnlyList<TombstoneKeyProblem> problems) =>
        $"{problems.Count} tombstone ledger row(s) hold a key that does not match the step's delete key and were not " +
        "applied or marked processed: " + string.Join("; ", problems.Take(10));
}

/// <summary>
/// Reads the key out of a tombstone ledger row.
/// <para>
/// This is the whole of the difference from the deployed implementation, and it is worth
/// stating plainly. That one splits the legacy key with <c>PARSENAME</c>, a function for
/// taking apart an object name: it counts parts from the <b>right</b> and returns null past
/// four. Give it a three-part key and ask for the parts left to right and the first two
/// come back null, the join to the destination matches no row, and the run deletes nothing
/// and reports nothing - it looks exactly like a ledger that had nothing in it.
/// </para>
/// <para>
/// So: from the left, with no limit, and a part count that disagrees with the step's delete
/// key is reported rather than joined on and quietly missed.
/// </para>
/// </summary>
public static class TombstoneKey
{
    /// <summary>
    /// Splits a legacy <c>KeyValue</c> into its parts, left to right, however many there
    /// are. An empty separator is refused rather than treated as "no split", which would
    /// turn every composite key into a one-part key that matches nothing.
    /// </summary>
    public static IReadOnlyList<string> SplitLegacy(string keyValue, string separator)
    {
        ArgumentNullException.ThrowIfNull(keyValue);

        if(string.IsNullOrEmpty(separator))
            throw new ArgumentException("a legacy tombstone key needs a separator to split on", nameof(separator));

        // Split with no count limit and no options: every occurrence of the separator makes
        // a part, an empty part stays an empty part, and the parts stay in the order the
        // source wrote them. A key value that itself contains the separator therefore
        // produces too many parts - which is caught as a mismatch below, and is the reason
        // TombstoneKeyFormat.Columns exists at all.
        return keyValue.Split(separator, StringSplitOptions.None);
    }

    /// <summary>
    /// The parts of a legacy key when they match the delete key, or null with the problem
    /// filled in when they do not.
    /// </summary>
    public static IReadOnlyList<string>? TryReadLegacy(
        long ledgerId,
        string? keyValue,
        string separator,
        int expectedParts,
        out TombstoneKeyProblem? problem)
    {
        if(keyValue is null)
        {
            problem = new TombstoneKeyProblem(ledgerId, null, 0, expectedParts);
            return null;
        }

        var parts = SplitLegacy(keyValue, separator);
        if(parts.Count != expectedParts)
        {
            problem = new TombstoneKeyProblem(ledgerId, keyValue, parts.Count, expectedParts);
            return null;
        }

        problem = null;
        return parts;
    }
}
