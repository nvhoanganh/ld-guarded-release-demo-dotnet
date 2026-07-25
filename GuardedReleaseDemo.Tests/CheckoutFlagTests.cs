using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server;
using LaunchDarkly.Sdk.Server.Integrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using LdConfig = LaunchDarkly.Sdk.Server.Configuration;

namespace GuardedReleaseDemo.Tests;

/// <summary>
/// Behavior tests for the checkout recommendations path (EnrichCheckout).
///
/// The enable-richer-recommendations flag has been retired: EnrichCheckout now
/// unconditionally returns the 7-item extended recommendations set. These tests
/// assert that static behavior.
///
/// Each test creates its own WebApplicationFactory backed by TestData so the
/// remaining flag (new-checkout-flow) is deterministic without touching a real
/// LaunchDarkly project.
///
/// IMPORTANT — startup sequencing
/// --------------------------------
/// WebApplicationFactory uses DeferredHostBuilder which runs Program.<Main>$ inside
/// IHostBuilder.Build(). builder.Configuration is accessed BEFORE Build() returns, so
/// ConfigureAppConfiguration hooks run too late. We therefore set the required env vars
/// in the static constructor so they are present when the entry-point executes:
///   LaunchDarkly__SdkKey      — satisfies the startup null-check
///   ASPNETCORE_ENVIRONMENT    — activates the existing ldStartWait = TimeSpan.Zero path
/// ConfigureTestServices then replaces the real LdClient with a TestData-backed one,
/// which IS applied during Build() — exactly when it is needed.
/// </summary>
public class CheckoutFlagTests
{
    static CheckoutFlagTests()
    {
        // Set before any factory is created so Program.<Main>$ can read them.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
        Environment.SetEnvironmentVariable("LaunchDarkly__SdkKey", "fake-sdk-key-for-tests");
    }

    // ---------------------------------------------------------------
    // Factory helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Creates an in-process test server whose LD client is replaced by a
    /// TestData-backed instance. new-checkout-flow is kept off so requests reach
    /// EnrichCheckout.
    /// </summary>
    private static WebApplicationFactory<Program> BuildFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Remove the real LdClient registered during app startup.
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(LdClient));
                    if (descriptor is not null)
                        services.Remove(descriptor);

                    var td = TestData.DataSource();

                    // Keep new-checkout-flow off so requests reach EnrichCheckout.
                    td.Update(td.Flag("new-checkout-flow")
                        .ValueForAll(LdValue.Of(false)));

                    var testLdConfig = LdConfig.Builder("fake-sdk-key-for-tests")
                        .DataSource(td)
                        .Events(Components.NoEvents)
                        .StartWaitTime(TimeSpan.Zero)
                        .Build();

                    services.AddSingleton(new LdClient(testLdConfig));
                });
            });
    }

    // ---------------------------------------------------------------
    // Recommendations behavior — now static (7-item extended set)
    // ---------------------------------------------------------------

    /// <summary>
    /// EnrichCheckout must return the 7-item extended recommendations array
    /// (extended-warranty, gift-wrap, express-shipping, loyalty-points,
    /// price-match, priority-support, carbon-offset).
    /// </summary>
    [Fact]
    public async Task EnrichCheckout_Returns7Recommendations()
    {
        using var factory = BuildFactory();
        using var client  = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/checkout",
            new { userId = "u-recs-test", cartTotal = 129.99 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var recs = doc.RootElement.GetProperty("recommendations")
                       .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(7, recs.Length);
        Assert.Contains("extended-warranty",  recs);
        Assert.Contains("gift-wrap",          recs);
        Assert.Contains("express-shipping",   recs);
        Assert.Contains("loyalty-points",     recs);
        Assert.Contains("price-match",        recs);
        Assert.Contains("priority-support",   recs);
        Assert.Contains("carbon-offset",      recs);
    }

    // ---------------------------------------------------------------
    // Guard tests (userId validation)
    // ---------------------------------------------------------------

    /// <summary>
    /// A missing userId must return 400 before EnrichCheckout is reached.
    /// </summary>
    [Fact]
    public async Task Checkout_MissingUserId_Returns400()
    {
        using var factory = BuildFactory();
        using var client  = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/checkout",
            new { userId = "", cartTotal = 50.00 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A null userId must return 400 before EnrichCheckout is reached.
    /// </summary>
    [Fact]
    public async Task Checkout_NullUserId_Returns400()
    {
        using var factory = BuildFactory();
        using var client  = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/checkout",
            new { userId = (string?)null, cartTotal = 50.00 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // Integration smoke test — response schema
    // ---------------------------------------------------------------

    /// <summary>
    /// A valid checkout response must include orderId, engine "v1", and
    /// processingMs alongside the 7-item recommendations array.
    /// </summary>
    [Fact]
    public async Task Checkout_ResponseSchemaIsComplete()
    {
        using var factory = BuildFactory();
        using var client  = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/checkout",
            new { userId = "u-schema", cartTotal = 75.00 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.String, root.GetProperty("orderId").ValueKind);
        Assert.Equal("v1",                 root.GetProperty("engine").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("processingMs").ValueKind);
        Assert.Equal(JsonValueKind.Array,  root.GetProperty("recommendations").ValueKind);
        Assert.Equal(7, root.GetProperty("recommendations").GetArrayLength());
    }

    // ---------------------------------------------------------------
    // Telemetry: Stopwatch + richer-rec-latency track
    // ---------------------------------------------------------------

    /// <summary>
    /// The Stopwatch + ld.Track("richer-rec-latency", ...) telemetry path is
    /// guarded by try/catch, so it must never prevent a successful 200 response
    /// with the 7-item array.
    /// </summary>
    [Fact]
    public async Task EnrichCheckout_StopwatchTelemetry_DoesNotAffectResponse()
    {
        using var factory = BuildFactory();
        using var client  = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/checkout",
            new { userId = "u-telemetry", cartTotal = 55.00 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var recs = doc.RootElement.GetProperty("recommendations")
                       .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(7, recs.Length);
        Assert.Contains("priority-support", recs);
        Assert.Contains("carbon-offset",    recs);
    }

    // ---------------------------------------------------------------
    // Error path: outer catch re-throws (tracks richer-rec-error)
    // ---------------------------------------------------------------

    /// <summary>
    /// The outer catch in EnrichCheckout always re-throws, so a normal request
    /// must yield a 200 (the error-track path is only reached on an actual
    /// exception). The Track call itself is guarded, so it can never swallow the
    /// response.
    /// </summary>
    [Fact]
    public async Task EnrichCheckout_ErrorCatchDoesNotSwallowResponse()
    {
        using var factory = BuildFactory();
        using var client  = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/checkout",
            new { userId = "u-error-path", cartTotal = 10.00 });

        // A 200 confirms the happy-path catch did NOT fire;
        // the error-track path is only reached on an actual exception.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array,
            doc.RootElement.GetProperty("recommendations").ValueKind);
    }
}
