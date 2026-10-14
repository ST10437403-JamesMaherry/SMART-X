using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SmartX.Application.Commands;
using SmartX.Application.Realtime;
using SmartX.Contracts.Commands;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;

namespace SmartX.Api.IntegrationTests.Realtime;

/// <summary>
/// Exercises actual HTTP command, Undo, history, and snapshot operations
/// against the complete Smart-X backend.
///
/// Each test uses an isolated WebApplicationFactory containing the real
/// registered-device repository and real Stack-backed command store.
///
/// Only the best-effort real-time notifier is replaced with a recording
/// test double so command invalidation behaviour can be asserted.
/// </summary>
public sealed class RealtimeCommandsEndpointTests : IDisposable
{
    private readonly CommandsApiFactory _factory = new();
    private readonly HttpClient _client;

    public RealtimeCommandsEndpointTests()
    {
        _client = CreateClient(_factory);
    }

    [Fact]
    public async Task Snapshot_RegisteredActuator_HasExplicitOffCommandedState()
    {
        SensorResponse sensor = await RegisterAsync(
            "snapshot-relay");

        RealtimeSnapshotResponse snapshot =
            await GetSnapshotAsync();

        RealtimeDeviceResponse device =
            Assert.Single(snapshot.Devices);

        Assert.Equal(sensor.Id, device.Id);
        Assert.Equal("Actuator", device.Category);
        Assert.Equal("Boolean", device.TelemetryDataType);
        Assert.Equal("Online", device.Status);

        Assert.NotNull(device.CommandedActuator);
        Assert.Equal(
            "Off",
            device.CommandedActuator.CommandedState);

        Assert.Equal(
            0,
            device.CommandedActuator.UndoDepth);

        Assert.Null(
            device.CommandedActuator.LatestUndoableCommandId);

        // No reported telemetry has been used to infer relay state.
        Assert.Null(device.LatestProcessedTelemetry);
        Assert.Null(device.LastProcessedAtUtc);

        ActuatorCommandHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal("Off", history.Actuator.CommandedState);
        Assert.Equal(0, history.TotalAuditCount);
        Assert.Empty(history.RecentAudit);
    }

    [Fact]
    public async Task RelayOn_ChangesRealStateAndPublishesCommandInvalidation()
    {
        SensorResponse sensor = await RegisterAsync(
            "command-relay");

        RelayCommandResponse result =
            await ExecuteAsync(sensor.Id, "RelayOn");

        Assert.Equal(sensor.Id, result.Command.TargetDeviceId);
        Assert.Equal("RelayOn", result.Command.CommandType);
        Assert.Equal("Off", result.Command.PreviousState);
        Assert.Equal("On", result.Command.ResultingState);

        Assert.True(result.Command.StateChanged);
        Assert.False(result.Command.IsReversal);
        Assert.Null(result.Command.ReversesCommandId);

        Assert.NotEqual(Guid.Empty, result.Command.CommandId);
        Assert.NotEqual(default, result.Command.ExecutedAtUtc);

        Assert.Equal(
            TimeSpan.Zero,
            result.Command.ExecutedAtUtc.Offset);

        Assert.Equal("On", result.Actuator.CommandedState);
        Assert.Equal(1, result.Actuator.UndoDepth);

        Assert.Equal(
            result.Command.CommandId,
            result.Actuator.LatestUndoableCommandId);

        RealtimeDeviceResponse device =
            await GetDeviceAsync(sensor.Id);

        Assert.Equal("Online", device.Status);

        Assert.NotNull(device.CommandedActuator);
        Assert.Equal("On", device.CommandedActuator.CommandedState);
        Assert.Equal(1, device.CommandedActuator.UndoDepth);

        // Commanded state does not fabricate a sensor reading.
        Assert.Null(device.LatestProcessedTelemetry);

        var notifications =
            _factory.Notifier.GetCommandNotifications();

        var notification = Assert.Single(notifications);

        Assert.Equal(
            "ActuatorCommandRecorded",
            notification.Reason);

        Assert.Equal(
            sensor.Id,
            notification.DeviceId);
    }

    [Fact]
    public async Task Undo_RestoresPreviousStateAndRetainsLinkedAudit()
    {
        SensorResponse sensor = await RegisterAsync(
            "undo-relay");

        RelayCommandResponse original =
            await ExecuteAsync(sensor.Id, "RelayOn");

        RelayCommandResponse reversal =
            await UndoAsync(
                sensor.Id,
                original.Command.CommandId);

        Assert.True(reversal.Command.IsReversal);
        Assert.True(reversal.Command.StateChanged);

        Assert.Equal(
            original.Command.CommandId,
            reversal.Command.ReversesCommandId);

        Assert.NotEqual(
            original.Command.CommandId,
            reversal.Command.CommandId);

        Assert.Equal("RelayOff", reversal.Command.CommandType);
        Assert.Equal("On", reversal.Command.PreviousState);
        Assert.Equal("Off", reversal.Command.ResultingState);

        Assert.Equal("Off", reversal.Actuator.CommandedState);
        Assert.Equal(0, reversal.Actuator.UndoDepth);

        Assert.Null(
            reversal.Actuator.LatestUndoableCommandId);

        ActuatorCommandHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal("Off", history.Actuator.CommandedState);
        Assert.Equal(0, history.Actuator.UndoDepth);
        Assert.Equal(2, history.TotalAuditCount);

        Assert.Equal(
            original.Command.CommandId,
            history.RecentAudit[0].CommandId);

        Assert.Equal(
            reversal.Command.CommandId,
            history.RecentAudit[1].CommandId);

        Assert.Equal(
            original.Command.CommandId,
            history.RecentAudit[1].ReversesCommandId);

        Assert.Equal(
            new[]
            {
                "ActuatorCommandRecorded",
                "ActuatorCommandUndone"
            },
            _factory.Notifier.GetCommandNotifications()
                .Select(notification => notification.Reason));

        // A reversal is not added to the Undo Stack.
        using HttpResponseMessage repeated =
            await _client.PostAsJsonAsync(
                UndoPath(sensor.Id),
                new UndoRelayCommandRequest(
                    reversal.Command.CommandId));

        await AssertProblemAsync(
            repeated,
            HttpStatusCode.Conflict,
            "Actuator command conflict");

        Assert.Equal(
            2,
            (await GetHistoryAsync(sensor.Id)).TotalAuditCount);
    }

    [Fact]
    public async Task MultipleCommands_UndoFollowsLifoOrder()
    {
        SensorResponse sensor = await RegisterAsync(
            "lifo-api-relay");

        RelayCommandResponse first =
            await ExecuteAsync(sensor.Id, "RelayOn");

        RelayCommandResponse second =
            await ExecuteAsync(sensor.Id, "RelayOff");

        RelayCommandResponse third =
            await ExecuteAsync(sensor.Id, "RelayOn");

        Assert.Equal(3, third.Actuator.UndoDepth);

        RelayCommandResponse undoThird =
            await UndoAsync(sensor.Id, third.Command.CommandId);

        Assert.Equal("Off", undoThird.Actuator.CommandedState);
        Assert.Equal(2, undoThird.Actuator.UndoDepth);

        Assert.Equal(
            second.Command.CommandId,
            undoThird.Actuator.LatestUndoableCommandId);

        RelayCommandResponse undoSecond =
            await UndoAsync(sensor.Id, second.Command.CommandId);

        Assert.Equal("On", undoSecond.Actuator.CommandedState);
        Assert.Equal(1, undoSecond.Actuator.UndoDepth);

        Assert.Equal(
            first.Command.CommandId,
            undoSecond.Actuator.LatestUndoableCommandId);

        RelayCommandResponse undoFirst =
            await UndoAsync(sensor.Id, first.Command.CommandId);

        Assert.Equal("Off", undoFirst.Actuator.CommandedState);
        Assert.Equal(0, undoFirst.Actuator.UndoDepth);

        ActuatorCommandHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(6, history.TotalAuditCount);

        Assert.Equal(
            new[]
            {
                third.Command.CommandId,
                second.Command.CommandId,
                first.Command.CommandId
            },
            history.RecentAudit
                .Skip(3)
                .Select(entry => entry.ReversesCommandId!.Value));
    }

    [Fact]
    public async Task CommandsAndUndo_AreIsolatedBetweenDevices()
    {
        SensorResponse first = await RegisterAsync(
            "isolation-relay-a");

        SensorResponse second = await RegisterAsync(
            "isolation-relay-b");

        RelayCommandResponse firstOn =
            await ExecuteAsync(first.Id, "RelayOn");

        RelayCommandResponse secondOn =
            await ExecuteAsync(second.Id, "RelayOn");

        RelayCommandResponse firstOff =
            await ExecuteAsync(first.Id, "RelayOff");

        RelayCommandResponse restored =
            await UndoAsync(
                first.Id,
                firstOff.Command.CommandId);

        Assert.Equal("On", restored.Actuator.CommandedState);

        ActuatorCommandHistoryResponse firstHistory =
            await GetHistoryAsync(first.Id);

        ActuatorCommandHistoryResponse secondHistory =
            await GetHistoryAsync(second.Id);

        Assert.Equal(3, firstHistory.TotalAuditCount);
        Assert.Equal(1, secondHistory.TotalAuditCount);

        Assert.Equal(1, firstHistory.Actuator.UndoDepth);
        Assert.Equal(1, secondHistory.Actuator.UndoDepth);

        Assert.Equal(
            firstOn.Command.CommandId,
            firstHistory.Actuator.LatestUndoableCommandId);

        Assert.Equal(
            secondOn.Command.CommandId,
            secondHistory.Actuator.LatestUndoableCommandId);

        Assert.Equal("On", firstHistory.Actuator.CommandedState);
        Assert.Equal("On", secondHistory.Actuator.CommandedState);
    }

    [Fact]
    public async Task NoOps_RemainAuditableWithoutConsumingUndoEntries()
    {
        SensorResponse sensor = await RegisterAsync(
            "noop-api-relay");

        RelayCommandResponse initialNoOp =
            await ExecuteAsync(sensor.Id, "RelayOff");

        Assert.False(initialNoOp.Command.StateChanged);
        Assert.Equal("Off", initialNoOp.Actuator.CommandedState);
        Assert.Equal(0, initialNoOp.Actuator.UndoDepth);

        RelayCommandResponse changed =
            await ExecuteAsync(sensor.Id, "RelayOn");

        RelayCommandResponse secondNoOp =
            await ExecuteAsync(sensor.Id, "RelayOn");

        Assert.False(secondNoOp.Command.StateChanged);
        Assert.Equal(1, secondNoOp.Actuator.UndoDepth);

        Assert.Equal(
            changed.Command.CommandId,
            secondNoOp.Actuator.LatestUndoableCommandId);

        RelayCommandResponse reversed =
            await UndoAsync(
                sensor.Id,
                changed.Command.CommandId);

        Assert.Equal("Off", reversed.Actuator.CommandedState);
        Assert.Equal(0, reversed.Actuator.UndoDepth);

        ActuatorCommandHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(4, history.TotalAuditCount);
        Assert.False(history.RecentAudit[0].StateChanged);
        Assert.False(history.RecentAudit[2].StateChanged);
        Assert.True(history.RecentAudit[3].IsReversal);

        // Both no-ops were accepted into the audit. Rejections must not
        // publish notifications, but accepted no-ops do invalidate history.
        Assert.Equal(
            4,
            _factory.Notifier.GetCommandNotifications().Length);
    }

    [Theory]
    [InlineData("RelayToggle")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("UnknownCommand")]
    public async Task InvalidCommandTypes_Return400WithoutMutation(
        string commandType)
    {
        SensorResponse sensor = await RegisterAsync(
            "invalid-type-relay");

        using HttpResponseMessage response =
            await _client.PostAsJsonAsync(
                CommandPath(sensor.Id),
                new RelayCommandRequest(commandType));

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "Invalid actuator command");

        ActuatorCommandHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal("Off", history.Actuator.CommandedState);
        Assert.Equal(0, history.Actuator.UndoDepth);
        Assert.Equal(0, history.TotalAuditCount);

        Assert.Empty(
            _factory.Notifier.GetCommandNotifications());
    }

    [Fact]
    public async Task MissingCommandAndUndoId_Return400()
    {
        SensorResponse sensor = await RegisterAsync(
            "missing-input-relay");

        using HttpResponseMessage commandResponse =
            await _client.PostAsJsonAsync(
                CommandPath(sensor.Id),
                new RelayCommandRequest(null));

        await AssertProblemAsync(
            commandResponse,
            HttpStatusCode.BadRequest,
            "Invalid actuator command");

        using HttpResponseMessage undoResponse =
            await _client.PostAsJsonAsync(
                UndoPath(sensor.Id),
                new UndoRelayCommandRequest(null));

        await AssertProblemAsync(
            undoResponse,
            HttpStatusCode.BadRequest,
            "Invalid actuator command");

        using HttpResponseMessage emptyIdResponse =
            await _client.PostAsJsonAsync(
                UndoPath(sensor.Id),
                new UndoRelayCommandRequest(Guid.Empty));

        await AssertProblemAsync(
            emptyIdResponse,
            HttpStatusCode.BadRequest,
            "Invalid actuator command");

        Assert.Equal(
            0,
            (await GetHistoryAsync(sensor.Id)).TotalAuditCount);
    }

    [Fact]
    public async Task UnknownTarget_Returns404ForCommandUndoAndHistory()
    {
        Guid unknownId = Guid.NewGuid();

        using HttpResponseMessage command =
            await _client.PostAsJsonAsync(
                CommandPath(unknownId),
                new RelayCommandRequest("RelayOn"));

        using HttpResponseMessage undo =
            await _client.PostAsJsonAsync(
                UndoPath(unknownId),
                new UndoRelayCommandRequest(Guid.NewGuid()));

        using HttpResponseMessage history =
            await _client.GetAsync(CommandPath(unknownId));

        await AssertProblemAsync(
            command,
            HttpStatusCode.NotFound,
            "Sensor not found");

        await AssertProblemAsync(
            undo,
            HttpStatusCode.NotFound,
            "Sensor not found");

        await AssertProblemAsync(
            history,
            HttpStatusCode.NotFound,
            "Sensor not found");

        Assert.Empty((await GetSnapshotAsync()).Devices);

        Assert.Empty(
            _factory.Notifier.GetCommandNotifications());
    }

    [Theory]
    [InlineData("Environmental", "Boolean")]
    [InlineData("PowerConsumption", "Boolean")]
    [InlineData("Actuator", "Integer")]
    [InlineData("Actuator", "Float")]
    public async Task UnsupportedCategoryOrType_Returns409(
        string category,
        string telemetryType)
    {
        SensorResponse sensor = await RegisterAsync(
            "unsupported-relay",
            category,
            telemetryType);

        using HttpResponseMessage command =
            await _client.PostAsJsonAsync(
                CommandPath(sensor.Id),
                new RelayCommandRequest("RelayOn"));

        await AssertProblemAsync(
            command,
            HttpStatusCode.Conflict,
            "Actuator command conflict");

        using HttpResponseMessage history =
            await _client.GetAsync(CommandPath(sensor.Id));

        await AssertProblemAsync(
            history,
            HttpStatusCode.Conflict,
            "Actuator command conflict");

        RealtimeDeviceResponse device =
            await GetDeviceAsync(sensor.Id);

        Assert.Null(device.CommandedActuator);

        // Even a rejected operation must not acquire an undoable change.
        IActuatorCommandStore store =
            _factory.Services.GetRequiredService<IActuatorCommandStore>();

        Assert.Equal(
            0,
            (await store.GetSnapshotAsync(sensor.Id)).UndoDepth);

        Assert.Empty(
            _factory.Notifier.GetCommandNotifications());
    }

    [Theory]
    [InlineData("Offline")]
    [InlineData("Warning")]
    [InlineData("Fault")]
    [InlineData("Maintenance")]
    [InlineData("Decommissioned")]
    public async Task NonOnlineDevice_RejectsCommandAndUndoWithoutStackMutation(
        string status)
    {
        SensorResponse sensor = await RegisterAsync(
            "ineligible-status-relay");

        RelayCommandResponse original =
            await ExecuteAsync(sensor.Id, "RelayOn");

        await SetStatusAsync(sensor.Id, status);

        using HttpResponseMessage command =
            await _client.PostAsJsonAsync(
                CommandPath(sensor.Id),
                new RelayCommandRequest("RelayOff"));

        using HttpResponseMessage undo =
            await _client.PostAsJsonAsync(
                UndoPath(sensor.Id),
                new UndoRelayCommandRequest(
                    original.Command.CommandId));

        await AssertProblemAsync(
            command,
            HttpStatusCode.Conflict,
            "Actuator command conflict");

        await AssertProblemAsync(
            undo,
            HttpStatusCode.Conflict,
            "Actuator command conflict");

        // Reading history remains available while the actuator is offline
        // or otherwise ineligible for state-changing operations.
        ActuatorCommandHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal("On", history.Actuator.CommandedState);
        Assert.Equal(1, history.Actuator.UndoDepth);
        Assert.Equal(1, history.TotalAuditCount);

        Assert.Equal(
            original.Command.CommandId,
            history.Actuator.LatestUndoableCommandId);

        RealtimeDeviceResponse device =
            await GetDeviceAsync(sensor.Id);

        Assert.Equal(status, device.Status);

        Assert.NotNull(device.CommandedActuator);
        Assert.Equal("On", device.CommandedActuator.CommandedState);

        // Only the original accepted command generated an invalidation.
        Assert.Single(
            _factory.Notifier.GetCommandNotifications());
    }

    [Fact]
    public async Task StaleUndo_Returns409AndPreservesLatestCommand()
    {
        SensorResponse sensor = await RegisterAsync(
            "stale-api-relay");

        RelayCommandResponse first =
            await ExecuteAsync(sensor.Id, "RelayOn");

        RelayCommandResponse second =
            await ExecuteAsync(sensor.Id, "RelayOff");

        using HttpResponseMessage stale =
            await _client.PostAsJsonAsync(
                UndoPath(sensor.Id),
                new UndoRelayCommandRequest(
                    first.Command.CommandId));

        await AssertProblemAsync(
            stale,
            HttpStatusCode.Conflict,
            "Actuator command conflict");

        ActuatorCommandHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal("Off", history.Actuator.CommandedState);
        Assert.Equal(2, history.Actuator.UndoDepth);

        Assert.Equal(
            second.Command.CommandId,
            history.Actuator.LatestUndoableCommandId);

        Assert.Equal(2, history.TotalAuditCount);

        Assert.Equal(
            2,
            _factory.Notifier.GetCommandNotifications().Length);
    }

    [Fact]
    public async Task ConcurrentUndo_SameExpectedId_SucceedsOnce()
    {
        SensorResponse sensor = await RegisterAsync(
            "concurrent-api-relay");

        RelayCommandResponse original =
            await ExecuteAsync(sensor.Id, "RelayOn");

        // Both HTTP requests supply the same optimistic concurrency ID.
        // Only one may remove the original from the actual device Stack.
        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<HttpResponseMessage>[] requests =
            Enumerable.Range(0, 2)
                .Select(async _ =>
                {
                    await start.Task;

                    return await _client.PostAsJsonAsync(
                        UndoPath(sensor.Id),
                        new UndoRelayCommandRequest(
                            original.Command.CommandId));
                })
                .ToArray();

        start.SetResult(true);

        HttpResponseMessage[] responses =
            await Task.WhenAll(requests);

        try
        {
            Assert.Equal(
                1,
                responses.Count(
                    response => response.StatusCode == HttpStatusCode.OK));

            Assert.Equal(
                1,
                responses.Count(
                    response => response.StatusCode == HttpStatusCode.Conflict));

            HttpResponseMessage rejected =
                Assert.Single(
                    responses,
                    response => response.StatusCode == HttpStatusCode.Conflict);

            await AssertProblemAsync(
                rejected,
                HttpStatusCode.Conflict,
                "Actuator command conflict");
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }

        ActuatorCommandHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal("Off", history.Actuator.CommandedState);
        Assert.Equal(0, history.Actuator.UndoDepth);
        Assert.Equal(2, history.TotalAuditCount);

        Assert.Equal(
            original.Command.CommandId,
            history.RecentAudit[1].ReversesCommandId);

        Assert.Equal(
            1,
            _factory.Notifier.GetCommandNotifications()
                .Count(notification =>
                    notification.Reason == "ActuatorCommandUndone"));
    }

    [Fact]
    public async Task AcceptedCommand_WhenNotificationFails_StillReturns200AndCommits()
    {
        SensorResponse sensor = await RegisterAsync(
            "notification-failure-relay");

        // Simulate a disconnected/failing real-time transport.
        _factory.Notifier.ThrowOnCommandNotifications = true;

        RelayCommandResponse command =
            await ExecuteAsync(sensor.Id, "RelayOn");

        RelayCommandResponse reversal =
            await UndoAsync(
                sensor.Id,
                command.Command.CommandId);

        Assert.Equal("On", command.Actuator.CommandedState);
        Assert.Equal("Off", reversal.Actuator.CommandedState);

        ActuatorCommandHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(2, history.TotalAuditCount);
        Assert.Equal(0, history.Actuator.UndoDepth);

        Assert.Equal(
            2,
            _factory.Notifier.GetCommandNotifications().Length);

        // Secondary notification failure cannot change the HTTP result
        // or cause the caller to retry a command that already succeeded.
    }

    [Fact]
    public async Task CommandedState_DoesNotChangePartOneReportedBooleanTelemetry()
    {
        SensorResponse sensor = await RegisterAsync(
            "telemetry-independent-relay");

        // Publish actual Boolean telemetry using the existing Part 1 API.
        using HttpResponseMessage accepted =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensor.Id}/telemetry",
                new IngestTelemetryRequest(
                    DateTimeOffset.UtcNow.AddSeconds(-5),
                    null,
                    null,
                    true));

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        string telemetryPath =
            $"/api/sensors/{sensor.Id}/telemetry";

        TelemetryReadingResponse[] before =
            await _client.GetFromJsonAsync<TelemetryReadingResponse[]>(
                telemetryPath) ?? [];

        Assert.Single(before);
        Assert.True(before[0].BooleanValue);

        // Changing the commanded relay state does not insert any
        // additional reported telemetry readings.
        RelayCommandResponse command =
            await ExecuteAsync(sensor.Id, "RelayOn");

        RelayCommandResponse reversal =
            await UndoAsync(
                sensor.Id,
                command.Command.CommandId);

        TelemetryReadingResponse[] after =
            await _client.GetFromJsonAsync<TelemetryReadingResponse[]>(
                telemetryPath) ?? [];

        Assert.Equal(before, after);
        Assert.Equal("Off", reversal.Actuator.CommandedState);

        RealtimeDeviceResponse device =
            await GetDeviceAsync(sensor.Id);

        Assert.Equal("Online", device.Status);
        Assert.NotNull(device.CommandedActuator);
        Assert.Equal("Off", device.CommandedActuator.CommandedState);
    }

    [Fact]
    public async Task AuditRead_ReturnsLatest100WithoutTruncatingActualHistory()
    {
        SensorResponse sensor = await RegisterAsync(
            "bounded-audit-relay");

        var commandIds = new List<Guid>();

        // Generate genuine alternating state changes through the HTTP
        // command endpoint, not fabricated records inserted into a List.
        for (int index = 0; index < 105; index++)
        {
            string type = index % 2 == 0
                ? "RelayOn"
                : "RelayOff";

            RelayCommandResponse result =
                await ExecuteAsync(sensor.Id, type);

            Assert.True(result.Command.StateChanged);

            commandIds.Add(result.Command.CommandId);
        }

        ActuatorCommandHistoryResponse history =
            await GetHistoryAsync(sensor.Id);

        Assert.Equal(105, history.TotalAuditCount);
        Assert.Equal(100, history.RecentAudit.Count);
        Assert.Equal(105, history.Actuator.UndoDepth);

        Assert.Equal(
            commandIds[5],
            history.RecentAudit[0].CommandId);

        Assert.Equal(
            commandIds[^1],
            history.RecentAudit[^1].CommandId);

        Assert.Equal(
            commandIds[^1],
            history.Actuator.LatestUndoableCommandId);

        Assert.Equal("On", history.Actuator.CommandedState);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    /// <summary>
    /// The HTTPS test address avoids unrelated development HTTPS redirect
    /// behaviour and exercises the real MVC/exception middleware pipeline.
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

    private static string CommandPath(Guid deviceId)
    {
        return $"/api/realtime/devices/{deviceId}/commands";
    }

    private static string UndoPath(Guid deviceId)
    {
        return $"{CommandPath(deviceId)}/undo";
    }

    /// <summary>
    /// Registers a real device through Part 1 and optionally marks it
    /// Online using the existing sensor-status endpoint.
    /// </summary>
    private async Task<SensorResponse> RegisterAsync(
        string identifier,
        string category = "Actuator",
        string telemetryType = "Boolean",
        bool online = true)
    {
        using HttpResponseMessage response =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                new RegisterSensorRequest(
                    $"Command Test {identifier}",
                    identifier,
                    "UniqueIdentifier",
                    new SensorDeploymentLocationRequest(
                        "Facility A",
                        "Zone 1",
                        identifier),
                    category,
                    telemetryType));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        SensorResponse sensor =
            await response.Content.ReadFromJsonAsync<SensorResponse>() ??
            throw new InvalidOperationException(
                "Registration response was empty.");

        if (online)
        {
            await SetStatusAsync(sensor.Id, "Online");
        }

        return sensor;
    }

    private async Task SetStatusAsync(Guid deviceId, string status)
    {
        using HttpResponseMessage response =
            await _client.PatchAsJsonAsync(
                $"/api/sensors/{deviceId}/status",
                new ChangeSensorStatusRequest(status));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<RelayCommandResponse> ExecuteAsync(
        Guid deviceId,
        string commandType)
    {
        using HttpResponseMessage response =
            await _client.PostAsJsonAsync(
                CommandPath(deviceId),
                new RelayCommandRequest(commandType));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<RelayCommandResponse>() ??
            throw new InvalidOperationException(
                "Command execution returned an empty response.");
    }

    private async Task<RelayCommandResponse> UndoAsync(
        Guid deviceId,
        Guid expectedLatestCommandId)
    {
        using HttpResponseMessage response =
            await _client.PostAsJsonAsync(
                UndoPath(deviceId),
                new UndoRelayCommandRequest(expectedLatestCommandId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<RelayCommandResponse>() ??
            throw new InvalidOperationException(
                "Undo returned an empty response.");
    }

    private async Task<ActuatorCommandHistoryResponse> GetHistoryAsync(
        Guid deviceId)
    {
        using HttpResponseMessage response =
            await _client.GetAsync(CommandPath(deviceId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content
            .ReadFromJsonAsync<ActuatorCommandHistoryResponse>() ??
            throw new InvalidOperationException(
                "Command history returned an empty response.");
    }

    private async Task<RealtimeSnapshotResponse> GetSnapshotAsync()
    {
        using HttpResponseMessage response =
            await _client.GetAsync("/api/realtime/snapshot");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content
            .ReadFromJsonAsync<RealtimeSnapshotResponse>() ??
            throw new InvalidOperationException(
                "Real-time snapshot returned an empty response.");
    }

    private async Task<RealtimeDeviceResponse> GetDeviceAsync(
        Guid deviceId)
    {
        RealtimeSnapshotResponse snapshot =
            await GetSnapshotAsync();

        return Assert.Single(
            snapshot.Devices,
            device => device.Id == deviceId);
    }

    /// <summary>
    /// Verifies that expected failures use the shared ProblemDetails
    /// contract, including a trace identifier and safe HTTP status.
    /// </summary>
    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedTitle)
    {
        Assert.Equal(expectedStatus, response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        ProblemDetails problem =
            await response.Content.ReadFromJsonAsync<ProblemDetails>() ??
            throw new InvalidOperationException(
                "Expected a ProblemDetails response.");

        Assert.Equal((int)expectedStatus, problem.Status);
        Assert.Equal(expectedTitle, problem.Title);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }

    /// <summary>
    /// Uses real HTTP middleware, DI, sensor persistence, application
    /// services, and command store. Only the notifier is replaced.
    /// </summary>
    private sealed class CommandsApiFactory : WebApplicationFactory<Program>
    {
        public RecordingNotifier Notifier { get; } = new();

        protected override void ConfigureWebHost(
            IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRealtimeNotifier>();

                services.AddSingleton<IRealtimeNotifier>(
                    Notifier);
            });
        }
    }

    /// <summary>
    /// Thread-safe recording notifier.
    ///
    /// Sensor registration and status changes also use IRealtimeNotifier,
    /// so assertions filter by the two command-specific reasons.
    ///
    /// Simulated delivery failures verify that a committed command cannot
    /// be reported as an HTTP failure merely because SignalR is unavailable.
    /// </summary>
    private sealed class RecordingNotifier : IRealtimeNotifier
    {
        private readonly ConcurrentQueue<(
            string Reason,
            Guid? DeviceId)> _notifications = new();

        public bool ThrowOnCommandNotifications { get; set; }

        public Task PublishAsync(
            string reason,
            Guid? sensorProfileId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _notifications.Enqueue((reason, sensorProfileId));

            if (ThrowOnCommandNotifications &&
                reason.StartsWith(
                    "ActuatorCommand",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Simulated real-time notification failure.");
            }

            return Task.CompletedTask;
        }

        public (string Reason, Guid? DeviceId)[] GetCommandNotifications()
        {
            return _notifications
                .Where(notification =>
                    notification.Reason.StartsWith(
                        "ActuatorCommand",
                        StringComparison.Ordinal))
                .ToArray();
        }
    }
}