using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

namespace SmartX.Domain.Tests.Telemetry;

/// <summary>
/// Proves keyset paging never drops packets from equal-timestamp buckets and
/// that every page is obtained with the AVL's pruned traversal rather than a
/// skip over all earlier packets.
/// </summary>
public sealed class AvlTelemetryIndexPaginationTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);

    private static readonly DeviceIdentifier Device =
        DeviceIdentifier.FromUniqueIdentifier("avl-page-test");

    [Fact]
    public void QueryRangePage_DuplicateTimestampAcrossPages_NeverLosesOrRepeatsReading()
    {
        var index = new AvlTelemetryIndex<int>();
        index.Insert(Packet(90, BaseTime.AddSeconds(2)));
        index.Insert(Packet(10, BaseTime));
        for (int i = 0; i < 17; i++)
        {
            index.Insert(Packet(100 + i, BaseTime.AddSeconds(1)));
        }

        var values = new List<int>();
        var sequences = new List<int>();
        DateTimeOffset? cursorTime = null;
        int? cursorSequence = null;
        int pageCount = 0;

        while (true)
        {
            AvlRangePage<int> page = index.QueryRangePage(
                BaseTime, BaseTime.AddSeconds(2), limit: 4,
                afterTimestampUtc: cursorTime,
                afterTimestampSequence: cursorSequence);

            pageCount++;
            Assert.InRange(page.Entries.Count, 1, 4);
            values.AddRange(page.Entries.Select(entry => entry.Packet.Payload));
            sequences.AddRange(page.Entries
                .Where(entry => entry.Packet.CapturedAtUtc == BaseTime.AddSeconds(1))
                .Select(entry => entry.TimestampSequence));

            if (!page.HasMore)
            {
                break;
            }

            AvlRangeEntry<int> last = page.Entries[^1];
            cursorTime = last.Packet.CapturedAtUtc;
            cursorSequence = last.TimestampSequence;
            Assert.True(pageCount < 10);
        }

        Assert.Equal(5, pageCount);
        Assert.Equal(
            new[] { 10 }.Concat(Enumerable.Range(100, 17)).Append(90).ToArray(),
            values.ToArray());
        Assert.Equal(Enumerable.Range(0, 17).ToArray(), sequences.ToArray());
        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public void QueryRangePage_EqualUtcOffsetContinuation_UsesSameBucket()
    {
        var index = new AvlTelemetryIndex<bool>();
        index.Insert(new TelemetryPacket<bool>(Device, true, BaseTime));
        index.Insert(new TelemetryPacket<bool>(
            Device, false, BaseTime.ToOffset(TimeSpan.FromHours(2))));

        AvlRangePage<bool> first = index.QueryRangePage(BaseTime, BaseTime, 1);
        Assert.True(first.HasMore);
        Assert.True(Assert.Single(first.Entries).Packet.Payload);
        Assert.Equal(0, first.Entries[0].TimestampSequence);

        AvlRangePage<bool> second = index.QueryRangePage(
            BaseTime.ToOffset(TimeSpan.FromHours(-5)),
            BaseTime.ToOffset(TimeSpan.FromHours(3)), 1,
            BaseTime.ToOffset(TimeSpan.FromHours(2)), 0);

        Assert.False(second.HasMore);
        Assert.False(Assert.Single(second.Entries).Packet.Payload);
        Assert.Equal(1, second.Entries[0].TimestampSequence);
    }

    [Fact]
    public void QueryRangePage_RejectsInvalidAndFabricatedContinuations()
    {
        var index = new AvlTelemetryIndex<int>();
        index.Insert(Packet(1, BaseTime));

        Assert.Throws<ArgumentException>(() => index.QueryRangePage(
            BaseTime, BaseTime, 1, BaseTime, null));
        Assert.Throws<ArgumentException>(() => index.QueryRangePage(
            BaseTime, BaseTime, 1, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.QueryRangePage(
            BaseTime, BaseTime, 1, BaseTime, -1));
        Assert.Throws<ArgumentException>(() => index.QueryRangePage(
            BaseTime, BaseTime, 1, BaseTime, 1));
        Assert.Throws<ArgumentException>(() => index.QueryRangePage(
            BaseTime, BaseTime.AddSeconds(1), 1,
            BaseTime.AddSeconds(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.QueryRangePage(
            BaseTime, BaseTime, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.QueryRangePage(
            BaseTime, BaseTime, AvlTelemetryIndex<int>.MaxRangeLimit + 1));
        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public void QueryRangePage_EmptyIndexHasNoContinuation()
    {
        var index = new AvlTelemetryIndex<double>();
        AvlRangePage<double> result = index.QueryRangePage(
            BaseTime, BaseTime.AddHours(1));

        Assert.Empty(result.Entries);
        Assert.False(result.HasMore);
    }

    [Fact]
    public void QueryRangePage_ReturnedListIsNotMutableOrAffectedByNewPackets()
    {
        var index = new AvlTelemetryIndex<int>();
        index.Insert(Packet(1, BaseTime));
        index.Insert(Packet(2, BaseTime.AddSeconds(1)));

        AvlRangePage<int> snapshot = index.QueryRangePage(
            BaseTime, BaseTime.AddSeconds(10), 2);
        index.Insert(Packet(3, BaseTime.AddSeconds(2)));

        Assert.Equal(new[] { 1, 2 }, snapshot.Entries.Select(e => e.Packet.Payload));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AvlRangeEntry<int>>)snapshot.Entries).Clear());
        Assert.Equal(3, index.Count);
    }

    [Fact]
    public void QueryRangePage_ThousandsOfSameTimestamp_AllPagesAreAddressable()
    {
        var index = new AvlTelemetryIndex<int>();
        const int total = 10_050;
        const int pageSize = 731;

        for (int i = 0; i < total; i++)
        {
            index.Insert(Packet(i, BaseTime));
        }

        DateTimeOffset? time = null;
        int? sequence = null;
        int expectedValue = 0;
        do
        {
            AvlRangePage<int> page = index.QueryRangePage(
                BaseTime, BaseTime, pageSize, time, sequence);

            foreach (AvlRangeEntry<int> entry in page.Entries)
            {
                Assert.Equal(expectedValue, entry.Packet.Payload);
                Assert.Equal(expectedValue, entry.TimestampSequence);
                expectedValue++;
            }

            if (!page.HasMore)
            {
                break;
            }

            AvlRangeEntry<int> last = page.Entries[^1];
            time = last.Packet.CapturedAtUtc;
            sequence = last.TimestampSequence;
        }
        while (true);

        Assert.Equal(total, expectedValue);
        Assert.Equal(1, index.DistinctTimestampCount);
        Assert.True(index.HasValidAvlInvariants());
    }

    private static TelemetryPacket<int> Packet(int value, DateTimeOffset time) =>
        new(Device, value, time);
}