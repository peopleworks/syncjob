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
            Skip = "DPAPI is a Windows service and this is not Windows";
    }
}
