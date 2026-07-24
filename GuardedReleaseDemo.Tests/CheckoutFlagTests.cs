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
/// Flag-path tests for the enable-richer-recommendations flag (v1 / control).
///
/// Each test creates its own WebApplicationFactory backed by TestData so flag
/// variations are fully deterministic without touching a real LaunchDarkly project.
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
    /// TestData-backed instance returning <paramref name="variation"/> for
    /// "enable-richer-recommendations" and false for "new-checkout-flow".
    /// </summary>
    private static WebApplicationFactory<Program> BuildFactory(string variation)
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

                    // Flag under test: deterministic variation.
                    td.Update(td.Flag("enable-richer-recommendations")
                        .ValueForAll(LdValue.Of(variation)));

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
    // T01 — treatment path: v1
    // ---------------------------------------------------------------

    /// <summary>
    /// When the flag returns "v1", EnrichCheckout must return the 5-item
    /// extended recommendations array (loyalty-points, price-match included).
    /// </summary>
    [Fact]
    public async Task EnrichCheckout_V1Variation_Returns5Recommendations()
    {
        using var factory = BuildFactory("v1");
        using var client  = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/checkout",
            new { userId = "u-v1-test", cartTotal = 99.99 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var recs = doc.RootElement.GetProperty("recommendations")
                       .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(5, recs.Length);
        Assert.Contains("loyalty-points", recs);
        Assert.Contains("price-match", recs);
        Assert.Contains("extended-warranty", recs);
        Assert.Contains("gift-wrap", recs);
        Assert.Contains("express-shipping", recs);
    }

    // ---------------------------------------------------------------
    // T01 — control path
    // ---------------------------------------------------------------

    /// <summary>
    /// When the flag returns "control", EnrichCheckout must return the 3-item
    /// baseline recommendations array (no loyalty-points or price-match).
    /// </summary>
    [Fact]
    public async Task EnrichCheckout_ControlVariation_Returns3Recommendations()
    {
        using var factory = BuildFactory("control");
        using var client  = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/checkout",
            new { userId = "u-ctrl-test", cartTotal = 49.99 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var recs = doc.RootElement.GetProperty("recommendations")
                       .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(3, recs.Length);
        Assert.DoesNotContain("loyalty-points", recs);
        Assert.DoesNotContain("price-match", recs);
        Assert.Contains("extended-warranty", recs);
        Assert.Contains("gift-wrap", recs);
        Assert.Contains("express-shipping", recs);
    }

    // ---------------------------------------------------------------
    // T12 — v1 and control are mutually exclusive (array size invariant)
    // ---------------------------------------------------------------

    /// <summary>
    /// The v1 path must return MORE recommendations than the control path;
    /// this guards against accidental merging of variation branches.
    /// </summary>
    [Fact]
    public async Task EnrichCheckout_V1ReturnsMoreItemsThanControl()
    {
        using var v1Factory      = BuildFactory("v1");
        using var controlFactory = BuildFactory("control");
        using var v1Client       = v1Factory.CreateClient();
        using var controlClient  = controlFactory.CreateClient();

        var body = new { userId = "u-cmp", cartTotal = 20.00 };

        var v1Response =
            await v1Client.PostAsJsonAsync("/api/checkout", body);
        var controlResponse =
            await controlClient.PostAsJsonAsync("/api/checkout", body);

        using var v1Doc =
            JsonDocument.Parse(await v1Response.Content.ReadAsStringAsync());
        using var controlDoc =
            JsonDocument.Parse(await controlResponse.Content.ReadAsStringAsync());

        var v1Count =
            v1Doc.RootElement.GetProperty("recommendations").GetArrayLength();
        var controlCount =
            controlDoc.RootElement.GetProperty("recommendations").GetArrayLength();

        Assert.True(v1Count > controlCount,
            $"Expected v1 recommendation count ({v1Count}) > control ({controlCount})");
    }

    // ---------------------------------------------------------------
    // Guard tests (userId validation) — both variations share this path
    // ---------------------------------------------------------------

    /// <summary>
    /// A missing userId must return 400 before flag evaluation is reached.
    /// </summary>
    [Fact]
    public async Task Checkout_MissingUserId_Returns400()
    {
        using var factory = BuildFactory("control");
        using var client  = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/checkout",
            new { userId = "", cartTotal = 50.00 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A null userId must return 400 before flag evaluation is reached.
    /// </summary>
    [Fact]
    public async Task Checkout_NullUserId_Returns400()
    {
        using var factory = BuildFactory("v1");
        using var client  = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/checkout",
            new { userId = (string?)null, cartTotal = 50.00 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------
    // Integration smoke tests — response schema
    // ---------------------------------------------------------------

    /// <summary>
    /// A valid v1 checkout response must include orderId, engine "v1", and
    /// processingMs alongside the recommendations array.
    /// </summary>
    [Fact]
    public async Task Checkout_V1Variation_ResponseSchemaIsComplete()
    {
        using var factory = BuildFactory("v1");
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
    }

    /// <summary>
    /// A valid control checkout response must also include orderId, engine "v1",
    /// and processingMs — verifying the control arm is not broken by the PR.
    /// </summary>
    [Fact]
    public async Task Checkout_ControlVariation_ResponseSchemaIsComplete()
    {
        using var factory = BuildFactory("control");
        using var client  = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/checkout",
            new { userId = "u-schema-ctrl", cartTotal = 75.00 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.String, root.GetProperty("orderId").ValueKind);
        Assert.Equal("v1",                 root.GetProperty("engine").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("processingMs").ValueKind);
        Assert.Equal(JsonValueKind.Array,  root.GetProperty("recommendations").ValueKind);
    }
}
