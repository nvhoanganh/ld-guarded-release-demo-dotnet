using System.Diagnostics;
using LaunchDarkly.Observability;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server;
using LaunchDarkly.Sdk.Server.Integrations;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

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
var ldConfig = Configuration.Builder(sdkKey)
    .Plugins(new PluginConfigurationBuilder()
        .Add(ObservabilityPlugin.Builder(builder.Services)
            .WithServiceName("guarded-release-demo")
            .WithServiceVersion("1.0.0")
            // Mirror every metric to the console so we can verify what's being emitted.
            .WithExtendedMeterConfiguration(m => m.AddConsoleExporter())
            .WithExtendedTracingConfig(t => t.AddConsoleExporter())
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

app.MapPost("/api/checkout", async (CheckoutRequest req, LdClient ld, ILogger<Program> log) =>
{
    if (string.IsNullOrWhiteSpace(req.UserId))
        return Results.BadRequest(new { error = "userId is required" });

    var context = Context.Builder(req.UserId).Kind("user").Build();
    var useNewFlow = ld.BoolVariation("new-checkout-flow", context, false);

    var sw = Stopwatch.StartNew();
    IResult result;

    try
    {
        if (useNewFlow)
        {
            // New checkout: slower, occasionally fails.
            await Task.Delay(Random.Shared.Next(300, 800));
            if (Random.Shared.NextDouble() < 0.20)
            {
                sw.Stop();
                log.LogWarning("New checkout failed for {UserId} after {Ms}ms", req.UserId, sw.ElapsedMilliseconds);
                result = Results.Json(
                    new { engine = "v2", error = "payment processor timeout" },
                    statusCode: 500);
            }
            else
            {
                sw.Stop();
                result = Results.Ok(new
                {
                    engine = "v2",
                    orderId = Guid.NewGuid().ToString("N"),
                    processingMs = sw.ElapsedMilliseconds,
                    cartTotal = req.CartTotal
                });
            }
        }
        else
        {
            // Old checkout: fast, stable.
            await Task.Delay(Random.Shared.Next(50, 100));
            sw.Stop();
            result = Results.Ok(new
            {
                engine = "v1",
                orderId = Guid.NewGuid().ToString("N"),
                processingMs = sw.ElapsedMilliseconds,
                cartTotal = req.CartTotal
            });
        }
    }
    finally
    {
        if (sw.IsRunning) sw.Stop();

        // Custom LD metric: numeric checkout latency. LD's Guarded Release can use
        // this directly (lower is better).
        ld.Track("checkout-latency", context, LdValue.Null, sw.ElapsedMilliseconds);
    }

    return result;
});

app.Run();

public record CheckoutRequest(string UserId, decimal CartTotal);
