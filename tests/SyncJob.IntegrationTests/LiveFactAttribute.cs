namespace SyncJob.IntegrationTests;

/// <summary>
/// A fact that needs a real SQL Server. Without <c>SYNCJOB_TEST_CONN</c> it is skipped
/// with the reason printed, rather than failing - so a contributor without a server
/// still gets a green unit-test run, and CI, which does set the variable, still gets
/// the coverage.
/// <para>
/// Skipping is decided once, when the attribute is constructed, so the test list reads
/// the same whether or not a server is there.
/// </para>
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if(!SqlServerFixture.IsConfigured)
            Skip = $"{SqlServerFixture.ConnectionEnvironmentVariable} is not set";
    }
}
