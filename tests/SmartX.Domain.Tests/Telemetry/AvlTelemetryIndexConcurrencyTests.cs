using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

using Xunit;

namespace SmartX.Domain.Tests.Telemetry;

/// <summary>
/// Exercises the P3-02 AVL's per-index synchronisation and snapshot isolation.
/// Each test owns its indexes, uses reproducible packet values, and checks
/// observable state rather than scheduling-dependent operation order or speed.
/// </summary>
public sealed class AvlTelemetryIndexConcurrencyTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);

    private static readonly DeviceIdentifier Device =
        DeviceIdentifier.FromUniqueIdentifier("avl-concurrency-fixture");

    [Fact]
    public async Task ParallelWriters_WithDistinctTimestamps_PreserveEveryPacket()
    {
        const int writers = 6;
        const int packetsPerWriter = 900;
        const int total = writers * packetsPerWriter;

        var index = new AvlTelemetryIndex<int>();
        var start = NewStartSignal();

        // Each writer advances through a different interleaved sequence.
        // The global insertion order is deliberately nondeterministic, but
        // chronological retrieval must contain every unique packet once.
        Task[] jobs = Enumerable.Range(0, writers).Select(writer => Task.Run(async () =>
        {
            await start.Task;

            for (int sequence = 0; sequence < packetsPerWriter; sequence++)
            {
                int value = (sequence * writers) + writer;
                index.Insert(Packet(value, value));

                if (sequence % 64 == 0)
                {
                    await Task.Yield();
                }
            }
        })).ToArray();

        start.SetResult(true);
        await Task.WhenAll(jobs);

        IReadOnlyList<TelemetryPacket<int>> result = index.QueryRange(
            BaseTime, BaseTime.AddMilliseconds(total - 1), limit: total);

        Assert.Equal(total, index.Count);
        Assert.Equal(total, index.DistinctTimestampCount);
        Assert.Equal(total, result.Count);
        Assert.Equal(Enumerable.Range(0, total), result.Select(packet => packet.Payload));
        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public async Task ParallelWriters_OnOneTimestamp_RetainEachProducerLocalOrder()
    {
        const int writers = 6;
        const int packetsPerWriter = 400;
        const int producerStride = 10_000;
        const int total = writers * packetsPerWriter;

        var index = new AvlTelemetryIndex<int>();
        var start = NewStartSignal();

        Task[] jobs = Enumerable.Range(0, writers).Select(writer => Task.Run(async () =>
        {
            await start.Task;

            for (int sequence = 0; sequence < packetsPerWriter; sequence++)
            {
                int value = (writer * producerStride) + sequence;
                DateTimeOffset capturedAt = BaseTime.ToOffset(
                    TimeSpan.FromHours(writer - 3));

                // Different offsets represent the same UTC instant. The
                // writer's own append order must remain stable in its bucket.
                index.Insert(new TelemetryPacket<int>(Device, value, capturedAt));

                if (sequence % 64 == 0)
                {
                    await Task.Yield();
                }
            }
        })).ToArray();

        start.SetResult(true);
        await Task.WhenAll(jobs);

        IReadOnlyList<TelemetryPacket<int>> result = index.QueryRange(
            BaseTime, BaseTime, limit: total);

        Assert.Equal(total, index.Count);
        Assert.Equal(1, index.DistinctTimestampCount);
        Assert.Equal(1, index.Height);
        Assert.Equal(total, result.Count);
        Assert.Equal(total, result.Select(packet => packet.Payload).Distinct().Count());

        for (int writer = 0; writer < writers; writer++)
        {
            int localWriter = writer;
            int[] observedOrder = result
                .Where(packet => packet.Payload / producerStride == localWriter)
                .Select(packet => packet.Payload % producerStride)
                .ToArray();

            // Cross-writer scheduling is intentionally NOT asserted: only
            // ordering within each sequential writer is guaranteed.
            Assert.Equal(Enumerable.Range(0, packetsPerWriter), observedOrder);
        }

        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public async Task SimultaneousReadersAndWriters_SeeBoundedOrderedSnapshots()
    {
        const int seedCount = 128;
        const int writers = 4;
        const int packetsPerWriter = 600;
        const int readers = 4;
        const int readsPerReader = 75;
        const int queryLimit = 512;
        const int total = seedCount + (writers * packetsPerWriter);

        var index = new AvlTelemetryIndex<int>();
        for (int i = 0; i < seedCount; i++)
        {
            index.Insert(Packet(i, i));
        }

        var start = NewStartSignal();

        Task[] writeJobs = Enumerable.Range(0, writers).Select(writer => Task.Run(async () =>
        {
            await start.Task;

            for (int sequence = 0; sequence < packetsPerWriter; sequence++)
            {
                int value = seedCount + (sequence * writers) + writer;
                index.Insert(Packet(value, value));

                if (sequence % 16 == 0)
                {
                    await Task.Yield();
                }
            }
        })).ToArray();

        Task[] readJobs = Enumerable.Range(0, readers).Select(_ => Task.Run(async () =>
        {
            await start.Task;

            for (int read = 0; read < readsPerReader; read++)
            {
                IReadOnlyList<TelemetryPacket<int>> snapshot = index.QueryRange(
                    BaseTime, BaseTime.AddMilliseconds(total), limit: queryLimit);

                // A query must never reveal an intermediate rotation, an
                // unsorted bucket, a duplicate packet, or a mutable tree view.
                Assert.InRange(snapshot.Count, seedCount, queryLimit);
                Assert.Equal(snapshot.Count,
                    snapshot.Select(packet => packet.Payload).Distinct().Count());

                for (int item = 1; item < snapshot.Count; item++)
                {
                    Assert.True(snapshot[item - 1].CapturedAtUtc <=
                        snapshot[item].CapturedAtUtc);
                }

                if (read % 5 == 0)
                {
                    await Task.Yield();
                }
            }
        })).ToArray();

        // Release all workers together without depending on which thread wins.
        start.SetResult(true);
        await Task.WhenAll(writeJobs.Concat(readJobs));

        Assert.Equal(total, index.Count);
        Assert.Equal(total, index.DistinctTimestampCount);
        Assert.True(index.HasValidAvlInvariants());

        IReadOnlyList<TelemetryPacket<int>> finalSnapshot = index.QueryRange(
            BaseTime, BaseTime.AddMilliseconds(total), limit: total);

        Assert.Equal(Enumerable.Range(0, total),
            finalSnapshot.Select(packet => packet.Payload));
    }

    [Fact]
    public async Task EarlierSnapshot_RemainsReadOnlyAfterParallelWrites()
    {
        const int initialCount = 100;
        const int writers = 4;
        const int packetsPerWriter = 175;

        var index = new AvlTelemetryIndex<int>();
        TelemetryPacket<int>[] originals = Enumerable.Range(0, initialCount)
            .Select(value => Packet(value, value))
            .ToArray();

        foreach (TelemetryPacket<int> packet in originals)
        {
            index.Insert(packet);
        }

        IReadOnlyList<TelemetryPacket<int>> snapshot = index.QueryRange(
            BaseTime, BaseTime.AddHours(1), limit: initialCount);

        Task[] jobs = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            for (int i = 0; i < packetsPerWriter; i++)
            {
                int value = initialCount + (i * writers) + writer;
                index.Insert(Packet(value, value));
            }
        })).ToArray();

        await Task.WhenAll(jobs);

        Assert.Equal(initialCount, snapshot.Count);
        for (int i = 0; i < initialCount; i++)
        {
            Assert.Same(originals[i], snapshot[i]);
        }

        Assert.Throws<NotSupportedException>(() =>
            ((IList<TelemetryPacket<int>>)snapshot).Clear());

        Assert.Equal(initialCount + (writers * packetsPerWriter), index.Count);
        Assert.True(index.HasValidAvlInvariants());
    }

    [Fact]
    public async Task IndependentTypedIndexes_IsolateParallelSensorSeries()
    {
        const int readings = 800;
        var integerIndex = new AvlTelemetryIndex<int>();
        var booleanIndex = new AvlTelemetryIndex<bool>();
        DeviceIdentifier otherDevice = DeviceIdentifier.FromUniqueIdentifier(
            "avl-boolean-series");

        Task integerWriter = Task.Run(() =>
        {
            for (int i = 0; i < readings; i++)
            {
                integerIndex.Insert(Packet(i, i));
            }
        });

        Task booleanWriter = Task.Run(() =>
        {
            for (int i = 0; i < readings; i++)
            {
                booleanIndex.Insert(new TelemetryPacket<bool>(
                    otherDevice, i % 2 == 0, BaseTime.AddMilliseconds(i)));
            }
        });

        await Task.WhenAll(integerWriter, booleanWriter);

        Assert.Equal(readings, integerIndex.Count);
        Assert.Equal(readings, booleanIndex.Count);
        Assert.Equal(0, integerIndex.QueryRange(
            BaseTime, BaseTime, limit: 1)[0].Payload);
        Assert.True(booleanIndex.QueryRange(
            BaseTime, BaseTime, limit: 1)[0].Payload);
        Assert.True(integerIndex.HasValidAvlInvariants());
        Assert.True(booleanIndex.HasValidAvlInvariants());
    }

    [Fact]
    public void DeterministicLargeFixture_ProvesNarrowQueryAndBalancedHeight()
    {
        const int distinctTimestamps = 25_000;
        const int duplicatePackets = 2_000;
        const int permutationMultiplier = 13_337;
        var index = new AvlTelemetryIndex<int>();

        // The multiplier is coprime to 25,000, so this deterministically
        // visits every millisecond key exactly once in a non-sorted order.
        for (int insertion = 0; insertion < distinctTimestamps; insertion++)
        {
            int key = (insertion * permutationMultiplier) % distinctTimestamps;
            index.Insert(Packet(key, key));
        }

        // Add one more reading at each of the first 2,000 timestamps. This
        // stresses buckets without creating new AVL nodes or increasing height.
        for (int key = 0; key < duplicatePackets; key++)
        {
            index.Insert(Packet(distinctTimestamps + key, key));
        }

        DateTimeOffset from = BaseTime.AddMilliseconds(10_000);
        DateTimeOffset to = BaseTime.AddMilliseconds(10_049);
        IReadOnlyList<TelemetryPacket<int>> narrow = index.QueryRange(
            from, to, limit: 50);
        IReadOnlyList<TelemetryPacket<int>> collision = index.QueryRange(
            BaseTime.AddMilliseconds(42), BaseTime.AddMilliseconds(42));

        Assert.Equal(distinctTimestamps + duplicatePackets, index.Count);
        Assert.Equal(distinctTimestamps, index.DistinctTimestampCount);
        Assert.Equal(Enumerable.Range(10_000, 50),
            narrow.Select(packet => packet.Payload));
        Assert.Equal(new[] { 42, distinctTimestamps + 42 },
            collision.Select(packet => packet.Payload));
        Assert.InRange(index.Height, 1, 2 * (int)Math.Ceiling(
            Math.Log2(distinctTimestamps + 1)));
        Assert.True(index.HasValidAvlInvariants());

        // No stopwatch threshold here: timings vary across processors and
        // loads. P3-11 can benchmark this same deterministic fixture separately.
    }

    private static TelemetryPacket<int> Packet(int value, int millisecond)
    {
        return new TelemetryPacket<int>(
            Device, value, BaseTime.AddMilliseconds(millisecond));
    }

    private static TaskCompletionSource<bool> NewStartSignal()
    {
        return new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}