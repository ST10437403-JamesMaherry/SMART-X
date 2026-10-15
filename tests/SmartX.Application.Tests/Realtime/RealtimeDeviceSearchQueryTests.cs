using SmartX.Application.Realtime;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Sensors;

namespace SmartX.Application.Tests.Realtime;

/// <summary>
/// Validates the discovery contract independently from ASP.NET model binding.
/// In particular, malformed inputs cannot accidentally become broad queries.
/// </summary>
public sealed class RealtimeDeviceSearchQueryTests
{
    [Fact]
    public void Validate_EmptyQuery_UsesBoundedDefaults()
    {
        ValidatedRealtimeDeviceSearchQuery result =
            new RealtimeDeviceSearchQuery().Validate();

        Assert.Null(result.Identifier);
        Assert.Null(result.Text);
        Assert.Null(result.Category);
        Assert.Null(result.Status);
        Assert.Null(result.HasActiveCondition);
        Assert.Null(result.ConditionType);
        Assert.Equal(25, result.Limit);
        Assert.Equal(0, result.Offset);
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:01")]
    [InlineData("aa-bb-cc-dd-ee-01")]
    [InlineData("aabbccddee01")]
    public void Validate_MacAddress_UsesCanonicalIdentifier(string input)
    {
        ValidatedRealtimeDeviceSearchQuery result =
            new RealtimeDeviceSearchQuery(
                DeviceIdentifier: input,
                IdentifierKind: "macaddress").Validate();

        Assert.NotNull(result.Identifier);
        Assert.Equal(DeviceIdentifierKind.MacAddress, result.Identifier.Kind);
        Assert.Equal("AA:BB:CC:DD:EE:01", result.Identifier.Value);
    }

    [Fact]
    public void Validate_CustomIdentifier_PreservesOrdinalCase()
    {
        ValidatedRealtimeDeviceSearchQuery result =
            new RealtimeDeviceSearchQuery(
                DeviceIdentifier: "  Node_A  ",
                IdentifierKind: "UniqueIdentifier").Validate();

        Assert.Equal("Node_A", result.Identifier?.Value);
        Assert.Equal(DeviceIdentifierKind.UniqueIdentifier, result.Identifier?.Kind);
    }

    [Theory]
    [InlineData("bad!", "UniqueIdentifier")]
    [InlineData("invalid-mac", "MacAddress")]
    [InlineData("AA:BB:CC:DD:EE:01", "UnknownKind")]
    [InlineData("AA:BB:CC:DD:EE:01", "")]
    [InlineData("", "MacAddress")]
    [InlineData("some-id", null)]
    [InlineData(null, "UniqueIdentifier")]
    public void Validate_IncompleteOrMalformedIdentifier_Rejects(
        string? identifier,
        string? kind)
    {
        Assert.Throws<InvalidRealtimeDeviceSearchQueryException>(() =>
            new RealtimeDeviceSearchQuery(
                DeviceIdentifier: identifier,
                IdentifierKind: kind).Validate());
    }

    [Fact]
    public void Validate_CombinedFilters_ParsesSupportedNamesAndTrimsText()
    {
        ValidatedRealtimeDeviceSearchQuery result =
            new RealtimeDeviceSearchQuery(
                Text: "  greenhouse  ",
                Category: " environmental ",
                Status: " ONLINE ",
                HasActiveCondition: " TrUe ",
                ConditionType: " telemetryspike ",
                Limit: "20",
                Offset: "2").Validate();

        Assert.Equal("greenhouse", result.Text);
        Assert.Equal(SensorCategory.Environmental, result.Category);
        Assert.Equal(SensorStatus.Online, result.Status);
        Assert.Equal(true, result.HasActiveCondition);
        Assert.Equal(DashboardIssueType.TelemetrySpike, result.ConditionType);
        Assert.Equal(20, result.Limit);
        Assert.Equal(2, result.Offset);
    }

    [Theory]
    [InlineData("category", "Unsupported")]
    [InlineData("category", "1")]
    [InlineData("category", "")]
    [InlineData("status", "Broken")]
    [InlineData("status", "2")]
    [InlineData("status", "")]
    [InlineData("conditionType", "Invalid")]
    [InlineData("conditionType", "1")]
    [InlineData("conditionType", "")]
    public void Validate_UnknownEnumOrNumericEnum_Rejects(string field, string input)
    {
        RealtimeDeviceSearchQuery query = field switch
        {
            "category" => new(Category: input),
            "status" => new(Status: input),
            _ => new(ConditionType: input)
        };

        Assert.Throws<InvalidRealtimeDeviceSearchQueryException>(() => query.Validate());
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData("anything", "x")]
    public void Validate_EmptyOrOverlongText_Rejects(string input, string? suffix)
    {
        string text = suffix is null
            ? input
            : string.Concat(Enumerable.Repeat(input, 50));

        Assert.Throws<InvalidRealtimeDeviceSearchQueryException>(() =>
            new RealtimeDeviceSearchQuery(Text: text).Validate());
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("201", null)]
    [InlineData("-1", null)]
    [InlineData("1.5", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, "-1")]
    [InlineData(null, "100001")]
    [InlineData(null, "1.1")]
    [InlineData(null, "abc")]
    [InlineData(null, "")]
    public void Validate_InvalidLimitOrOffset_Rejects(string? limit, string? offset)
    {
        Assert.Throws<InvalidRealtimeDeviceSearchQueryException>(() =>
            new RealtimeDeviceSearchQuery(Limit: limit, Offset: offset).Validate());
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    public void Validate_FalseActiveFilter_RemainsDistinctFromOmitted(string value)
    {
        ValidatedRealtimeDeviceSearchQuery result =
            new RealtimeDeviceSearchQuery(HasActiveCondition: value).Validate();

        Assert.Equal(false, result.HasActiveCondition);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("unknown")]
    public void Validate_UnsupportedBoolean_Rejects(string value)
    {
        Assert.Throws<InvalidRealtimeDeviceSearchQueryException>(() =>
            new RealtimeDeviceSearchQuery(HasActiveCondition: value).Validate());
    }

    [Fact]
    public void Validate_ContradictoryActiveAndTypeFilters_Rejects()
    {
        Assert.Throws<InvalidRealtimeDeviceSearchQueryException>(() =>
            new RealtimeDeviceSearchQuery(
                HasActiveCondition: "false",
                ConditionType: "SensorDisconnected").Validate());
    }

    [Fact]
    public void Validate_BoundaryLimitsAndOffset_Accepts()
    {
        ValidatedRealtimeDeviceSearchQuery result =
            new RealtimeDeviceSearchQuery(Limit: "200", Offset: "100000").Validate();

        Assert.Equal(200, result.Limit);
        Assert.Equal(100_000, result.Offset);
    }
}