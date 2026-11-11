namespace SmartX.Domain.Topology;

/// <summary>
/// One immutable, undirected simulated radio link between distinct vertices.
/// <see cref="SimulatedEnergyCost"/> is a relative positive routing weight,
/// NOT measured watts, joules or a claim of physical network connectivity.
/// </summary>
public sealed class MeshLink
{
    public MeshLink(
        Guid id,
        MeshNodeKey first,
        MeshNodeKey second,
        double simulatedEnergyCost)
    {
        if (id == Guid.Empty)
        {
            throw new InvalidMeshTopologyException(
                "A mesh link requires a non-empty identity.");
        }

        if (!first.IsValid || !second.IsValid)
        {
            throw new InvalidMeshTopologyException(
                "Both mesh link endpoints must be valid node identities.");
        }

        if (first == second)
        {
            throw new InvalidMeshTopologyException(
                "A mesh link cannot connect a vertex to itself.");
        }

        ValidateCost(simulatedEnergyCost);

        Id = id;
        // Canonical ordering makes A--B and B--A the same unordered pair.
        if (first.CompareTo(second) < 0)
        {
            NodeA = first;
            NodeB = second;
        }
        else
        {
            NodeA = second;
            NodeB = first;
        }

        SimulatedEnergyCost = simulatedEnergyCost;
    }

    public Guid Id { get; }

    public MeshNodeKey NodeA { get; }

    public MeshNodeKey NodeB { get; }

    /// <summary>
    /// Strictly positive finite simulated energy-weight units for Prim MST.
    /// The value is not a calibrated physical energy measurement.
    /// </summary>
    public double SimulatedEnergyCost { get; }

    public bool Contains(MeshNodeKey node) => NodeA == node || NodeB == node;

    public bool Connects(MeshNodeKey first, MeshNodeKey second) =>
        (NodeA == first && NodeB == second) ||
        (NodeA == second && NodeB == first);

    public MeshNodeKey OtherEndpoint(MeshNodeKey node)
    {
        if (node == NodeA)
        {
            return NodeB;
        }

        if (node == NodeB)
        {
            return NodeA;
        }

        throw new InvalidMeshTopologyException(
            "The requested vertex is not an endpoint of this mesh link.");
    }

    /// <summary>
    /// Preserves link identity and endpoints when an operator changes the
    /// simulated cost. Existing snapshots retain the original object.
    /// </summary>
    public MeshLink WithSimulatedEnergyCost(double newCost) =>
        new(Id, NodeA, NodeB, newCost);

    private static void ValidateCost(double cost)
    {
        if (!double.IsFinite(cost) || cost <= 0d)
        {
            throw new InvalidMeshTopologyException(
                "A simulated mesh link cost must be finite and greater than zero.");
        }
    }
}