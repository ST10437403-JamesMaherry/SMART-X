using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using SmartX.Api.Realtime;
using SmartX.Application.Realtime;
using SmartX.Contracts.Commands;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;
using SmartX.Infrastructure.Realtime;

namespace SmartX.Api.IntegrationTests.Realtime;

/// <summary>
/// Cross-feature regression tests for P2-05.
///
/// Three independent orders must remain correct:
/// 1. Part 1 stores telemetry in ingestion order.
/// 2. P2-03 dispatches telemetry in critical-first / ordinary-FIFO order.
/// 3. P2-05 displays events by their UTC occurrence timestamps.
///
/// All scenarios use the real HTTP API, registered sensor repository,
/// processing worker, actuator command service, Undo Stack, and
/// SortedDictionary-backed operational history.
/// </summary>
public sealed class RealtimeHistoryCompatibilityTests
{
    private static readonly DateTimeOffset CaptureBase =
        new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies that 151 real telemetry packets of each supported type
    /// reach operational history through the worker.
    ///
    /// Neither Part 1's recent-100 endpoint nor P2-03's recent-100
    /// diagnostic window may truncate the independent P2-05 timeline.
    /// </summary>
    [Theory]
    [InlineData("Float")]
    [InlineData("Integer")]
    [InlineData("Boolean")]
    public async Task JaggedBatchOver100_RetainsEveryTypedEvent_WithoutChangingPartOneHistory(
        string telemetryType)
    {
        await using var factory = new TelemetryProcessingScenarioFactory();
        using HttpClient client = CreateClient(factory);

        await SetOrdinaryDispatchPausedAsync(client, true);

        SensorResponse sensor = await RegisterAsync(
            client, "Environmental", telemetryType);

        IngestTelemetryRequest[] packets = Enumerable.Range(0, 151)
            .Select(index => CreateTypedReading(
                telemetryType,
                index,
                CaptureBase.AddSeconds(index)))
            .ToArray();

        // Uneven jagged batches, including one empty inner batch.
        IngestTelemetryBatchRequest batch = new(
            [
                packets[..47],
                [],
                packets[47..100],
                packets[100..]
            ]);

        using HttpResponseMessage batchResponse =
            await client.PostAsJsonAsync(
                $"/api/sensors/{sensor.Id}/telemetry/batches",
                batch);

        Assert.Equal(HttpStatusCode.Created, batchResponse.StatusCode);

        TelemetryBatchIngestionResponse acceptedBatch =
            await batchResponse.Content
                .ReadFromJsonAsync<TelemetryBatchIngestionResponse>()
            ?? throw new InvalidOperationException(
                "Batch ingestion response was missing.");

        Assert.Equal(sensor.Id, acceptedBatch.SensorProfileId);
        Assert.Equal(telemetryType, acceptedBatch.TelemetryDataType);
        Assert.Equal(4, acceptedBatch.BatchCount);
        Assert.Equal(151, acceptedBatch.AcceptedPacketCount);

        // Acceptance is not successful background processing.
        RealtimeHistoryResponse pendingHistory =
            await ReadHistoryAsync(client, sensor.Id);

        Assert.Equal(0L, pendingHistory.TotalRecordedEvents);
        Assert.Empty(pendingHistory.Events);

        TelemetryProcessingResponse pendingProcessing =
            await ReadProcessingAsync(client);

        Assert.Equal(151L, pendingProcessing.AcceptedCount);
        Assert.Equal(151, pendingProcessing.OrdinaryPendingCount);
        Assert.Equal(0L, pendingProcessing.DispatchedCount);
        Assert.Equal(0L, pendingProcessing.ProcessedCount);

        // Part 1 has already persisted all packets at acceptance.
        TelemetryReadingResponse[] partOneBefore =
            await ReadPartOneRecentAsync(client, sensor.Id, 100);

        Assert.Equal(100, partOneBefore.Length);

        for (int index = 0; index < 100; index++)
        {
            AssertTypedReading(
                partOneBefore[index],
                sensor.Id,
                telemetryType,
                packets[index + 51]);
        }

        await SetOrdinaryDispatchPausedAsync(client, false);

        RealtimeHistoryResponse history =
            await WaitForHistoryCountAsync(client, sensor.Id, 151);

        await WaitForProbeCountAsync(factory.Probe, 151);

        TelemetryProcessingResponse processing =
            await ReadProcessingAsync(client);

        Assert.Null(processing.Failure);
        Assert.Equal(151L, processing.AcceptedCount);
        Assert.Equal(151L, processing.DispatchedCount);
        Assert.Equal(151L, processing.ProcessedCount);
        Assert.Equal(0, processing.PendingCount);
        Assert.Equal(0L, processing.OutstandingDispatchedCount);

        // Existing P2-03 diagnostic history stays capped at 100.
        Assert.Equal(100, processing.RecentDispatches.Count);

        Assert.Equal(
            Enumerable.Range(52, 100).Select(value => (long)value),
            processing.RecentDispatches.Select(
                record => record.EnqueueSequence));

        // P2-05 retains the whole processed timeline.
        Assert.Equal(151L, history.TotalRecordedEvents);
        Assert.Equal(151, history.RetainedEventCount);
        Assert.Equal(10_000, history.RetentionCapacity);
        Assert.Equal(151, history.Events.Count);
        Assert.Equal("Ascending", history.Order);

        Assert.Equal(
            Enumerable.Range(1, 151).Select(value => (long)value),
            history.Events.Select(entry => entry.Sequence));

        Assert.Equal(
            151,
            history.Events.Select(entry => entry.EventId)
                .Distinct().Count());

        for (int index = 0; index < packets.Length; index++)
        {
            RealtimeHistoryEventResponse entry = history.Events[index];

            Assert.Equal("TelemetryProcessed", entry.Kind);
            Assert.Equal(sensor.Id, entry.TargetDeviceId);
            Assert.Null(entry.Command);

            Assert.Equal((long)index + 1, entry.EnqueueSequence);
            Assert.Equal((long)index + 1, entry.DequeueSequence);
            Assert.Equal("Ordinary", entry.ProcessingLane);

            Assert.Equal(
                packets[index].CapturedAtUtc!.Value.ToUniversalTime(),
                entry.OccurredAtUtc);

            Assert.True(entry.RecordedAtUtc >= entry.OccurredAtUtc);
            Assert.NotNull(entry.AcceptedAtUtc);

            TelemetryReadingResponse actual =
                Assert.IsType<TelemetryReadingResponse>(entry.Telemetry);

            AssertTypedReading(
                actual,
                sensor.Id,
                telemetryType,
                packets[index]);
        }

        RealtimeHistoryResponse defaultHistory =
            await ReadHistoryAsync(client, sensor.Id);

        Assert.Equal(100, defaultHistory.Events.Count);
        Assert.Equal(151L, defaultHistory.TotalRecordedEvents);
        Assert.Equal(151, defaultHistory.RetainedEventCount);

        Assert.Equal(52L, defaultHistory.Events[0].Sequence);
        Assert.Equal(151L, defaultHistory.Events[^1].Sequence);

        for (int index = 0; index < 100; index++)
        {
            Assert.Equal(
                partOneBefore[index],
                defaultHistory.Events[index].Telemetry);
        }

        TelemetryReadingResponse[] partOneAfter =
            await ReadPartOneRecentAsync(client, sensor.Id, 100);

        Assert.Equal(partOneBefore, partOneAfter);
        Assert.Equal(151, factory.Probe.Completed.Length);
    }

    /// <summary>
    /// Demonstrates that historical capture timestamps and equivalent
    /// UTC instants do not change Part 1's ingestion-order behaviour.
    /// </summary>
    [Fact]
    public async Task OutOfOrderAndEqualCaptureTimes_KeepPartOneIngestionOrder()
    {
        await using var factory = new TelemetryProcessingScenarioFactory();
        using HttpClient client = CreateClient(factory);

        await SetOrdinaryDispatchPausedAsync(client, true);

        SensorResponse sensor =
            await RegisterAsync(client, "Environmental", "Integer");

        IngestTelemetryRequest[] requests =
        [
            new(CaptureBase.AddSeconds(40), null, 10, null),
            new(
                CaptureBase.AddSeconds(10)
                    .ToOffset(TimeSpan.FromHours(2)),
                null, 11, null),
            new(
                CaptureBase.AddSeconds(10)
                    .ToOffset(TimeSpan.FromHours(-4)),
                null, 12, null),
            new(CaptureBase, null, 13, null),
            new(CaptureBase.AddSeconds(25), null, 14, null)
        ];

        foreach (IngestTelemetryRequest request in requests)
        {
            await SubmitTelemetryAsync(client, sensor.Id, request);
        }

        TelemetryReadingResponse[] partOneBefore =
            await ReadPartOneRecentAsync(client, sensor.Id, 100);

        Assert.Equal(
            new[] { 10, 11, 12, 13, 14 },
            partOneBefore.Select(reading => reading.IntegerValue!.Value));

        for (int index = 0; index < requests.Length; index++)
        {
            Assert.Equal(
                requests[index].CapturedAtUtc!.Value.ToUniversalTime(),
                partOneBefore[index].CapturedAtUtc);
        }

        Assert.Empty(
            (await ReadHistoryAsync(client, sensor.Id)).Events);

        await SetOrdinaryDispatchPausedAsync(client, false);

        RealtimeHistoryResponse chronology =
            await WaitForHistoryCountAsync(client, sensor.Id, 5);

        // Sorted by capture time: 0s, 10s, 10s, 25s, 40s.
        Assert.Equal(
            new[] { 13, 11, 12, 14, 10 },
            chronology.Events.Select(
                entry => entry.Telemetry!.IntegerValue!.Value));

        // Store sequence remains based on actual processing order.
        Assert.Equal(
            new long[] { 4, 2, 3, 5, 1 },
            chronology.Events.Select(entry => entry.Sequence));

        Assert.Equal(
            chronology.Events[1].OccurredAtUtc,
            chronology.Events[2].OccurredAtUtc);

        Assert.NotEqual(
            chronology.Events[1].EventId,
            chronology.Events[2].EventId);

        Assert.All(
            chronology.Events,
            entry => Assert.Equal(
                TimeSpan.Zero,
                entry.OccurredAtUtc.Offset));

        RealtimeHistoryResponse ascending =
            await ReadHistoryAsync(
                client, sensor.Id, "?limit=3&order=asc");

        RealtimeHistoryResponse descending =
            await ReadHistoryAsync(
                client, sensor.Id, "?limit=3&order=desc");

        Assert.Equal(
            new[] { 12, 14, 10 },
            ascending.Events.Select(
                entry => entry.Telemetry!.IntegerValue!.Value));

        Assert.Equal(
            new[] { 10, 14, 12 },
            descending.Events.Select(
                entry => entry.Telemetry!.IntegerValue!.Value));

        TelemetryReadingResponse[] lastThree =
            await ReadPartOneRecentAsync(client, sensor.Id, 3);

        Assert.Equal(
            new[] { 12, 13, 14 },
            lastThree.Select(
                reading => reading.IntegerValue!.Value));

        Assert.Equal(
            partOneBefore,
            await ReadPartOneRecentAsync(client, sensor.Id, 100));
    }

    /// <summary>
    /// Verifies three distinct real ordering guarantees:
    ///
    /// Ingestion:  A, B, C
    /// Processing: C, A, B
    /// Chronology: A, C, B
    ///
    /// Important classifier invariant:
    /// A newly accepted packet whose capture timestamp is older than the
    /// previous accepted packet is not eligible for spike classification.
    ///
    /// Therefore B and C intentionally share the SAME capture timestamp.
    /// C arrives later with a critical value, remains eligible for spike
    /// classification, and bypasses the paused ordinary lane.
    ///
    /// The SortedDictionary stores C and B in one timestamp bucket and
    /// preserves both in stable processing-insertion order.
    /// </summary>
    [Fact]
    public async Task CriticalBypass_ProducesThreeDistinctCorrectOrders()
    {
        await using var factory = new TelemetryProcessingScenarioFactory();
        using HttpClient client = CreateClient(factory);

        await SetOrdinaryDispatchPausedAsync(client, true);

        SensorResponse sensor =
            await RegisterAsync(client, "Environmental", "Integer");

        // A: ordinary, captured at +10 seconds.
        await SubmitTelemetryAsync(
            client,
            sensor.Id,
            new IngestTelemetryRequest(
                CaptureBase.AddSeconds(10),
                null,
                10,
                null));

        // B: ordinary, captured at +30 seconds.
        await SubmitTelemetryAsync(
            client,
            sensor.Id,
            new IngestTelemetryRequest(
                CaptureBase.AddSeconds(30),
                null,
                11,
                null));

        TelemetryProcessingResponse pending =
            await ReadProcessingAsync(client);

        Assert.Equal(2, pending.OrdinaryPendingCount);
        Assert.Equal(0, pending.CriticalPendingCount);
        Assert.Equal(0L, pending.DispatchedCount);

        // C: critical, captured at the SAME instant as B.
        //
        // It arrives after B. Because its timestamp is equal rather than
        // older, the classifier is allowed to compare 11 -> 100.
        //
        // Relative change is 89%, above the existing 50% critical
        // threshold, so C enters the real PriorityQueue.
        await SubmitTelemetryAsync(
            client,
            sensor.Id,
            new IngestTelemetryRequest(
                CaptureBase.AddSeconds(30),
                null,
                100,
                null));

        // With ordinary dispatch paused, only genuine critical work
        // can be completed. No artificial history rows are inserted.
        RealtimeHistoryResponse criticalCompleted =
            await WaitForHistoryCountAsync(
                client,
                sensor.Id,
                1);

        RealtimeHistoryEventResponse critical =
            Assert.Single(criticalCompleted.Events);

        Assert.Equal("TelemetryProcessed", critical.Kind);
        Assert.Equal(100, critical.Telemetry!.IntegerValue);
        Assert.Equal("Critical", critical.ProcessingLane);
        Assert.Equal("Critical", critical.Severity);
        Assert.Equal(3L, critical.EnqueueSequence);
        Assert.Equal(1L, critical.DequeueSequence);
        Assert.Equal(1L, critical.Sequence);

        Assert.Equal(
            CaptureBase.AddSeconds(30),
            critical.OccurredAtUtc);

        TelemetryProcessingResponse bypass =
            await ReadProcessingAsync(client);

        Assert.Equal(1L, bypass.ProcessedCount);
        Assert.Equal(2, bypass.OrdinaryPendingCount);
        Assert.True(bypass.OrdinaryDispatchPaused);

        // Let the genuine FIFO queue finish A followed by B.
        await SetOrdinaryDispatchPausedAsync(client, false);

        RealtimeHistoryResponse chronology =
            await WaitForHistoryCountAsync(
                client,
                sensor.Id,
                3);

        await WaitForProbeCountAsync(factory.Probe, 3);

        TelemetryProcessingResponse processing =
            await ReadProcessingAsync(client);

        // -------------------------------------------------------------
        // Order 1: actual worker dispatch = C, A, B
        // -------------------------------------------------------------
        Assert.Equal(
            new[] { 100, 10, 11 },
            processing.RecentDispatches.Select(
                record => record.Telemetry.IntegerValue!.Value));

        Assert.Equal(
            new long[] { 3, 1, 2 },
            processing.RecentDispatches.Select(
                record => record.EnqueueSequence));

        Assert.Equal(
            new long[] { 1, 2, 3 },
            processing.RecentDispatches.Select(
                record => record.DequeueSequence));

        Assert.Equal(
            new[] { "Critical", "Ordinary", "Ordinary" },
            processing.RecentDispatches.Select(
                record => record.Lane));

        Assert.Equal(
            new[] { true, false, false },
            processing.RecentDispatches.Select(
                record => record.UpdatedLatestObservation));

        // -------------------------------------------------------------
        // Order 2: original Part 1 ingestion = A, B, C
        // -------------------------------------------------------------
        TelemetryReadingResponse[] partOne =
            await ReadPartOneRecentAsync(
                client,
                sensor.Id,
                100);

        Assert.Equal(
            new[] { 10, 11, 100 },
            partOne.Select(
                reading => reading.IntegerValue!.Value));

        Assert.Equal(
            new[]
            {
                CaptureBase.AddSeconds(10),
                CaptureBase.AddSeconds(30),
                CaptureBase.AddSeconds(30)
            },
            partOne.Select(reading => reading.CapturedAtUtc));

        // -------------------------------------------------------------
        // Order 3: SortedDictionary chronology = A, C, B
        // -------------------------------------------------------------
        Assert.Equal(
            new[] { 10, 100, 11 },
            chronology.Events.Select(
                entry => entry.Telemetry!.IntegerValue!.Value));

        Assert.Equal(
            new long[] { 2, 1, 3 },
            chronology.Events.Select(
                entry => entry.Sequence));

        Assert.Equal(
            new long[] { 1, 3, 2 },
            chronology.Events.Select(
                entry => entry.EnqueueSequence!.Value));

        Assert.Equal(
            new[]
            {
                CaptureBase.AddSeconds(10),
                CaptureBase.AddSeconds(30),
                CaptureBase.AddSeconds(30)
            },
            chronology.Events.Select(
                entry => entry.OccurredAtUtc));

        // B and C have the same UTC timestamp, but neither is lost.
        Assert.Equal(
            chronology.Events[1].OccurredAtUtc,
            chronology.Events[2].OccurredAtUtc);

        Assert.NotEqual(
            chronology.Events[1].EventId,
            chronology.Events[2].EventId);

        Assert.Equal(
            3,
            chronology.Events
                .Select(entry => entry.EventId)
                .Distinct()
                .Count());

        Assert.Equal(3L, chronology.TotalRecordedEvents);
        Assert.Equal(3, chronology.RetainedEventCount);

        // -------------------------------------------------------------
        // Latest observation recency:
        //
        // B and C share a capture time. C has the greater arrival
        // sequence, so C remains the latest processed observation
        // even though B was processed after C.
        // -------------------------------------------------------------
        RealtimeSnapshotResponse snapshot =
            await client.GetFromJsonAsync<RealtimeSnapshotResponse>(
                "/api/realtime/snapshot")
            ?? throw new InvalidOperationException(
                "Real-time snapshot was missing.");

        RealtimeDeviceResponse device =
            Assert.Single(
                snapshot.Devices,
                entry => entry.Id == sensor.Id);

        Assert.Equal(
            100,
            device.LatestProcessedTelemetry!.IntegerValue);

        Assert.Equal("Online", device.Status);
        Assert.Null(processing.Failure);
        Assert.Equal(3L, processing.ProcessedCount);
        Assert.Equal(0, processing.PendingCount);
        Assert.Equal(0L, processing.OutstandingDispatchedCount);
    }

    /// <summary>
    /// Uses a fixed clock to prove actual telemetry, commands, no-ops
    /// and Undo reversals survive in one equal-timestamp bucket.
    /// </summary>
    [Fact]
    public async Task EqualTimeTelemetryCommandsAndUndo_RetainDistinctStableEvents()
    {
        DateTimeOffset fixedTimeUtc =
            DateTimeOffset.UtcNow
                .AddMinutes(-5)
                .ToUniversalTime();

        await using var factory = new ConfiguredHistoryFactory(
            fixedInstantUtc: fixedTimeUtc);

        using HttpClient client = CreateClient(factory);

        SensorResponse sensor =
            await RegisterOnlineActuatorAsync(client);

        DateTimeOffset offsetCapture =
            fixedTimeUtc.ToOffset(TimeSpan.FromHours(2));

        await SubmitTelemetryAsync(
            client,
            sensor.Id,
            new IngestTelemetryRequest(
                offsetCapture,
                null,
                null,
                false));

        await WaitForHistoryCountAsync(client, sensor.Id, 1);

        RelayCommandResponse original =
            await ExecuteCommandAsync(client, sensor.Id, "RelayOn");

        RelayCommandResponse noOp =
            await ExecuteCommandAsync(client, sensor.Id, "RelayOn");

        Assert.True(original.Command.StateChanged);
        Assert.False(noOp.Command.StateChanged);
        Assert.Equal(1, noOp.Actuator.UndoDepth);

        RelayCommandResponse reversal =
            await UndoCommandAsync(
                client,
                sensor.Id,
                original.Command.CommandId);

        Assert.Equal("Off", reversal.Actuator.CommandedState);
        Assert.Equal(0, reversal.Actuator.UndoDepth);

        RealtimeHistoryResponse history =
            await ReadHistoryAsync(client, sensor.Id);

        Assert.Equal(4L, history.TotalRecordedEvents);
        Assert.Equal(4, history.RetainedEventCount);
        Assert.Equal(4, history.Events.Count);

        Assert.Equal(
            new[]
            {
                "TelemetryProcessed",
                "RelayCommand",
                "RelayCommand",
                "RelayUndo"
            },
            history.Events.Select(entry => entry.Kind));

        Assert.All(
            history.Events,
            entry =>
            {
                Assert.Equal(
                    fixedTimeUtc,
                    entry.OccurredAtUtc);

                Assert.Equal(
                    TimeSpan.Zero,
                    entry.OccurredAtUtc.Offset);

                Assert.Equal(
                    TimeSpan.Zero,
                    entry.RecordedAtUtc.Offset);
            });

        Assert.Equal(
            new long[] { 1, 2, 3, 4 },
            history.Events.Select(entry => entry.Sequence));

        Assert.Equal(
            4,
            history.Events.Select(entry => entry.EventId)
                .Distinct().Count());

        RealtimeHistoryEventResponse telemetry =
            history.Events[0];

        Assert.False(telemetry.Telemetry!.BooleanValue);
        Assert.Null(telemetry.Command);

        RealtimeHistoryEventResponse command =
            history.Events[1];

        Assert.Equal(
            original.Command.CommandId,
            command.Command!.CommandId);

        Assert.Equal("Off", command.Command.PreviousState);
        Assert.Equal("On", command.Command.ResultingState);
        Assert.True(command.Command.StateChanged);

        RealtimeHistoryEventResponse noOpEvent =
            history.Events[2];

        Assert.Equal(
            noOp.Command.CommandId,
            noOpEvent.Command!.CommandId);

        Assert.False(noOpEvent.Command.StateChanged);

        Assert.Equal(
            original.Command.CommandId,
            noOp.Actuator.LatestUndoableCommandId);

        RealtimeHistoryEventResponse undoEvent =
            history.Events[3];

        Assert.True(undoEvent.Command!.IsReversal);

        Assert.Equal(
            reversal.Command.CommandId,
            undoEvent.Command.CommandId);

        Assert.Equal(
            original.Command.CommandId,
            undoEvent.Command.ReversesCommandId);

        Assert.Equal("On", undoEvent.Command.PreviousState);
        Assert.Equal("Off", undoEvent.Command.ResultingState);

        RealtimeHistoryResponse descending =
            await ReadHistoryAsync(
                client,
                sensor.Id,
                "?limit=4&order=desc");

        Assert.Equal(
            new long[] { 4, 3, 2, 1 },
            descending.Events.Select(entry => entry.Sequence));

        // Commands and reversals must not fabricate telemetry.
        TelemetryReadingResponse[] partOne =
            await ReadPartOneRecentAsync(client, sensor.Id, 100);

        TelemetryReadingResponse reported =
            Assert.Single(partOne);

        Assert.False(reported.BooleanValue);
        Assert.Equal(fixedTimeUtc, reported.CapturedAtUtc);
    }

    /// <summary>
    /// Verifies a pending telemetry packet is not recorded prematurely.
    /// After it eventually processes, its older capture timestamp places
    /// it before commands that were recorded earlier.
    /// </summary>
    [Fact]
    public async Task PendingTelemetry_CommandsAndUndo_PreserveCaptureChronology()
    {
        await using var factory = new TelemetryProcessingScenarioFactory();
        using HttpClient client = CreateClient(factory);

        await SetOrdinaryDispatchPausedAsync(client, true);

        SensorResponse sensor =
            await RegisterOnlineActuatorAsync(client);

        await SubmitTelemetryAsync(
            client,
            sensor.Id,
            new IngestTelemetryRequest(
                CaptureBase,
                null,
                null,
                false));

        TelemetryProcessingResponse pending =
            await ReadProcessingAsync(client);

        Assert.Equal(1, pending.OrdinaryPendingCount);
        Assert.Equal(0L, pending.ProcessedCount);

        Assert.Empty(
            (await ReadHistoryAsync(client, sensor.Id)).Events);

        RelayCommandResponse original =
            await ExecuteCommandAsync(
                client,
                sensor.Id,
                "RelayOn");

        RelayCommandResponse reversal =
            await UndoCommandAsync(
                client,
                sensor.Id,
                original.Command.CommandId);

        Assert.True(reversal.Command.IsReversal);
        Assert.Equal("Off", reversal.Actuator.CommandedState);

        RealtimeHistoryResponse whilePaused =
            await ReadHistoryAsync(client, sensor.Id);

        Assert.Equal(2L, whilePaused.TotalRecordedEvents);

        Assert.Equal(
            new[] { "RelayCommand", "RelayUndo" },
            whilePaused.Events.Select(entry => entry.Kind));

        await SetOrdinaryDispatchPausedAsync(client, false);

        RealtimeHistoryResponse completed =
            await WaitForHistoryCountAsync(client, sensor.Id, 3);

        Assert.Equal(
            new[]
            {
                "TelemetryProcessed",
                "RelayCommand",
                "RelayUndo"
            },
            completed.Events.Select(entry => entry.Kind));

        // The telemetry entered the history last but sorts first.
        Assert.Equal(
            new long[] { 3, 1, 2 },
            completed.Events.Select(entry => entry.Sequence));

        Assert.True(
            completed.Events[0].OccurredAtUtc <
            completed.Events[1].OccurredAtUtc);

        Assert.Equal(
            original.Command.CommandId,
            completed.Events[2].Command!.ReversesCommandId);

        TelemetryReadingResponse[] partOne =
            await ReadPartOneRecentAsync(client, sensor.Id, 100);

        Assert.False(Assert.Single(partOne).BooleanValue);

        RealtimeSnapshotResponse snapshot =
            await client.GetFromJsonAsync<RealtimeSnapshotResponse>(
                "/api/realtime/snapshot")
            ?? throw new InvalidOperationException(
                "Real-time snapshot was missing.");

        RealtimeDeviceResponse device =
            Assert.Single(
                snapshot.Devices,
                entry => entry.Id == sensor.Id);

        Assert.Equal("Online", device.Status);
        Assert.Equal("Off", device.CommandedActuator!.CommandedState);
        Assert.False(device.LatestProcessedTelemetry!.BooleanValue);
    }

    /// <summary>
    /// Uses an isolated three-event history capacity to verify that
    /// chronological eviction does not modify original Part 1 persistence.
    /// </summary>
    [Fact]
    public async Task SmallHistoryCapacity_EvictsOldestOccurrences_NotPartOneReadings()
    {
        await using var factory = new ConfiguredHistoryFactory(
            retentionCapacity: 3);

        using HttpClient client = CreateClient(factory);

        await SetOrdinaryDispatchPausedAsync(client, true);

        SensorResponse sensor =
            await RegisterAsync(client, "Environmental", "Integer");

        int[] captureSeconds = [2, 5, 0, 3, 4, 1];

        for (int index = 0; index < captureSeconds.Length; index++)
        {
            await SubmitTelemetryAsync(
                client,
                sensor.Id,
                new IngestTelemetryRequest(
                    CaptureBase.AddSeconds(captureSeconds[index]),
                    null,
                    10 + index,
                    null));
        }

        TelemetryReadingResponse[] persistedBefore =
            await ReadPartOneRecentAsync(client, sensor.Id, 100);

        Assert.Equal(
            new[] { 10, 11, 12, 13, 14, 15 },
            persistedBefore.Select(
                reading => reading.IntegerValue!.Value));

        await SetOrdinaryDispatchPausedAsync(client, false);

        RealtimeHistoryResponse history =
            await WaitForHistoryCountAsync(client, sensor.Id, 6);

        Assert.Equal(6L, history.TotalRecordedEvents);
        Assert.Equal(3, history.RetainedEventCount);
        Assert.Equal(3, history.RetentionCapacity);
        Assert.Equal(3, history.Events.Count);

        // Newest capture instants are +3s, +4s and +5s.
        Assert.Equal(
            new[] { 13, 14, 11 },
            history.Events.Select(
                entry => entry.Telemetry!.IntegerValue!.Value));

        Assert.Equal(
            new long[] { 4, 5, 2 },
            history.Events.Select(entry => entry.Sequence));

        TelemetryReadingResponse[] persistedAfter =
            await ReadPartOneRecentAsync(client, sensor.Id, 100);

        Assert.Equal(persistedBefore, persistedAfter);
        Assert.Equal(6, persistedAfter.Length);

        TelemetryProcessingResponse processing =
            await ReadProcessingAsync(client);

        Assert.Equal(6L, processing.ProcessedCount);
        Assert.Null(processing.Failure);
    }

    /// <summary>
    /// An injected processor failure must never appear in operational
    /// history as successful processing. Part 1 accepted persistence
    /// remains available independently of the worker failure.
    /// </summary>
    [Fact]
    public async Task FailedDispatch_RemainsInPartOneButNeverEntersProcessedHistory()
    {
        await using var factory =
            new TelemetryProcessingScenarioFactory(
                blockFirst: true,
                failFirst: true);

        using HttpClient client = CreateClient(factory);

        SensorResponse sensor =
            await RegisterAsync(client, "Environmental", "Integer");

        await SubmitTelemetryAsync(
            client,
            sensor.Id,
            new IngestTelemetryRequest(
                CaptureBase,
                null,
                25,
                null));

        await factory.Probe.FirstStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(10));

        // The existing test-only decorator throws before invoking the
        // real processor, preserving the genuine failure semantics.
        factory.Probe.ReleaseFirst.TrySetResult();

        TelemetryProcessingWorker worker =
            factory.Services
                .GetServices<IHostedService>()
                .OfType<TelemetryProcessingWorker>()
                .Single();

        await worker.ExecuteTask!.WaitAsync(
            TimeSpan.FromSeconds(10));

        TelemetryProcessingResponse processing =
            await ReadProcessingAsync(client);

        Assert.Equal("Faulted", processing.WorkerStatus);
        Assert.Equal(1L, processing.AcceptedCount);
        Assert.Equal(1L, processing.DispatchedCount);
        Assert.Equal(0L, processing.ProcessedCount);
        Assert.Equal(1L, processing.OutstandingDispatchedCount);
        Assert.NotNull(processing.Failure);

        Assert.Empty(factory.Probe.Completed);

        RealtimeHistoryResponse history =
            await ReadHistoryAsync(client, sensor.Id);

        Assert.Equal(0L, history.TotalRecordedEvents);
        Assert.Equal(0, history.RetainedEventCount);
        Assert.Empty(history.Events);

        TelemetryReadingResponse[] original =
            await ReadPartOneRecentAsync(client, sensor.Id, 100);

        Assert.Equal(25, Assert.Single(original).IntegerValue);
    }

    // ---------------------------------------------------------------------
    // HTTP test helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Exercises the actual HTTP application without unrelated
    /// development HTTPS redirection failures.
    /// </summary>
    private static HttpClient CreateClient(
        WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });
    }

    /// <summary>
    /// Registers an actual Smart-X sensor through the Part 1 API.
    /// </summary>
    private static async Task<SensorResponse> RegisterAsync(
        HttpClient client,
        string category,
        string telemetryType)
    {
        string identifier =
            $"p205-compat-{Guid.NewGuid():N}";

        using HttpResponseMessage response =
            await client.PostAsJsonAsync(
                "/api/sensors",
                new RegisterSensorRequest(
                    "P2-05 Compatibility Device",
                    identifier,
                    "UniqueIdentifier",
                    new SensorDeploymentLocationRequest(
                        "Evidence Facility",
                        "History Zone",
                        identifier),
                    category,
                    telemetryType));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content
            .ReadFromJsonAsync<SensorResponse>()
            ?? throw new InvalidOperationException(
                "Sensor registration returned no response.");
    }

    private static async Task<SensorResponse> RegisterOnlineActuatorAsync(
        HttpClient client)
    {
        SensorResponse sensor =
            await RegisterAsync(client, "Actuator", "Boolean");

        using HttpResponseMessage response =
            await client.PatchAsJsonAsync(
                $"/api/sensors/{sensor.Id}/status",
                new ChangeSensorStatusRequest("Online"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return sensor;
    }

    private static async Task<TelemetryReadingResponse> SubmitTelemetryAsync(
        HttpClient client,
        Guid sensorId,
        IngestTelemetryRequest request)
    {
        using HttpResponseMessage response =
            await client.PostAsJsonAsync(
                $"/api/sensors/{sensorId}/telemetry",
                request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content
            .ReadFromJsonAsync<TelemetryReadingResponse>()
            ?? throw new InvalidOperationException(
                "Telemetry ingestion returned no response.");
    }

    /// <summary>
    /// Generates strongly typed HTTP telemetry with irrelevant fields
    /// explicitly null.
    /// </summary>
    private static IngestTelemetryRequest CreateTypedReading(
        string telemetryType,
        int index,
        DateTimeOffset capturedAtUtc)
    {
        return telemetryType switch
        {
            "Float" => new IngestTelemetryRequest(
                capturedAtUtc,
                1000.25 + index,
                null,
                null),

            "Integer" => new IngestTelemetryRequest(
                capturedAtUtc,
                null,
                1000 + index,
                null),

            "Boolean" => new IngestTelemetryRequest(
                capturedAtUtc,
                null,
                null,
                index % 2 != 0),

            _ => throw new ArgumentOutOfRangeException(
                nameof(telemetryType))
        };
    }

    /// <summary>
    /// Compares the original typed telemetry payload exactly.
    /// </summary>
    private static void AssertTypedReading(
        TelemetryReadingResponse actual,
        Guid sensorId,
        string telemetryType,
        IngestTelemetryRequest expected)
    {
        Assert.Equal(sensorId, actual.SensorProfileId);
        Assert.Equal(telemetryType, actual.TelemetryDataType);

        Assert.Equal(expected.FloatValue, actual.FloatValue);
        Assert.Equal(expected.IntegerValue, actual.IntegerValue);
        Assert.Equal(expected.BooleanValue, actual.BooleanValue);

        Assert.Equal(
            expected.CapturedAtUtc!.Value.ToUniversalTime(),
            actual.CapturedAtUtc);
    }

    private static async Task<RelayCommandResponse> ExecuteCommandAsync(
        HttpClient client,
        Guid sensorId,
        string commandType)
    {
        using HttpResponseMessage response =
            await client.PostAsJsonAsync(
                $"/api/realtime/devices/{sensorId}/commands",
                new RelayCommandRequest(commandType));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content
            .ReadFromJsonAsync<RelayCommandResponse>()
            ?? throw new InvalidOperationException(
                "Relay command returned no response.");
    }

    private static async Task<RelayCommandResponse> UndoCommandAsync(
        HttpClient client,
        Guid sensorId,
        Guid expectedLatestCommandId)
    {
        using HttpResponseMessage response =
            await client.PostAsJsonAsync(
                $"/api/realtime/devices/{sensorId}/commands/undo",
                new UndoRelayCommandRequest(expectedLatestCommandId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content
            .ReadFromJsonAsync<RelayCommandResponse>()
            ?? throw new InvalidOperationException(
                "Undo returned no response.");
    }

    private static async Task SetOrdinaryDispatchPausedAsync(
        HttpClient client,
        bool paused)
    {
        using HttpResponseMessage response =
            await client.PutAsJsonAsync(
                "/api/realtime/simulation/ordinary-dispatch",
                new SetOrdinaryDispatchRequest(paused));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<RealtimeHistoryResponse> ReadHistoryAsync(
        HttpClient client,
        Guid sensorId,
        string query = "",
        CancellationToken cancellationToken = default)
    {
        return await client
            .GetFromJsonAsync<RealtimeHistoryResponse>(
                $"/api/realtime/devices/{sensorId}/history{query}",
                cancellationToken)
            ?? throw new InvalidOperationException(
                "Operational history returned no response.");
    }

    private static async Task<TelemetryReadingResponse[]> ReadPartOneRecentAsync(
        HttpClient client,
        Guid sensorId,
        int limit)
    {
        return await client
            .GetFromJsonAsync<TelemetryReadingResponse[]>(
                $"/api/sensors/{sensorId}/telemetry?limit={limit}")
            ?? throw new InvalidOperationException(
                "Part 1 recent history returned no response.");
    }

    /// <summary>
    /// Reads processing diagnostics and verifies the existing queue
    /// accounting invariants.
    /// </summary>
    private static async Task<TelemetryProcessingResponse> ReadProcessingAsync(
        HttpClient client)
    {
        TelemetryProcessingResponse result =
            await client.GetFromJsonAsync<TelemetryProcessingResponse>(
                "/api/realtime/processing")
            ?? throw new InvalidOperationException(
                "Processing diagnostics returned no response.");

        Assert.Equal(
            result.AcceptedCount,
            result.PendingCount +
            result.ProcessedCount +
            result.OutstandingDispatchedCount);

        Assert.Equal(
            result.DispatchedCount,
            result.ProcessedCount +
            result.OutstandingDispatchedCount);

        return result;
    }

    /// <summary>
    /// Waits for actual operational-history events rather than only the
    /// processing counter, avoiding the small post-completion projection
    /// race window.
    /// </summary>
    private static async Task<RealtimeHistoryResponse> WaitForHistoryCountAsync(
        HttpClient client,
        Guid sensorId,
        long expectedCount)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(20));

        try
        {
            while (true)
            {
                RealtimeHistoryResponse response =
                    await ReadHistoryAsync(
                        client,
                        sensorId,
                        "?limit=500",
                        timeout.Token);

                if (response.TotalRecordedEvents >= expectedCount)
                {
                    return response;
                }

                await Task.Delay(20, timeout.Token);
            }
        }
        catch (OperationCanceledException)
            when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for {expectedCount} operational " +
                $"history events for device '{sensorId}'.");
        }
    }

    /// <summary>
    /// The original P2-03 probe records real successful completions
    /// separately from the bounded processing diagnostic window.
    /// </summary>
    private static async Task WaitForProbeCountAsync(
        TelemetryProcessingProbe probe,
        int expectedCount)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(20));

        try
        {
            while (probe.Completed.Length < expectedCount)
            {
                await Task.Delay(20, timeout.Token);
            }
        }
        catch (OperationCanceledException)
            when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for {expectedCount} real " +
                "telemetry worker completions.");
        }
    }

    // ---------------------------------------------------------------------
    // Isolated integration-test dependency configuration
    // ---------------------------------------------------------------------

    /// <summary>
    /// Creates an otherwise genuine API host with optional test-only
    /// retention capacity or frozen application time.
    ///
    /// Production settings and other test hosts remain unchanged.
    /// </summary>
    private sealed class ConfiguredHistoryFactory
        : WebApplicationFactory<Program>
    {
        private readonly int? _retentionCapacity;
        private readonly DateTimeOffset? _fixedInstantUtc;

        private readonly string _storagePath = Path.Combine(
            Path.GetTempPath(),
            "smartx-history-compatibility",
            Guid.NewGuid().ToString("N"));

        public ConfiguredHistoryFactory(
            int? retentionCapacity = null,
            DateTimeOffset? fixedInstantUtc = null)
        {
            _retentionCapacity = retentionCapacity;
            _fixedInstantUtc =
                fixedInstantUtc?.ToUniversalTime();
        }

        protected override void ConfigureWebHost(
            IWebHostBuilder builder)
        {
            builder.UseSetting(
                "Attachments:StoragePath",
                _storagePath);

            builder.UseSetting(
                "Attachments:EncryptionKeyBase64",
                Convert.ToBase64String(new byte[32]));

            builder.ConfigureTestServices(services =>
            {
                if (_retentionCapacity is int capacity)
                {
                    services.RemoveAll<IRealtimeHistoryStore>();

                    services.AddSingleton<IRealtimeHistoryStore>(
                        new InMemoryRealtimeHistoryStore(capacity));
                }

                if (_fixedInstantUtc is DateTimeOffset instant)
                {
                    services.RemoveAll<TimeProvider>();

                    services.AddSingleton<TimeProvider>(
                        new FrozenTimeProvider(instant));
                }
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();

            if (Directory.Exists(_storagePath))
            {
                Directory.Delete(
                    _storagePath,
                    recursive: true);
            }
        }
    }

    /// <summary>
    /// Returns one deterministic UTC instant with a non-zero offset.
    /// This proves domain and history UTC normalisation through real
    /// HTTP operations.
    /// </summary>
    private sealed class FrozenTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _instantUtc;

        public FrozenTimeProvider(DateTimeOffset instantUtc)
        {
            _instantUtc = instantUtc.ToUniversalTime();
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _instantUtc.ToOffset(
                TimeSpan.FromHours(2));
        }
    }
}