using System.Runtime.InteropServices;

namespace SyncJob.IntegrationTests;

/// <summary>
/// A fact that needs DPAPI, which is a Windows service.
/// <para>
/// The same shape as <see cref="LiveFactAttribute"/> and for the same reason: CI builds
/// on Linux as well as Windows, and a test that cannot run there should say so rather
/// than fail. Nothing is lost by skipping - the SQLite catalog these secrets belong to
/// is a Windows surface end to end.
/// </para>
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = NotWindows;
    }

    internal const string NotWindows = "DPAPI is a Windows service and this is not Windows";
}

/// <summary>
/// A fact that needs both a real SQL Server and Windows: the surfaces whose
/// configuration lives in the SQLite catalog, because the catalog's credentials are
/// protected with DPAPI and reading one on Linux throws
/// <c>PlatformNotSupportedException</c> before the test reaches its first assertion.
/// <para>
/// Both conditions, not either. CI builds on Linux and Windows and runs its live tests
/// on Linux, so a test that needs the one thing Linux cannot do has to say so - and the
/// engine's own live tests, which need no DPAPI, must keep running there.
/// </para>
/// </summary>
public sealed class WindowsLiveFactAttribute : FactAttribute
{
    public WindowsLiveFactAttribute()
    {
        if(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = WindowsFactAttribute.NotWindows;
        else if(!SqlServerFixture.IsConfigured)
            Skip = $"{SqlServerFixture.ConnectionEnvironmentVariable} is not set";
    }
}
