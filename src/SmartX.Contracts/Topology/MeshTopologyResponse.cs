using SmartX.Contracts.Sensors;

namespace SmartX.Contracts.Topology;

/// <summary>
/// Read-only representation of one configured, undirected simulated mesh.
/// The version is process-local, and a link is never inferred from proximity.
/// </summary>
public sealed record MeshTopologyResponse(
    long GraphVersion,
    string GatewayKey,
    int NodeCount,
    int LinkCount,
    IReadOnlyList<MeshVertexResponse> Nodes,
    IReadOnlyList<MeshLinkResponse> Links);

/// <summary>
/// A gateway or an ACTUALLY registered sensor. Sensor health is captured in
/// the graph snapshot; descriptive metadata comes from the sensor registry.
/// Null sensor fields for the logical gateway are intentional.
/// </summary>
public sealed record MeshVertexResponse(
    string Key,
    string Kind,
    Guid? SensorProfileId,
    string? Name,
    string? DeviceIdentifier,
    string? IdentifierKind,
    string? Category,
    string? Status,
    bool IsForwardingEligible,
    string? TelemetryDataType,
    SensorDeploymentLocationResponse? DeploymentLocation);

/// <summary>
/// Canonical undirected mesh edge. SimulatedEnergyCost is a strictly positive
/// relative routing weight, NOT a measurement of actual radio energy use.
/// </summary>
public sealed record MeshLinkResponse(
    Guid Id,
    string NodeAKey,
    string NodeBKey,
    double SimulatedEnergyCost);