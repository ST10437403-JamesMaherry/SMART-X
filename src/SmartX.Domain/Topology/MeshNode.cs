using SmartX.Domain.Sensors;

namespace SmartX.Domain.Topology;

/// <summary>
/// Immutable view of a graph vertex and its current sensor operational state.
/// The store must refresh sensor state from the authoritative sensor registry.
/// A mesh node does not create or register a new Smart-X sensor.
/// </summary>
public sealed class MeshNode
{
    public MeshNode(MeshNodeKey key, SensorStatus? status)
    {
        if (!key.IsValid)
        {
            throw new InvalidMeshTopologyException(
                "A mesh node must have a valid gateway or registered-sensor key.");
        }

        if (key.IsGateway && status is not null)
        {
            throw new InvalidMeshTopologyException(
                "The logical gateway cannot have a sensor operational status.");
        }

        if (!key.IsGateway && status is null)
        {
            throw new InvalidMeshTopologyException(
                "A sensor mesh node requires its current sensor status.");
        }

        if (status is not null &&
            !Enum.IsDefined(typeof(SensorStatus), status.Value))
        {
            throw new InvalidMeshTopologyException(
                "The mesh node contains an unsupported sensor status.");
        }

        Key = key;
        Status = status;
    }

    public MeshNodeKey Key { get; }

    /// <summary>
    /// Null for the logical gateway; otherwise a genuine Smart-X sensor state.
    /// Offline and decommissioned sensors stay distinguishable in inventory.
    /// </summary>
    public SensorStatus? Status { get; }

    public bool IsGateway => Key.IsGateway;

    /// <summary>
    /// Conservative forwarding policy used by later BFS and MST projections:
    /// Online/Warning sensors can relay; Offline/Fault/Maintenance/
    /// Decommissioned sensors cannot. A link alone never proves reachability.
    /// </summary>
    public bool IsForwardingEligible =>
        IsGateway ||
        Status is SensorStatus.Online or SensorStatus.Warning;

    public static MeshNode Gateway { get; } =
        new(MeshNodeKey.Gateway, null);

    /// <summary>
    /// Derives a vertex from an actual sensor profile without copying the
    /// hardware identifier or fabricating a physical radio connection.
    /// </summary>
    public static MeshNode FromSensor(SensorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new MeshNode(MeshNodeKey.ForSensor(profile.Id), profile.Status);
    }

    /// <summary>
    /// Creates a replacement sensor-state projection without mutating any
    /// already published graph snapshot. Only the store may commit it.
    /// </summary>
    public MeshNode WithStatus(SensorStatus status)
    {
        if (IsGateway)
        {
            throw new InvalidMeshTopologyException(
                "The logical gateway does not have a sensor status.");
        }

        return new MeshNode(Key, status);
    }
}