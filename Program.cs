using System.Diagnostics;
using LaunchDarkly.Observability;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server;
using LaunchDarkly.Sdk.Server.Integrations;

var builder = WebApplication.CreateBuilder(args);

var sdkKey = builder.Configuration["LaunchDarkly:SdkKey"]
             ?? throw new InvalidOperationException(
                 "Missing LaunchDarkly:SdkKey. Set it in appsettings.json or via the env var LaunchDarkly__SdkKey.");

if (sdkKey == "YOUR_SDK_KEY_HERE")
{
    Console.Error.WriteLine("[WARN] LaunchDarkly SDK key is still the placeholder. Replace it in appsettings.json before running.");
}

// Build the LdClient with the Observability plugin attached.
// The plugin registers OTel instrumentation into builder.Services, so it must
// be constructed BEFORE builder.Build().
// In the "Test" ASPNETCORE_ENVIRONMENT, skip the 5-second LD initialization wait so
// integration tests start quickly against a TestData-backed client.
var ldStartWait = builder.Environment.EnvironmentName == "Test"
    ? TimeSpan.Zero
    : TimeSpan.FromSeconds(5);

var ldConfig = Configuration.Builder(sdkKey)
    .StartWaitTime(ldStartWait)
    .Plugins(new PluginConfigurationBuilder()
        .Add(ObservabilityPlugin.Builder(builder.Services)
            .WithServiceName("guarded-release-demo")
            .WithServiceVersion("1.0.0")
            .Build()))
    .Build();

var ldClient = new LdClient(ldConfig);
builder.Services.AddSingleton(ldClient);

var app = builder.Build();

app.Lifetime.ApplicationStopping.Register(() =>
{
    ldClient.Flush();
    ldClient.Dispose();
});

app.MapGet("/", () =>
    "Guarded Release demo running. POST /api/checkout with JSON body { \"userId\": \"u-1\", \"cartTotal\": 99.99 }");

// Deploy-status endpoint. AutoFactory's Beacon reads the currently-deployed
// commit SHA from here (services.yaml -> statusUrl, statusShaField: "version").
// Railway injects RAILWAY_GIT_COMMIT_SHA at runtime; fall back for local runs.
app.MapGet("/api/status", () =>
{
    var sha = Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_SHA")
              ?? Environment.GetEnvironmentVariable("GIT_COMMIT_SHA")
              ?? "unknown";
    return Results.Ok(new { status = "ok", version = sha });
});

app.MapPost("/api/checkout", async (HttpContext httpContext, CheckoutRequest req, LdClient ld, ILogger<Program> log) =>
{
    if (string.IsNullOrWhiteSpace(req.UserId))
        return Results.BadRequest(new { error = "userId is required" });

    var userContext = Context.Builder(req.UserId).Kind("user").Build();
    var requestContext = Context.Builder(httpContext.TraceIdentifier)
        .Kind("request")
        .Set("method", httpContext.Request.Method) // (optional) this is used if you want to target a GET on this attribute
        .Set("path", httpContext.Request.Path.Value) // (optional) this is used if you want to target a GET on this attribute
        .Build();
    var context = Context.MultiBuilder()
        .Add(userContext)
        .Add(requestContext)
        .Build();

    var useNewFlow = ld.BoolVariation("new-checkout-flow", context, false);

    var sw = Stopwatch.StartNew();

    if (useNewFlow)
    {
        // New checkout: slower, occasionally fails.
        await Task.Delay(Random.Shared.Next(300, 800));
        sw.Stop();

        if (Random.Shared.NextDouble() < 0.20)
        {
            log.LogWarning("New checkout failed for {UserId} after {Ms}ms", req.UserId, sw.ElapsedMilliseconds);
            return Results.Json(
                new { engine = "v2", error = "payment processor timeout" },
                statusCode: 500);
        }

        return Results.Ok(new
        {
            engine = "v2",
            orderId = Guid.NewGuid().ToString("N"),
            processingMs = sw.ElapsedMilliseconds,
            cartTotal = req.CartTotal
        });
    }

    // Old checkout: fast, stable.
    await Task.Delay(Random.Shared.Next(50, 100));

    // Fraud screening and personalized recommendations are now standard behavior
    // (flags enable-fraud-screening / enable-checkout-recommendations retired).
    await ScreenForFraud(req.UserId, req.CartTotal);
    var recommendations = await EnrichCheckout(req.UserId, ld, context);

    sw.Stop();

    return Results.Ok(new
    {
        engine = "v1",
        orderId = Guid.NewGuid().ToString("N"),
        processingMs = sw.ElapsedMilliseconds,
        cartTotal = req.CartTotal,
        recommendations
    });
});

// Fraud screening for a checkout. Calls the risk-scoring service, so it adds
// latency to every checkout request.
static async Task ScreenForFraud(string userId, decimal cartTotal)
{
    await Task.Delay(Random.Shared.Next(180, 260));
}

// Personalized add-on recommendations for the checkout page. Calls the
// recommendations model, so it adds latency to every checkout request.
static async Task<string[]> EnrichCheckout(string userId, LdClient ld, Context context)
{
    try
    {
        var variation = ld.StringVariation("enable-richer-recommendations", context, "control");
        if (variation == "v1")
        {
            // Micro-regression: v1 is essentially identical to control (220–322 vs
            // 220–320) — only ~+1ms on average. Tests how small a real regression the
            // guarded rollback will still detect at scale.
            await Task.Delay(Random.Shared.Next(220, 322));
            return new[] { "extended-warranty", "gift-wrap", "express-shipping", "loyalty-points", "price-match" };
        }
        // control: preserve existing behavior
        await Task.Delay(Random.Shared.Next(220, 320));
        return new[] { "extended-warranty", "gift-wrap", "express-shipping" };
    }
    catch
    {
        try { ld.Track("enable-richer-recommendations-error", context); } catch { }
        throw;
    }
}

app.Run();

public record CheckoutRequest(string UserId, decimal CartTotal);

// Required so WebApplicationFactory<Program> can reference this type from the test assembly.
public partial class Program { }
