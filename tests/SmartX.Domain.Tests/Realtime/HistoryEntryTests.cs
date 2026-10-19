using SmartX.Domain.Commands;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Realtime;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

namespace SmartX.Domain.Tests.Realtime;

/// <summary>
/// Verifies that operational history retains exact typed source events,
/// original command audit links, UTC timestamps and immutable order metadata.
/// </summary>
public sealed class HistoryEntryTests
{
    private static readonly Guid TargetId = Guid.Parse("936f16ef-d64b-4870-8d5d-8e4cb242fe85");
    private static readonly DateTimeOffset BaseTime = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly DeviceIdentifier Source = DeviceIdentifier.FromUniqueIdentifier("history-domain-device");

    [Fact]
    public void FromProcessedTelemetry_RetainsExactNativeIntegerPacketAndDispatchMetadata()
    {
        var packet = new TelemetryPacket<int>(Source, 123, BaseTime);
        var item = new TelemetryWorkItem<int>(TargetId, packet, DashboardIssueSeverity.Critical,
            BaseTime.AddSeconds(1), 19);
        var dispatch = new TelemetryDispatch(item, 2);

        HistoryEntry entry = HistoryEntry.FromProcessedTelemetry(dispatch, BaseTime.AddSeconds(3));

        Assert.Equal("telemetry:00000000000000000019", entry.EventId);
        Assert.Equal(TargetId, entry.TargetDeviceId);
        Assert.Equal(HistoryEventKind.TelemetryProcessed, entry.Kind);
        Assert.Equal(BaseTime, entry.OccurredAtUtc);
        Assert.Equal(BaseTime.AddSeconds(3), entry.RecordedAtUtc);
        Assert.Equal(0, entry.Sequence);
        Assert.Null(entry.Command);
        Assert.Same(dispatch, entry.TelemetryDispatch);
        Assert.Same(packet, Assert.IsType<TelemetryWorkItem<int>>(entry.TelemetryDispatch!.WorkItem).Packet);
        Assert.Equal(123, Assert.IsType<TelemetryWorkItem<int>>(entry.TelemetryDispatch.WorkItem).Packet.Payload);
        Assert.Equal(19, entry.TelemetryDispatch.WorkItem.ArrivalSequence);
        Assert.Equal(2, entry.TelemetryDispatch.DequeueSequence);
        Assert.Equal(TelemetryProcessingLane.Critical, entry.TelemetryDispatch.WorkItem.Lane);
    }

    [Fact]
    public void FromProcessedTelemetry_PreservesFloatingAndBooleanPayloadTypes()
    {
        var floating = CreateDispatch(13.75, 1, BaseTime);
        var boolean = CreateDispatch(false, 2, BaseTime.AddSeconds(1));

        HistoryEntry first = HistoryEntry.FromProcessedTelemetry(floating, BaseTime.AddMinutes(1));
        HistoryEntry second = HistoryEntry.FromProcessedTelemetry(boolean, BaseTime.AddMinutes(1));

        Assert.Equal(13.75,
            Assert.IsType<TelemetryWorkItem<double>>(first.TelemetryDispatch!.WorkItem).Packet.Payload);
        Assert.False(
            Assert.IsType<TelemetryWorkItem<bool>>(second.TelemetryDispatch!.WorkItem).Packet.Payload);
        Assert.Equal(TelemetryDataType.Float, first.TelemetryDispatch.WorkItem.DataType);
        Assert.Equal(TelemetryDataType.Boolean, second.TelemetryDispatch.WorkItem.DataType);
    }

    [Fact]
    public void FromCommand_PreservesOriginalAndLinkedReversalAsDistinctEvents()
    {
        CommandRecord original = new(Guid.NewGuid(), TargetId, RelayCommandType.RelayOn,
            BaseTime, RelayState.Off, RelayState.On);
        CommandRecord reversal = new(Guid.NewGuid(), TargetId, RelayCommandType.RelayOff,
            BaseTime.AddSeconds(1), RelayState.On, RelayState.Off, original.Id);

        HistoryEntry command = HistoryEntry.FromCommand(original);
        HistoryEntry undo = HistoryEntry.FromCommand(reversal);

        Assert.Equal(HistoryEventKind.RelayCommand, command.Kind);
        Assert.Equal(HistoryEventKind.RelayUndo, undo.Kind);
        Assert.Equal($"command:{original.Id:D}", command.EventId);
        Assert.Equal($"command:{reversal.Id:D}", undo.EventId);
        Assert.Same(original, command.Command);
        Assert.Same(reversal, undo.Command);
        Assert.Equal(original.Id, undo.Command!.ReversesCommandId);
        Assert.Equal(RelayState.Off, undo.Command.ResultingState);
        Assert.Null(command.TelemetryDispatch);
        Assert.Null(undo.TelemetryDispatch);
    }

    [Fact]
    public void FromProcessedTelemetry_NormalisesOffsetOccurrenceAndProcessingTimes()
    {
        var captureOffset = BaseTime.ToOffset(TimeSpan.FromHours(2));
        var processedOffset = BaseTime.AddSeconds(3).ToOffset(TimeSpan.FromHours(-4));
        var dispatch = CreateDispatch(5, 1, captureOffset);

        HistoryEntry entry = HistoryEntry.FromProcessedTelemetry(dispatch, processedOffset);

        Assert.Equal(BaseTime, entry.OccurredAtUtc);
        Assert.Equal(BaseTime.AddSeconds(3), entry.RecordedAtUtc);
        Assert.Equal(TimeSpan.Zero, entry.OccurredAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, entry.RecordedAtUtc.Offset);
    }

    [Fact]
    public void WithSequence_ReturnsAnImmutableCopyAndCannotResequence()
    {
        var source = HistoryEntry.FromProcessedTelemetry(CreateDispatch(7, 1, BaseTime), BaseTime);

        HistoryEntry stored = source.WithSequence(15);

        Assert.Equal(0, source.Sequence);
        Assert.Equal(15, stored.Sequence);
        Assert.Equal(source.EventId, stored.EventId);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.WithSequence(0));
        Assert.Throws<InvalidOperationException>(() => stored.WithSequence(16));
    }

    [Fact]
    public void FromProcessedTelemetry_RejectsInvalidDispatchOrTimestamp()
    {
        var valid = CreateDispatch(3, 1, BaseTime);
        var invalidSequence = new TelemetryDispatch(valid.WorkItem, 0);

        Assert.Throws<ArgumentNullException>(() =>
            HistoryEntry.FromProcessedTelemetry(null!, BaseTime));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HistoryEntry.FromProcessedTelemetry(invalidSequence, BaseTime));
        Assert.Throws<ArgumentException>(() =>
            HistoryEntry.FromProcessedTelemetry(valid, default));
        Assert.Throws<ArgumentNullException>(() => HistoryEntry.FromCommand(null!));
    }

    [Fact]
    public void MatchesSource_DeduplicatesRepeatedCallbackButRejectsChangedPayload()
    {
        var original = HistoryEntry.FromProcessedTelemetry(
            CreateDispatch(10, 42, BaseTime), BaseTime.AddSeconds(1));
        var callback = HistoryEntry.FromProcessedTelemetry(
            CreateDispatch(10, 42, BaseTime), BaseTime.AddSeconds(9));
        var changed = HistoryEntry.FromProcessedTelemetry(
            CreateDispatch(11, 42, BaseTime), BaseTime.AddSeconds(1));

        Assert.True(original.MatchesSource(callback));
        Assert.False(original.MatchesSource(changed));
    }

    private static TelemetryDispatch CreateDispatch<T>(T payload, long arrivalSequence,
        DateTimeOffset capturedAtUtc) where T : struct
    {
        var packet = new TelemetryPacket<T>(Source, payload, capturedAtUtc);
        var item = new TelemetryWorkItem<T>(TargetId, packet, DashboardIssueSeverity.Information,
            BaseTime.AddSeconds(1), arrivalSequence);
        return new TelemetryDispatch(item, arrivalSequence);
    }
}