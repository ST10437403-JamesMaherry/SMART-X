using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

using Xunit;

namespace SmartX.Domain.Tests.Telemetry;

public sealed class AvlTelemetryIndexTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(30, 20, 10)] // LL: single right rotation
    [InlineData(10, 20, 30)] // RR: single left rotation
    [InlineData(30, 10, 20)] // LR: left rotation then right rotation
    [InlineData(10, 30, 20)] // RL: right rotation then left rotation
    public void Insert_WhenUnbalanced_RotatesToMiddleTimestamp(int first, int second, int third)
    {
        var index = new AvlTelemetryIndex<int>();

        index.Insert(CreatePacket(first));
        index.Insert(CreatePacket(second));
        index.Insert(CreatePacket(third));

        Assert.Equal(BaseTime.AddSeconds(20), index.RootTimestampUtc);
        Assert.Equal(2, index.Height);
        Assert.Equal(3, index.Count);
        Assert.Equal(3, index.DistinctTimestampCount);
        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public void EmptyIndex_HasNoRootAndZeroHeight()
    {
        var index = new AvlTelemetryIndex<bool>();

        Assert.Null(index.RootTimestampUtc);
        Assert.Equal(0, index.Count);
        Assert.Equal(0, index.Height);
        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public void EqualUtcInstants_AreGroupedWithoutOverwritingPackets()
    {
        var index = new AvlTelemetryIndex<int>();
        TelemetryPacket<int> original = CreatePacket(0);
        DateTimeOffset sameInstantDifferentOffset = BaseTime.ToOffset(TimeSpan.FromHours(2));
        var sameInstant = new TelemetryPacket<int>(original.SourceDeviceIdentifier, 99, sameInstantDifferentOffset);

        index.Insert(original);
        index.Insert(sameInstant);

        Assert.Equal(2, index.Count);
        Assert.Equal(1, index.DistinctTimestampCount);
        Assert.Equal(BaseTime, index.RootTimestampUtc);
        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public void ManyAscendingKeys_StayBalanced()
    {
        var index = new AvlTelemetryIndex<int>();

        for (int i = 0; i < 1_000; i++)
        {
            index.Insert(CreatePacket(i));
        }

        Assert.Equal(1_000, index.Count);
        Assert.Equal(1_000, index.DistinctTimestampCount);
        Assert.True(index.Height <= 11); // A 1,000-key AVL cannot approach linear height.
        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public void NullPackets_AreRejectedWithoutChangingIndex()
    {
        var index = new AvlTelemetryIndex<int>();

        Assert.Throws<ArgumentNullException>(() => index.Insert(null!));
        Assert.Equal(0, index.Count);
        Assert.True(index.HasValidAvlInvariants());
    }

    private static TelemetryPacket<int> CreatePacket(int seconds)
    {
        DeviceIdentifier id = DeviceIdentifier.FromUniqueIdentifier("avl-test-device");
        return new TelemetryPacket<int>(id, seconds, BaseTime.AddSeconds(seconds));
    }
}