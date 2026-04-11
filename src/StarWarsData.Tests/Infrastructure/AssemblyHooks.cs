namespace StarWarsData.Tests.Infrastructure;

/// <summary>
/// Single MSTest assembly cleanup hook. Disposes any fixtures that were lazily
/// initialized during the run — unit-only runs touch nothing here, integration runs
/// dispose Testcontainers, and Agent runs additionally clear cached client refs.
/// </summary>
[TestClass]
public static class AssemblyHooks
{
    [AssemblyCleanup]
    public static async Task GlobalCleanup()
    {
        await ApiFixture.DisposeAsync();
        await CheckpointStoreFixture.DisposeAsync();
        await AgentFixture.DisposeAsync();
    }
}
