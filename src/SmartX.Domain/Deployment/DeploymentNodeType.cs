namespace SmartX.Domain.Deployment;

/// <summary>
/// Represents the supported levels within a Smart-X physical
/// sensor deployment hierarchy.
/// </summary>
public enum DeploymentNodeType
{
    Facility = 0,
    Zone = 1,
    SubZone = 2,
    Node = 3
}
