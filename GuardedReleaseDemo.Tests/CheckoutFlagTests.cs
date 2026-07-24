using System.Net.Http.Json;
using System.Text.Json;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server;
using LaunchDarkly.Sdk.Server.Integrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GuardedReleaseDemo.Tests;

// ---------------------------------------------------------------------------
// Response DTO for POST /api/checkout (old-engine path only)
// ---------------------------------------------------------------------------
internal record CheckoutOkResponse(
    string Engine,
    string OrderId,
    long ProcessingMs,
    decimal CartTotal,
    string[] Recommendations
);

// ---------------------------------------------------------------------------
// Factory: boots the real ASP.NET Core app but replaces the LdClient with a
// TestData-backed one so no real LD connection is made and flag variations
// can be controlled per-test.
//
// Minimal-API note: WebApplicationFactory re-runs Program.cs to capture the
// host; configuration is read inside Program.cs *before* ConfigureWebHost
// callbacks fire.  Setting process env vars in the static constructor is the
// canonical way to satisfy startup assertions (e.g. required SDK key) before
// the factory is even instantiated.
// ---------------------------------------------------------------------------
internal class FlagTestFactory : WebApplicationFactory<Program>
{
    private readonly TestData _td = TestData.DataSource();
    private readonly string _variation;

    static FlagTestFactory()
    {
        // These are read by WebApplicationBuilder.CreateBuilder(args) before
        // any ConfigureWebHost override can inject them.
        Environment.SetEnvironmentVariable("LaunchDarkly__SdkKey", "test-sdk-key");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
    }

    public FlagTestFactory(string variation)
    {
        _variation = variation;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        builder.ConfigureServices(services =>
        {
            // Pre-seed the flag BEFORE the client is used.
            _td.Update(_td.Flag("enable-richer-recommendations")
                .ValueForAll(LdValue.Of(_variation)));

            // Remove the real LdClient registered in Program.cs and replace
            // it with a TestData-backed client that never reaches LD servers.
            var existing = services.SingleOrDefault(d => d.ServiceType == typeof(LdClient));
            if (existing != null) services.Remove(existing);

            var ldCfg = LaunchDarkly.Sdk.Server.Configuration.Builder("test-sdk-key")
                .DataSource(_td)
                .Events(Components.NoEvents)
                .StartWaitTime(TimeSpan.Zero)
                .Build();
            services.AddSingleton(new LdClient(ldCfg));
        });
    }
}

// ---------------------------------------------------------------------------
// Flag-path tests for enable-richer-recommendations
//
// T01: control path  → flag returns "control"  → 3-item recommendations (preserved)
// T01: treatment (v1) → flag returns "v1"       → 5-item recommendations (new items)
// ---------------------------------------------------------------------------
public class CheckoutFlagTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static async Task<CheckoutOkResponse> PostCheckout(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/checkout",
            new { userId = "u-flag-test", cartTotal = 49.99m });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CheckoutOkResponse>(JsonOpts);
        Assert.NotNull(body);
        return body!;
    }

    // ------------------------------------------------------------------
    // Control path: flag variation = "control"
    // EnrichCheckout must return the original 3-item set.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Control_Variation_Returns3StandardRecommendations()
    {
        await using var factory = new FlagTestFactory("control");
        var body = await PostCheckout(factory.CreateClient());

        Assert.Equal(3, body.Recommendations.Length);
        Assert.Contains("extended-warranty", body.Recommendations);
        Assert.Contains("gift-wrap", body.Recommendations);
        Assert.Contains("express-shipping", body.Recommendations);
        Assert.DoesNotContain("loyalty-points", body.Recommendations);
        Assert.DoesNotContain("price-match", body.Recommendations);
    }

    // ------------------------------------------------------------------
    // Treatment path: flag variation = "v1"
    // EnrichCheckout must return the enriched 5-item set.
    // ------------------------------------------------------------------
    [Fact]
    public async Task V1_Variation_Returns5EnrichedRecommendations()
    {
        await using var factory = new FlagTestFactory("v1");
        var body = await PostCheckout(factory.CreateClient());

        Assert.Equal(5, body.Recommendations.Length);
        Assert.Contains("extended-warranty", body.Recommendations);
        Assert.Contains("gift-wrap", body.Recommendations);
        Assert.Contains("express-shipping", body.Recommendations);
        Assert.Contains("loyalty-points", body.Recommendations);
        Assert.Contains("price-match", body.Recommendations);
    }

    // ------------------------------------------------------------------
    // Both variations use the old checkout engine (engine = "v1" in the
    // response refers to the checkout engine, not the flag variation).
    // ------------------------------------------------------------------
    [Fact]
    public async Task BothVariations_UseOldCheckoutEngineNotNewFlow()
    {
        await using var controlFactory = new FlagTestFactory("control");
        await using var v1Factory = new FlagTestFactory("v1");

        var controlBody = await PostCheckout(controlFactory.CreateClient());
        var v1Body = await PostCheckout(v1Factory.CreateClient());

        Assert.Equal("v1", controlBody.Engine);
        Assert.Equal("v1", v1Body.Engine);
    }
}
