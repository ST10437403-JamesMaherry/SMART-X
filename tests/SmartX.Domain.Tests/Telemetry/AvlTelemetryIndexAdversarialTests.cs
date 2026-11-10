using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

using Xunit;

namespace SmartX.Domain.Tests.Telemetry;

/// <summary>
/// Deterministic, adversarial tests for the generic AVL telemetry index.
///
/// Unlike the small rotation examples in AvlTelemetryIndexTests, these tests
/// exercise many rotations, difficult insertion orders, UTC-offset collisions,
/// and large inclusive range queries against an independent reference model.
/// They verify structural properties and exact results, not wall-clock timings.
/// </summary>
public sealed class AvlTelemetryIndexAdversarialTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    private static readonly DeviceIdentifier Device =
        DeviceIdentifier.FromUniqueIdentifier("avl-adversarial-test");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MonotonicInput_StaysBalancedAndReturnsAllReadingsInTimeOrder(bool descending)
    {
        const int distinctKeys = 8_192;
        var index = new AvlTelemetryIndex<int>();
        int[] insertionOrder = Enumerable.Range(0, distinctKeys).ToArray();

        if (descending)
        {
            Array.Reverse(insertionOrder);
        }

        // A naive BST degenerates into a linked list for ascending/descending
        // keys. The AVL must perform repeated rotations to preserve its height.
        for (int ordinal = 0; ordinal < insertionOrder.Length; ordinal++)
        {
            int millisecond = insertionOrder[ordinal];
            index.Insert(Packet(payload: millisecond, millisecond: millisecond));

            // Check the full height/bucket/order invariants during construction,
            // not merely after the final insertion has hidden earlier mistakes.
            if ((ordinal + 1) % 1_024 == 0)
            {
                Assert.True(index.HasValidAvlInvariants());
            }
        }

        Assert.Equal(distinctKeys, index.Count);
        Assert.Equal(distinctKeys, index.DistinctTimestampCount);
        Assert.True(index.HasValidAvlInvariants());
        Assert.InRange(index.Height, 1, MaximumValidAvlHeight(distinctKeys));

        IReadOnlyList<TelemetryPacket<int>> all = index.QueryRange(
            BaseTime, BaseTime.AddMilliseconds(distinctKeys - 1), limit: distinctKeys);

        Assert.Equal(distinctKeys, all.Count);
        Assert.Equal(Enumerable.Range(0, distinctKeys), all.Select(packet => packet.Payload));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(2_026)]
    [InlineData(7_312)]
    public void PermutedUniqueKeys_PreserveBalanceAndExactNarrowRanges(int seed)
    {
        const int distinctKeys = 4_096;
        var index = new AvlTelemetryIndex<int>();
        var random = new Random(seed);
        int[] timestamps = Enumerable.Range(0, distinctKeys).ToArray();

        // A seeded Fisher-Yates permutation gives reproducible coverage of
        // internal LL, LR, RL and RR rotations, beyond root-only examples.
        for (int i = timestamps.Length - 1; i > 0; i--)
        {
            int swapWith = random.Next(i + 1);
            (timestamps[i], timestamps[swapWith]) = (timestamps[swapWith], timestamps[i]);
        }

        for (int i = 0; i < timestamps.Length; i++)
        {
            index.Insert(Packet(payload: timestamps[i], millisecond: timestamps[i]));

            if ((i + 1) % 512 == 0)
            {
                Assert.True(index.HasValidAvlInvariants());
            }
        }

        Assert.Equal(distinctKeys, index.Count);
        Assert.Equal(distinctKeys, index.DistinctTimestampCount);
        Assert.InRange(index.Height, 1, MaximumValidAvlHeight(distinctKeys));
        Assert.True(index.HasValidAvlInvariants());

        // The correct interval must be returned without sorting the original
        // random insertion order in the implementation being tested.
        IReadOnlyList<TelemetryPacket<int>> slice = index.QueryRange(
            BaseTime.AddMilliseconds(1_101),
            BaseTime.AddMilliseconds(1_233),
            limit: 500);

        Assert.Equal(Enumerable.Range(1_101, 133), slice.Select(packet => packet.Payload));
        Assert.Empty(index.QueryRange(
            BaseTime.AddMilliseconds(-500),
            BaseTime.AddMilliseconds(-1)));
        Assert.Empty(index.QueryRange(
            BaseTime.AddMilliseconds(distinctKeys),
            BaseTime.AddMilliseconds(distinctKeys + 1)));
    }

    [Fact]
    public void RandomDuplicateBuckets_MatchIndependentStableRangeOracle()
    {
        const int packetCount = 8_000;
        var index = new AvlTelemetryIndex<int>();
        var random = new Random(73_120_26);
        var inserted = new List<TelemetryPacket<int>>(packetCount);

        // Repeated timestamp collisions, out-of-order arrivals, and different
        // offsets for the same UTC instant must retain every original packet.
        for (int ordinal = 0; ordinal < packetCount; ordinal++)
        {
            int millisecond = random.Next(-800, 801);
            TimeSpan offset = TimeSpan.FromHours((ordinal % 3 - 1) * 5);
            TelemetryPacket<int> packet = Packet(ordinal, millisecond, offset);
            inserted.Add(packet);
            index.Insert(packet);
        }

        Assert.Equal(packetCount, index.Count);
        Assert.Equal(inserted.Select(packet => packet.CapturedAtUtc).Distinct().Count(),
            index.DistinctTimestampCount);
        Assert.InRange(index.Height, 1, MaximumValidAvlHeight(index.DistinctTimestampCount));
        Assert.Equal(TimeSpan.Zero, index.RootTimestampUtc!.Value.Offset);
        Assert.True(index.HasValidAvlInvariants());

        // The oracle intentionally uses the List + stable sort approach that
        // production avoids. This isolates correctness from AVL implementation.
        for (int query = 0; query < 40; query++)
        {
            int first = random.Next(-950, 951);
            int second = random.Next(-950, 951);
            int from = Math.Min(first, second);
            int to = Math.Max(first, second);
            int limit = (query % 5) switch
            {
                0 => 1,
                1 => 7,
                2 => 100,
                3 => 1_000,
                _ => AvlTelemetryIndex<int>.MaxRangeLimit
            };

            // Query offsets deliberately differ from the packets' offsets.
            DateTimeOffset fromInstant = BaseTime.AddMilliseconds(from)
                .ToOffset(TimeSpan.FromHours(3));
            DateTimeOffset toInstant = BaseTime.AddMilliseconds(to)
                .ToOffset(TimeSpan.FromHours(-4));

            TelemetryPacket<int>[] expected = inserted
                .Select((packet, insertionOrdinal) => (Packet: packet, Ordinal: insertionOrdinal))
                .Where(entry => entry.Packet.CapturedAtUtc >= fromInstant.ToUniversalTime()
                    && entry.Packet.CapturedAtUtc <= toInstant.ToUniversalTime())
                .OrderBy(entry => entry.Packet.CapturedAtUtc)
                .ThenBy(entry => entry.Ordinal)
                .Take(limit)
                .Select(entry => entry.Packet)
                .ToArray();

            IReadOnlyList<TelemetryPacket<int>> actual = index.QueryRange(
                fromInstant, toInstant, limit);

            Assert.Equal(expected.Length, actual.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                // Reference identity also proves the index did not invent,
                // coerce, merge or silently replace any accepted readings.
                Assert.Same(expected[i], actual[i]);
            }
        }

        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public void TickPrecisionAndUtcBoundaries_AreInclusiveAndStable()
    {
        var index = new AvlTelemetryIndex<int>();
        DateTimeOffset before = BaseTime.AddTicks(9);
        DateTimeOffset exact = BaseTime.AddTicks(10);
        DateTimeOffset after = BaseTime.AddTicks(11);

        index.Insert(new TelemetryPacket<int>(Device, 30, after));
        index.Insert(new TelemetryPacket<int>(Device, 10, before));
        index.Insert(new TelemetryPacket<int>(Device, 20, exact));
        index.Insert(new TelemetryPacket<int>(Device, 21,
            exact.ToOffset(TimeSpan.FromHours(12))));

        Assert.Equal(new[] { 10, 20, 21, 30 }, index.QueryRange(
            before.ToOffset(TimeSpan.FromHours(-5)),
            after.ToOffset(TimeSpan.FromHours(6)),
            limit: 10).Select(packet => packet.Payload));
        Assert.Equal(new[] { 20, 21 }, index.QueryRange(
            exact, exact.ToOffset(TimeSpan.FromHours(-3)))
            .Select(packet => packet.Payload));
        Assert.Empty(index.QueryRange(before.AddTicks(-1), before.AddTicks(-1)));
        Assert.Empty(index.QueryRange(after.AddTicks(1), after.AddTicks(1)));
        Assert.Equal(4, index.Count);
        Assert.Equal(3, index.DistinctTimestampCount);
        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public void AHotTimestampBucket_DoesNotIncreaseNodeHeightOrLoseArrivalOrder()
    {
        const int packetCount = 6_000;
        var index = new AvlTelemetryIndex<int>();

        // Thousands of events with one timestamp are a single AVL node,
        // not thousands of duplicate tree keys or overwritten readings.
        for (int i = 0; i < packetCount; i++)
        {
            index.Insert(Packet(payload: i, millisecond: 0));
        }

        Assert.Equal(packetCount, index.Count);
        Assert.Equal(1, index.DistinctTimestampCount);
        Assert.Equal(1, index.Height);
        Assert.Equal(BaseTime, index.RootTimestampUtc);
        Assert.True(index.HasValidAvlInvariants());
        Assert.Equal(Enumerable.Range(0, packetCount), index.QueryRange(
            BaseTime, BaseTime, limit: packetCount).Select(packet => packet.Payload));
    }

    [Fact]
    public void LargeRange_RespectsDefaultAndMaximumPacketLimits()
    {
        const int packetCount = 10_050;
        var index = new AvlTelemetryIndex<int>();

        for (int i = packetCount - 1; i >= 0; i--)
        {
            index.Insert(Packet(payload: i, millisecond: i));
        }

        DateTimeOffset end = BaseTime.AddMilliseconds(packetCount - 1);
        IReadOnlyList<TelemetryPacket<int>> defaultResult = index.QueryRange(BaseTime, end);
        IReadOnlyList<TelemetryPacket<int>> maximumResult = index.QueryRange(
            BaseTime, end, AvlTelemetryIndex<int>.MaxRangeLimit);

        Assert.Equal(AvlTelemetryIndex<int>.DefaultRangeLimit, defaultResult.Count);
        Assert.Equal(AvlTelemetryIndex<int>.MaxRangeLimit, maximumResult.Count);
        Assert.Equal(0, defaultResult[0].Payload);
        Assert.Equal(AvlTelemetryIndex<int>.DefaultRangeLimit - 1,
            defaultResult[^1].Payload);
        Assert.Equal(0, maximumResult[0].Payload);
        Assert.Equal(AvlTelemetryIndex<int>.MaxRangeLimit - 1,
            maximumResult[^1].Payload);
        Assert.Equal(packetCount, index.Count);
        Assert.InRange(index.Height, 1, MaximumValidAvlHeight(packetCount));
        Assert.True(index.HasValidAvlInvariants());
    }

    private static TelemetryPacket<int> Packet(
        int payload,
        int millisecond,
        TimeSpan? offset = null)
    {
        // Use a single registered-identifier value object across fixtures;
        // timestamps (not object identity) are the actual AVL keys.
        DateTimeOffset capturedAt = BaseTime.AddMilliseconds(millisecond)
            .ToOffset(offset ?? TimeSpan.Zero);
        return new TelemetryPacket<int>(Device, payload, capturedAt);
    }

    /// <summary>
    /// Computes the exact AVL height ceiling from the minimum node counts:
    /// N(0)=0, N(1)=1, N(h)=1+N(h-1)+N(h-2). An observed AVL with more height
    /// than this bound cannot be correctly balanced for its distinct key count.
    /// </summary>
    private static int MaximumValidAvlHeight(int distinctKeys)
    {
        if (distinctKeys <= 0)
        {
            return 0;
        }

        int nodesAtPreviousPreviousHeight = 0;
        int nodesAtPreviousHeight = 1;
        int height = 1;

        while (nodesAtPreviousHeight <= distinctKeys)
        {
            int nodesAtNextHeight = 1 + nodesAtPreviousHeight + nodesAtPreviousPreviousHeight;
            nodesAtPreviousPreviousHeight = nodesAtPreviousHeight;
            nodesAtPreviousHeight = nodesAtNextHeight;
            height++;
        }

        return height - 1;
    }
}