using System.Diagnostics;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server;
using LaunchDarkly.Sdk.Server.Telemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var sdkKey = builder.Configuration["LaunchDarkly:SdkKey"]
             ?? throw new InvalidOperationException(
                 "Missing LaunchDarkly:SdkKey. Set it in appsettings.json or via the env var LaunchDarkly__SdkKey.");

if (sdkKey == "YOUR_SDK_KEY_HERE")
{
    Console.Error.WriteLine("[WARN] LaunchDarkly SDK key is still the placeholder. Replace it in appsettings.json before running.");
}

const string ServiceName = "guarded-release-demo";
const string ServiceVersion = "1.0.0";

// Exporting straight to LaunchDarkly's hosted OTLP collector — no self-hosted
// collector in the path. Swap this for a local collector URL (e.g.
// http://otel-collector:4318) if you need any of:
//   - restricted egress (only the collector talks to the internet)
//   - central sampling / redaction / batching before data leaves your network
//   - fan-out to multiple backends (LD + Datadog/Honeycomb/etc.)
//   - aggregating non-SDK sources (infra metrics, logs, other languages)
// In that case, move the LD endpoint + SDK-key header into the collector's
// exporters: block instead of configuring them here.
const string OtlpEndpoint = "https://otel.observability.app.launchdarkly.com:4318";

void ConfigureOtlp(OtlpExporterOptions opts, string signalPath)
{
    opts.Endpoint = new Uri($"{OtlpEndpoint}/v1/{signalPath}");
    opts.Protocol = OtlpExportProtocol.HttpProtobuf;
}

// LD's collector identifies the project from the `launchdarkly.project_id`
// resource attribute (set to the SDK key) — no auth header.
// https://launchdarkly.com/docs/sdk/features/opentelemetry-server-side#setting-resource-attributes
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService(serviceName: ServiceName, serviceVersion: ServiceVersion)
        .AddAttributes(new[] { new KeyValuePair<string, object>("launchdarkly.project_id", sdkKey) }))
    .WithTracing(t => t
        .AddSource(TracingHook.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o => ConfigureOtlp(o, "traces")))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter(o => ConfigureOtlp(o, "metrics")));

builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
    o.SetResourceBuilder(ResourceBuilder.CreateDefault()
        .AddService(serviceName: ServiceName, serviceVersion: ServiceVersion)
        .AddAttributes(new[] { new KeyValuePair<string, object>("launchdarkly.project_id", sdkKey) }));
    o.AddOtlpExporter(e => ConfigureOtlp(e, "logs"));
});

// TracingHook emits a `feature_flag` span event on every variation call with the
// `feature_flag.context.key` attribute. LD's collector drops traces that don't
// contain at least one such event, so this is required for OTel-SDK-only setups.
var ldConfig = Configuration.Builder(sdkKey)
    .Hooks(Components.Hooks().Add(TracingHook.Builder().Build()))
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

app.MapPost("/api/checkout", async (HttpContext httpContext, CheckoutRequest req, LdClient ld, ILogger<Program> log) =>
{
    if (string.IsNullOrWhiteSpace(req.UserId))
        return Results.BadRequest(new { error = "userId is required" });

    var userContext = Context.Builder(req.UserId).Kind("user").Build();
    var requestContext = Context.Builder(httpContext.TraceIdentifier)
        .Kind("request")
        .Set("method", httpContext.Request.Method)
        .Set("path", httpContext.Request.Path.Value)
        .Build();
    var context = Context.MultiBuilder()
        .Add(userContext)
        .Add(requestContext)
        .Build();

    var useNewFlow = ld.BoolVariation("new-checkout-flow", context, false);

    var sw = Stopwatch.StartNew();

    if (useNewFlow)
    {
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

    await Task.Delay(Random.Shared.Next(50, 100));
    sw.Stop();

    return Results.Ok(new
    {
        engine = "v1",
        orderId = Guid.NewGuid().ToString("N"),
        processingMs = sw.ElapsedMilliseconds,
        cartTotal = req.CartTotal
    });
});

app.Run();

public record CheckoutRequest(string UserId, decimal CartTotal);
