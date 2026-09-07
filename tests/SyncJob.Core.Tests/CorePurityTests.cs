using System.Reflection;

namespace SyncJob.Core.Tests;

/// <summary>
/// The one rule the Core has to keep to be worth extracting at all, enforced by a test
/// rather than by good intentions.
/// <para>
/// Three consumers are meant to share this engine: the SyncJob CLI, the Windows
/// service, and later SqlArchive and the DataSync console. That only works while the
/// Core stays free of the things that belong to a particular surface - a console
/// renderer, a configuration store. The moment one of them is referenced here, the
/// other consumers inherit it, and the fourth one refuses to.
/// </para>
/// </summary>
public sealed class CorePurityTests
{
    /// <summary>
    /// Loaded from the file rather than through a type, so this holds while the Core is
    /// still being filled in and has no type to hang a <c>typeof</c> on.
    /// </summary>
    private static Assembly Core =>
        Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "SyncJob.Core.dll"));

    [Theory]
    [InlineData("Spectre.Console", "a console renderer: the Core must not know it is being watched")]
    [InlineData("Microsoft.Data.Sqlite", "a configuration store: the Core takes objects, the surfaces bring the storage")]
    [InlineData("Microsoft.Extensions.Hosting.WindowsServices", "a hosting model: that belongs to the service, not to the engine")]
    public void TheCoreDoesNotReference(string assemblyName, string why)
    {
        var referenced = Core.GetReferencedAssemblies().Select(x => x.Name).ToList();

        Assert.False(
            referenced.Contains(assemblyName, StringComparer.OrdinalIgnoreCase),
            $"SyncJob.Core references {assemblyName}, which is {why}. " +
            $"Referenced: {string.Join(", ", referenced)}");
    }
}
