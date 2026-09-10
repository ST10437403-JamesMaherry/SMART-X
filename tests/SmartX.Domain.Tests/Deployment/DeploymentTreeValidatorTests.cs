using SmartX.Domain.Deployment;

using Xunit;

namespace SmartX.Tests.Domain.Deployment;

/// <summary>
/// Verifies recursive Smart-X deployment hierarchy validation,
/// including valid trees and defensive edge cases.
/// </summary>
public sealed class DeploymentTreeValidatorTests
{
    [Fact]
    public void Validate_ShallowFacilityWithNoChildren_ReturnsValid()
    {
        // Arrange
        var facility = new DeploymentTreeNode(
            "Facility A",
            DeploymentNodeType.Facility);

        var validator = new DeploymentTreeValidator();

        // Act
        DeploymentTreeValidationResult result =
            validator.Validate(facility);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(
            DeploymentValidationErrorCode.None,
            result.ErrorCode);
        Assert.Null(result.FailedPath);
    }

    [Fact]
    public void Validate_FullFourLevelHierarchy_ReturnsValid()
    {
        // Arrange
        DeploymentTreeNode facility = BuildValidDeploymentTree();

        var validator = new DeploymentTreeValidator();

        // Act
        DeploymentTreeValidationResult result =
            validator.Validate(facility);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(
            DeploymentValidationErrorCode.None,
            result.ErrorCode);
    }

    [Fact]
    public void Validate_WhenMaximumDepthIsExceeded_ReturnsFailedPath()
    {
        // Arrange
        DeploymentTreeNode facility = BuildValidDeploymentTree();

        // The normal hierarchy reaches depth 4.
        // Restricting the validator to 3 proves that the recursion
        // depth protection terminates traversal safely.
        var validator = new DeploymentTreeValidator(
            maximumDepth: 3);

        // Act
        DeploymentTreeValidationResult result =
            validator.Validate(facility);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(
            DeploymentValidationErrorCode.MaximumDepthExceeded,
            result.ErrorCode);
        Assert.Equal(4, result.FailedDepth);
        Assert.Equal(
            "Facility A -> Zone 1 -> Sub-Zone B -> Sensor Node 01",
            result.FailedPath);
    }

    [Fact]
    public void Validate_WhenCycleExists_ReturnsCycleFailure()
    {
        // Arrange
        var facility = new DeploymentTreeNode(
            "Facility A",
            DeploymentNodeType.Facility);

        var zone = new DeploymentTreeNode(
            "Zone 1",
            DeploymentNodeType.Zone,
            facility.Id);

        facility.AddChild(zone);

        // Deliberately point the Zone back to the Facility.
        // This creates:
        //
        // Facility A -> Zone 1 -> Facility A -> ...
        zone.AddChild(facility);

        // Use a larger depth so cycle detection is tested independently
        // from the maximum-depth protection.
        var validator = new DeploymentTreeValidator(
            maximumDepth: 10);

        // Act
        DeploymentTreeValidationResult result =
            validator.Validate(facility);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(
            DeploymentValidationErrorCode.CycleDetected,
            result.ErrorCode);
        Assert.Equal(
            "Facility A -> Zone 1 -> Facility A",
            result.FailedPath);
    }

    [Fact]
    public void Validate_WhenChildReferencesWrongParent_ReturnsInvalidParent()
    {
        // Arrange
        var facility = new DeploymentTreeNode(
            "Facility A",
            DeploymentNodeType.Facility);

        var zone = new DeploymentTreeNode(
            "Zone 1",
            DeploymentNodeType.Zone,
            Guid.NewGuid());

        facility.AddChild(zone);

        var validator = new DeploymentTreeValidator();

        // Act
        DeploymentTreeValidationResult result =
            validator.Validate(facility);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(
            DeploymentValidationErrorCode.InvalidParent,
            result.ErrorCode);
        Assert.Equal(
            "Facility A -> Zone 1",
            result.FailedPath);
    }

    [Fact]
    public void Validate_WhenHierarchyLevelIsSkipped_ReturnsInvalidHierarchy()
    {
        // Arrange
        var facility = new DeploymentTreeNode(
            "Facility A",
            DeploymentNodeType.Facility);

        // A SubZone cannot exist directly beneath a Facility.
        var subZone = new DeploymentTreeNode(
            "Sub-Zone B",
            DeploymentNodeType.SubZone,
            facility.Id);

        facility.AddChild(subZone);

        var validator = new DeploymentTreeValidator();

        // Act
        DeploymentTreeValidationResult result =
            validator.Validate(facility);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(
            DeploymentValidationErrorCode.InvalidHierarchy,
            result.ErrorCode);
        Assert.Equal(
            "Facility A -> Sub-Zone B",
            result.FailedPath);
    }

    [Fact]
    public void Validate_WhenSensorNodeHasChildren_ReturnsLeafFailure()
    {
        // Arrange
        var facility = new DeploymentTreeNode(
            "Facility A",
            DeploymentNodeType.Facility);

        var zone = new DeploymentTreeNode(
            "Zone 1",
            DeploymentNodeType.Zone,
            facility.Id);

        var subZone = new DeploymentTreeNode(
            "Sub-Zone B",
            DeploymentNodeType.SubZone,
            zone.Id);

        var sensorNode = new DeploymentTreeNode(
            "Sensor Node 01",
            DeploymentNodeType.Node,
            subZone.Id);

        var unexpectedChild = new DeploymentTreeNode(
            "Unexpected Child",
            DeploymentNodeType.Node,
            sensorNode.Id);

        facility.AddChild(zone);
        zone.AddChild(subZone);
        subZone.AddChild(sensorNode);
        sensorNode.AddChild(unexpectedChild);

        var validator = new DeploymentTreeValidator();

        // Act
        DeploymentTreeValidationResult result =
            validator.Validate(facility);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(
            DeploymentValidationErrorCode.LeafNodeHasChildren,
            result.ErrorCode);
        Assert.Equal(
            "Facility A -> Zone 1 -> Sub-Zone B -> Sensor Node 01",
            result.FailedPath);
    }

    [Fact]
    public void Validate_WhenSameNodeIsReferencedTwice_ReturnsDuplicateFailure()
    {
        // Arrange
        var facility = new DeploymentTreeNode(
            "Facility A",
            DeploymentNodeType.Facility);

        var zone = new DeploymentTreeNode(
            "Zone 1",
            DeploymentNodeType.Zone,
            facility.Id);

        // Deliberately attach the same configuration object twice.
        facility.AddChild(zone);
        facility.AddChild(zone);

        var validator = new DeploymentTreeValidator();

        // Act
        DeploymentTreeValidationResult result =
            validator.Validate(facility);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(
            DeploymentValidationErrorCode.DuplicateNodeReference,
            result.ErrorCode);
        Assert.Equal(
            "Facility A -> Zone 1",
            result.FailedPath);
    }

    [Fact]
    public void Validate_WhenRootIsNotFacility_ReturnsRootTypeFailure()
    {
        // Arrange
        var zone = new DeploymentTreeNode(
            "Zone 1",
            DeploymentNodeType.Zone);

        var validator = new DeploymentTreeValidator();

        // Act
        DeploymentTreeValidationResult result =
            validator.Validate(zone);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(
            DeploymentValidationErrorCode.RootMustBeFacility,
            result.ErrorCode);
        Assert.Equal(
            "Zone 1",
            result.FailedPath);
    }

    [Fact]
    public void Validate_WhenRootDeclaresParent_ReturnsRootParentFailure()
    {
        // Arrange
        var facility = new DeploymentTreeNode(
            "Facility A",
            DeploymentNodeType.Facility,
            Guid.NewGuid());

        var validator = new DeploymentTreeValidator();

        // Act
        DeploymentTreeValidationResult result =
            validator.Validate(facility);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(
            DeploymentValidationErrorCode.RootHasParent,
            result.ErrorCode);
        Assert.Equal(
            "Facility A",
            result.FailedPath);
    }

    [Fact]
    public void Validate_WhenRootIsNull_ReturnsRootMissingFailure()
    {
        // Arrange
        var validator = new DeploymentTreeValidator();

        // Act
        DeploymentTreeValidationResult result =
            validator.Validate(null);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(
            DeploymentValidationErrorCode.RootMissing,
            result.ErrorCode);
        Assert.Null(result.FailedPath);
        Assert.Equal(0, result.FailedDepth);
    }

    /// <summary>
    /// Builds the deepest valid Smart-X hierarchy:
    ///
    /// Facility A
    ///     -> Zone 1
    ///         -> Sub-Zone B
    ///             -> Sensor Node 01
    /// </summary>
    private static DeploymentTreeNode BuildValidDeploymentTree()
    {
        var facility = new DeploymentTreeNode(
            "Facility A",
            DeploymentNodeType.Facility);

        var zone = new DeploymentTreeNode(
            "Zone 1",
            DeploymentNodeType.Zone,
            facility.Id);

        var subZone = new DeploymentTreeNode(
            "Sub-Zone B",
            DeploymentNodeType.SubZone,
            zone.Id);

        var sensorNode = new DeploymentTreeNode(
            "Sensor Node 01",
            DeploymentNodeType.Node,
            subZone.Id);

        facility.AddChild(zone);
        zone.AddChild(subZone);
        subZone.AddChild(sensorNode);

        return facility;
    }
}
