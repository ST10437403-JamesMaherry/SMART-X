const string clientCorsPolicyName = "SmartXClient";

var builder = WebApplication.CreateBuilder(args);

var allowedClientOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>()
    ?? [];

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

builder.Services.AddCors(options =>
{
    options.AddPolicy(clientCorsPolicyName, policy =>
    {
        // An empty production origin list rejects cross-origin browser requests.
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