using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using SmartX.Api.Dashboard;
using SmartX.Application.Realtime;
using SmartX.Contracts.Dashboard;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;
using SmartX.Domain.Dashboard;

namespace SmartX.Api.IntegrationTests.Dashboard;

/// <summary>
/// End-to-end P2-06 regression coverage through real Smart-X HTTP operations.
///
/// The only test doubles capture outgoing SignalR invalidation calls; they
/// do not simulate sensors, anomaly classification, HashSet membership,
/// snapshots, the telemetry repository, or the P2-05 history store.
/// </summary>
public sealed class RealtimeActiveConditionRegressionTests : IAsyncLifetime
{
    private static readonly string TestEncryptionKey =
        Convert.ToBase64String(Enumerable.Range(1, 32)
            .Select(value => (byte)value).ToArray());

    private readonly string _attachmentPath = Path.Combine(
        Path.GetTempPath(),
        "smartx-p206-regression",
        Guid.NewGuid().ToString("N"));

    private readonly DateTimeOffset _telemetryBase =
        DateTimeOffset.UtcNow.AddMinutes(-3);

    private RegressionApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_attachmentPath);
        _factory = new RegressionApiFactory(_attachmentPath, TestEncryptionKey);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_attachmentPath))
        {
            Directory.Delete(_attachmentPath, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentSnapshotReads_NeverMultiplyAnExistingConfigurationEpisode()
    {
        SensorResponse sensor = await RegisterAsync();

        // Establish the genuine issue once. Concurrent GETs should now be
        // pure reads of the same existing HashSet membership and issue ID.
        RealtimeActiveConditionResponse original = Assert.Single(
            (await GetRealtimeAsync()).ActiveConditions);

        Assert.Equal("ConfigurationIssue", original.ConditionType);
        Assert.Equal(1, original.OccurrenceCount);

        Task[] readers = Enumerable.Range(0, 32)
            .Select(async index =>
            {
                if (index % 2 == 0)
                {
                    RealtimeActiveConditionsResponse realtime =
                        await GetRealtimeAsync();
                    RealtimeActiveConditionResponse current =
                        Assert.Single(realtime.ActiveConditions);
                    Assert.Equal(original.IssueId, current.IssueId);
                    Assert.Equal(original.MembershipKey, current.MembershipKey);
                }
                else
                {
                    DashboardSnapshotResponse dashboard =
                        await GetDashboardAsync();
                    DashboardIssueResponse current = Assert.Single(dashboard.Issues);
                    Assert.Equal(original.IssueId, current.Id);
                }
            })
            .ToArray();

        await Task.WhenAll(readers);

        RealtimeActiveConditionsResponse after = await GetRealtimeAsync();
        RealtimeActiveConditionResponse issue = Assert.Single(after.ActiveConditions);
        DashboardIssueCollection collection = GetCollection();

        Assert.Equal(original.IssueId, issue.IssueId);
        Assert.Equal(1, issue.OccurrenceCount);
        Assert.Equal(1, after.ActiveConditionCount);
        Assert.Equal(1, after.UnacknowledgedConditionCount);
        Assert.Equal(1, collection.ActiveConditionCount);
        Assert.Single(collection);
        Assert.True(collection.ContainsActiveCondition(
            sensor.Id, DashboardIssueType.ConfigurationIssue));

        AssertPublicSnapshotsAgree(await GetDashboardAsync(), after);
    }

    [Fact]
    public async Task ConcurrentAcknowledgements_AreIdempotent_AndDoNotResolveAnActiveKey()
    {
        SensorResponse sensor = await RegisterAsync();
        RealtimeActiveConditionResponse issue = Assert.Single(
            (await GetRealtimeAsync()).ActiveConditions);

        // Simulate duplicate submissions and browser retries of the same
        // operator action. All requests target one original issue ID.
        DashboardIssueResponse[] acknowledgements = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(async _ =>
            {
                using HttpResponseMessage response = await _client.PostAsync(
                    $"/api/dashboard/issues/{issue.IssueId}/acknowledge", null);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return await response.Content
                    .ReadFromJsonAsync<DashboardIssueResponse>()
                    ?? throw new InvalidOperationException("Empty acknowledgement response.");
            }));

        Assert.All(acknowledgements, response =>
        {
            Assert.Equal(issue.IssueId, response.Id);
            Assert.Equal("Acknowledged", response.State);
        });

        RealtimeActiveConditionsResponse after = await GetRealtimeAsync();
        RealtimeActiveConditionResponse stillActive = Assert.Single(
            after.ActiveConditions);

        Assert.Equal(issue.IssueId, stillActive.IssueId);
        Assert.Equal(issue.MembershipKey, stillActive.MembershipKey);
        Assert.Equal("Acknowledged", stillActive.State);
        Assert.Equal(1, stillActive.OccurrenceCount);
        Assert.NotNull(stillActive.AcknowledgedAtUtc);
        Assert.Equal(1, after.ActiveConditionCount);
        Assert.Equal(0, after.UnacknowledgedConditionCount);

        DashboardIssueCollection collection = GetCollection();
        Assert.Equal(1, collection.ActiveConditionCount);
        Assert.Single(collection);
        Assert.True(collection.ContainsActiveCondition(
            sensor.Id, DashboardIssueType.ConfigurationIssue));

        AssertPublicSnapshotsAgree(await GetDashboardAsync(), after);
    }

    [Fact]
    public async Task RepeatedSpikes_CreateOneSpikeAndOneRepeatedFailure_ThenRecoverAndRecur()
    {
        SensorResponse sensor = await RegisterAsync();
        await UploadConfigurationAsync(sensor.Id);

        // Three distinct critical spikes are still three legitimate
        // telemetry observations. The HashSet deduplicates active rows,
        // not the observations or their corresponding worker dispatches.
        double[] values = [100, 230, 520, 1200, 1201, 1202, 2500];
        for (int index = 0; index < 4; index++)
        {
            await IngestFloatAsync(sensor.Id, values[index], index * 10);
        }

        RealtimeActiveConditionsResponse abnormal = await GetRealtimeAsync();
        Assert.Equal(2, abnormal.ActiveConditionCount);
        Assert.Equal(2, abnormal.UnacknowledgedConditionCount);
        Assert.Equal(2, abnormal.ActiveConditions
            .Select(condition => condition.MembershipKey).Distinct().Count());

        RealtimeActiveConditionResponse spike = Assert.Single(
            abnormal.ActiveConditions, condition => condition.ConditionType == "TelemetrySpike");
        RealtimeActiveConditionResponse repeated = Assert.Single(
            abnormal.ActiveConditions, condition => condition.ConditionType == "RepeatedFailure");

        Assert.Equal(3, spike.OccurrenceCount);
        Assert.Equal("Critical", spike.Severity);
        Assert.Equal("Critical", repeated.Severity);
        Assert.Equal(1, repeated.OccurrenceCount);
        Assert.Equal($"{sensor.Id:N}:TelemetrySpike", spike.MembershipKey);
        Assert.Equal($"{sensor.Id:N}:RepeatedFailure", repeated.MembershipKey);

        // The policy requires TWO consecutive stable observations.
        await IngestFloatAsync(sensor.Id, values[4], 40);
        RealtimeActiveConditionsResponse oneStable = await GetRealtimeAsync();
        Assert.Equal(2, oneStable.ActiveConditionCount);
        Assert.Contains(oneStable.ActiveConditions, row => row.IssueId == spike.IssueId);
        Assert.Contains(oneStable.ActiveConditions, row => row.IssueId == repeated.IssueId);

        await IngestFloatAsync(sensor.Id, values[5], 50);
        RealtimeActiveConditionsResponse recovered = await GetRealtimeAsync();
        Assert.Empty(recovered.ActiveConditions);
        Assert.Equal(0, recovered.ActiveConditionCount);
        Assert.False(GetCollection().ContainsActiveCondition(
            sensor.Id, DashboardIssueType.TelemetrySpike));
        Assert.False(GetCollection().ContainsActiveCondition(
            sensor.Id, DashboardIssueType.RepeatedFailure));

        DashboardSnapshotResponse historical = await GetDashboardAsync(true);
        Assert.Contains(historical.Issues, row =>
            row.Id == spike.IssueId && row.State == "Resolved");
        Assert.Contains(historical.Issues, row =>
            row.Id == repeated.IssueId && row.State == "Resolved");

        // A later new critical spike is a fresh episode for the original
        // membership key, not a resurrection of the resolved issue ID.
        await IngestFloatAsync(sensor.Id, values[6], 60);
        RealtimeActiveConditionsResponse recurrence = await GetRealtimeAsync();
        RealtimeActiveConditionResponse newSpike = Assert.Single(
            recurrence.ActiveConditions);

        Assert.Equal("TelemetrySpike", newSpike.ConditionType);
        Assert.Equal(spike.MembershipKey, newSpike.MembershipKey);
        Assert.NotEqual(spike.IssueId, newSpike.IssueId);
        Assert.Equal(1, newSpike.OccurrenceCount);
        Assert.Equal(1, recurrence.ActiveConditionCount);
        Assert.True(GetCollection().ContainsActiveCondition(
            sensor.Id, DashboardIssueType.TelemetrySpike));

        // Both original Part 1 persistence and the independent P2-05
        // worker-completion history must contain every accepted packet.
        TelemetryReadingResponse[] partOne = await _client
            .GetFromJsonAsync<TelemetryReadingResponse[]>(
                $"/api/sensors/{sensor.Id}/telemetry?limit=100")
            ?? throw new InvalidOperationException("Missing Part 1 telemetry history.");

        Assert.Equal(values, partOne.Select(row => row.FloatValue!.Value));

        RealtimeHistoryResponse history = await WaitForHistoryCountAsync(sensor.Id, 7);
        Assert.Equal(7L, history.TotalRecordedEvents);
        Assert.Equal(7, history.Events.Count);
        Assert.Equal(values, history.Events.Select(row => row.Telemetry!.FloatValue!.Value));
        Assert.All(history.Events, row => Assert.Equal("TelemetryProcessed", row.Kind));

        AssertPublicSnapshotsAgree(await GetDashboardAsync(), recurrence);
    }

    [Fact]
    public async Task RecoveringOneSensor_DoesNotResolveAnotherSensorsAcknowledgedCondition()
    {
        SensorResponse first = await RegisterAsync();
        SensorResponse second = await RegisterAsync();
        await UploadConfigurationAsync(first.Id);
        await UploadConfigurationAsync(second.Id);

        await IngestFloatAsync(first.Id, 100, 0);
        await IngestFloatAsync(second.Id, 100, 0);
        await IngestFloatAsync(first.Id, 230, 10);
        await IngestFloatAsync(second.Id, 250, 10);

        RealtimeActiveConditionsResponse initial = await GetRealtimeAsync();
        Assert.Equal(2, initial.ActiveConditionCount);

        RealtimeActiveConditionResponse firstSpike = Assert.Single(
            initial.ActiveConditions,
            item => item.SensorProfileId == first.Id && item.ConditionType == "TelemetrySpike");
        RealtimeActiveConditionResponse secondSpike = Assert.Single(
            initial.ActiveConditions,
            item => item.SensorProfileId == second.Id && item.ConditionType == "TelemetrySpike");

        using (HttpResponseMessage acknowledge = await _client.PostAsync(
            $"/api/dashboard/issues/{firstSpike.IssueId}/acknowledge", null))
        {
            Assert.Equal(HttpStatusCode.OK, acknowledge.StatusCode);
        }

        // Two stable readings only for the second sensor.
        await IngestFloatAsync(second.Id, 251, 20);
        await IngestFloatAsync(second.Id, 252, 30);

        RealtimeActiveConditionsResponse after = await GetRealtimeAsync();
        RealtimeActiveConditionResponse surviving = Assert.Single(
            after.ActiveConditions);

        Assert.Equal(first.Id, surviving.SensorProfileId);
        Assert.Equal(firstSpike.IssueId, surviving.IssueId);
        Assert.Equal("Acknowledged", surviving.State);
        Assert.Equal(1, after.ActiveConditionCount);
        Assert.Equal(0, after.UnacknowledgedConditionCount);

        Assert.True(GetCollection().ContainsActiveCondition(
            first.Id, DashboardIssueType.TelemetrySpike));
        Assert.False(GetCollection().ContainsActiveCondition(
            second.Id, DashboardIssueType.TelemetrySpike));

        DashboardSnapshotResponse history = await GetDashboardAsync(true);
        Assert.Contains(history.Issues, item =>
            item.Id == secondSpike.IssueId && item.State == "Resolved");
        Assert.Contains(history.Issues, item =>
            item.Id == firstSpike.IssueId && item.State == "Acknowledged");

        AssertPublicSnapshotsAgree(await GetDashboardAsync(), after);
    }

    [Fact]
    public async Task ThirdFaultAndDecommission_ResolveRepeatedFailure_AndRetainTerminalEpisode()
    {
        SensorResponse sensor = await RegisterAsync();
        await UploadConfigurationAsync(sensor.Id);
        await ChangeStatusAsync(sensor.Id, "Online");

        // Fault observations accumulate across genuine Online recoveries.
        // The third fault activates the established RepeatedFailure type.
        for (int iteration = 0; iteration < 3; iteration++)
        {
            await ChangeStatusAsync(sensor.Id, "Fault");
            if (iteration < 2)
            {
                await ChangeStatusAsync(sensor.Id, "Online");
            }
        }

        RealtimeActiveConditionsResponse faulted = await GetRealtimeAsync();
        Assert.Equal(2, faulted.ActiveConditionCount);

        RealtimeActiveConditionResponse stateIssue = Assert.Single(
            faulted.ActiveConditions, item => item.ConditionType == "SensorStateChange");
        RealtimeActiveConditionResponse repeated = Assert.Single(
            faulted.ActiveConditions, item => item.ConditionType == "RepeatedFailure");

        Assert.Equal("Critical", stateIssue.Severity);
        Assert.Equal("Critical", repeated.Severity);
        Assert.Equal(2, GetCollection().ActiveConditionCount);

        await ChangeStatusAsync(sensor.Id, "Decommissioned");

        RealtimeActiveConditionsResponse after = await GetRealtimeAsync();
        RealtimeActiveConditionResponse terminal = Assert.Single(after.ActiveConditions);

        Assert.Equal("SensorStateChange", terminal.ConditionType);
        Assert.Equal("Information", terminal.Severity);
        Assert.Equal(stateIssue.MembershipKey, terminal.MembershipKey);
        Assert.NotEqual(stateIssue.IssueId, terminal.IssueId);
        Assert.Equal(1, after.ActiveConditionCount);
        Assert.Equal(1, GetCollection().ActiveConditionCount);
        Assert.False(GetCollection().ContainsActiveCondition(
            sensor.Id, DashboardIssueType.RepeatedFailure));
        Assert.False(GetCollection().ContainsActiveCondition(
            sensor.Id, DashboardIssueType.SensorDisconnected));
        Assert.True(GetCollection().ContainsActiveCondition(
            sensor.Id, DashboardIssueType.SensorStateChange));

        DashboardSnapshotResponse history = await GetDashboardAsync(true);
        Assert.Contains(history.Issues, item =>
            item.Id == repeated.IssueId && item.State == "Resolved");
        Assert.Contains(history.Issues, item =>
            item.Id == stateIssue.IssueId && item.State == "Resolved");
        Assert.Contains(history.Issues, item =>
            item.Id == terminal.IssueId && item.State == "Active");

        AssertPublicSnapshotsAgree(await GetDashboardAsync(), after);
    }

    [Fact]
    public async Task ExistingDashboardAndRealtimeInvalidations_StillPublish_WhileReadsAreSilent()
    {
        SensorResponse sensor = await RegisterAsync();

        Assert.Equal(
            new[] { DashboardChangeReasons.SensorRegistered },
            _factory.DashboardChanges.ReasonsFor(sensor.Id));
        Assert.Equal(
            new[] { DashboardChangeReasons.SensorRegistered },
            _factory.RealtimeChanges.ReasonsFor(sensor.Id));

        // GET reconciliation creates a missing-configuration condition
        // but is not a new external telemetry/status event and therefore
        // must not create a notification loop on every dashboard refresh.
        RealtimeActiveConditionResponse issue = Assert.Single(
            (await GetRealtimeAsync()).ActiveConditions);
        await GetDashboardAsync();
        await GetRealtimeAsync();

        Assert.Single(_factory.DashboardChanges.ReasonsFor(sensor.Id));
        Assert.Single(_factory.RealtimeChanges.ReasonsFor(sensor.Id));

        using (HttpResponseMessage acknowledgement = await _client.PostAsync(
            $"/api/dashboard/issues/{issue.IssueId}/acknowledge", null))
        {
            Assert.Equal(HttpStatusCode.OK, acknowledgement.StatusCode);
        }

        await ChangeStatusAsync(sensor.Id, "Online");
        await ChangeStatusAsync(sensor.Id, "Offline");
        await ChangeStatusAsync(sensor.Id, "Online");

        string[] expected =
        [
            DashboardChangeReasons.SensorRegistered,
            DashboardChangeReasons.IssueAcknowledged,
            DashboardChangeReasons.SensorStatusChanged,
            DashboardChangeReasons.SensorStatusChanged,
            DashboardChangeReasons.SensorStatusChanged
        ];

        Assert.Equal(expected, _factory.DashboardChanges.ReasonsFor(sensor.Id));
        Assert.Equal(expected, _factory.RealtimeChanges.ReasonsFor(sensor.Id));

        RealtimeActiveConditionsResponse last = await GetRealtimeAsync();
        Assert.Contains(last.ActiveConditions, item =>
            item.IssueId == issue.IssueId && item.State == "Acknowledged");
        Assert.DoesNotContain(last.ActiveConditions, item =>
            item.ConditionType == "SensorDisconnected");
        AssertPublicSnapshotsAgree(await GetDashboardAsync(), last);

        // Both transports publish invalidations, never independent mutable
        // alert snapshots. Another read must not generate new notifications.
        await GetRealtimeAsync();
        Assert.Equal(expected, _factory.DashboardChanges.ReasonsFor(sensor.Id));
        Assert.Equal(expected, _factory.RealtimeChanges.ReasonsFor(sensor.Id));
    }

    // ------------------------------------------------------------------
    // HTTP and source-of-truth assertions
    // ------------------------------------------------------------------

    private DashboardIssueCollection GetCollection() =>
        _factory.Services.GetRequiredService<DashboardIssueCollection>();

    private async Task<RealtimeActiveConditionsResponse> GetRealtimeAsync() =>
        await _client.GetFromJsonAsync<RealtimeActiveConditionsResponse>(
            "/api/realtime/snapshot/active-conditions")
        ?? throw new InvalidOperationException("Missing real-time active-condition response.");

    private async Task<DashboardSnapshotResponse> GetDashboardAsync(
        bool includeResolved = false) =>
        await _client.GetFromJsonAsync<DashboardSnapshotResponse>(
            includeResolved
                ? "/api/dashboard/issues?includeResolved=true"
                : "/api/dashboard/issues")
        ?? throw new InvalidOperationException("Missing Part 1 dashboard response.");

    private static void AssertPublicSnapshotsAgree(
        DashboardSnapshotResponse dashboard,
        RealtimeActiveConditionsResponse realtime)
    {
        Assert.Equal(dashboard.ActiveIssueCount, realtime.ActiveConditionCount);
        Assert.Equal(dashboard.UnacknowledgedIssueCount,
            realtime.UnacknowledgedConditionCount);
        Assert.Equal(realtime.ActiveConditionCount, realtime.ActiveConditions.Count);

        Guid[] partOneIds = dashboard.Issues.Select(issue => issue.Id)
            .OrderBy(id => id).ToArray();
        Guid[] partTwoIds = realtime.ActiveConditions.Select(issue => issue.IssueId)
            .OrderBy(id => id).ToArray();

        Assert.Equal(partOneIds, partTwoIds);
        Assert.Equal(realtime.ActiveConditions.Count,
            realtime.ActiveConditions.Select(issue => issue.MembershipKey)
                .Distinct(StringComparer.Ordinal).Count());

        foreach (RealtimeActiveConditionResponse condition in realtime.ActiveConditions)
        {
            DashboardIssueResponse source = Assert.Single(dashboard.Issues,
                issue => issue.Id == condition.IssueId);
            Assert.Equal(source.SensorProfileId, condition.SensorProfileId);
            Assert.Equal(source.Type, condition.ConditionType);
            Assert.Equal(source.State, condition.State);
            Assert.Equal(source.Severity, condition.Severity);
            Assert.Equal(source.OccurrenceCount, condition.OccurrenceCount);
            Assert.Equal($"{source.SensorProfileId:N}:{source.Type}", condition.MembershipKey);
        }
    }

    private async Task<SensorResponse> RegisterAsync()
    {
        string identifier = $"p206-regression-{Guid.NewGuid():N}";
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/sensors",
            new RegisterSensorRequest(
                "P2-06 HashSet regression sensor",
                identifier,
                "UniqueIdentifier",
                new SensorDeploymentLocationRequest(
                    "Regression Facility", "Set Lifecycle", identifier),
                "Environmental",
                "Float"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<SensorResponse>()
            ?? throw new InvalidOperationException("Registration returned no sensor.");
    }

    private async Task ChangeStatusAsync(Guid sensorId, string status)
    {
        using HttpResponseMessage response = await _client.PatchAsJsonAsync(
            $"/api/sensors/{sensorId}/status",
            new ChangeSensorStatusRequest(status));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task IngestFloatAsync(Guid sensorId, double value, int seconds)
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/api/sensors/{sensorId}/telemetry",
            new IngestTelemetryRequest(
                _telemetryBase.AddSeconds(seconds), value, null, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task UploadConfigurationAsync(Guid sensorId)
    {
        byte[] configuration =
            """
            {"sampleRateSeconds":5,"enabled":true}
            """u8.ToArray();

        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(configuration);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(file, "file", "p206-regression-config.json");

        using HttpResponseMessage response = await _client.PostAsync(
            $"/api/sensors/{sensorId}/attachments?category=Configuration", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<RealtimeHistoryResponse> WaitForHistoryCountAsync(
        Guid sensorId, long expectedCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                RealtimeHistoryResponse history = await _client
                    .GetFromJsonAsync<RealtimeHistoryResponse>(
                        $"/api/realtime/devices/{sensorId}/history?limit=500",
                        timeout.Token)
                    ?? throw new InvalidOperationException("Missing P2-05 history.");

                if (history.TotalRecordedEvents >= expectedCount)
                {
                    return history;
                }

                await Task.Delay(25, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out waiting for {expectedCount} processed entries for {sensorId}.");
        }
    }

    // ------------------------------------------------------------------
    // Test-only interception of the existing invalidation transports.
    // Only transports are substituted; all lifecycle/HTTP components are real.
    // ------------------------------------------------------------------

    private sealed class RegressionApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _storagePath;
        private readonly string _encryptionKey;

        public RecordingDashboardClient DashboardChanges { get; } = new();
        public RecordingRealtimeNotifier RealtimeChanges { get; } = new();

        public RegressionApiFactory(string storagePath, string encryptionKey)
        {
            _storagePath = storagePath;
            _encryptionKey = encryptionKey;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Attachments:StoragePath", _storagePath);
            builder.UseSetting("Attachments:EncryptionKeyBase64", _encryptionKey);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHubContext<DashboardHub, IDashboardClient>>();
                services.AddSingleton<IHubContext<DashboardHub, IDashboardClient>>(
                    new RecordingDashboardHubContext(DashboardChanges));

                services.RemoveAll<IRealtimeNotifier>();
                services.AddSingleton<IRealtimeNotifier>(RealtimeChanges);
            });
        }
    }

    private sealed class RecordingDashboardClient : IDashboardClient
    {
        private readonly ConcurrentQueue<DashboardChangedResponse> _changes = new();

        public Task DashboardChanged(DashboardChangedResponse change)
        {
            _changes.Enqueue(change);
            return Task.CompletedTask;
        }

        public string[] ReasonsFor(Guid sensorId) => _changes
            .ToArray()
            .Where(change => change.SensorProfileId == sensorId)
            .Select(change => change.Reason)
            .ToArray();
    }

    private sealed class RecordingDashboardHubContext : IHubContext<DashboardHub, IDashboardClient>
    {
        public RecordingDashboardHubContext(RecordingDashboardClient client)
        {
            Clients = new RecordingDashboardHubClients(client);
        }

        public IHubClients<IDashboardClient> Clients { get; }
        public IGroupManager Groups => throw new NotSupportedException(
            "Group operations are not needed for broadcast-only notifications.");
    }

    private sealed class RecordingDashboardHubClients : IHubClients<IDashboardClient>
    {
        public RecordingDashboardHubClients(IDashboardClient client)
        {
            All = client;
        }

        public IDashboardClient All { get; }
        public IDashboardClient AllExcept(IReadOnlyList<string> ignored) => All;
        public IDashboardClient Client(string id) => All;
        public IDashboardClient Clients(IReadOnlyList<string> ids) => All;
        public IDashboardClient Group(string name) => All;
        public IDashboardClient GroupExcept(string name, IReadOnlyList<string> ignored) => All;
        public IDashboardClient Groups(IReadOnlyList<string> names) => All;
        public IDashboardClient User(string userId) => All;
        public IDashboardClient Users(IReadOnlyList<string> userIds) => All;
    }

    private sealed class RecordingRealtimeNotifier : IRealtimeNotifier
    {
        private readonly ConcurrentQueue<(string Reason, Guid? DeviceId)> _changes = new();

        public Task PublishAsync(
            string reason,
            Guid? sensorProfileId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _changes.Enqueue((reason, sensorProfileId));
            return Task.CompletedTask;
        }

        public string[] ReasonsFor(Guid sensorId) => _changes
            .ToArray()
            .Where(change => change.DeviceId == sensorId)
            .Select(change => change.Reason)
            .ToArray();
    }
}