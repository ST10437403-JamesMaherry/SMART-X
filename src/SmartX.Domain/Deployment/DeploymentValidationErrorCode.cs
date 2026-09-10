namespace SmartX.Domain.Deployment;

/// <summary>
/// Identifies the reason a deployment hierarchy failed validation.
/// Strongly typed error codes avoid fragile string comparisons.
/// </summary>
public enum DeploymentValidationErrorCode
{
    None = 0,
    RootMissing = 1,
    RootMustBeFacility = 2,
    RootHasParent = 3,
    MaximumDepthExceeded = 4,
    CycleDetected = 5,
    DuplicateNodeReference = 6,
    InvalidParent = 7,
    InvalidHierarchy = 8,
    LeafNodeHasChildren = 9
}