namespace SmartX.Domain.Deployment;

/// <summary>
/// Recursively validates Smart-X physical deployment hierarchies.
///
/// The expected hierarchy is:
/// Facility -> Zone -> SubZone -> Node.
///
/// Validation protects against:
/// - Excessive recursion depth.
/// - Cyclic references.
/// - Reused node references.
/// - Incorrect parent identifiers.
/// - Invalid hierarchy transitions.
/// - Children being attached beneath physical sensor nodes.
/// </summary>
public sealed class DeploymentTreeValidator
{
    /// <summary>
    /// The standard Smart-X hierarchy contains four levels:
    /// Facility -> Zone -> SubZone -> Node.
    /// </summary>
    public const int DefaultMaximumDepth = 4;

    private readonly int _maximumDepth;

    /// <summary>
    /// Creates a deployment-tree validator.
    /// </summary>
    /// <param name="maximumDepth">
    /// Maximum permitted hierarchy depth. Root Facility is depth 1.
    /// </param>
    public DeploymentTreeValidator(
        int maximumDepth = DefaultMaximumDepth)
    {
        if (maximumDepth < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDepth),
                maximumDepth,
                "Maximum deployment-tree depth must be at least 1.");
        }

        _maximumDepth = maximumDepth;
    }

    /// <summary>
    /// Validates an entire deployment hierarchy from its root.
    /// </summary>
    /// <param name="root">Root of the deployment hierarchy.</param>
    /// <returns>
    /// A detailed result indicating whether validation succeeded and,
    /// when it fails, the exact hierarchy path responsible.
    /// </returns>
    public DeploymentTreeValidationResult Validate(
        DeploymentTreeNode? root)
    {
        if (root is null)
        {
            return DeploymentTreeValidationResult.Failure(
                DeploymentValidationErrorCode.RootMissing,
                "A deployment tree must contain a root Facility.",
                null,
                0);
        }

        // Tracks every node encountered during the complete traversal.
        // This detects the same node being attached in multiple places.
        var visitedNodeIds = new HashSet<Guid>();

        // Tracks only the nodes in the current recursive call path.
        // Encountering one of these IDs again proves that a cycle exists.
        var activePathNodeIds = new HashSet<Guid>();

        // Human-readable path maintained as recursion progresses.
        var currentPath = new List<string>();

        return ValidateNode(
            root,
            parent: null,
            depth: 1,
            visitedNodeIds,
            activePathNodeIds,
            currentPath);
    }

    /// <summary>
    /// Recursively validates the current node and all descendants.
    /// </summary>
    private DeploymentTreeValidationResult ValidateNode(
        DeploymentTreeNode current,
        DeploymentTreeNode? parent,
        int depth,
        HashSet<Guid> visitedNodeIds,
        HashSet<Guid> activePathNodeIds,
        List<string> currentPath)
    {
        currentPath.Add(current.Name);

        // We only remove the current node from the active recursion path
        // when this invocation actually inserted it.
        bool addedToActivePath = false;

        try
        {
            // Safety guard: stop recursion before an unexpectedly deep
            // or malicious configuration can exhaust the call stack.
            if (depth > _maximumDepth)
            {
                return Failure(
                    DeploymentValidationErrorCode.MaximumDepthExceeded,
                    $"Deployment tree exceeded the maximum depth of {_maximumDepth}.",
                    currentPath,
                    depth);
            }

            // If the ID already exists in the current recursion path,
            // following the relationship would return to an ancestor.
            if (activePathNodeIds.Contains(current.Id))
            {
                return Failure(
                    DeploymentValidationErrorCode.CycleDetected,
                    $"A deployment cycle was detected at '{current.Name}'.",
                    currentPath,
                    depth);
            }

            // A valid tree cannot reuse the same node instance/identifier
            // beneath multiple branches.
            if (visitedNodeIds.Contains(current.Id))
            {
                return Failure(
                    DeploymentValidationErrorCode.DuplicateNodeReference,
                    $"Deployment node '{current.Name}' is referenced more than once.",
                    currentPath,
                    depth);
            }

            activePathNodeIds.Add(current.Id);
            addedToActivePath = true;

            visitedNodeIds.Add(current.Id);

            if (parent is null)
            {
                // The only valid root for the physical deployment tree
                // is a Facility.
                if (current.Type != DeploymentNodeType.Facility)
                {
                    return Failure(
                        DeploymentValidationErrorCode.RootMustBeFacility,
                        $"Deployment root must be a Facility, but '{current.Name}' is {current.Type}.",
                        currentPath,
                        depth);
                }

                // A root element cannot itself belong to another parent.
                if (current.ParentId is not null)
                {
                    return Failure(
                        DeploymentValidationErrorCode.RootHasParent,
                        $"Root Facility '{current.Name}' must not declare a parent.",
                        currentPath,
                        depth);
                }
            }
            else
            {
                // Protect against configuration records being placed
                // beneath a parent different from the one they declare.
                if (current.ParentId != parent.Id)
                {
                    return Failure(
                        DeploymentValidationErrorCode.InvalidParent,
                        $"Deployment node '{current.Name}' does not reference its actual parent '{parent.Name}'.",
                        currentPath,
                        depth);
                }

                // Ensure that the physical hierarchy follows the required
                // Facility -> Zone -> SubZone -> Node ordering.
                if (!IsValidChildRelationship(parent.Type, current.Type))
                {
                    return Failure(
                        DeploymentValidationErrorCode.InvalidHierarchy,
                        $"A {current.Type} cannot be placed directly beneath a {parent.Type}.",
                        currentPath,
                        depth);
                }
            }

            // Physical sensor Nodes are leaves in this deployment tree.
            // They cannot contain further deployment elements.
            if (current.Type == DeploymentNodeType.Node &&
                current.Children.Count > 0)
            {
                return Failure(
                    DeploymentValidationErrorCode.LeafNodeHasChildren,
                    $"Sensor node '{current.Name}' is a leaf and cannot contain child deployment elements.",
                    currentPath,
                    depth);
            }

            /*
             * RECURSION BASE CASE
             *
             * Once the current element has no children, there are no
             * further descendants to inspect and this recursive branch
             * is complete.
             */
            if (current.Children.Count == 0)
            {
                return DeploymentTreeValidationResult.Success();
            }

            // RECURSIVE CASE:
            // Validate every child using the current node as its parent.
            foreach (DeploymentTreeNode child in current.Children)
            {
                DeploymentTreeValidationResult childResult = ValidateNode(
                    child,
                    current,
                    depth + 1,
                    visitedNodeIds,
                    activePathNodeIds,
                    currentPath);

                // Stop immediately when the first invalid branch is found.
                // The result already contains the complete failed path.
                if (!childResult.IsValid)
                {
                    return childResult;
                }
            }

            return DeploymentTreeValidationResult.Success();
        }
        finally
        {
            // Backtracking is essential to recursive traversal.
            // Remove this invocation's state before returning to its parent.
            if (addedToActivePath)
            {
                activePathNodeIds.Remove(current.Id);
            }

            currentPath.RemoveAt(currentPath.Count - 1);
        }
    }

    /// <summary>
    /// Defines the only legal parent/child transitions within
    /// the Smart-X physical deployment hierarchy.
    /// </summary>
    private static bool IsValidChildRelationship(
        DeploymentNodeType parentType,
        DeploymentNodeType childType)
    {
        return (parentType, childType) switch
        {
            (DeploymentNodeType.Facility, DeploymentNodeType.Zone) => true,
            (DeploymentNodeType.Zone, DeploymentNodeType.SubZone) => true,
            (DeploymentNodeType.SubZone, DeploymentNodeType.Node) => true,
            _ => false
        };
    }

    /// <summary>
    /// Creates a failure result using the current recursive path.
    /// </summary>
    private static DeploymentTreeValidationResult Failure(
        DeploymentValidationErrorCode errorCode,
        string message,
        IReadOnlyCollection<string> path,
        int depth)
    {
        string failedPath = string.Join(" -> ", path);

        return DeploymentTreeValidationResult.Failure(
            errorCode,
            message,
            failedPath,
            depth);
    }
}
