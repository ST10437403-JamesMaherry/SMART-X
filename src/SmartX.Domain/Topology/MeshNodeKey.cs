namespace SmartX.Domain.Topology;

/// <summary>
/// Defines the two kinds of vertex permitted in the simulated Smart-X mesh.
/// The logical gateway is not a sensor and never reuses a sensor profile ID.
/// </summary>
public enum MeshNodeKind : byte
{
    Gateway = 1,
    Sensor = 2
}

/// <summary>
/// Immutable graph vertex identity. Sensor vertices use the authoritative
/// <c>SensorProfile.Id</c>, not a display name, MAC string or deployment path.
///
/// A default-constructed value is deliberately invalid; graph boundaries
/// must check <see cref="IsValid"/> before accepting an external key.
/// </summary>
public readonly record struct MeshNodeKey : IComparable<MeshNodeKey>
{
    private MeshNodeKey(MeshNodeKind kind, Guid sensorProfileId)
    {
        Kind = kind;
        SensorProfileId = sensorProfileId;
    }

    public MeshNodeKind Kind { get; }

    /// <summary>
    /// Authoritative registered sensor ID, or <see cref="Guid.Empty"/> for
    /// the unique logical gateway.
    /// </summary>
    public Guid SensorProfileId { get; }

    /// <summary>
    /// The one gateway identity, present even when no sensors are registered.
    /// </summary>
    public static MeshNodeKey Gateway { get; } =
        new(MeshNodeKind.Gateway, Guid.Empty);

    public bool IsGateway => Kind == MeshNodeKind.Gateway && IsValid;

    public bool IsValid =>
        (Kind == MeshNodeKind.Gateway && SensorProfileId == Guid.Empty) ||
        (Kind == MeshNodeKind.Sensor && SensorProfileId != Guid.Empty);

    /// <summary>
    /// Creates an identity for an existing sensor profile. The application
    /// and infrastructure layers still verify that the ID is registered.
    /// </summary>
    public static MeshNodeKey ForSensor(Guid sensorProfileId)
    {
        if (sensorProfileId == Guid.Empty)
        {
            throw new InvalidMeshTopologyException(
                "A sensor mesh node requires a non-empty registered sensor ID.");
        }

        return new MeshNodeKey(MeshNodeKind.Sensor, sensorProfileId);
    }

    /// <summary>
    /// Gateway first, then sensors in stable Guid order. This ordering is
    /// independent of hash-table insertion order and enables stable API/UI
    /// projections and deterministic algorithm tie-breaking.
    /// </summary>
    public int CompareTo(MeshNodeKey other)
    {
        int kindComparison = Kind.CompareTo(other.Kind);
        return kindComparison != 0
            ? kindComparison
            : SensorProfileId.CompareTo(other.SensorProfileId);
    }

    public override string ToString() =>
        !IsValid
            ? "invalid-mesh-node"
            : IsGateway
                ? "gateway"
                : $"sensor/{SensorProfileId:D}";
}