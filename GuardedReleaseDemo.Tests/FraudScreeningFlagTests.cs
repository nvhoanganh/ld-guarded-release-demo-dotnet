using System.Net;
using System.Net.Http.Json;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server;
using LaunchDarkly.Sdk.Server.Integrations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GuardedReleaseDemo.Tests;

/// <summary>
/// Flag-path tests for the <c>enable-fraud-screening</c> feature flag (key: enable-fraud-screening).
///
/// Flag is STRING MULTIVARIATE with two relevant values:
///   "v1"      — ScreenForFraud() is invoked (new behavior, this PR)
///   "control" — ScreenForFraud() is skipped (existing behavior preserved)
///
/// Each test creates an isolated <see cref="WebApplicationFactory{TEntryPoint}"/> whose
/// <see cref="LdClient"/> is replaced with a <see cref="TestData"/>-backed client so no
/// network calls are made to LaunchDarkly and flag values are deterministic.
/// </summary>
public class FraudScreeningFlagTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns an <see cref="HttpClient"/> whose LdClient always returns
    /// <paramref name="fraudVariation"/> for the enable-fraud-screening flag.
    /// All other flags are held at their fail-safe defaults so they do not
    /// interfere with fraud-flag assertions.
    /// </summary>
    private static HttpClient CreateClient(string fraudVariation)
    {   
        // For WebApplicationBuilder-based minimal APIs, ConfigureAppConfiguration callbacks
        // run too late — Program.cs reads builder.Configuration before the hook fires.
        // Setting the env var at process level ensures IConfiguration sees the key in time.
        Environment.SetEnvironmentVariable("LaunchDarkly__SdkKey", "sdk-test-fake-key");
        // "Test" environment triggers StartWaitTime(TimeSpan.Zero) in Program.cs so the
        // startup LdClient (replaced below) doesn't block on LD network initialisation.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");

        // Build a real LdClient backed by TestData — it never touches LD's network.
        var testData = TestData.DataSource();

        // Keep new-checkout-flow = false so requests always flow through the
        // old-checkout branch where the fraud-screening flag is evaluated.
        testData.Update(testData.Flag("new-checkout-flow")
            .BooleanFlag()
            .VariationForAll(false));

        // Pin the fraud flag to the requested variation.
        testData.Update(testData.Flag("enable-fraud-screening")
            .ValueForAll(LdValue.Of(fraudVariation)));

        var testLdConfig = Configuration.Builder("sdk-test-fake-key")
            .DataSource(testData)
            .Events(Components.NoEvents)
            .Build();
        var testLdClient = new LdClient(testLdConfig);

        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.ConfigureServices(services =>
                {
                    // Remove the LdClient that Program.cs created at startup and
                    // inject our TestData-backed client. The endpoint handler
                    // receives its LdClient via DI, so it will use this one.
                    var existing = services.SingleOrDefault(
                        d => d.ServiceType == typeof(LdClient));
                    if (existing is not null)
                        services.Remove(existing);

                    services.AddSingleton(testLdClient);
                });
            });

        return factory.CreateClient();
    }

    private static readonly object CheckoutPayload = new { userId = "u-001", cartTotal = 99.99 };
    private static readonly object EmptyUserPayload = new { userId = "", cartTotal = 50m };

    // -----------------------------------------------------------------------
    // T01 — treatment path (v1): ScreenForFraud is called
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FraudFlag_V1_CheckoutReturns200WithValidBody()
    {
        // Arrange
        var client = CreateClient("v1");

        // Act
        var response = await client.PostAsJsonAsync("/api/checkout", CheckoutPayload);

        // Assert: request succeeds and response shape matches old-checkout path.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CheckoutResponse>();
        Assert.NotNull(body);
        Assert.Equal("v1", body!.Engine);
        Assert.NotEmpty(body.OrderId);
    }

    [Fact]
    public async Task FraudFlag_V1_ProcessingMsReflectsScreenForFraudDelay()
    {
        // ScreenForFraud adds Task.Delay(Random.Shared.Next(180, 260)).
        // So processingMs on the v1 path must always be >= 180 ms.
        var client = CreateClient("v1");

        var response = await client.PostAsJsonAsync("/api/checkout",
            new { userId = "u-v1-latency", cartTotal = 200m });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CheckoutResponse>();
        Assert.NotNull(body);
        Assert.True(body!.ProcessingMs >= 180,
            $"v1 path: expected processingMs >= 180 ms (fraud check minimum), got {body.ProcessingMs} ms");
    }

    // -----------------------------------------------------------------------
    // T01 — control path: ScreenForFraud is skipped
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FraudFlag_Control_CheckoutReturns200WithValidBody()
    {
        // Arrange
        var client = CreateClient("control");

        // Act
        var response = await client.PostAsJsonAsync("/api/checkout", CheckoutPayload);

        // Assert: control path preserves existing checkout behavior.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CheckoutResponse>();
        Assert.NotNull(body);
        Assert.Equal("v1", body!.Engine);
        Assert.NotEmpty(body.OrderId);
    }

    [Fact]
    public async Task FraudFlag_Control_ProcessingMsExcludesScreenForFraudDelay()
    {
        // On the control path, only the base delay fires: Task.Delay(Next(50, 100)).
        // processingMs must be well below the fraud-check minimum of 180 ms.
        // We use 150 ms as the ceiling to absorb any runtime overhead.
        var client = CreateClient("control");

        var response = await client.PostAsJsonAsync("/api/checkout",
            new { userId = "u-control-latency", cartTotal = 200m });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CheckoutResponse>();
        Assert.NotNull(body);
        Assert.True(body!.ProcessingMs < 150,
            $"control path: expected processingMs < 150 ms (no fraud check), got {body.ProcessingMs} ms");
    }

    // -----------------------------------------------------------------------
    // T12 — paired guard: userId validation fires before flag evaluation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FraudFlag_EmptyUserId_Returns400OnV1Path()
    {
        var client = CreateClient("v1");
        var response = await client.PostAsJsonAsync("/api/checkout", EmptyUserPayload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FraudFlag_EmptyUserId_Returns400OnControlPath()
    {
        var client = CreateClient("control");
        var response = await client.PostAsJsonAsync("/api/checkout", EmptyUserPayload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

// Matches the JSON response produced by the old-checkout branch in Program.cs.
file record CheckoutResponse(
    string Engine,
    string OrderId,
    long ProcessingMs,
    decimal CartTotal);
