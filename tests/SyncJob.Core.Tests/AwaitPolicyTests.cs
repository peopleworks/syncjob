using System.Text.RegularExpressions;

namespace SyncJob.Core.Tests;

/// <summary>
/// Every awaited task in the Core says <c>ConfigureAwait(false)</c>, and this is what
/// keeps saying so after everyone has forgotten the decision.
/// <para>
/// The reason is one line long: this engine is a library, its callers are a console, a
/// Windows service and - before long - a desktop application, and the last of those has
/// a synchronisation context. An await that captures one and a caller that blocks on
/// the result are a deadlock, and it is a deadlock that appears in the surface nobody
/// tested rather than in the engine.
/// </para>
/// <para>
/// The obvious enforcement, the CA2007 analyser, was tried and rejected. It also flags
/// <c>await using</c>, where the only way to satisfy it is to split the declaration in
/// two and keep a variable alive that exists solely to be disposed:
/// <code>
/// var connection = new SqlConnection(cs);
/// await using var scope = connection.ConfigureAwait(false);
/// </code>
/// Forty-two times, in a codebase whose whole character is that every line says
/// something. What is bought is the continuation of a <c>DisposeAsync</c>, which
/// carries none of the work; what is paid is forty-two lines of noise. So the rule here
/// is narrower than CA2007 and enforced by this test instead: awaited <b>tasks</b>,
/// which is where the waiting actually happens. A new one that forgets fails the build
/// on the next run.
/// </para>
/// </summary>
public sealed class AwaitPolicyTests
{
    /// <summary>
    /// An <c>await</c> that is not <c>await using</c> or <c>await foreach</c>, with
    /// enough of what follows to tell whether it was configured.
    /// </summary>
    private static readonly Regex AwaitedTask = new(
        @"(?<!\w)await[ \t\r\n]+(?!using\b|foreach\b)",
        RegexOptions.Compiled);

    [Fact]
    public void EveryAwaitedTaskInTheCore_SaysConfigureAwaitFalse()
    {
        var unconfigured = new List<string>();

        foreach(var file in CoreSourceFiles())
        {
            var text = File.ReadAllText(file);

            foreach(Match match in AwaitedTask.Matches(text))
            {
                if(!IsCode(text, match.Index))
                    continue;

                var last = LastCallOfAwaitedExpression(text, match.Index + match.Length);
                if(last is null or "ConfigureAwait")
                    continue;

                unconfigured.Add($"{Path.GetFileName(file)}:{LineOf(text, match.Index)}: {Snippet(text, match.Index)}");
            }
        }

        Assert.True(
            unconfigured.Count == 0,
            $"{unconfigured.Count} awaited task(s) in SyncJob.Core do not call ConfigureAwait(false); " +
            "a caller with a synchronisation context that blocks on one of these deadlocks:" +
            Environment.NewLine + string.Join(Environment.NewLine, unconfigured));
    }

    /// <summary>
    /// The exception is deliberate and bounded, so it is counted rather than waved at:
    /// if the number of <c>await using</c> statements moves, someone is either adding a
    /// connection or has found a way to remove one, and either is worth a second look
    /// at the paragraph above.
    /// </summary>
    [Fact]
    public void TheAwaitUsingExemption_IsStillOnlyDisposals()
    {
        var offending = new List<string>();

        foreach(var file in CoreSourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for(var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if(!line.StartsWith("await using", StringComparison.Ordinal))
                    continue;

                // A disposal declares something. "await using (expr)" would be awaiting
                // a value someone else owns, which is not what the exemption covers.
                if(!line.Contains(" var ", StringComparison.Ordinal))
                    offending.Add($"{Path.GetFileName(file)}:{i + 1}: {line}");
            }
        }

        Assert.True(
            offending.Count == 0,
            "the await-using exemption covers declaring a disposable and nothing else:" +
            Environment.NewLine + string.Join(Environment.NewLine, offending));
    }

    private static IEnumerable<string> CoreSourceFiles()
    {
        var root = FindCoreSource();

        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal);
    }

    /// <summary>
    /// Walks up from the test binary to the repository. The test reads source rather
    /// than IL because the compiler erases the difference: an awaited task with and
    /// without ConfigureAwait produce state machines that differ only in which builder
    /// method is called, and asserting on that would be a test nobody could read.
    /// </summary>
    private static string FindCoreSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while(directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "SyncJob.Core");
            if(Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"src/SyncJob.Core is not above {AppContext.BaseDirectory}, so the await policy cannot be checked");
    }

    /// <summary>
    /// True when the position is in code rather than inside a comment or a string
    /// literal - the SQL in this engine is written in raw string literals, and the word
    /// "await" appears in prose in more than one doc comment.
    /// </summary>
    private static bool IsCode(string text, int index)
    {
        var i = 0;
        while(i < index)
        {
            if(text.AsSpan(i).StartsWith("\"\"\""))
            {
                var end = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                if(end < 0 || end + 3 > index)
                    return false;

                i = end + 3;
            }
            else if(text[i] == '"')
            {
                i++;
                while(i < text.Length && text[i] != '"')
                    i += text[i] == '\\' ? 2 : 1;

                if(i >= index)
                    return false;

                i++;
            }
            else if(text.AsSpan(i).StartsWith("//"))
            {
                var end = text.IndexOf('\n', i);
                if(end < 0 || end > index)
                    return false;

                i = end;
            }
            else
            {
                i++;
            }
        }

        return true;
    }

    /// <summary>
    /// The name of the last call in the awaited expression's chain - so
    /// <c>a.b.RunAsync(x).ConfigureAwait(false)</c> answers "ConfigureAwait" and
    /// <c>a.b.RunAsync(x)</c> answers "RunAsync" - or null when the chain cannot be
    /// read.
    /// <para>
    /// The whole chain, not the first call in it. An earlier version of this stopped at
    /// the first closing parenthesis and asked whether <c>.ConfigureAwait</c> came
    /// next, which got three shapes exactly backwards: it flagged the correct
    /// <c>await task.ConfigureAwait(false)</c> on a bare task variable, it flagged the
    /// correct <c>await Factory(x).RunAsync(y).ConfigureAwait(false)</c>, and it let the
    /// genuinely wrong <c>await task;</c> through - a bare await on a task variable
    /// being precisely the form that captures the context. WP 1.5d hit all three and
    /// worked around them, which is the wrong way round: a test that pushes correct code
    /// into workarounds is worse than no test, because it is obeyed.
    /// </para>
    /// </summary>
    private static string? LastCallOfAwaitedExpression(string text, int start)
    {
        var i = text.AsSpan(start).StartsWith("new ") ? start + 4 : start;
        string? last = null;

        while(true)
        {
            var nameStart = i;
            while(i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '@'))
                i++;

            if(i == nameStart)
                return null;

            last = text[nameStart..i];
            i = SkipWhitespace(text, i);

            if(i < text.Length && text[i] == '<')
            {
                i = SkipBalanced(text, i, '<', '>');
                if(i < 0)
                    return null;

                i = SkipWhitespace(text, i);
            }

            if(i < text.Length && text[i] == '(')
            {
                i = SkipBalanced(text, i, '(', ')');
                if(i < 0)
                    return null;

                i = SkipWhitespace(text, i);
            }

            // Only a dot continues the chain. Anything else - a semicolon, a comparison,
            // the ? of a conditional - ends the expression, and the name just read is
            // the one that had to say ConfigureAwait.
            if(i >= text.Length || text[i] != '.')
                return last;

            i = SkipWhitespace(text, i + 1);
        }
    }

    private static int SkipWhitespace(string text, int i)
    {
        while(i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        return i;
    }

    private static int SkipBalanced(string text, int i, char open, char close)
    {
        var depth = 0;
        while(i < text.Length)
        {
            if(text[i] == open)
            {
                depth++;
            }
            else if(text[i] == close)
            {
                depth--;
                if(depth == 0)
                    return i + 1;
            }
            else if(text[i] == '"')
            {
                i++;
                while(i < text.Length && text[i] != '"')
                    i += text[i] == '\\' ? 2 : 1;
            }

            i++;
        }

        return -1;
    }

    private static int LineOf(string text, int index) =>
        text.AsSpan(0, index).Count('\n') + 1;

    private static string Snippet(string text, int index)
    {
        var end = text.IndexOf('\n', index);
        var line = end < 0 ? text[index..] : text[index..end];

        return line.Length > 90 ? line[..90] + "..." : line;
    }
}
