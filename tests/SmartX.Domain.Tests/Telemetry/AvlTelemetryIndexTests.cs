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

    [Fact]
    public void QueryRange_ReturnsEmptySnapshotWhenIndexIsEmptyOrIntervalHasNoMatches()
    {
        var index = new AvlTelemetryIndex<int>();

        Assert.Empty(index.QueryRange(BaseTime, BaseTime.AddHours(1)));

        index.Insert(CreatePacket(10));
        Assert.Empty(index.QueryRange(BaseTime.AddSeconds(20), BaseTime.AddSeconds(30)));
        Assert.Empty(index.QueryRange(BaseTime.AddSeconds(-20), BaseTime.AddSeconds(-10)));
        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public void QueryRange_IncludesBothEndpointsAndOrdersOutOfOrderPackets()
    {
        var index = new AvlTelemetryIndex<int>();
        TelemetryPacket<int> latest = CreatePacket(30);
        TelemetryPacket<int> earliest = CreatePacket(10);
        TelemetryPacket<int> middle = CreatePacket(20);

        index.Insert(latest);
        index.Insert(earliest);
        index.Insert(middle);
        index.Insert(CreatePacket(50));

        IReadOnlyList<TelemetryPacket<int>> result = index.QueryRange(
            BaseTime.AddSeconds(10), BaseTime.AddSeconds(30));

        Assert.Equal(new[] { 10, 20, 30 }, result.Select(packet => packet.Payload).ToArray());
        Assert.Same(earliest, result[0]);
        Assert.Same(middle, result[1]);
        Assert.Same(latest, result[2]);
        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public void QueryRange_EqualUtcInstantsKeepStableInsertionOrderAndLimit()
    {
        var index = new AvlTelemetryIndex<int>();
        DeviceIdentifier id = DeviceIdentifier.FromUniqueIdentifier("avl-equal-time");
        DateTimeOffset sameInstantWithOffset = BaseTime.ToOffset(TimeSpan.FromHours(2));

        index.Insert(new TelemetryPacket<int>(id, 100, BaseTime));
        index.Insert(new TelemetryPacket<int>(id, 200, sameInstantWithOffset));
        index.Insert(new TelemetryPacket<int>(id, 300, BaseTime));
        index.Insert(new TelemetryPacket<int>(id, 400, BaseTime.AddSeconds(1)));

        IReadOnlyList<TelemetryPacket<int>> allAtInstant = index.QueryRange(
            BaseTime.ToOffset(TimeSpan.FromHours(-3)), sameInstantWithOffset, limit: 3);
        IReadOnlyList<TelemetryPacket<int>> bounded = index.QueryRange(
            BaseTime, BaseTime.AddSeconds(1), limit: 2);

        Assert.Equal(new[] { 100, 200, 300 }, allAtInstant.Select(p => p.Payload).ToArray());
        Assert.Equal(new[] { 100, 200 }, bounded.Select(p => p.Payload).ToArray());
        Assert.Equal(4, index.Count);
        Assert.Equal(2, index.DistinctTimestampCount);
    }

    [Fact]
    public void QueryRange_BoundedSnapshotDoesNotChangeAfterFurtherInsertions()
    {
        var index = new AvlTelemetryIndex<int>();
        index.Insert(CreatePacket(10));
        index.Insert(CreatePacket(20));

        IReadOnlyList<TelemetryPacket<int>> snapshot = index.QueryRange(
            BaseTime, BaseTime.AddSeconds(50));

        // ReadOnlyCollection rejects attempted mutation and is not a view of
        // the mutable AVL timestamp bucket or a live index query.
        Assert.Throws<NotSupportedException>(() =>
            ((IList<TelemetryPacket<int>>)snapshot).Add(CreatePacket(30)));

        index.Insert(CreatePacket(15));

        Assert.Equal(new[] { 10, 20 }, snapshot.Select(p => p.Payload).ToArray());
        Assert.Equal(new[] { 10, 15, 20 }, index.QueryRange(
            BaseTime, BaseTime.AddSeconds(50)).Select(p => p.Payload).ToArray());
    }

    [Fact]
    public void QueryRange_RejectsReversedBoundsAndInvalidLimitsWithoutChangingTree()
    {
        var index = new AvlTelemetryIndex<int>();
        index.Insert(CreatePacket(10));

        Assert.Throws<ArgumentException>(() => index.QueryRange(
            BaseTime.AddMinutes(1), BaseTime));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.QueryRange(
            BaseTime, BaseTime.AddMinutes(1), limit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.QueryRange(
            BaseTime, BaseTime.AddMinutes(1), limit: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.QueryRange(
            BaseTime, BaseTime.AddMinutes(1), limit: AvlTelemetryIndex<int>.MaxRangeLimit + 1));

        Assert.Single(index.QueryRange(BaseTime, BaseTime.AddMinutes(1)));
        Assert.Equal(1, index.Count);
        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public void QueryRange_HandlesBooleanAndDoublePayloadsWithoutCoercion()
    {
        DeviceIdentifier id = DeviceIdentifier.FromUniqueIdentifier("avl-generic-types");
        var boolIndex = new AvlTelemetryIndex<bool>();
        var doubleIndex = new AvlTelemetryIndex<double>();

        boolIndex.Insert(new TelemetryPacket<bool>(id, true, BaseTime));
        doubleIndex.Insert(new TelemetryPacket<double>(id, 12.5, BaseTime));

        Assert.True(Assert.Single(boolIndex.QueryRange(BaseTime, BaseTime)).Payload);
        Assert.Equal(12.5, Assert.Single(doubleIndex.QueryRange(BaseTime, BaseTime)).Payload);
    }

    private static TelemetryPacket<int> CreatePacket(int seconds)
    {
        DeviceIdentifier id = DeviceIdentifier.FromUniqueIdentifier("avl-test-device");
        return new TelemetryPacket<int>(id, seconds, BaseTime.AddSeconds(seconds));
    }
}