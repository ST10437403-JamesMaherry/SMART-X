using SmartX.Application.Realtime;
using SmartX.Domain.Commands;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Realtime;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

namespace SmartX.Application.Tests.Realtime;

/// <summary>
/// Verifies the operational-history recorder's source integrity,
/// idempotency and failure isolation.
///
/// These tests use a small in-memory probe for IRealtimeHistoryStore.
/// The actual SortedDictionary implementation is tested separately in
/// Infrastructure, and the complete live workflow in API integration tests.
/// </summary>
public sealed class OperationalHistoryRecorderTests
{
    private static readonly Guid TargetDeviceId =
        Guid.Parse("a6f627e0-b7c3-497e-a89d-9e4ce4f15302");

    private static readonly DateTimeOffset CaptureTime =
        new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static readonly DeviceIdentifier SourceIdentifier =
        DeviceIdentifier.FromUniqueIdentifier(
            "history-recorder-test-device");

    [Fact]
    public void RecordProcessedTelemetry_PreservesNativePayloadAndSourceMetadata()
    {
        var store = new RecordingHistoryStore();
        var recorder = new OperationalHistoryRecorder(store);

        TelemetryDispatch dispatch =
            CreateIntegerDispatch(
                arrivalSequence: 9,
                dequeueSequence: 2,
                value: 0,
                capturedAtUtc: CaptureTime.ToOffset(
                    TimeSpan.FromHours(2)));

        DateTimeOffset processedAtUtc =
            CaptureTime.AddSeconds(5).ToOffset(
                TimeSpan.FromHours(-3));

        bool recorded = recorder.RecordProcessedTelemetry(
            dispatch,
            processedAtUtc);

        Assert.True(recorded);

        HistoryEntry entry =
            Assert.Single(store.Entries);

        Assert.Equal(
            HistoryEventKind.TelemetryProcessed,
            entry.Kind);

        Assert.Equal(
            "telemetry:00000000000000000009",
            entry.EventId);

        Assert.Equal(TargetDeviceId, entry.TargetDeviceId);

        Assert.Equal(CaptureTime, entry.OccurredAtUtc);

        Assert.Equal(
            CaptureTime.AddSeconds(5),
            entry.RecordedAtUtc);

        Assert.Equal(
            TimeSpan.Zero,
            entry.OccurredAtUtc.Offset);

        Assert.Equal(
            TimeSpan.Zero,
            entry.RecordedAtUtc.Offset);

        Assert.Equal(1L, entry.Sequence);

        Assert.Same(dispatch, entry.TelemetryDispatch);
        Assert.Null(entry.Command);

        TelemetryWorkItem<int> typed =
            Assert.IsType<TelemetryWorkItem<int>>(
                entry.TelemetryDispatch!.WorkItem);

        // Zero is a legitimate Integer reading, not a missing payload.
        Assert.Equal(0, typed.Packet.Payload);
        Assert.Equal(9L, typed.ArrivalSequence);
        Assert.Equal(2L, entry.TelemetryDispatch.DequeueSequence);

        Assert.Equal(
            TelemetryDataType.Integer,
            typed.DataType);

        Assert.Equal(
            TelemetryProcessingLane.Ordinary,
            typed.Lane);
    }

    [Fact]
    public void RecordProcessedTelemetry_RepeatedCallback_IsIdempotent()
    {
        var store = new RecordingHistoryStore();
        var recorder = new OperationalHistoryRecorder(store);

        TelemetryDispatch dispatch =
            CreateIntegerDispatch(
                arrivalSequence: 17,
                dequeueSequence: 3,
                value: 42,
                capturedAtUtc: CaptureTime);

        Assert.True(
            recorder.RecordProcessedTelemetry(
                dispatch,
                CaptureTime.AddSeconds(2)));

        // Recording timestamps may differ on a repeated callback.
        // The immutable original source identity and payload do not.
        Assert.False(
            recorder.RecordProcessedTelemetry(
                dispatch,
                CaptureTime.AddMinutes(1)));

        HistoryEntry entry =
            Assert.Single(store.Entries);

        Assert.Equal(1L, entry.Sequence);
        Assert.Equal(2, store.AppendAttempts);

        Assert.Equal(
            CaptureTime.AddSeconds(2),
            entry.RecordedAtUtc);

        Assert.Equal(
            17L,
            entry.TelemetryDispatch!.WorkItem.ArrivalSequence);
    }

    [Fact]
    public void RecordAcceptedCommand_RetainsOriginalNoOpAndLinkedUndo()
    {
        var store = new RecordingHistoryStore();
        var recorder = new OperationalHistoryRecorder(store);

        CommandRecord original = new(
            Guid.NewGuid(),
            TargetDeviceId,
            RelayCommandType.RelayOn,
            CaptureTime,
            RelayState.Off,
            RelayState.On);

        // An accepted no-op is operationally meaningful but does not
        // create a new undoable Stack entry.
        CommandRecord noOp = new(
            Guid.NewGuid(),
            TargetDeviceId,
            RelayCommandType.RelayOn,
            CaptureTime,
            RelayState.On,
            RelayState.On);

        CommandRecord reversal = new(
            Guid.NewGuid(),
            TargetDeviceId,
            RelayCommandType.RelayOff,
            CaptureTime,
            RelayState.On,
            RelayState.Off,
            original.Id);

        Assert.True(
            recorder.RecordAcceptedCommand(original));

        Assert.True(
            recorder.RecordAcceptedCommand(noOp));

        Assert.True(
            recorder.RecordAcceptedCommand(reversal));

        Assert.Equal(3, store.Entries.Count);

        Assert.Equal(
            new long[] { 1, 2, 3 },
            store.Entries.Select(entry => entry.Sequence));

        Assert.Equal(
            new[]
            {
                HistoryEventKind.RelayCommand,
                HistoryEventKind.RelayCommand,
                HistoryEventKind.RelayUndo
            },
            store.Entries.Select(entry => entry.Kind));

        Assert.Equal(
            original.Id,
            store.Entries[0].Command!.Id);

        Assert.True(
            store.Entries[0].Command!.StateChanged);

        Assert.False(
            store.Entries[1].Command!.StateChanged);

        Assert.True(
            store.Entries[2].Command!.IsReversal);

        Assert.Equal(
            original.Id,
            store.Entries[2].Command!.ReversesCommandId);

        Assert.Equal(
            RelayState.Off,
            store.Entries[2].Command!.ResultingState);

        Assert.All(
            store.Entries,
            entry => Assert.Equal(
                CaptureTime,
                entry.OccurredAtUtc));

        // Replaying the same successful source event cannot duplicate it.
        Assert.False(
            recorder.RecordAcceptedCommand(original));

        Assert.Equal(3, store.Entries.Count);
    }

    [Fact]
    public void ProjectionFailure_DoesNotEscapeOrReportCommittedOperationAsFailed()
    {
        var store = new RecordingHistoryStore
        {
            ThrowOnAppend = true
        };

        var recorder = new OperationalHistoryRecorder(store);

        CommandRecord original = new(
            Guid.NewGuid(),
            TargetDeviceId,
            RelayCommandType.RelayOn,
            CaptureTime,
            RelayState.Off,
            RelayState.On);

        // Simulate an unexpected secondary history storage failure.
        //
        // The original command has already been committed by the
        // authoritative actuator store. This projection must not turn
        // the successful operation into a misleading client failure.
        bool projected =
            recorder.RecordAcceptedCommand(original);

        Assert.False(projected);
        Assert.Empty(store.Entries);
        Assert.Equal(1, store.AppendAttempts);

        // Restoring the secondary history store allows the same
        // source event to be recorded without fabricating another
        // manual command execution.
        store.ThrowOnAppend = false;

        Assert.True(
            recorder.RecordAcceptedCommand(original));

        Assert.Single(store.Entries);
        Assert.Equal(2, store.AppendAttempts);

        // The identical successful source event is now idempotent.
        Assert.False(
            recorder.RecordAcceptedCommand(original));

        Assert.Single(store.Entries);
    }

    [Fact]
    public void ConflictingSourceIdentity_CannotReplaceExistingHistory()
    {
        var store = new RecordingHistoryStore();
        var recorder = new OperationalHistoryRecorder(store);

        Guid sourceCommandId = Guid.NewGuid();

        CommandRecord original = new(
            sourceCommandId,
            TargetDeviceId,
            RelayCommandType.RelayOn,
            CaptureTime,
            RelayState.Off,
            RelayState.On);

        // Same immutable command identity, but different source state.
        // The store rejects the conflicting reuse rather than updating
        // or erasing the original audited event.
        CommandRecord conflicting = new(
            sourceCommandId,
            TargetDeviceId,
            RelayCommandType.RelayOn,
            CaptureTime,
            RelayState.On,
            RelayState.On);

        Assert.True(
            recorder.RecordAcceptedCommand(original));

        Assert.False(
            recorder.RecordAcceptedCommand(conflicting));

        HistoryEntry retained =
            Assert.Single(store.Entries);

        Assert.Equal(
            RelayState.Off,
            retained.Command!.PreviousState);

        Assert.Equal(
            RelayState.On,
            retained.Command.ResultingState);

        Assert.True(retained.Command.StateChanged);
        Assert.Equal(1L, retained.Sequence);
    }

    [Fact]
    public void NullSources_AreRejectedBeforeAnyStoreMutation()
    {
        var store = new RecordingHistoryStore();
        var recorder = new OperationalHistoryRecorder(store);

        Assert.Throws<ArgumentNullException>(
            () => recorder.RecordAcceptedCommand(null!));

        Assert.Throws<ArgumentNullException>(
            () => recorder.RecordProcessedTelemetry(
                null!,
                CaptureTime));

        Assert.Empty(store.Entries);
        Assert.Equal(0, store.AppendAttempts);
    }

    /// <summary>
    /// Constructs the exact strongly typed packet and processing metadata
    /// accepted by P2-03. No object payload or JSON reconstruction is used.
    /// </summary>
    private static TelemetryDispatch CreateIntegerDispatch(
        long arrivalSequence,
        long dequeueSequence,
        int value,
        DateTimeOffset capturedAtUtc)
    {
        var packet = new TelemetryPacket<int>(
            SourceIdentifier,
            value,
            capturedAtUtc);

        var item = new TelemetryWorkItem<int>(
            TargetDeviceId,
            packet,
            DashboardIssueSeverity.Information,
            CaptureTime.AddSeconds(1),
            arrivalSequence);

        return new TelemetryDispatch(
            item,
            dequeueSequence);
    }

    /// <summary>
    /// A focused application-boundary probe.
    ///
    /// This intentionally does not duplicate the SortedDictionary
    /// implementation. Concrete chronological ordering, retention,
    /// device isolation and concurrency are covered by the existing
    /// Infrastructure tests and the new live API tests.
    /// </summary>
    private sealed class RecordingHistoryStore : IRealtimeHistoryStore
    {
        private readonly Dictionary<
            (Guid TargetDeviceId, string EventId),
            HistoryEntry> _bySource = new();

        private readonly List<HistoryEntry> _entries = [];

        public IReadOnlyList<HistoryEntry> Entries =>
            _entries.AsReadOnly();

        public int AppendAttempts { get; private set; }

        public bool ThrowOnAppend { get; set; }

        public bool TryAppend(HistoryEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            AppendAttempts++;

            if (ThrowOnAppend)
            {
                throw new InvalidOperationException(
                    "Injected operational-history projection failure.");
            }

            var identity = (
                entry.TargetDeviceId,
                entry.EventId);

            if (_bySource.TryGetValue(
                identity,
                out HistoryEntry? existing))
            {
                if (!existing.MatchesSource(entry))
                {
                    throw new InvalidOperationException(
                        "Conflicting reuse of a source-event identity.");
                }

                return false;
            }

            HistoryEntry stored =
                entry.WithSequence(_entries.Count + 1L);

            _entries.Add(stored);
            _bySource.Add(identity, stored);

            return true;
        }

        public RealtimeHistorySnapshot GetSnapshot(
            Guid targetDeviceId,
            int limit = IRealtimeHistoryStore.DefaultQueryLimit,
            HistorySortOrder order = HistorySortOrder.Ascending)
        {
            throw new NotSupportedException(
                "This probe validates recording only; " +
                "real chronological queries have separate coverage.");
        }
    }
}