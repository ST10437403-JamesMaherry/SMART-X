using SmartX.Api.ExceptionHandling;
using SmartX.Application.Attachments;
using SmartX.Application.Sensors;
using SmartX.Infrastructure.Attachments;
using SmartX.Infrastructure.Sensors;

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

builder.Services.AddControllers();

builder.Services.AddOpenApi();

// Central ProblemDetails support provides one consistent API error format.
builder.Services.AddProblemDetails();

builder.Services.AddExceptionHandler<
    ApiExceptionHandler>();

builder.Services.AddSingleton<TimeProvider>(
    TimeProvider.System);

// -------------------------------------------------------------------------
// Sensor persistence and application services
// -------------------------------------------------------------------------

// Part 1 uses process-local sensor storage. The Application layer depends
// only on ISensorRepository, allowing this implementation to be replaced
// by database persistence in a later Smart-X stage.
builder.Services.AddSingleton<
    ISensorRepository,
    InMemorySensorRepository>();

// Application services are scoped to the HTTP request. This keeps their
// lifecycle compatible with future scoped persistence implementations.
builder.Services.AddScoped<
    SensorRegistrationService>();

builder.Services.AddScoped<
    SensorManagementService>();

// -------------------------------------------------------------------------
// Secure attachment infrastructure
// -------------------------------------------------------------------------

// Cryptographic infrastructure is resolved only when attachment
// functionality is used. Unrelated endpoints therefore remain independent,
// while attachment operations still require a configured encryption key.
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
            // An empty production origin list rejects cross-origin browser
            // requests by default.
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

// Exception handling must execute before application endpoints so errors
// raised by Application or Domain services can become ProblemDetails.
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