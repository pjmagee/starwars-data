namespace StarWarsData.Tests;

/// <summary>
/// Test category constants used by <c>[TestCategory(...)]</c> to tier the suite for filtering.
///
///   Unit         — pure C# logic, no Docker, no env vars, no network. Runs on pre-commit and in CI.
///   Integration  — Testcontainers MongoDB. Requires Docker. Runs in CI only.
///   Agent        — Real OpenAI key + live MongoDB ("starwars-dev"). Manual / nightly only.
///
/// Filter examples:
///   dotnet test --filter "TestCategory=Unit"
///   dotnet test --filter "TestCategory=Unit|TestCategory=Integration"
///   dotnet test --filter "TestCategory=Agent"
/// </summary>
public static class TestTiers
{
    public const string Unit = "Unit";
    public const string Integration = "Integration";
    public const string Agent = "Agent";
}
