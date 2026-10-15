using Microsoft.AspNetCore.Mvc;

using SmartX.Api.Dashboard;
using SmartX.Api.ExceptionHandling;
using SmartX.Api.Realtime;
using SmartX.Application.Attachments;
using SmartX.Application.Commands;
using SmartX.Application.Dashboard;
using SmartX.Application.Interactions;
using SmartX.Application.Realtime;
using SmartX.Application.Sensors;
using SmartX.Application.Telemetry;
using SmartX.Domain.Dashboard;
using SmartX.Infrastructure.Attachments;
using SmartX.Infrastructure.Commands;
using SmartX.Infrastructure.Interactions;
using SmartX.Infrastructure.Realtime;
using SmartX.Infrastructure.Sensors;
using SmartX.Infrastructure.Telemetry;

const string clientCorsPolicyName = "SmartXClient";

var builder = WebApplication.CreateBuilder(args);

/*
 * HTTPS is enabled for ordinary application execution.
 *
 * Trusted Docker deployments can terminate external HTTPS at the
 * reverse proxy while using the existing private API connection.
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
    builder.Configuration["Attachments:StoragePath"]
    ?? "App_Data/attachments";

var attachmentStoragePath =
    Path.IsPathRooted(configuredAttachmentStoragePath)
        ? Path.GetFullPath(configuredAttachmentStoragePath)
        : Path.GetFullPath(
            Path.Combine(
                builder.Environment.ContentRootPath,
                configuredAttachmentStoragePath));

// -------------------------------------------------------------------------
// MVC and automatic request validation
// -------------------------------------------------------------------------

builder.Services
    .AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
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

                problemDetails.Extensions["traceId"] =
                    context.HttpContext.TraceIdentifier;

                var result =
                    new BadRequestObjectResult(
                        problemDetails);

                result.ContentTypes.Add(
                    "application/problem+json");

                return result;
            };
    });

builder.Services.AddOpenApi();

// Both existing hubs remain available. Their notifications invalidate
// client snapshots; HTTP continues to own authoritative state.
builder.Services.AddSignalR();

// -------------------------------------------------------------------------
// Exception handling and deterministic time
// -------------------------------------------------------------------------

builder.Services.AddProblemDetails();

builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.Services.AddSingleton<TimeProvider>(
    TimeProvider.System);

// -------------------------------------------------------------------------
// Authoritative registered-device persistence
// -------------------------------------------------------------------------

// The P2-02 canonical Dictionary repository remains the only owner of
// registered-device metadata, identity, category, and operational status.
builder.Services.AddSingleton<
    ISensorRepository,
    InMemorySensorRepository>();

builder.Services.AddScoped<SensorRegistrationService>();

builder.Services.AddScoped<SensorManagementService>();

// -------------------------------------------------------------------------
// P2-05 chronological operational history
// -------------------------------------------------------------------------

// This singleton owns one real per-device
// SortedDictionary<DateTimeOffset, List<HistoryEntry>>.
//
// Its retention and query limits are independent of Part 1 recent
// telemetry storage and P2-03 processing diagnostic history.
builder.Services.AddSingleton<
    IRealtimeHistoryStore,
    InMemoryRealtimeHistoryStore>();

// The recorder receives successful outcomes from the existing worker
// processor and command service. It never replays those operations.
builder.Services.AddSingleton<OperationalHistoryRecorder>();

// Registration checks, validated limits, chronological ordering,
// and transport mapping are implemented in the scoped read service.
builder.Services.AddScoped<RealtimeHistoryService>();

// -------------------------------------------------------------------------
// P2-04 manual actuator commands and genuine Stack-backed Undo
// -------------------------------------------------------------------------

// One process-wide store owns simulated commanded state, per-device
// Stack<CommandRecord> history, and permanent command audit.
//
// It does not duplicate the registered sensor repository.
builder.Services.AddSingleton<
    IActuatorCommandStore,
    InMemoryActuatorCommandStore>();

// Commands and Undo are validated through the same application service.
// DI now also provides the shared operational history recorder.
// One bounded process-local interaction stream; no personal operator profiles.
builder.Services.AddSingleton<IOperatorInteractionStore, InMemoryOperatorInteractionStore>();
builder.Services.AddScoped<OperatorInteractionService>();
builder.Services.AddScoped<ActuatorCommandService>();

// -------------------------------------------------------------------------
// Part 2 real-time snapshots and telemetry processing
// -------------------------------------------------------------------------

// Preserves P2-02/P2-03 snapshots and P2-04 commanded-state projection.
builder.Services.AddScoped<RealtimeSnapshotService>();
// Read-only P2-08 discovery reuses the Dictionary-backed registry and
// the original HashSet-backed dashboard issues; it owns no separate index.
builder.Services.AddScoped<RealtimeDeviceSearchService>();

builder.Services.AddSingleton<
    ITelemetryRepository,
    InMemoryTelemetryRepository>();

// Process-wide FIFO and critical-priority buffers, worker diagnostics,
// and actual processed-device observations remain unchanged.
builder.Services.AddSingleton<
    ITelemetryWorkBuffer,
    InMemoryTelemetryWorkBuffer>();

builder.Services.AddSingleton<
    ITelemetryProcessingStore,
    InMemoryTelemetryProcessingStore>();

// The existing scoped processor now records a chronological event only
// after its normal state.Complete() call succeeds.
builder.Services.AddScoped<
    ITelemetryProcessor,
    TelemetryProcessingService>();

builder.Services.AddSingleton<
    IRealtimeNotifier,
    RealtimeNotifier>();

// The worker still consumes through a fresh operation scope for each
// dispatch; no request cancellation token is used for accepted work.
builder.Services.AddHostedService<TelemetryProcessingWorker>();

builder.Services.AddSingleton<TelemetryIngestionGate>();

builder.Services.AddSingleton<TelemetryAnomalyClassifier>();

builder.Services.AddScoped<
    ITelemetryAcceptanceObserver,
    DashboardTelemetryAcceptanceObserver>();

builder.Services.AddScoped<TelemetryAcceptanceService>();

builder.Services.AddScoped<SensorTelemetryService>();

// -------------------------------------------------------------------------
// Secure attachment infrastructure
// -------------------------------------------------------------------------

// Preserve the existing encryption boundary. The attachment cipher and
// encrypted filesystem storage are resolved only when needed.
builder.Services.AddSingleton<IAttachmentCipher>(
    _ => new ChunkedAesGcmAttachmentCipher(
        builder.Configuration["Attachments:EncryptionKeyBase64"]));

builder.Services.AddSingleton<ISensorAttachmentStore>(
    serviceProvider =>
        new DeferredSensorAttachmentStore(
            () =>
                new FileSystemSensorAttachmentStore(
                    attachmentStoragePath,
                    serviceProvider.GetRequiredService<IAttachmentCipher>())));

builder.Services.AddScoped<SensorAttachmentService>();

builder.Services.AddScoped<RegisteredSensorAttachmentService>();

// -------------------------------------------------------------------------
// Part 1 Adaptive Anomaly Spotlight
// -------------------------------------------------------------------------

// Existing process-wide issue state, anomaly policy and recovery
// behaviour remain authoritative and are not replayed by the worker.
builder.Services.AddSingleton<DashboardIssueCollection>();

builder.Services.AddSingleton<AdaptiveAnomalyPolicy>();

builder.Services.AddSingleton<AdaptiveAnomalyAnalysisService>();

builder.Services.AddSingleton<DashboardIssueService>();

builder.Services.AddSingleton<DashboardRealtimeNotifier>();

builder.Services.AddScoped<DashboardEventCoordinator>();

// -------------------------------------------------------------------------
// CORS and browser-client access
// -------------------------------------------------------------------------

builder.Services.AddCors(options =>
{
    options.AddPolicy(
        clientCorsPolicyName,
        policy =>
        {
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

// -------------------------------------------------------------------------
// Request pipeline
// -------------------------------------------------------------------------

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (httpsRedirectionEnabled)
{
    app.UseHttpsRedirection();
}

app.UseCors(clientCorsPolicyName);

app.UseAuthorization();

app.MapControllers();

// Existing SignalR hubs remain intact. Commands and telemetry continue
// to notify clients through their established HTTP-backed workflows.
app.MapHub<DashboardHub>("/hubs/dashboard");

app.MapHub<RealtimeHub>("/hubs/realtime");

app.Run();

// Exposes the host entry point to ASP.NET Core integration tests.
public partial class Program
{
}