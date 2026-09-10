using SmartX.Domain.Exceptions;
using SmartX.Domain.Sensors;

using Xunit;

namespace SmartX.Domain.Tests.Sensors;

public sealed class DeploymentLocationTests
{
    [Fact]
    public void Constructor_WhenValid_CreatesNormalisedLocation()
    {
        var location = new DeploymentLocation(
            "  Facility A  ",
            "  Zone 1  ",
            "  Node-01  ");

        Assert.Equal("Facility A", location.Facility);
        Assert.Equal("Zone 1", location.Zone);
        Assert.Equal("Node-01", location.NodeId);

        Assert.Equal(
            "Facility A > Zone 1 > Node-01",
            location.ToString());
    }

    [Theory]
    [InlineData(null, "Zone 1", "Node-01")]
    [InlineData("Facility A", "   ", "Node-01")]
    [InlineData("Facility A", "Zone 1", "")]
    public void Constructor_WhenRequiredSegmentMissing_ThrowsMeaningfulException(
        string? facility,
        string? zone,
        string? nodeId)
    {
        Assert.Throws<InvalidDeploymentLocationException>(
            () => new DeploymentLocation(
                facility,
                zone,
                nodeId));
    }

    [Fact]
    public void Constructor_WhenAnySegmentTooLong_ThrowsMeaningfulException()
    {
        var tooLong =
            new string('A', DeploymentLocation.MaxSegmentLength + 1);

        Assert.Throws<InvalidDeploymentLocationException>(
            () => new DeploymentLocation(
                tooLong,
                "Zone 1",
                "Node-01"));

        Assert.Throws<InvalidDeploymentLocationException>(
            () => new DeploymentLocation(
                "Facility A",
                tooLong,
                "Node-01"));

        Assert.Throws<InvalidDeploymentLocationException>(
            () => new DeploymentLocation(
                "Facility A",
                "Zone 1",
                tooLong));
    }

    [Fact]
    public void Constructor_WhenControlCharactersPresent_ThrowsException()
    {
        Assert.Throws<InvalidDeploymentLocationException>(
            () => new DeploymentLocation(
                "Facility A",
                "Zone\n1",
                "Node-01"));
    }
}