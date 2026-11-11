namespace SmartX.Domain.Topology;

/// <summary>
/// One connected component of forwarding-eligible sensors that cannot reach
/// the gateway. Offline/Fault/Maintenance/Decommissioned devices are instead
/// classified as unavailable and do not appear in these components.
/// </summary>
public sealed class MeshDisconnectedComponent
{
    public MeshDisconnectedComponent(IEnumerable<MeshNodeKey> sensors)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        MeshNodeKey[] entries = sensors.ToArray();

        if (entries.Length == 0)
        {
            throw new InvalidMeshTopologyException(
                "A disconnected mesh component must contain a sensor.");
        }

        MeshNodeKey? previous = null;
        foreach (MeshNodeKey key in entries)
        {
            if (!key.IsValid || key.IsGateway ||
                (previous is not null && previous.Value.CompareTo(key) >= 0))
            {
                throw new InvalidMeshTopologyException(
                    "A disconnected component needs unique, ordered sensor identities.");
            }

            previous = key;
        }

        Sensors = Array.AsReadOnly(entries);
    }

    /// <summary>
    /// Sensor keys in ascending stable identity order. At least one entry.
    /// </summary>
    public IReadOnlyList<MeshNodeKey> Sensors { get; }
}