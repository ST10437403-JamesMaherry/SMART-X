using SmartX.Domain.Exceptions;
using SmartX.Domain.Sensors;

using Xunit;

namespace SmartX.Domain.Tests.Sensors;

public sealed class DeviceIdentifierTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", "AA:BB:CC:DD:EE:FF")]
    [InlineData("aa-bb-cc-dd-ee-ff", "AA:BB:CC:DD:EE:FF")]
    [InlineData("aabbccddeeff", "AA:BB:CC:DD:EE:FF")]
    public void FromMacAddress_WhenValid_NormalisesAddress(
        string input,
        string expected)
    {
        var identifier = DeviceIdentifier.FromMacAddress(input);

        Assert.Equal(expected, identifier.Value);
        Assert.Equal(
            DeviceIdentifierKind.MacAddress,
            identifier.Kind);
        Assert.Equal(expected, identifier.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("GG:BB:CC:DD:EE:FF")]
    public void FromMacAddress_WhenInvalid_ThrowsMeaningfulException(
        string? input)
    {
        Assert.Throws<InvalidDeviceIdentifierException>(
            () => DeviceIdentifier.FromMacAddress(input));
    }

    [Theory]
    [InlineData("sensor-01")]
    [InlineData("node_42")]
    [InlineData("greenhouse.zone1.temperature")]
    public void FromUniqueIdentifier_WhenValid_PreservesIdentifier(
        string input)
    {
        var identifier =
            DeviceIdentifier.FromUniqueIdentifier(input);

        Assert.Equal(input, identifier.Value);
        Assert.Equal(
            DeviceIdentifierKind.UniqueIdentifier,
            identifier.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sensor node 1")]
    [InlineData("sensor/node/1")]
    public void FromUniqueIdentifier_WhenInvalid_ThrowsMeaningfulException(
        string? input)
    {
        Assert.Throws<InvalidDeviceIdentifierException>(
            () => DeviceIdentifier.FromUniqueIdentifier(input));
    }

    [Fact]
    public void FromUniqueIdentifier_WhenTooLong_ThrowsMeaningfulException()
    {
        var input = new string(
            'A',
            DeviceIdentifier.MaxUniqueIdentifierLength + 1);

        Assert.Throws<InvalidDeviceIdentifierException>(
            () => DeviceIdentifier.FromUniqueIdentifier(input));
    }
}
