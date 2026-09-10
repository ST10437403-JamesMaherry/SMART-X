namespace SmartX.Domain.Deployment;

/// <summary>
/// Represents one element within a Smart-X deployment hierarchy.
///
/// A deployment tree follows the intended structure:
/// Facility -> Zone -> SubZone -> Node.
///
/// Structural validation is intentionally handled by
/// <see cref="DeploymentTreeValidator"/> rather than this model.
/// This allows imported or externally supplied configuration trees
/// to be constructed first and validated safely afterwards.
/// </summary>
public sealed class DeploymentTreeNode
{
    private readonly List<DeploymentTreeNode> _children = [];

    /// <summary>
    /// Creates a deployment tree node.
    /// </summary>
    /// <param name="name">Human-readable deployment name.</param>
    /// <param name="type">Hierarchy level represented by the node.</param>
    /// <param name="parentId">
    /// Identifier of the expected parent. A root Facility should use null.
    /// </param>
    /// <param name="id">
    /// Optional explicit identifier, primarily useful when reconstructing
    /// stored deployment configurations.
    /// </param>
    public DeploymentTreeNode(
        string name,
        DeploymentNodeType type,
        Guid? parentId = null,
        Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "Deployment node name cannot be empty.",
                nameof(name));
        }

        Guid resolvedId = id ?? Guid.NewGuid();

        if (resolvedId == Guid.Empty)
        {
            throw new ArgumentException(
                "Deployment node identifier cannot be an empty GUID.",
                nameof(id));
        }

        if (parentId == Guid.Empty)
        {
            throw new ArgumentException(
                "Parent identifier cannot be an empty GUID.",
                nameof(parentId));
        }

        Id = resolvedId;
        Name = name.Trim();
        Type = type;
        ParentId = parentId;
    }

    /// <summary>
    /// Gets the unique identifier of this deployment element.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Gets the human-readable name of the deployment element.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the hierarchy level represented by this element.
    /// </summary>
    public DeploymentNodeType Type { get; }

    /// <summary>
    /// Gets the identifier of the node's declared parent.
    /// A root Facility should have no parent identifier.
    /// </summary>
    public Guid? ParentId { get; }

    /// <summary>
    /// Provides read-only access to the node's child collection.
    /// </summary>
    public IReadOnlyList<DeploymentTreeNode> Children => _children;

    /// <summary>
    /// Adds a child to the current deployment element.
    ///
    /// Hierarchy validation is deliberately not performed here because
    /// malformed imported configuration structures must still be capable
    /// of being represented and subsequently validated by the recursive
    /// validator.
    /// </summary>
    /// <param name="child">Deployment element to attach.</param>
    public void AddChild(DeploymentTreeNode child)
    {
        ArgumentNullException.ThrowIfNull(child);

        _children.Add(child);
    }
}
