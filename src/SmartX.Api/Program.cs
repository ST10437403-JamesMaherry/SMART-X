using Microsoft.AspNetCore.Mvc;

using SmartX.Api.ExceptionHandling;
using SmartX.Application.Attachments;
using SmartX.Application.Sensors;
using SmartX.Application.Telemetry;
using SmartX.Infrastructure.Attachments;
using SmartX.Infrastructure.Sensors;
using SmartX.Infrastructure.Telemetry;

const string clientCorsPolicyName = "SmartXClient";

var builder = WebApplication.CreateBuilder(args);

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

// -------------------------------------------------------------------------
// API error handling
// -------------------------------------------------------------------------

builder.Services.AddProblemDetails();

builder.Services.AddExceptionHandler<
    ApiExceptionHandler>();

builder.Services.AddSingleton<TimeProvider>(
    TimeProvider.System);

// -------------------------------------------------------------------------
// Sensor persistence and Application services
// -------------------------------------------------------------------------

// Sensor state must survive between individual HTTP requests during the
// lifetime of the Part 1 backend process.
builder.Services.AddSingleton<
    ISensorRepository,
    InMemorySensorRepository>();

builder.Services.AddScoped<
    SensorRegistrationService>();

builder.Services.AddScoped<
    SensorManagementService>();

// -------------------------------------------------------------------------
// Telemetry persistence and Application services
// -------------------------------------------------------------------------

// Telemetry remains process-local during Part 1. The repository abstraction
// allows a database or distributed store to replace this implementation in a
// later Smart-X phase without changing controllers or Application services.
builder.Services.AddSingleton<
    ITelemetryRepository,
    InMemoryTelemetryRepository>();

builder.Services.AddScoped<
    SensorTelemetryService>();

// -------------------------------------------------------------------------
// Secure attachment infrastructure
// -------------------------------------------------------------------------

// Cryptographic infrastructure is resolved only when attachment
// functionality is used. Unrelated endpoints remain independent while
// attachment operations still require a valid encryption key.
builder.Services.AddSingleton<IAttachmentCipher>(
    _ => new ChunkedAesGcmAttachmentCipher(
        builder.Configuration[
            "Attachments:EncryptionKeyBase64"]));

builder.Services.AddSingleton<ISensorAttachmentStore>(
    serviceProvider =>
        new FileSystemSensorAttachmentStore(
            attachmentStoragePath,
            serviceProvider
                .GetRequiredService<IAttachmentCipher>()));

builder.Services.AddScoped<
    SensorAttachmentService>();

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

app.UseHttpsRedirection();

app.UseCors(clientCorsPolicyName);

app.UseAuthorization();

app.MapControllers();

app.Run();

// Expose the generated entry point to the integration-test project.
public partial class Program
{
}