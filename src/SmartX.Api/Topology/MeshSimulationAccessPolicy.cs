using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace SmartX.Api.Topology;

/// <summary>
/// Prevents accidental activation of simulation-only mutation endpoints
/// when the API is hosted outside Development/Test. Production and Docker
/// demos MUST opt in explicitly through Topology:Simulation:Enabled=true
/// (environment variable: Topology__Simulation__Enabled=true).
///
/// This controls only the logical in-memory mesh, never physical hardware.
/// An enabled demo must not be exposed as an unauthenticated public service.
/// </summary>
public sealed class MeshSimulationAccessPolicy
{
    public MeshSimulationAccessPolicy(
        IHostEnvironment environment,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        IsEnabled = environment.IsDevelopment() ||
            environment.IsEnvironment("Test") ||
            configuration.GetValue<bool>("Topology:Simulation:Enabled");
    }

    public bool IsEnabled { get; }
}