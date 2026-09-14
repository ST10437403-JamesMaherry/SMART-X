using SmartX.Application.Dashboard;

namespace SmartX.Application.Tests.Dashboard;

public sealed class AdaptiveAnomalyPolicyTests
{
    [Fact]
    public void Constructor_WithDefaults_CreatesExpectedPolicy()
    {
        var policy =
            new AdaptiveAnomalyPolicy();

        Assert.Equal(
            0.20,
            policy.WarningRelativeChange);

        Assert.Equal(
            0.50,
            policy.CriticalRelativeChange);

        Assert.Equal(
            TimeSpan.FromMinutes(5),
            policy.MaximumSpikeInterval);

        Assert.Equal(
            2,
            policy.StableObservationsForRecovery);

        Assert.Equal(
            3,
            policy.RepeatedFailureThreshold);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_WhenWarningThresholdInvalid_Throws(
        double warningThreshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                new AdaptiveAnomalyPolicy(
                    warningRelativeChange:
                        warningThreshold));
    }

    [Theory]
    [InlineData(0.20)]
    [InlineData(0.10)]
    public void Constructor_WhenCriticalNotAboveWarning_Throws(
        double criticalThreshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                new AdaptiveAnomalyPolicy(
                    warningRelativeChange: 0.20,
                    criticalRelativeChange:
                        criticalThreshold));
    }

    [Fact]
    public void Constructor_WhenSpikeIntervalInvalid_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                new AdaptiveAnomalyPolicy(
                    maximumSpikeInterval:
                        TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_WhenRecoveryCountInvalid_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                new AdaptiveAnomalyPolicy(
                    stableObservationsForRecovery: 0));
    }

    [Fact]
    public void Constructor_WhenRepeatedFailureThresholdBelowTwo_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () =>
                new AdaptiveAnomalyPolicy(
                    repeatedFailureThreshold: 1));
    }
}