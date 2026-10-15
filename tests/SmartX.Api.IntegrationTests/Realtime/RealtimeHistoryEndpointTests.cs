using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using SmartX.Application.Commands;
using SmartX.Application.Realtime;
using SmartX.Contracts.Commands;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;
using SmartX.Domain.Realtime;

namespace SmartX.Api.IntegrationTests.Realtime;

/// <summary>
/// Verifies the complete P2-05 HTTP operational-history workflow.
///
/// Each test runs against its own ASP.NET Core test host with the actual
/// singleton sensor repository, telemetry worker, actuator command store,
/// and SortedDictionary-backed chronological history store.
///
/// Test scenarios use genuine HTTP commands and telemetry ingestion.
/// </summary>
public sealed class RealtimeHistoryEndpointTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly HttpClient _client;

    public RealtimeHistoryEndpointTests()
    {
        _client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });
    }

    [Fact]
    public async Task GetHistory_RegisteredDeviceWithoutEvents_ReturnsEmptyTimeline()
    {
        SensorResponse sensor = await RegisterAsync(
            "empty-history",
            "Environmental",
            "Float");

        RealtimeHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(sensor.Id, history.TargetDeviceId);

        Assert.Equal(0, history.TotalRecordedEvents);
        Assert.Equal(0, history.RetainedEventCount);

        Assert.Equal(10_000, history.RetentionCapacity);
        Assert.Equal(100, history.Limit);

        Assert.Equal("Ascending", history.Order);
        Assert.Empty(history.Events);
    }

    [Fact]
    public async Task Telemetry_WhileOrdinaryDispatchPaused_AppearsOnlyAfterProcessing()
    {
        // Hold ordinary processing before accepting the telemetry packet.
        // The critical lane remains available, as required by P2-03.
        await SetOrdinaryDispatchPausedAsync(true);

        SensorResponse sensor = await RegisterAsync(
            "processing-history",
            "Environmental",
            "Integer");

        DateTimeOffset capturedAtUtc =
            DateTimeOffset.UtcNow.AddMinutes(-5);

        using HttpResponseMessage ingestion =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensor.Id}/telemetry",
                new IngestTelemetryRequest(
                    capturedAtUtc,
                    null,
                    27,
                    null));

        Assert.Equal(
            HttpStatusCode.Created,
            ingestion.StatusCode);

        TelemetryReadingResponse accepted =
            await ingestion.Content
                .ReadFromJsonAsync<TelemetryReadingResponse>()
            ?? throw new InvalidOperationException(
                "Telemetry acceptance returned no response.");

        // Successful acceptance does not mean the worker has processed it.
        RealtimeHistoryResponse before =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(0, before.TotalRecordedEvents);
        Assert.Empty(before.Events);

        await SetOrdinaryDispatchPausedAsync(false);

        // Wait for the actual hosted worker to complete and publish history.
        RealtimeHistoryResponse completed =
            await WaitForHistoryCountAsync(sensor.Id, 1);

        Assert.Equal(1, completed.TotalRecordedEvents);
        Assert.Equal(1, completed.RetainedEventCount);

        RealtimeHistoryEventResponse entry =
            Assert.Single(completed.Events);

        Assert.Equal("TelemetryProcessed", entry.Kind);
        Assert.Equal(sensor.Id, entry.TargetDeviceId);

        Assert.NotNull(entry.Telemetry);
        Assert.Null(entry.Command);

        Assert.Equal(
            accepted,
            entry.Telemetry);

        Assert.Equal(
            "Integer",
            entry.Telemetry.TelemetryDataType);

        Assert.Equal(27, entry.Telemetry.IntegerValue);
        Assert.Null(entry.Telemetry.FloatValue);
        Assert.Null(entry.Telemetry.BooleanValue);

        Assert.Equal(
            capturedAtUtc.ToUniversalTime(),
            entry.OccurredAtUtc);

        Assert.True(
            entry.RecordedAtUtc >= entry.OccurredAtUtc);

        Assert.Equal(
            "Ordinary",
            entry.ProcessingLane);

        Assert.Equal("Information", entry.Severity);

        Assert.NotNull(entry.EnqueueSequence);
        Assert.NotNull(entry.DequeueSequence);
        Assert.NotNull(entry.AcceptedAtUtc);

        Assert.True(entry.EnqueueSequence > 0);
        Assert.True(entry.DequeueSequence > 0);

        // A history read must not insert the same processed packet again.
        RealtimeHistoryResponse repeatedRead =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(
            completed.TotalRecordedEvents,
            repeatedRead.TotalRecordedEvents);

        Assert.Equal(
            entry.EventId,
            Assert.Single(repeatedRead.Events).EventId);
    }

    [Fact]
    public async Task CommandsAndUndo_AppearInSameTimelineWithReversalLink()
    {
        SensorResponse sensor = await RegisterOnlineActuatorAsync(
            "mixed-history");

        // First generate an actual Boolean telemetry event.
        DateTimeOffset telemetryCaptureTime =
            DateTimeOffset.UtcNow.AddMinutes(-10);

        using HttpResponseMessage telemetryResponse =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensor.Id}/telemetry",
                new IngestTelemetryRequest(
                    telemetryCaptureTime,
                    null,
                    null,
                    false));

        Assert.Equal(
            HttpStatusCode.Created,
            telemetryResponse.StatusCode);

        await WaitForHistoryCountAsync(sensor.Id, 1);

        // Issue one successful state-changing manual command.
        RelayCommandResponse original =
            await ExecuteCommandAsync(
                sensor.Id,
                "RelayOn");

        Assert.True(original.Command.StateChanged);
        Assert.Equal("On", original.Actuator.CommandedState);

        // Repeating RelayOn is accepted as a no-op, not a Stack push.
        RelayCommandResponse noOp =
            await ExecuteCommandAsync(
                sensor.Id,
                "RelayOn");

        Assert.False(noOp.Command.StateChanged);
        Assert.Equal(1, noOp.Actuator.UndoDepth);

        // Reverse the actual latest state-changing command.
        RelayCommandResponse reversal =
            await UndoCommandAsync(
                sensor.Id,
                original.Command.CommandId);

        Assert.True(reversal.Command.IsReversal);

        Assert.Equal(
            original.Command.CommandId,
            reversal.Command.ReversesCommandId);

        Assert.Equal("Off", reversal.Actuator.CommandedState);
        Assert.Equal(0, reversal.Actuator.UndoDepth);

        RealtimeHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(4, history.TotalRecordedEvents);
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

        // History chronology uses telemetry capture time.
        RealtimeHistoryEventResponse first =
            history.Events[0];

        Assert.NotNull(first.Telemetry);
        Assert.Null(first.Command);

        Assert.Equal(
            telemetryCaptureTime.ToUniversalTime(),
            first.OccurredAtUtc);

        Assert.Equal(
            false,
            first.Telemetry.BooleanValue);

        RealtimeHistoryEventResponse commandEvent =
            history.Events[1];

        Assert.NotNull(commandEvent.Command);
        Assert.Null(commandEvent.Telemetry);

        Assert.Equal(
            original.Command.CommandId,
            commandEvent.Command.CommandId);

        Assert.Equal(
            "Off",
            commandEvent.Command.PreviousState);

        Assert.Equal(
            "On",
            commandEvent.Command.ResultingState);

        RealtimeHistoryEventResponse noOpEvent =
            history.Events[2];

        Assert.NotNull(noOpEvent.Command);
        Assert.False(noOpEvent.Command.StateChanged);

        Assert.Equal(
            noOp.Command.CommandId,
            noOpEvent.Command.CommandId);

        RealtimeHistoryEventResponse undoEvent =
            history.Events[3];

        Assert.NotNull(undoEvent.Command);
        Assert.True(undoEvent.Command.IsReversal);

        Assert.Equal(
            reversal.Command.CommandId,
            undoEvent.Command.CommandId);

        Assert.Equal(
            original.Command.CommandId,
            undoEvent.Command.ReversesCommandId);

        Assert.Equal(
            "On",
            undoEvent.Command.PreviousState);

        Assert.Equal(
            "Off",
            undoEvent.Command.ResultingState);

        // Every event has a distinct stable identity and store sequence.
        Assert.Equal(
            4,
            history.Events
                .Select(entry => entry.EventId)
                .Distinct()
                .Count());

        Assert.Equal(
            new long[] { 1, 2, 3, 4 },
            history.Events.Select(entry => entry.Sequence));

        Assert.All(
            history.Events,
            entry =>
            {
                Assert.Equal(
                    TimeSpan.Zero,
                    entry.OccurredAtUtc.Offset);

                Assert.Equal(
                    TimeSpan.Zero,
                    entry.RecordedAtUtc.Offset);
            });
    }

    [Fact]
    public async Task DuplicateCommandCallback_DoesNotCreateDuplicateHistory()
    {
        SensorResponse sensor = await RegisterOnlineActuatorAsync(
            "duplicate-history");

        RelayCommandResponse original =
            await ExecuteCommandAsync(
                sensor.Id,
                "RelayOn");

        IActuatorCommandStore commandStore =
            _factory.Services
                .GetRequiredService<IActuatorCommandStore>();

        IRealtimeHistoryStore historyStore =
            _factory.Services
                .GetRequiredService<IRealtimeHistoryStore>();

        // Fetch the real stored original command, not a fabricated record.
        var actuatorSnapshot =
            await commandStore.GetSnapshotAsync(sensor.Id);

        var originalCommand =
            Assert.Single(actuatorSnapshot.AuditTrail);

        Assert.Equal(
            original.Command.CommandId,
            originalCommand.Id);

        // Simulate a duplicate callback carrying that exact source record.
        bool appended =
            historyStore.TryAppend(
                HistoryEntry.FromCommand(originalCommand));

        Assert.False(appended);

        RealtimeHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(1, history.TotalRecordedEvents);
        Assert.Equal(1, history.RetainedEventCount);

        RealtimeHistoryEventResponse entry =
            Assert.Single(history.Events);

        Assert.Equal(
            $"command:{original.Command.CommandId:D}",
            entry.EventId);

        Assert.Equal(
            original.Command.CommandId,
            entry.Command!.CommandId);

        // Reading the history endpoint again also has no mutation effect.
        RealtimeHistoryResponse secondRead =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(
            1,
            secondRead.TotalRecordedEvents);
    }

    [Fact]
    public async Task RejectedStaleUndo_DoesNotEnterChronologicalHistory()
    {
        SensorResponse sensor = await RegisterOnlineActuatorAsync(
            "stale-history");

        RelayCommandResponse original =
            await ExecuteCommandAsync(
                sensor.Id,
                "RelayOn");

        RelayCommandResponse reversal =
            await UndoCommandAsync(
                sensor.Id,
                original.Command.CommandId);

        Assert.Equal("Off", reversal.Actuator.CommandedState);

        RealtimeHistoryResponse before =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(2, before.TotalRecordedEvents);

        // The original has already been popped from the Undo Stack.
        // This must produce 409 and no further history entry.
        using HttpResponseMessage rejected =
            await _client.PostAsJsonAsync(
                UndoPath(sensor.Id),
                new UndoRelayCommandRequest(
                    original.Command.CommandId));

        Assert.Equal(
            HttpStatusCode.Conflict,
            rejected.StatusCode);

        RealtimeHistoryResponse after =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(
            before.TotalRecordedEvents,
            after.TotalRecordedEvents);

        Assert.Equal(
            before.RetainedEventCount,
            after.RetainedEventCount);

        Assert.Equal(
            before.Events.Select(entry => entry.EventId),
            after.Events.Select(entry => entry.EventId));

        Assert.Equal(
            new[] { "RelayCommand", "RelayUndo" },
            after.Events.Select(entry => entry.Kind));
    }

    [Fact]
    public async Task DifferentDevices_HaveIndependentOperationalTimelines()
    {
        SensorResponse first =
            await RegisterOnlineActuatorAsync("history-a");

        SensorResponse second =
            await RegisterOnlineActuatorAsync("history-b");

        await ExecuteCommandAsync(first.Id, "RelayOn");

        await ExecuteCommandAsync(second.Id, "RelayOn");
        await ExecuteCommandAsync(second.Id, "RelayOff");

        RealtimeHistoryResponse firstHistory =
            await GetHistoryAsync(first.Id);

        RealtimeHistoryResponse secondHistory =
            await GetHistoryAsync(second.Id);

        Assert.Equal(1, firstHistory.TotalRecordedEvents);
        Assert.Equal(2, secondHistory.TotalRecordedEvents);

        Assert.Single(firstHistory.Events);
        Assert.Equal(2, secondHistory.Events.Count);

        Assert.All(
            firstHistory.Events,
            entry => Assert.Equal(first.Id, entry.TargetDeviceId));

        Assert.All(
            secondHistory.Events,
            entry => Assert.Equal(second.Id, entry.TargetDeviceId));

        // Each device's store-assigned sequence begins independently.
        Assert.Equal(
            1,
            Assert.Single(firstHistory.Events).Sequence);

        Assert.Equal(
            new long[] { 1, 2 },
            secondHistory.Events.Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task BoundedQuery_UsesNewestEventsAndRequestedSortOrder()
    {
        SensorResponse sensor = await RegisterOnlineActuatorAsync(
            "bounded-history");

        RelayCommandResponse first =
            await ExecuteCommandAsync(
                sensor.Id,
                "RelayOn");

        RelayCommandResponse second =
            await ExecuteCommandAsync(
                sensor.Id,
                "RelayOff");

        RelayCommandResponse third =
            await ExecuteCommandAsync(
                sensor.Id,
                "RelayOn");

        RealtimeHistoryResponse ascending =
            await GetHistoryAsync(
                sensor.Id,
                "?limit=2&order=ascending");

        RealtimeHistoryResponse descending =
            await GetHistoryAsync(
                sensor.Id,
                "?limit=2&order=desc");

        Assert.Equal(3, ascending.TotalRecordedEvents);
        Assert.Equal(3, ascending.RetainedEventCount);
        Assert.Equal(2, ascending.Limit);

        Assert.Equal("Ascending", ascending.Order);
        Assert.Equal("Descending", descending.Order);

        // These are the latest two events, not the oldest two.
        Assert.Equal(
            new[]
            {
                second.Command.CommandId,
                third.Command.CommandId
            },
            ascending.Events
                .Select(entry => entry.Command!.CommandId));

        Assert.Equal(
            new[]
            {
                third.Command.CommandId,
                second.Command.CommandId
            },
            descending.Events
                .Select(entry => entry.Command!.CommandId));

        Assert.DoesNotContain(
            ascending.Events,
            entry => entry.Command?.CommandId ==
                first.Command.CommandId);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=-1")]
    [InlineData("?limit=501")]
    [InlineData("?order=unknown")]
    [InlineData("?order=1")]
    [InlineData("?order=")]
    public async Task InvalidHistoryQuery_Returns400ProblemDetails(
        string query)
    {
        SensorResponse sensor = await RegisterAsync(
            "invalid-query",
            "Environmental",
            "Integer");

        using HttpResponseMessage response =
            await _client.GetAsync(
                HistoryPath(sensor.Id) + query);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "Invalid history query");

        RealtimeHistoryResponse validHistory =
            await GetHistoryAsync(sensor.Id);

        Assert.Empty(validHistory.Events);
        Assert.Equal(0, validHistory.TotalRecordedEvents);
    }

    [Fact]
    public async Task UnparseableLimit_UsesExistingModelValidationResponse()
    {
        SensorResponse sensor = await RegisterAsync(
            "model-validation",
            "Environmental",
            "Float");

        using HttpResponseMessage response =
            await _client.GetAsync(
                HistoryPath(sensor.Id) + "?limit=not-a-number");

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "Request validation failed");
    }

    [Fact]
    public async Task UnknownRegisteredDevice_Returns404ProblemDetails()
    {
        Guid unknownDeviceId = Guid.NewGuid();

        using HttpResponseMessage response =
            await _client.GetAsync(
                HistoryPath(unknownDeviceId));

        await AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            "Sensor not found");
    }

    [Fact]
    public async Task HistoryRead_DoesNotDependOnCurrentOnlineStatus()
    {
        SensorResponse sensor = await RegisterOnlineActuatorAsync(
            "offline-history");

        RelayCommandResponse accepted =
            await ExecuteCommandAsync(
                sensor.Id,
                "RelayOn");

        using HttpResponseMessage statusResponse =
            await _client.PatchAsJsonAsync(
                $"/api/sensors/{sensor.Id}/status",
                new ChangeSensorStatusRequest("Maintenance"));

        Assert.Equal(
            HttpStatusCode.OK,
            statusResponse.StatusCode);

        // Command history remains readable even when new commands and
        // Undo operations are no longer eligible.
        RealtimeHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(1, history.TotalRecordedEvents);

        Assert.Equal(
            accepted.Command.CommandId,
            Assert.Single(history.Events).Command!.CommandId);

        using HttpResponseMessage rejectedCommand =
            await _client.PostAsJsonAsync(
                CommandPath(sensor.Id),
                new RelayCommandRequest("RelayOff"));

        Assert.Equal(
            HttpStatusCode.Conflict,
            rejectedCommand.StatusCode);

        RealtimeHistoryResponse after =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(1, after.TotalRecordedEvents);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    /// <summary>
    /// Registers a real device using the existing Part 1 HTTP workflow.
    /// A unique identifier keeps every test independent and explicit.
    /// </summary>
    private async Task<SensorResponse> RegisterAsync(
        string identifier,
        string category,
        string telemetryType)
    {
        using HttpResponseMessage response =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                new RegisterSensorRequest(
                    $"P2-05 {identifier}",
                    $"p205-{identifier}-{Guid.NewGuid():N}",
                    "UniqueIdentifier",
                    new SensorDeploymentLocationRequest(
                        "Facility A",
                        "Zone 1",
                        identifier),
                    category,
                    telemetryType));

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        return await response.Content
            .ReadFromJsonAsync<SensorResponse>()
            ?? throw new InvalidOperationException(
                "Sensor registration returned no response.");
    }

    private async Task<SensorResponse> RegisterOnlineActuatorAsync(
        string identifier)
    {
        SensorResponse sensor =
            await RegisterAsync(
                identifier,
                "Actuator",
                "Boolean");

        using HttpResponseMessage response =
            await _client.PatchAsJsonAsync(
                $"/api/sensors/{sensor.Id}/status",
                new ChangeSensorStatusRequest("Online"));

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        return sensor;
    }

    /// <summary>
    /// Calls the original P2-04 HTTP command endpoint.
    /// </summary>
    private async Task<RelayCommandResponse> ExecuteCommandAsync(
        Guid deviceId,
        string commandType)
    {
        using HttpResponseMessage response =
            await _client.PostAsJsonAsync(
                CommandPath(deviceId),
                new RelayCommandRequest(commandType));

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        return await response.Content
            .ReadFromJsonAsync<RelayCommandResponse>()
            ?? throw new InvalidOperationException(
                "Manual command returned no response.");
    }

    /// <summary>
    /// Calls the original genuine Stack-backed Undo endpoint.
    /// </summary>
    private async Task<RelayCommandResponse> UndoCommandAsync(
        Guid deviceId,
        Guid expectedCommandId)
    {
        using HttpResponseMessage response =
            await _client.PostAsJsonAsync(
                UndoPath(deviceId),
                new UndoRelayCommandRequest(expectedCommandId));

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        return await response.Content
            .ReadFromJsonAsync<RelayCommandResponse>()
            ?? throw new InvalidOperationException(
                "Undo returned no response.");
    }

    private static string CommandPath(Guid deviceId)
    {
        return $"/api/realtime/devices/{deviceId}/commands";
    }

    private static string UndoPath(Guid deviceId)
    {
        return $"{CommandPath(deviceId)}/undo";
    }

    private static string HistoryPath(Guid deviceId)
    {
        return $"/api/realtime/devices/{deviceId}/history";
    }

    /// <summary>
    /// Reads the actual registered-device timeline through HTTP.
    /// </summary>
    private async Task<RealtimeHistoryResponse> GetHistoryAsync(
        Guid deviceId,
        string query = "")
    {
        using HttpResponseMessage response =
            await _client.GetAsync(HistoryPath(deviceId) + query);

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        return await response.Content
            .ReadFromJsonAsync<RealtimeHistoryResponse>()
            ?? throw new InvalidOperationException(
                "History endpoint returned no response.");
    }

    /// <summary>
    /// Pauses or resumes the existing P2-03 ordinary dispatch lane.
    ///
    /// This allows tests to prove that ingestion alone does not create
    /// a successfully processed operational-history event.
    /// </summary>
    private async Task SetOrdinaryDispatchPausedAsync(bool paused)
    {
        using HttpResponseMessage response =
            await _client.PutAsJsonAsync(
                "/api/realtime/simulation/ordinary-dispatch",
                new SetOrdinaryDispatchRequest(paused));

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);
    }

    /// <summary>
    /// Waits for the real background worker and projection to finish.
    ///
    /// A completed processing counter and a published history event are
    /// separate observations; tests must not rely on an HTTP race.
    /// </summary>
    private async Task<RealtimeHistoryResponse> WaitForHistoryCountAsync(
        Guid deviceId,
        long expectedCount)
    {
        const int maxAttempts = 400;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            RealtimeHistoryResponse history =
                await GetHistoryAsync(deviceId);

            if (history.TotalRecordedEvents >= expectedCount)
            {
                return history;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            "The real telemetry worker did not publish the expected " +
            "operational-history event within the test timeout.");
    }

    /// <summary>
    /// Verifies the established API-wide ProblemDetails contract.
    /// </summary>
    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedTitle)
    {
        Assert.Equal(
            expectedStatus,
            response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        ProblemDetails problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>()
            ?? throw new InvalidOperationException(
                "Expected a ProblemDetails response.");

        Assert.Equal(
            (int)expectedStatus,
            problem.Status);

        Assert.Equal(
            expectedTitle,
            problem.Title);

        Assert.True(
            problem.Extensions.ContainsKey("traceId"));
    }
}