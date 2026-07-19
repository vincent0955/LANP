using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GameDashboard.Tests.Integration;

/// <summary>
/// Integration tests require a real, reachable Docker engine (Docker Desktop or
/// the bundled runtime locally, a native dockerd in CI). They are tagged with
/// [Trait("Category", "Integration")] and excluded from the default `dotnet test`
/// run — see the README for the exact filter — so that normal unit-test runs never
/// require an engine to be running.
///
/// To run integration tests explicitly:
///   dotnet test --filter "Category=Integration"
///
/// To run everything except integration tests (the default developer workflow):
///   dotnet test --filter "Category!=Integration"
///
/// See design.md → Testing Strategy: integration tests; tasks.md → Task 10.1.
/// </summary>
public static class IntegrationTestCollection
{
    public const string Name = "Integration";
}

/// <summary>
/// Shared fixture: boots the real ASP.NET Core app (via WebApplicationFactory) once
/// for all integration tests in the collection, pointed at whatever Docker engine
/// the client factory's endpoint probing resolves to. Tests are responsible for
/// their own resource cleanup (each test deploys uniquely-named, disposable servers).
/// </summary>
public sealed class IntegrationTestFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>
    /// Matches the JsonSerializerOptions the app itself registers in Program.cs
    /// (JsonStringEnumConverter) — HttpClient.ReadFromJsonAsync uses its own
    /// default options otherwise and will fail to parse enum-as-string fields
    /// like ServerDetail.Status.
    /// </summary>
    public static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public HttpClient Client { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Client = CreateClient();
        return Task.CompletedTask;
    }

    public new Task DisposeAsync()
    {
        Client.Dispose();
        return Task.CompletedTask;
    }
}

[CollectionDefinition(IntegrationTestCollection.Name)]
public sealed class IntegrationTestCollectionDefinition : ICollectionFixture<IntegrationTestFixture>
{
}
