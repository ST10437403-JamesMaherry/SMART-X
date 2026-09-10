using SmartX.Domain.Exceptions;

namespace SmartX.Domain.Sensors;

/// <summary>
/// Represents the physical deployment location of a Smart-X sensor.
///
/// The Facility -> Zone -> Node hierarchy deliberately supports the
/// hierarchical deployment model that will later be used by the
/// recursive configuration validation feature.
/// </summary>
public sealed record DeploymentLocation
{
    public const int MaxSegmentLength = 80;

    public DeploymentLocation(
        string? facility,
        string? zone,
        string? nodeId)
    {
        Facility = ValidateSegment(facility, nameof(Facility));
        Zone = ValidateSegment(zone, nameof(Zone));
        NodeId = ValidateSegment(nodeId, nameof(NodeId));
    }

    /// <summary>
    /// Facility or deployment site containing the sensor.
    /// </summary>
    public string Facility { get; }

    /// <summary>
    /// Zone or logical area within the facility.
    /// </summary>
    public string Zone { get; }

    /// <summary>
    /// Node identifier within the selected zone.
    /// </summary>
    public string NodeId { get; }

    private static string ValidateSegment(
        string? value,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDeploymentLocationException(
                $"Deployment location {fieldName} is required.");
        }

        var normalised = value.Trim();

        if (normalised.Length > MaxSegmentLength)
        {
            throw new InvalidDeploymentLocationException(
                $"Deployment location {fieldName} cannot exceed " +
                $"{MaxSegmentLength} characters.");
        }

        if (normalised.Any(char.IsControl))
        {
            throw new InvalidDeploymentLocationException(
                $"Deployment location {fieldName} contains invalid " +
                "control characters.");
        }

        return normalised;
    }

    /// <summary>
    /// Provides a readable hierarchical representation for logs and UI use.
    /// </summary>
    public override string ToString()
    {
        return $"{Facility} > {Zone} > {NodeId}";
    }
}
