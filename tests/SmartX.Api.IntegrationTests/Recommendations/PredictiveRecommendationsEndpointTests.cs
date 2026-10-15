using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SmartX.Application.Realtime;
using SmartX.Contracts.Commands;
using SmartX.Contracts.Dashboard;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Recommendations;
using SmartX.Contracts.Sensors;

namespace SmartX.Api.IntegrationTests.Recommendations;

/// <summary>
/// Tests the real P2-09 HTTP projection without seeding fake learned counts.
/// All training flows use sensor status changes, actual active episodes,
/// explicit P2-08 operator events, and accepted Stack-backed relay commands.
/// Only the best-effort SignalR notifier is replaced with a recording stub.
/// </summary>
public sealed class PredictiveRecommendationsEndpointTests : IDisposable
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(),
        "smartx-p209-recommendation-http", Guid.NewGuid().ToString("N"));
    private readonly RecommendationApiFactory _factory;
    private readonly HttpClient _client;

    public PredictiveRecommendationsEndpointTests()
    {
        Directory.CreateDirectory(_storage);
        _factory = new RecommendationApiFactory(_storage);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task EmptySnapshot_IsBoundedCacheDisabledAndReadOnly()
    {
        long prior = (await GetInteractionHistory()).TotalRecorded;
        int notifications = _factory.Notifier.Snapshot().Length;

        for (int i = 0; i < 4; i++)
        {
            using HttpResponseMessage response = await _client.GetAsync(
                "/api/realtime/recommendations");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore == true);

            PredictedRecommendationsResponse snapshot = (await response.Content
                .ReadFromJsonAsync<PredictedRecommendationsResponse>())!;
            Assert.NotNull(snapshot);
            Assert.Equal(3, snapshot.MinimumSupportingEpisodes);
            Assert.Equal(TimeSpan.FromDays(7).TotalSeconds, snapshot.EvidenceWindowSeconds);
            Assert.Empty(snapshot.Recommendations);
            Assert.Null(snapshot.NextRefreshAtUtc);
            Assert.Equal(TimeSpan.Zero, snapshot.EvaluatedAtUtc.Offset);
        }

        Assert.Equal(prior, (await GetInteractionHistory()).TotalRecorded);
        Assert.Equal(notifications, _factory.Notifier.Snapshot().Length);
    }

    [Fact]
    public async Task GenuineThreeEpisodeTraining_ReturnsExplainedContextWithoutExecuting()
    {
        (SensorResponse source, SensorResponse target) = await RegisterPair();
        await TrainAsync(source, target, episodes: 2);

        // A new matching alert is live, but two earlier independent episodes
        // are insufficient to produce a suggestion.
        await ChangeStatus(source.Id, "Offline");
        Assert.Empty((await GetRecommendations()).Recommendations);
        await ChangeStatus(source.Id, "Online");

        await TrainAsync(source, target, episodes: 1);
        await ChangeStatus(source.Id, "Offline");
        DashboardIssueResponse current = await GetCurrentDisconnect(source.Id);

        long before = (await GetInteractionHistory()).TotalRecorded;
        int eventCount = _factory.Notifier.Snapshot().Length;
        int auditBefore = (await GetCommandHistory(target.Id)).TotalAuditCount;

        PredictedRecommendationsResponse recommendations = await GetRecommendations();
        PredictedRecommendationResponse action = Assert.Single(recommendations.Recommendations);

        Assert.Equal(current.Id, action.SourceAlertEpisodeId);
        Assert.Equal(source.Id, action.SourceDeviceId);
        Assert.Equal(source.Name, action.SourceDeviceName);
        Assert.Equal("SensorDisconnected", action.ConditionType);
        Assert.Equal(current.Severity, action.Severity);
        Assert.Equal("NotApplicable", action.Movement);
        Assert.Equal(target.Id, action.TargetActuatorId);
        Assert.Equal(target.Name, action.TargetActuatorName);
        Assert.Equal("RelayOn", action.CommandType);
        Assert.Equal(3, action.SupportingEpisodes);
        Assert.InRange(action.Score, 0, 100);
        Assert.Contains(action.ConfidenceBand, new[] { "High", "Moderate", "Emerging" });
        Assert.Contains("3 distinct alert episodes", action.Explanation);
        Assert.Contains("heuristic", action.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.True(action.LastLearnedAtUtc <= recommendations.EvaluatedAtUtc);
        Assert.True(action.ExpiresAtUtc >= recommendations.EvaluatedAtUtc);
        Assert.Equal(action.ExpiresAtUtc, recommendations.NextRefreshAtUtc);

        // HTTP projection cannot mutate the relay or inflate the training
        // stream even if several browser tabs request recommendations.
        for (int i = 0; i < 3; i++)
        {
            Assert.Single((await GetRecommendations()).Recommendations);
        }

        Assert.Equal(before, (await GetInteractionHistory()).TotalRecorded);
        Assert.Equal(eventCount, _factory.Notifier.Snapshot().Length);
        Assert.Equal(auditBefore, (await GetCommandHistory(target.Id)).TotalAuditCount);
        Assert.Equal("Off", (await GetCommandHistory(target.Id)).Actuator.CommandedState);
    }

    [Fact]
    public async Task RecoveryAndLiveActuatorState_ImmediatelyChangeReadOnlyRecommendations()
    {
        (SensorResponse source, SensorResponse target) = await RegisterPair();
        await TrainAsync(source, target, episodes: 3);
        await ChangeStatus(source.Id, "Offline");
        Assert.Single((await GetRecommendations()).Recommendations);

        // No re-training is required: the old evidence survives, but only a
        // CURRENT unresolved episode can permit a recommendation.
        await ChangeStatus(source.Id, "Online");
        Assert.Empty((await GetRecommendations()).Recommendations);
        await ChangeStatus(source.Id, "Offline");
        Assert.Single((await GetRecommendations()).Recommendations);

        await ChangeStatus(target.Id, "Offline");
        Assert.Empty((await GetRecommendations()).Recommendations);
        await ChangeStatus(target.Id, "Online");
        Assert.Single((await GetRecommendations()).Recommendations);

        // The recommendation is NOT executed by GET. A deliberate command
        // suppresses it as a would-be no-op; real Undo makes it eligible again.
        string commandUrl = $"/api/realtime/devices/{target.Id}/commands";
        using HttpResponseMessage commandResponse = await _client.PostAsJsonAsync(
            commandUrl, new RelayCommandRequest("RelayOn"));
        Assert.Equal(HttpStatusCode.OK, commandResponse.StatusCode);
        RelayCommandResponse command = (await commandResponse.Content
            .ReadFromJsonAsync<RelayCommandResponse>())!;
        Assert.True(command.Command.StateChanged);
        Assert.Empty((await GetRecommendations()).Recommendations);

        using HttpResponseMessage undo = await _client.PostAsJsonAsync(commandUrl + "/undo",
            new UndoRelayCommandRequest(command.Command.CommandId));
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Single((await GetRecommendations()).Recommendations);
    }

    [Fact]
    public async Task SuccessfulOperatorPostsPublishRealtimeInvalidation_ButReadsAndBadPostsDoNot()
    {
        SensorResponse viewed = await Register("Viewed", "Environmental", "Float");
        int before = _factory.Notifier.Snapshot().Length;

        await GetRecommendations();
        await _client.GetAsync("/api/realtime/snapshot");
        await _client.GetAsync("/api/realtime/devices/search?text=Viewed");
        await GetInteractionHistory();
        Assert.Equal(before, _factory.Notifier.Snapshot().Length);

        // Client-claimed command successes and invalid identifiers must not
        // manufacture operator evidence or send refresh notifications.
        using HttpResponseMessage fake = await _client.PostAsJsonAsync(
            "/api/realtime/interactions/search", new RecordDeviceSearchRequest(
                "CommandExecuted", null, null, null, null, null, null, null,
                null, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, fake.StatusCode);
        Assert.Equal(before, _factory.Notifier.Snapshot().Length);

        using HttpResponseMessage search = await _client.PostAsJsonAsync(
            "/api/realtime/interactions/search", new RecordDeviceSearchRequest(
                "SearchSubmitted", null, null, "Viewed", null, null, null, null,
                "25", "0", viewed.Id, null));
        Assert.Equal(HttpStatusCode.Created, search.StatusCode);

        using HttpResponseMessage view = await _client.PostAsJsonAsync(
            "/api/realtime/interactions/view", new RecordOperatorViewRequest(viewed.Id, null));
        Assert.Equal(HttpStatusCode.Created, view.StatusCode);

        (string Reason, Guid? DeviceId)[] recent = _factory.Notifier.Snapshot()[before..];
        Assert.Equal(2, recent.Length);
        Assert.All(recent, change =>
        {
            Assert.Equal("OperatorInteractionRecorded", change.Reason);
            Assert.Equal(viewed.Id, change.DeviceId);
        });
        Assert.Equal(2, (await GetInteractionHistory()).TotalRecorded);

        await ChangeStatus(viewed.Id, "Online");
        await ChangeStatus(viewed.Id, "Offline");
        Assert.Contains(_factory.Notifier.Snapshot(), change =>
            change.Reason == "sensor-status-changed" && change.DeviceId == viewed.Id);
    }

    [Fact]
    public async Task AppliedFiltersAndExplicitAlertViews_InvalidateWithoutPassiveInflation()
    {
        SensorResponse source = await Register("Alert view source", "Environmental", "Float");
        await ChangeStatus(source.Id, "Online");
        await ChangeStatus(source.Id, "Offline");
        DashboardIssueResponse current = await GetCurrentDisconnect(source.Id);
        int before = _factory.Notifier.Snapshot().Length;

        // A zero-result deliberate filter change is still an intentional
        // operator action. The signal carries no fictitious selected sensor.
        using HttpResponseMessage filter = await _client.PostAsJsonAsync(
            "/api/realtime/interactions/search", new RecordDeviceSearchRequest(
                "FiltersChanged", null, null, "nonexistent-device-name",
                null, null, null, null, "25", "0", null, null));
        Assert.Equal(HttpStatusCode.Created, filter.StatusCode);

        using HttpResponseMessage view = await _client.PostAsJsonAsync(
            "/api/realtime/interactions/view", new RecordOperatorViewRequest(
                source.Id, current.Id));
        Assert.Equal(HttpStatusCode.Created, view.StatusCode);

        (string Reason, Guid? DeviceId)[] messages = _factory.Notifier.Snapshot()[before..];
        Assert.Equal(2, messages.Length);
        Assert.All(messages, change => Assert.Equal("OperatorInteractionRecorded", change.Reason));
        Assert.Null(messages[0].DeviceId);
        Assert.Equal(source.Id, messages[1].DeviceId);

        long recorded = (await GetInteractionHistory()).TotalRecorded;
        await GetRecommendations();
        await _client.GetAsync("/api/realtime/devices/search?text=nonexistent-device-name");
        Assert.Equal(recorded, (await GetInteractionHistory()).TotalRecorded);
        Assert.Equal(before + 2, _factory.Notifier.Snapshot().Length);
    }

    [Fact]
    public async Task FailedSignalRDelivery_DoesNotRollBackRecordedInteraction()
    {
        SensorResponse sensor = await Register("Failure-safe", "Environmental", "Float");
        _factory.Notifier.FailOperatorNotifications = true;

        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/realtime/interactions/view", new RecordOperatorViewRequest(sensor.Id, null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single((await GetInteractionHistory()).Events);
        Assert.Contains(_factory.Notifier.Snapshot(), change =>
            change.Reason == "OperatorInteractionRecorded" && change.DeviceId == sensor.Id);
    }

    [Fact]
    public async Task RelevantCommandAndUndo_AreAlreadyBroadcastThroughExistingRealtimeHubPath()
    {
        SensorResponse target = await Register("Notifications relay", "Actuator", "Boolean");
        await ChangeStatus(target.Id, "Online");
        int before = _factory.Notifier.Snapshot().Length;

        string path = $"/api/realtime/devices/{target.Id}/commands";
        using HttpResponseMessage commandResponse = await _client.PostAsJsonAsync(
            path, new RelayCommandRequest("RelayOn"));
        Assert.Equal(HttpStatusCode.OK, commandResponse.StatusCode);
        RelayCommandResponse command = (await commandResponse.Content
            .ReadFromJsonAsync<RelayCommandResponse>())!;
        using HttpResponseMessage undoResponse = await _client.PostAsJsonAsync(
            path + "/undo", new UndoRelayCommandRequest(command.Command.CommandId));
        Assert.Equal(HttpStatusCode.OK, undoResponse.StatusCode);

        (string Reason, Guid? DeviceId)[] messages = _factory.Notifier.Snapshot()[before..];
        Assert.Equal(new[] { "ActuatorCommandRecorded", "ActuatorCommandUndone" },
            messages.Select(change => change.Reason).ToArray());
        Assert.All(messages, change => Assert.Equal(target.Id, change.DeviceId));
    }

    private async Task<(SensorResponse Source, SensorResponse Target)> RegisterPair()
    {
        SensorResponse source = await Register("Source temperature", "Environmental", "Float");
        SensorResponse target = await Register("Target relay", "Actuator", "Boolean");
        await ChangeStatus(source.Id, "Online");
        await ChangeStatus(target.Id, "Online");
        return (source, target);
    }

    private async Task TrainAsync(SensorResponse source, SensorResponse target, int episodes)
    {
        for (int i = 0; i < episodes; i++)
        {
            await ChangeStatus(source.Id, "Offline");
            DashboardIssueResponse issue = await GetCurrentDisconnect(source.Id);

            using HttpResponseMessage view = await _client.PostAsJsonAsync(
                "/api/realtime/interactions/view", new RecordOperatorViewRequest(source.Id, issue.Id));
            Assert.Equal(HttpStatusCode.Created, view.StatusCode);

            using HttpResponseMessage selection = await _client.PostAsJsonAsync(
                "/api/realtime/interactions/search", new RecordDeviceSearchRequest(
                    "SearchSubmitted", null, null, target.Name, "Actuator", "Online",
                    null, null, "25", "0", target.Id, issue.Id));
            Assert.Equal(HttpStatusCode.Created, selection.StatusCode);

            string path = $"/api/realtime/devices/{target.Id}/commands";
            using HttpResponseMessage commandResponse = await _client.PostAsJsonAsync(
                path, new RelayCommandRequest("RelayOn"));
            Assert.Equal(HttpStatusCode.OK, commandResponse.StatusCode);
            RelayCommandResponse command = (await commandResponse.Content
                .ReadFromJsonAsync<RelayCommandResponse>())!;
            Assert.True(command.Command.StateChanged);

            // Undo is the genuine P2-04 Stack reversal, never another learned
            // RelayOff pattern. It resets state for the next independent episode.
            using HttpResponseMessage undoResponse = await _client.PostAsJsonAsync(
                path + "/undo", new UndoRelayCommandRequest(command.Command.CommandId));
            Assert.Equal(HttpStatusCode.OK, undoResponse.StatusCode);
            await ChangeStatus(source.Id, "Online");
        }
    }

    private async Task<DashboardIssueResponse> GetCurrentDisconnect(Guid id)
    {
        DashboardSnapshotResponse snapshot = (await _client.GetFromJsonAsync<DashboardSnapshotResponse>(
            "/api/dashboard/issues"))!;
        return Assert.Single(snapshot.Issues, issue =>
            issue.SensorProfileId == id && issue.Type == "SensorDisconnected");
    }

    private async Task<PredictedRecommendationsResponse> GetRecommendations() =>
        (await _client.GetFromJsonAsync<PredictedRecommendationsResponse>(
            "/api/realtime/recommendations"))!;

    private async Task<OperatorInteractionHistoryResponse> GetInteractionHistory() =>
        (await _client.GetFromJsonAsync<OperatorInteractionHistoryResponse>(
            "/api/realtime/interactions"))!;

    private async Task<ActuatorCommandHistoryResponse> GetCommandHistory(Guid id) =>
        (await _client.GetFromJsonAsync<ActuatorCommandHistoryResponse>(
            $"/api/realtime/devices/{id}/commands"))!;

    private async Task<SensorResponse> Register(string name, string category, string dataType)
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/sensors", new RegisterSensorRequest(
                name, "p209-c3-" + Guid.NewGuid().ToString("N"), "UniqueIdentifier",
                new SensorDeploymentLocationRequest("Predictive Integration Lab", "API", "Node 1"),
                category, dataType));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SensorResponse>())!;
    }

    private async Task ChangeStatus(Guid id, string status)
    {
        using HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/sensors/{id}/status", new ChangeSensorStatusRequest(status));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
    }

    /// <summary>
    /// Single test-host instance per test. Domain, learning, active alerts and
    /// command/interaction stores are REAL; the notifier merely records calls.
    /// </summary>
    private sealed class RecommendationApiFactory(string storage) : WebApplicationFactory<Program>
    {
        public RecordingNotifier Notifier { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Attachments:StoragePath", storage);
            builder.UseSetting("Attachments:EncryptionKeyBase64",
                Convert.ToBase64String(Enumerable.Range(1, 32)
                    .Select(value => (byte)value).ToArray()));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRealtimeNotifier>();
                services.AddSingleton<IRealtimeNotifier>(Notifier);
            });
        }
    }

    private sealed class RecordingNotifier : IRealtimeNotifier
    {
        private readonly ConcurrentQueue<(string Reason, Guid? DeviceId)> _events = new();

        public bool FailOperatorNotifications { get; set; }

        public Task PublishAsync(string reason, Guid? sensorProfileId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Enqueue((reason, sensorProfileId));
            if (FailOperatorNotifications && reason == "OperatorInteractionRecorded")
            {
                throw new InvalidOperationException("Simulated SignalR delivery failure.");
            }

            return Task.CompletedTask;
        }

        public (string Reason, Guid? DeviceId)[] Snapshot() => _events.ToArray();
    }
}