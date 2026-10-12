using Microsoft.AspNetCore.Mvc;

using SmartX.Api.Dashboard;
using SmartX.Api.ExceptionHandling;
using SmartX.Application.Attachments;
using SmartX.Application.Dashboard;
using SmartX.Application.Realtime;
using SmartX.Application.Sensors;
using SmartX.Application.Telemetry;
using SmartX.Domain.Dashboard;
using SmartX.Infrastructure.Attachments;
using SmartX.Infrastructure.Sensors;
using SmartX.Infrastructure.Telemetry;

const string clientCorsPolicyName = "SmartXClient";

var builder = WebApplication.CreateBuilder(args);

/*
 * Local development normally terminates HTTPS directly in ASP.NET Core.
 *
 * Container deployments may instead terminate external traffic at a reverse
 * proxy. In that case the internal proxy-to-API connection can remain HTTP
 * without causing an unnecessary redirect loop.
 *
 * HTTPS redirection remains enabled by default and must be explicitly
 * disabled by the deployment configuration.
 */
var httpsRedirectionEnabled =
    builder.Configuration.GetValue(
        "HttpsRedirection:Enabled",
        defaultValue: true);

var allowedClientOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>()
    ?? [];

var configuredAttachmentStoragePath =
    builder.Configuration[
        "Attachments:StoragePath"]
    ?? "App_Data/attachments";

var attachmentStoragePath =
    Path.IsPathRooted(configuredAttachmentStoragePath)
        ? Path.GetFullPath(
            configuredAttachmentStoragePath)
        : Path.GetFullPath(
            Path.Combine(
                builder.Environment.ContentRootPath,
                configuredAttachmentStoragePath));

// -------------------------------------------------------------------------
// MVC and automatic validation responses
// -------------------------------------------------------------------------

builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        // [ApiController] automatically short-circuits requests when model
        // binding fails. This includes malformed JSON, invalid JSON types,
        // and other request-body conversion failures.
        //
        // Configure that automatic path explicitly so it follows the same
        // RFC-style ProblemDetails contract as the rest of the Smart-X API.
        options.InvalidModelStateResponseFactory =
            context =>
            {
                var problemDetails =
                    new ValidationProblemDetails(
                        context.ModelState)
                    {
                        Status =
                            StatusCodes.Status400BadRequest,

                        Title =
                            "Request validation failed",

                        Detail =
                            "The request body or one or more request " +
                            "values are invalid.",

                        Instance =
                            context.HttpContext.Request.Path
                    };

                // A trace identifier allows validation responses to be
                // correlated with server-side diagnostics without exposing
                // internal implementation details.
                problemDetails.Extensions["traceId"] =
                    context.HttpContext.TraceIdentifier;

                var result =
                    new BadRequestObjectResult(
                        problemDetails);

                // Validation failures use the standard ProblemDetails media
                // type rather than being returned as ordinary JSON.
                result.ContentTypes.Add(
                    "application/problem+json");

                return result;
            };
    });

builder.Services.AddOpenApi();

// SignalR provides lightweight server-push notifications for the Adaptive
// Anomaly Spotlight. HTTP remains the authoritative state-transfer channel.
builder.Services.AddSignalR();

// -------------------------------------------------------------------------
// API error handling
// -------------------------------------------------------------------------

builder.Services.AddProblemDetails();

builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.Services.AddSingleton<TimeProvider>(
    TimeProvider.System);

// -------------------------------------------------------------------------
// Sensor persistence and Application services
// -------------------------------------------------------------------------

// Sensor state must survive between individual HTTP requests during the
// lifetime of the Part 1 backend process.
builder.Services.AddSingleton<ISensorRepository, InMemorySensorRepository>();

builder.Services.AddScoped<SensorRegistrationService>();

builder.Services.AddScoped<SensorManagementService>();

// Part 2 reads project the existing singleton sensor authority into detached
// transport values. They do not create another registry or own device state.
builder.Services.AddScoped<RealtimeSnapshotService>();

// -------------------------------------------------------------------------
// Telemetry persistence and Application services
// -------------------------------------------------------------------------

// Telemetry remains process-local during Part 1. The repository abstraction
// allows a database or distributed store to replace this implementation in a
// later Smart-X phase without changing controllers or Application services.
builder.Services.AddSingleton<ITelemetryRepository, InMemoryTelemetryRepository>();

builder.Services.AddScoped<SensorTelemetryService>();

// -------------------------------------------------------------------------
// Secure attachment infrastructure
// -------------------------------------------------------------------------

// Cryptographic infrastructure is resolved only when attachment
// functionality is used. Unrelated endpoints remain independent while
// attachment operations still require a valid encryption key.
builder.Services.AddSingleton<IAttachmentCipher>(
    _ => new ChunkedAesGcmAttachmentCipher(
        builder.Configuration["Attachments:EncryptionKeyBase64"]));

/*
 * The anomaly analyzer also needs attachment metadata when evaluating sensor
 * configuration health. Defer concrete store/cipher construction so ordinary
 * health, sensor and telemetry requests do not unexpectedly require the
 * attachment encryption key.
 */
builder.Services.AddSingleton<ISensorAttachmentStore>(
    serviceProvider =>
        new DeferredSensorAttachmentStore(
            () =>
                new FileSystemSensorAttachmentStore(
                    attachmentStoragePath,
                    serviceProvider.GetRequiredService<IAttachmentCipher>())));

builder.Services.AddScoped<SensorAttachmentService>();

// Adds the cross-aggregate rule that attachment operations may target only
// sensor profiles registered through ISensorRepository.
builder.Services.AddScoped<RegisteredSensorAttachmentService>();

// -------------------------------------------------------------------------
// Adaptive Anomaly Spotlight
// -------------------------------------------------------------------------

/*
 * The dashboard collection represents process-wide operational state and must
 * therefore be shared across HTTP requests and SignalR connections.
 */
builder.Services.AddSingleton<DashboardIssueCollection>();

/*
 * Policy values are immutable for the lifetime of the Part 1 application.
 */
builder.Services.AddSingleton<AdaptiveAnomalyPolicy>();

/*
 * Analysis owns thread-safe process-level recovery counters and coordinates
 * singleton repository abstractions, so one shared analyzer instance is used.
 */
builder.Services.AddSingleton<AdaptiveAnomalyAnalysisService>();

builder.Services.AddSingleton<DashboardIssueService>();

builder.Services.AddSingleton<DashboardRealtimeNotifier>();

/*
 * The coordinator depends on scoped application services used by individual
 * requests while delegating persistent issue state to singleton components.
 */
builder.Services.AddScoped<DashboardEventCoordinator>();

// -------------------------------------------------------------------------
// Browser client access
// -------------------------------------------------------------------------

builder.Services.AddCors(options =>
{
    options.AddPolicy(
        clientCorsPolicyName,
        policy =>
        {
            // Production remains closed to browser origins unless explicitly
            // configured.
            if (allowedClientOrigins.Length > 0)
            {
                policy
                    .WithOrigins(allowedClientOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            }
        });
});

var app = builder.Build();

// Expected Application and Domain failures are translated centrally into
// RFC-style ProblemDetails responses.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

/*
 * Preserve HTTPS redirection for normal Smart-X execution while allowing a
 * trusted deployment boundary, such as the Docker Nginx reverse proxy, to
 * perform transport termination instead.
 */
if (httpsRedirectionEnabled)
{
    app.UseHttpsRedirection();
}

app.UseCors(clientCorsPolicyName);

app.UseAuthorization();

app.MapControllers();

/*
 * Connected React clients subscribe here for lightweight dashboard
 * invalidation events and retrieve authoritative issue state over HTTP.
 */
app.MapHub<DashboardHub>("/hubs/dashboard");

app.Run();

// Expose the generated entry point to the integration-test project.
public partial class Program
{
}