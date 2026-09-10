using SmartX.Domain.Exceptions;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

using Xunit;

namespace SmartX.Domain.Tests.Telemetry;

public sealed class HistoricalTelemetryBufferTests
{
    private static readonly DateTimeOffset BaseTime =
        new(
            2026,
            9,
            10,
            10,
            0,
            0,
            TimeSpan.Zero);

    [Fact]
    public void FromJaggedBatches_WhenUneven_PreservesBatchAndSequenceOrder()
    {
        TelemetryPacket<int>[][] rawBatches =
        [
            [
                CreateIntegerPacket(10, 0),
                CreateIntegerPacket(11, 1)
            ],
            [
                CreateIntegerPacket(20, 2)
            ],
            [
                CreateIntegerPacket(30, 3),
                CreateIntegerPacket(31, 4),
                CreateIntegerPacket(32, 5)
            ]
        ];

        var history =
            HistoricalTelemetryBuffer<int>
                .FromJaggedBatches(rawBatches);

        Assert.Equal(6, history.Count);
        Assert.Equal(3, history.BatchCount);

        // The flattened history must preserve both outer batch order and
        // the sequence of packets inside each batch.
        Assert.Equal(10, history[0].Payload);
        Assert.Equal(11, history[1].Payload);
        Assert.Equal(20, history[2].Payload);
        Assert.Equal(30, history[3].Payload);
        Assert.Equal(31, history[4].Payload);
        Assert.Equal(32, history[5].Payload);
    }

    [Fact]
    public void FromJaggedBatches_PreservesOriginalBatchBoundaries()
    {
        TelemetryPacket<int>[][] rawBatches =
        [
            [
                CreateIntegerPacket(10, 0),
                CreateIntegerPacket(11, 1)
            ],
            [
                CreateIntegerPacket(20, 2)
            ],
            [
                CreateIntegerPacket(30, 3),
                CreateIntegerPacket(31, 4)
            ]
        ];

        var history =
            HistoricalTelemetryBuffer<int>
                .FromJaggedBatches(rawBatches);

        var first = history.GetBatchRange(0);
        var second = history.GetBatchRange(1);
        var third = history.GetBatchRange(2);

        Assert.Equal(0, first.StartIndex);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, first.EndExclusive);

        Assert.Equal(2, second.StartIndex);
        Assert.Equal(1, second.Count);
        Assert.Equal(3, second.EndExclusive);

        Assert.Equal(3, third.StartIndex);
        Assert.Equal(2, third.Count);
        Assert.Equal(5, third.EndExclusive);
    }

    [Fact]
    public void FromJaggedBatches_WhenOuterArrayEmpty_ReturnsEmptyHistory()
    {
        TelemetryPacket<int>[][] rawBatches = [];

        var history =
            HistoricalTelemetryBuffer<int>
                .FromJaggedBatches(rawBatches);

        Assert.Empty(history);
        Assert.Empty(history);
        Assert.Equal(0, history.BatchCount);
    }

    [Fact]
    public void FromJaggedBatches_WhenBatchEmpty_PreservesEmptyBatch()
    {
        TelemetryPacket<int>[][] rawBatches =
        [
            [
                CreateIntegerPacket(10, 0)
            ],
            [],
            [
                CreateIntegerPacket(20, 1)
            ]
        ];

        var history =
            HistoricalTelemetryBuffer<int>
                .FromJaggedBatches(rawBatches);

        Assert.Equal(2, history.Count);
        Assert.Equal(3, history.BatchCount);

        var emptyBatch = history.GetBatchRange(1);

        Assert.True(emptyBatch.IsEmpty);
        Assert.Equal(1, emptyBatch.StartIndex);
        Assert.Equal(0, emptyBatch.Count);

        Assert.Empty(history.EnumerateBatch(1));
    }

    [Fact]
    public void EnumerateBatch_ReturnsOnlyPacketsFromRequestedBatch()
    {
        TelemetryPacket<int>[][] rawBatches =
        [
            [
                CreateIntegerPacket(10, 0),
                CreateIntegerPacket(11, 1)
            ],
            [
                CreateIntegerPacket(20, 2),
                CreateIntegerPacket(21, 3)
            ]
        ];

        var history =
            HistoricalTelemetryBuffer<int>
                .FromJaggedBatches(rawBatches);

        var secondBatch =
            history
                .EnumerateBatch(1)
                .Select(packet => packet.Payload)
                .ToArray();

        Assert.Equal([20, 21], secondBatch);
    }

    [Fact]
    public void FromJaggedBatches_WhenOuterArrayNull_ThrowsMeaningfulException()
    {
        var exception =
            Assert.Throws<InvalidTelemetryBatchException>(
                () =>
                    HistoricalTelemetryBuffer<int>
                        .FromJaggedBatches(null));

        Assert.Contains(
            "cannot be null",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Null(exception.BatchIndex);
        Assert.Null(exception.SequenceIndex);
    }

    [Fact]
    public void FromJaggedBatches_WhenInnerBatchNull_IdentifiesBatch()
    {
        TelemetryPacket<int>[][] rawBatches =
        [
            [
                CreateIntegerPacket(10, 0)
            ],
            null!
        ];

        var exception =
            Assert.Throws<InvalidTelemetryBatchException>(
                () =>
                    HistoricalTelemetryBuffer<int>
                        .FromJaggedBatches(rawBatches));

        Assert.Equal(1, exception.BatchIndex);
        Assert.Null(exception.SequenceIndex);

        Assert.Contains(
            "batch 1",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromJaggedBatches_WhenPacketNull_IdentifiesExactPosition()
    {
        TelemetryPacket<int>[][] rawBatches =
        [
            [
                CreateIntegerPacket(10, 0),
                null!
            ]
        ];

        var exception =
            Assert.Throws<InvalidTelemetryBatchException>(
                () =>
                    HistoricalTelemetryBuffer<int>
                        .FromJaggedBatches(rawBatches));

        Assert.Equal(0, exception.BatchIndex);
        Assert.Equal(1, exception.SequenceIndex);

        Assert.Contains(
            "sequence 1",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromJaggedBatches_AfterConversion_IsIndependentOfArraySlots()
    {
        var originalPacket =
            CreateIntegerPacket(10, 0);

        TelemetryPacket<int>[][] rawBatches =
        [
            [
                originalPacket
            ]
        ];

        var history =
            HistoricalTelemetryBuffer<int>
                .FromJaggedBatches(rawBatches);

        // Replacing a slot in the original raw array must not replace the
        // corresponding entry already transferred into the List-backed
        // historical collection.
        rawBatches[0][0] =
            CreateIntegerPacket(999, 1);

        Assert.Same(originalPacket, history[0]);
        Assert.Equal(10, history[0].Payload);
    }

    [Fact]
    public void FromJaggedBatches_WithBooleanPayloads_RemainsGeneric()
    {
        var identifier = CreateIdentifier();

        TelemetryPacket<bool>[][] rawBatches =
        [
            [
                new TelemetryPacket<bool>(
                    identifier,
                    false,
                    BaseTime),

                new TelemetryPacket<bool>(
                    identifier,
                    true,
                    BaseTime.AddSeconds(1))
            ]
        ];

        var history =
            HistoricalTelemetryBuffer<bool>
                .FromJaggedBatches(rawBatches);

        bool firstState = history[0].Payload;
        bool secondState = history[1].Payload;

        Assert.False(firstState);
        Assert.True(secondState);
    }

    [Fact]
    public void GetBatchRange_WhenIndexInvalid_ThrowsArgumentOutOfRangeException()
    {
        TelemetryPacket<int>[][] rawBatches =
        [
            [
                CreateIntegerPacket(10, 0)
            ]
        ];

        var history =
            HistoricalTelemetryBuffer<int>
                .FromJaggedBatches(rawBatches);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => history.GetBatchRange(1));
    }

    private static TelemetryPacket<int> CreateIntegerPacket(
        int payload,
        int secondsAfterBaseTime)
    {
        return new TelemetryPacket<int>(
            CreateIdentifier(),
            payload,
            BaseTime.AddSeconds(secondsAfterBaseTime));
    }

    private static DeviceIdentifier CreateIdentifier()
    {
        return DeviceIdentifier.FromMacAddress(
            "AA:BB:CC:DD:EE:FF");
    }
}