using SmartX.Application.Dashboard;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

namespace SmartX.Application.Tests.Realtime;

public sealed class TelemetryAnomalyClassifierTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly DeviceIdentifier Source = DeviceIdentifier.FromUniqueIdentifier("classification-node");
    private readonly TelemetryAnomalyClassifier _classifier = new(new AdaptiveAnomalyPolicy());

    [Theory]
    [InlineData(100, 81, 1, DashboardIssueSeverity.Information)]
    [InlineData(100, 80, 1, DashboardIssueSeverity.Warning)]
    [InlineData(100, 50, 1, DashboardIssueSeverity.Critical)]
    [InlineData(0, 0, 1, DashboardIssueSeverity.Information)]
    [InlineData(0, 10, 1, DashboardIssueSeverity.Critical)]
    [InlineData(-10, 10, 1, DashboardIssueSeverity.Critical)]
    [InlineData(10, 30, 300, DashboardIssueSeverity.Critical)]
    [InlineData(10, 30, 301, DashboardIssueSeverity.Information)]
    [InlineData(10, 30, 0, DashboardIssueSeverity.Critical)]
    public void NumericPairs_ReuseRelativeThresholdsAndFiveMinuteWindow(
        double previous, double current, int seconds, DashboardIssueSeverity expected)
    {
        var result = _classifier.Classify(
            new NumericTelemetryPacket<double>(Source, previous, BaseTime),
            new NumericTelemetryPacket<double>(Source, current, BaseTime.AddSeconds(seconds)));

        Assert.True(result.HasEvidence);
        Assert.Equal(expected, result.Severity);
        Assert.Equal(expected != DashboardIssueSeverity.Information, result.IsAnomaly);
    }

    [Fact]
    public void MissingBaselineAndBackfill_AreNotStableRecoveryEvidence()
    {
        var current = new NumericTelemetryPacket<int>(Source, 30, BaseTime);
        Assert.False(_classifier.Classify<int>(null, current).HasEvidence);
        Assert.False(_classifier.Classify(
            new NumericTelemetryPacket<int>(Source, 10, BaseTime.AddSeconds(1)), current).HasEvidence);
    }

    [Fact]
    public void BooleanFalse_IsStableEvidenceWithoutNumericArithmetic()
    {
        var result = _classifier.Classify<bool>(null, new TelemetryPacket<bool>(Source, false, BaseTime));
        Assert.True(result.HasEvidence);
        Assert.False(result.IsAnomaly);
        Assert.Equal(DashboardIssueSeverity.Information, result.Severity);
    }

    [Theory]
    [InlineData(int.MinValue, int.MaxValue)]
    [InlineData(int.MaxValue, int.MinValue)]
    [InlineData(int.MinValue, 0)]
    public void IntegerExtremes_ClassifyWithoutOverflow(int previous, int current)
    {
        var result = _classifier.Classify(
            new NumericTelemetryPacket<int>(Source, previous, BaseTime),
            new NumericTelemetryPacket<int>(Source, current, BaseTime.AddSeconds(1)));
        Assert.Equal(DashboardIssueSeverity.Critical, result.Severity);
    }

    [Fact]
    public void ExtremeFiniteDoubles_RemainClassifiable()
    {
        var result = _classifier.Classify(
            new NumericTelemetryPacket<double>(Source, -double.MaxValue, BaseTime),
            new NumericTelemetryPacket<double>(Source, double.MaxValue, BaseTime.AddSeconds(1)));
        Assert.Equal(DashboardIssueSeverity.Critical, result.Severity);
    }

    [Fact]
    public void CustomPolicy_IsSharedInsteadOfHardCoded()
    {
        var classifier = new TelemetryAnomalyClassifier(new AdaptiveAnomalyPolicy(
            warningRelativeChange: 0.1, criticalRelativeChange: 0.3));
        var result = classifier.Classify(
            new NumericTelemetryPacket<int>(Source, 100, BaseTime),
            new NumericTelemetryPacket<int>(Source, 70, BaseTime.AddSeconds(1)));
        Assert.Equal(DashboardIssueSeverity.Critical, result.Severity);
    }
}