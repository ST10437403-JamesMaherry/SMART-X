using SmartX.Application.Attachments;
using SmartX.Infrastructure.Attachments;

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

builder.Services.AddSingleton<TimeProvider>(
    TimeProvider.System);

// Cryptographic infrastructure is resolved only when attachment
// functionality is used. This keeps unrelated endpoints such as /health
// independent while still requiring a valid key for attachment operations.
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

builder.Services.AddSingleton<
    SensorAttachmentService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy(
        clientCorsPolicyName,
        policy =>
        {
            // An empty production origin list rejects cross-origin
            // browser requests.
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