using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using SmartX.Contracts.Dashboard;
using SmartX.Contracts.Realtime;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;
using SmartX.Domain.Dashboard;

namespace SmartX.Api.IntegrationTests.Dashboard;

/// <summary>
/// P2-06 real-time active-condition integration tests.
///
/// The tests use the actual ASP.NET Core API, canonical sensor repository,
/// existing Part 1 anomaly service, shared DashboardIssueCollection and its
/// HashSet-backed active membership.
///
/// They prove that Part 1 and Part 2 observe the same condition episodes
/// without introducing a second alert store.
/// </summary>
public sealed class RealtimeActiveConditionsEndpointTests : IAsyncLifetime
{
    private static readonly string TestEncryptionKey =
        Convert.ToBase64String(
            Enumerable.Range(1, 32)
                .Select(value => (byte)value)
                .ToArray());

    private readonly string _storagePath =
        Path.Combine(
            Path.GetTempPath(),
            "smartx-p206-active-conditions",
            Guid.NewGuid().ToString("N"));

    private readonly DateTimeOffset _telemetryBaseTime =
        DateTimeOffset.UtcNow.AddMinutes(-3);

    private ActiveConditionApiFactory _factory = null!;
    private HttpClient _client = null!;

    /// <summary>
    /// Each test gets a fresh application host and attachment directory.
    /// The normal production Domain, Application, Infrastructure and
    /// SignalR registrations remain active.
    /// </summary>
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_storagePath);

        _factory = new ActiveConditionApiFactory(
            _storagePath,
            TestEncryptionKey);

        _client = _factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Dispose of the isolated API and its encrypted test attachments.
    /// </summary>
    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();

        if (Directory.Exists(_storagePath))
        {
            Directory.Delete(
                _storagePath,
                recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task EmptyRegistry_ReturnsEmptyActiveConditionsSnapshot()
    {
        RealtimeActiveConditionsResponse realtime =
            await GetRealtimeConditionsAsync();

        DashboardSnapshotResponse dashboard =
            await GetDashboardAsync();

        Assert.Equal(0, realtime.ActiveConditionCount);
        Assert.Equal(0, realtime.UnacknowledgedConditionCount);
        Assert.Empty(realtime.ActiveConditions);

        Assert.Equal(TimeSpan.Zero, realtime.GeneratedAtUtc.Offset);

        AssertSnapshotsAgree(dashboard, realtime);

        DashboardIssueCollection collection = GetIssueCollection();

        Assert.Equal(0, collection.ActiveConditionCount);
        Assert.Empty(collection.GetActiveSnapshot(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task MissingConfiguration_IsVisibleThroughBothSnapshots_WithoutDuplicateActivation()
    {
        SensorResponse sensor = await RegisterSensorAsync();

        // This is the FIRST dashboard-related GET.
        // The new real-time endpoint must perform the same configuration
        // reconciliation as the original Part 1 dashboard endpoint.
        RealtimeActiveConditionsResponse first =
            await GetRealtimeConditionsAsync();

        Assert.Equal(1, first.ActiveConditionCount);
        Assert.Equal(1, first.UnacknowledgedConditionCount);

        RealtimeActiveConditionResponse configuration =
            Assert.Single(first.ActiveConditions);

        Assert.Equal(sensor.Id, configuration.SensorProfileId);
        Assert.Equal("ConfigurationIssue", configuration.ConditionType);
        Assert.Equal("Warning", configuration.Severity);
        Assert.Equal("Active", configuration.State);
        Assert.Equal(1, configuration.OccurrenceCount);

        Assert.Equal(
            $"{sensor.Id:N}:ConfigurationIssue",
            configuration.MembershipKey);

        Assert.NotEqual(Guid.Empty, configuration.IssueId);

        DashboardIssueCollection collection = GetIssueCollection();

        Assert.True(
            collection.ContainsActiveCondition(
                sensor.Id,
                DashboardIssueType.ConfigurationIssue));

        Assert.Equal(1, collection.ActiveConditionCount);

        // Re-reading either snapshot must not create another activation
        // or increment the occurrence count for unchanged configuration.
        RealtimeActiveConditionsResponse second =
            await GetRealtimeConditionsAsync();

        DashboardSnapshotResponse dashboard =
            await GetDashboardAsync();

        RealtimeActiveConditionResponse repeated =
            Assert.Single(second.ActiveConditions);

        Assert.Equal(configuration.IssueId, repeated.IssueId);
        Assert.Equal(configuration.MembershipKey, repeated.MembershipKey);
        Assert.Equal(1, repeated.OccurrenceCount);

        Assert.Equal(1, collection.ActiveConditionCount);
        Assert.Single(collection);

        AssertSnapshotsAgree(dashboard, second);
    }

    [Fact]
    public async Task ConfigurationRecovery_RemovesMembershipButPreservesPartOneHistory()
    {
        SensorResponse sensor = await RegisterSensorAsync();

        RealtimeActiveConditionResponse original =
            Assert.Single(
                (await GetRealtimeConditionsAsync()).ActiveConditions);

        Assert.Equal(
            "ConfigurationIssue",
            original.ConditionType);

        await UploadConfigurationAsync(sensor.Id);

        RealtimeActiveConditionsResponse recovered =
            await GetRealtimeConditionsAsync();

        Assert.Equal(0, recovered.ActiveConditionCount);
        Assert.Equal(0, recovered.UnacknowledgedConditionCount);
        Assert.Empty(recovered.ActiveConditions);

        DashboardIssueCollection collection = GetIssueCollection();

        Assert.False(
            collection.ContainsActiveCondition(
                sensor.Id,
                DashboardIssueType.ConfigurationIssue));

        Assert.Equal(0, collection.ActiveConditionCount);

        // The resolved episode is retained in the original Part 1
        // dashboard history; removing membership must not erase audit.
        DashboardSnapshotResponse historical =
            await GetDashboardAsync(includeResolved: true);

        DashboardIssueResponse resolved =
            Assert.Single(
                historical.Issues,
                issue => issue.Id == original.IssueId);

        Assert.Equal("Resolved", resolved.State);
        Assert.NotNull(resolved.ResolvedAtUtc);

        Assert.Equal(0, historical.ActiveIssueCount);

        // Repeating configuration reads cannot reactivate a condition
        // while valid configuration evidence is still attached.
        RealtimeActiveConditionsResponse repeated =
            await GetRealtimeConditionsAsync();

        Assert.Empty(repeated.ActiveConditions);
        Assert.Equal(0, collection.ActiveConditionCount);
    }

    [Fact]
    public async Task WarningToFault_EscalatesOneIssueWithoutDuplicatingMembership()
    {
        SensorResponse sensor = await RegisterSensorAsync();

        await UploadConfigurationAsync(sensor.Id);

        await ChangeStatusAsync(sensor.Id, "Online");
        await ChangeStatusAsync(sensor.Id, "Warning");

        RealtimeActiveConditionsResponse warning =
            await GetRealtimeConditionsAsync();

        RealtimeActiveConditionResponse first =
            Assert.Single(warning.ActiveConditions);

        Assert.Equal("SensorStateChange", first.ConditionType);
        Assert.Equal("Warning", first.Severity);
        Assert.Equal("Active", first.State);
        Assert.Equal(1, first.OccurrenceCount);

        await ChangeStatusAsync(sensor.Id, "Fault");

        RealtimeActiveConditionsResponse fault =
            await GetRealtimeConditionsAsync();

        RealtimeActiveConditionResponse escalated =
            Assert.Single(fault.ActiveConditions);

        Assert.Equal(first.IssueId, escalated.IssueId);
        Assert.Equal(first.MembershipKey, escalated.MembershipKey);

        Assert.Equal("Critical", escalated.Severity);
        Assert.Equal(2, escalated.OccurrenceCount);

        Assert.Equal(1, fault.ActiveConditionCount);
        Assert.Equal(1, fault.UnacknowledgedConditionCount);

        DashboardIssueCollection collection = GetIssueCollection();

        Assert.Equal(1, collection.ActiveConditionCount);

        Assert.True(
            collection.ContainsActiveCondition(
                sensor.Id,
                DashboardIssueType.SensorStateChange));

        // Operator acknowledgement does not clear the underlying fault.
        await AcknowledgeAsync(escalated.IssueId);

        RealtimeActiveConditionsResponse acknowledged =
            await GetRealtimeConditionsAsync();

        RealtimeActiveConditionResponse afterAcknowledgement =
            Assert.Single(acknowledged.ActiveConditions);

        Assert.Equal(escalated.IssueId, afterAcknowledgement.IssueId);
        Assert.Equal(escalated.MembershipKey, afterAcknowledgement.MembershipKey);

        Assert.Equal("Acknowledged", afterAcknowledgement.State);
        Assert.NotNull(afterAcknowledgement.AcknowledgedAtUtc);

        Assert.Equal(1, acknowledged.ActiveConditionCount);
        Assert.Equal(0, acknowledged.UnacknowledgedConditionCount);
        Assert.Equal(2, afterAcknowledgement.OccurrenceCount);

        Assert.Equal(1, collection.ActiveConditionCount);

        AssertSnapshotsAgree(
            await GetDashboardAsync(),
            acknowledged);
    }

    [Fact]
    public async Task OfflineOnlineAndLaterOffline_CreateDistinctConditionEpisodes()
    {
        SensorResponse sensor = await RegisterSensorAsync();

        await UploadConfigurationAsync(sensor.Id);

        // A newly registered sensor starts Offline, so establish
        // connectivity before simulating a genuine disconnect.
        await ChangeStatusAsync(sensor.Id, "Online");
        await ChangeStatusAsync(sensor.Id, "Offline");

        RealtimeActiveConditionsResponse disconnected =
            await GetRealtimeConditionsAsync();

        RealtimeActiveConditionResponse first =
            Assert.Single(disconnected.ActiveConditions);

        Assert.Equal("SensorDisconnected", first.ConditionType);
        Assert.Equal("Warning", first.Severity);
        Assert.Equal("Active", first.State);

        Assert.True(
            GetIssueCollection().ContainsActiveCondition(
                sensor.Id,
                DashboardIssueType.SensorDisconnected));

        // An acknowledgement is not recovery.
        await AcknowledgeAsync(first.IssueId);

        RealtimeActiveConditionsResponse acknowledged =
            await GetRealtimeConditionsAsync();

        Assert.Equal(1, acknowledged.ActiveConditionCount);
        Assert.Equal(0, acknowledged.UnacknowledgedConditionCount);

        RealtimeActiveConditionResponse acknowledgedIssue =
            Assert.Single(acknowledged.ActiveConditions);

        Assert.Equal(first.IssueId, acknowledgedIssue.IssueId);
        Assert.Equal("Acknowledged", acknowledgedIssue.State);

        // A genuine Offline -> Online transition resolves the condition.
        await ChangeStatusAsync(sensor.Id, "Online");

        RealtimeActiveConditionsResponse online =
            await GetRealtimeConditionsAsync();

        Assert.Equal(0, online.ActiveConditionCount);
        Assert.Empty(online.ActiveConditions);

        Assert.False(
            GetIssueCollection().ContainsActiveCondition(
                sensor.Id,
                DashboardIssueType.SensorDisconnected));

        DashboardSnapshotResponse historical =
            await GetDashboardAsync(includeResolved: true);

        DashboardIssueResponse resolved =
            Assert.Single(
                historical.Issues,
                issue => issue.Id == first.IssueId);

        Assert.Equal("Resolved", resolved.State);

        // A later disconnect has the same membership identity, but it
        // must start a fresh operator-facing activation episode.
        await ChangeStatusAsync(sensor.Id, "Offline");

        RealtimeActiveConditionsResponse recurrence =
            await GetRealtimeConditionsAsync();

        RealtimeActiveConditionResponse second =
            Assert.Single(recurrence.ActiveConditions);

        Assert.Equal(first.MembershipKey, second.MembershipKey);
        Assert.NotEqual(first.IssueId, second.IssueId);

        Assert.Equal(1, second.OccurrenceCount);
        Assert.Equal("Active", second.State);

        Assert.Equal(1, recurrence.ActiveConditionCount);
        Assert.Equal(1, recurrence.UnacknowledgedConditionCount);

        AssertSnapshotsAgree(
            await GetDashboardAsync(),
            recurrence);
    }

    [Fact]
    public async Task MultipleSensorsAndConditionTypes_UseDistinctStableKeys()
    {
        SensorResponse sensorA = await RegisterSensorAsync();
        SensorResponse sensorB = await RegisterSensorAsync();

        // Neither device has configuration, so both legitimately
        // have a ConfigurationIssue.
        //
        // Sensor A additionally enters a degraded warning state.
        await ChangeStatusAsync(sensorA.Id, "Online");
        await ChangeStatusAsync(sensorA.Id, "Warning");

        RealtimeActiveConditionsResponse realtime =
            await GetRealtimeConditionsAsync();

        Assert.Equal(3, realtime.ActiveConditionCount);
        Assert.Equal(3, realtime.UnacknowledgedConditionCount);

        Assert.Equal(3, realtime.ActiveConditions.Count);

        Assert.Equal(
            3,
            realtime.ActiveConditions
                .Select(condition => condition.MembershipKey)
                .Distinct(StringComparer.Ordinal)
                .Count());

        Assert.Contains(
            realtime.ActiveConditions,
            condition =>
                condition.SensorProfileId == sensorA.Id &&
                condition.ConditionType == "ConfigurationIssue");

        Assert.Contains(
            realtime.ActiveConditions,
            condition =>
                condition.SensorProfileId == sensorA.Id &&
                condition.ConditionType == "SensorStateChange");

        Assert.Contains(
            realtime.ActiveConditions,
            condition =>
                condition.SensorProfileId == sensorB.Id &&
                condition.ConditionType == "ConfigurationIssue");

        DashboardIssueCollection collection = GetIssueCollection();

        Assert.Equal(3, collection.ActiveConditionCount);

        Assert.True(
            collection.ContainsActiveCondition(
                sensorA.Id,
                DashboardIssueType.ConfigurationIssue));

        Assert.True(
            collection.ContainsActiveCondition(
                sensorA.Id,
                DashboardIssueType.SensorStateChange));

        Assert.True(
            collection.ContainsActiveCondition(
                sensorB.Id,
                DashboardIssueType.ConfigurationIssue));

        // Recovering one condition cannot remove another type or
        // another sensor's membership.
        await UploadConfigurationAsync(sensorA.Id);

        RealtimeActiveConditionsResponse afterOneRecovery =
            await GetRealtimeConditionsAsync();

        Assert.Equal(2, afterOneRecovery.ActiveConditionCount);

        Assert.False(
            collection.ContainsActiveCondition(
                sensorA.Id,
                DashboardIssueType.ConfigurationIssue));

        Assert.True(
            collection.ContainsActiveCondition(
                sensorA.Id,
                DashboardIssueType.SensorStateChange));

        Assert.True(
            collection.ContainsActiveCondition(
                sensorB.Id,
                DashboardIssueType.ConfigurationIssue));

        AssertSnapshotsAgree(
            await GetDashboardAsync(),
            afterOneRecovery);
    }

    [Fact]
    public async Task TelemetrySpike_RepeatedReportsRecoverAndRecurAsFreshEpisode()
    {
        SensorResponse sensor = await RegisterSensorAsync();

        await UploadConfigurationAsync(sensor.Id);

        // Establish the baseline. Valid telemetry also marks the
        // initially Offline sensor Online through the existing gateway.
        await IngestFloatAsync(sensor.Id, 100.0, 0);

        // First critical spike.
        await IngestFloatAsync(sensor.Id, 220.0, 10);

        RealtimeActiveConditionsResponse firstSnapshot =
            await GetRealtimeConditionsAsync();

        RealtimeActiveConditionResponse first =
            Assert.Single(firstSnapshot.ActiveConditions);

        Assert.Equal("TelemetrySpike", first.ConditionType);
        Assert.Equal("Critical", first.Severity);
        Assert.Equal(1, first.OccurrenceCount);

        // A second abnormal observation updates the same episode.
        // This does not cross the existing three-event repeated-failure
        // threshold, so it should not create another condition type.
        await IngestFloatAsync(sensor.Id, 500.0, 20);

        RealtimeActiveConditionsResponse repeatedSnapshot =
            await GetRealtimeConditionsAsync();

        RealtimeActiveConditionResponse repeated =
            Assert.Single(repeatedSnapshot.ActiveConditions);

        Assert.Equal(first.IssueId, repeated.IssueId);
        Assert.Equal(first.MembershipKey, repeated.MembershipKey);
        Assert.Equal(2, repeated.OccurrenceCount);
        Assert.Equal("Critical", repeated.Severity);

        Assert.Equal(1, repeatedSnapshot.ActiveConditionCount);

        // One stable reading is not sufficient for recovery.
        await IngestFloatAsync(sensor.Id, 501.0, 30);

        RealtimeActiveConditionsResponse partiallyRecovered =
            await GetRealtimeConditionsAsync();

        RealtimeActiveConditionResponse pendingRecovery =
            Assert.Single(partiallyRecovered.ActiveConditions);

        Assert.Equal(first.IssueId, pendingRecovery.IssueId);
        Assert.Equal(1, partiallyRecovered.ActiveConditionCount);

        // The second consecutive stable reading confirms recovery.
        await IngestFloatAsync(sensor.Id, 502.0, 40);

        RealtimeActiveConditionsResponse recovered =
            await GetRealtimeConditionsAsync();

        Assert.Equal(0, recovered.ActiveConditionCount);
        Assert.Empty(recovered.ActiveConditions);

        DashboardIssueCollection collection = GetIssueCollection();

        Assert.False(
            collection.ContainsActiveCondition(
                sensor.Id,
                DashboardIssueType.TelemetrySpike));

        // A later large change is a NEW episode, not a continuation
        // of the resolved one.
        await IngestFloatAsync(sensor.Id, 1100.0, 50);

        RealtimeActiveConditionsResponse recurrence =
            await GetRealtimeConditionsAsync();

        RealtimeActiveConditionResponse second =
            Assert.Single(recurrence.ActiveConditions);

        Assert.Equal("TelemetrySpike", second.ConditionType);
        Assert.Equal(first.MembershipKey, second.MembershipKey);

        Assert.NotEqual(first.IssueId, second.IssueId);
        Assert.Equal(1, second.OccurrenceCount);
        Assert.Equal("Active", second.State);

        Assert.Equal(1, recurrence.ActiveConditionCount);

        // All six accepted telemetry readings still belong to the
        // original Part 1 telemetry repository.
        TelemetryReadingResponse[] recent =
            await _client.GetFromJsonAsync<TelemetryReadingResponse[]>(
                $"/api/sensors/{sensor.Id}/telemetry?limit=100")
            ?? throw new InvalidOperationException(
                "Part 1 telemetry response was missing.");

        Assert.Equal(6, recent.Length);

        Assert.Equal(
            new[] { 100.0, 220.0, 500.0, 501.0, 502.0, 1100.0 },
            recent.Select(reading => reading.FloatValue!.Value));

        AssertSnapshotsAgree(
            await GetDashboardAsync(),
            recurrence);
    }

    [Fact]
    public async Task Decommissioning_ResolvesOldEpisodeAndPreservesTerminalStateIssue()
    {
        SensorResponse sensor = await RegisterSensorAsync();

        await UploadConfigurationAsync(sensor.Id);

        await ChangeStatusAsync(sensor.Id, "Online");
        await ChangeStatusAsync(sensor.Id, "Warning");

        RealtimeActiveConditionResponse warning =
            Assert.Single(
                (await GetRealtimeConditionsAsync()).ActiveConditions);

        Assert.Equal("SensorStateChange", warning.ConditionType);

        // The existing Part 1 lifecycle resolves non-actionable issues
        // and retains a new informational terminal-state issue.
        await ChangeStatusAsync(sensor.Id, "Decommissioned");

        RealtimeActiveConditionsResponse after =
            await GetRealtimeConditionsAsync();

        RealtimeActiveConditionResponse terminal =
            Assert.Single(after.ActiveConditions);

        Assert.Equal("SensorStateChange", terminal.ConditionType);
        Assert.Equal("Information", terminal.Severity);

        Assert.Equal(warning.MembershipKey, terminal.MembershipKey);
        Assert.NotEqual(warning.IssueId, terminal.IssueId);

        Assert.Equal(1, after.ActiveConditionCount);

        DashboardIssueCollection collection = GetIssueCollection();

        Assert.True(
            collection.ContainsActiveCondition(
                sensor.Id,
                DashboardIssueType.SensorStateChange));

        Assert.False(
            collection.ContainsActiveCondition(
                sensor.Id,
                DashboardIssueType.SensorDisconnected));

        Assert.False(
            collection.ContainsActiveCondition(
                sensor.Id,
                DashboardIssueType.TelemetrySpike));

        DashboardSnapshotResponse history =
            await GetDashboardAsync(includeResolved: true);

        DashboardIssueResponse previous =
            Assert.Single(
                history.Issues,
                issue => issue.Id == warning.IssueId);

        Assert.Equal("Resolved", previous.State);

        DashboardIssueResponse terminalHistory =
            Assert.Single(
                history.Issues,
                issue => issue.Id == terminal.IssueId);

        Assert.Equal("Active", terminalHistory.State);
        Assert.Equal("Sensor decommissioned", terminalHistory.Title);

        AssertSnapshotsAgree(
            await GetDashboardAsync(),
            after);
    }

    // ---------------------------------------------------------------------
    // Shared assertions
    // ---------------------------------------------------------------------

    /// <summary>
    /// Verifies both public read contracts report the same underlying
    /// active episode identities, condition types, operator states and
    /// occurrence counts.
    ///
    /// Priority scores and timestamps are not compared between separate
    /// HTTP reads because time-dependent rankings are evaluated anew.
    /// </summary>
    private static void AssertSnapshotsAgree(
        DashboardSnapshotResponse dashboard,
        RealtimeActiveConditionsResponse realtime)
    {
        Assert.Equal(
            dashboard.ActiveIssueCount,
            realtime.ActiveConditionCount);

        Assert.Equal(
            dashboard.UnacknowledgedIssueCount,
            realtime.UnacknowledgedConditionCount);

        Assert.Equal(
            realtime.ActiveConditionCount,
            realtime.ActiveConditions.Count);

        Assert.Equal(
            dashboard.Issues.Select(issue => issue.Id),
            realtime.ActiveConditions.Select(condition => condition.IssueId));

        foreach (RealtimeActiveConditionResponse condition
            in realtime.ActiveConditions)
        {
            DashboardIssueResponse source =
                Assert.Single(
                    dashboard.Issues,
                    issue => issue.Id == condition.IssueId);

            Assert.Equal(
                source.SensorProfileId,
                condition.SensorProfileId);

            Assert.Equal(source.Type, condition.ConditionType);
            Assert.Equal(source.Severity, condition.Severity);
            Assert.Equal(source.State, condition.State);

            Assert.Equal(
                source.OccurrenceCount,
                condition.OccurrenceCount);

            Assert.Equal(
                $"{source.SensorProfileId:N}:{source.Type}",
                condition.MembershipKey);

            Assert.NotEqual(
                "Resolved",
                condition.State);
        }

        Assert.Equal(
            realtime.ActiveConditions.Count,
            realtime.ActiveConditions
                .Select(condition => condition.MembershipKey)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    // ---------------------------------------------------------------------
    // Real HTTP workflow helpers
    // ---------------------------------------------------------------------

    private DashboardIssueCollection GetIssueCollection()
    {
        return _factory.Services
            .GetRequiredService<DashboardIssueCollection>();
    }

    private async Task<RealtimeActiveConditionsResponse>
        GetRealtimeConditionsAsync()
    {
        return await _client
            .GetFromJsonAsync<RealtimeActiveConditionsResponse>(
                "/api/realtime/snapshot/active-conditions")
            ?? throw new InvalidOperationException(
                "Real-time active-condition snapshot was missing.");
    }

    private async Task<DashboardSnapshotResponse> GetDashboardAsync(
        bool includeResolved = false)
    {
        string path = includeResolved
            ? "/api/dashboard/issues?includeResolved=true"
            : "/api/dashboard/issues";

        return await _client
            .GetFromJsonAsync<DashboardSnapshotResponse>(path)
            ?? throw new InvalidOperationException(
                "Dashboard snapshot was missing.");
    }

    private async Task<SensorResponse> RegisterSensorAsync()
    {
        string identifier =
            $"p206-condition-{Guid.NewGuid():N}";

        using HttpResponseMessage response =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                new RegisterSensorRequest(
                    "P2-06 Active Condition Sensor",
                    identifier,
                    "UniqueIdentifier",
                    new SensorDeploymentLocationRequest(
                        "Evidence Facility",
                        "Active Condition Zone",
                        identifier),
                    "Environmental",
                    "Float"));

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        return await response.Content
            .ReadFromJsonAsync<SensorResponse>()
            ?? throw new InvalidOperationException(
                "Sensor registration response was missing.");
    }

    private async Task ChangeStatusAsync(
        Guid sensorId,
        string status)
    {
        using HttpResponseMessage response =
            await _client.PatchAsJsonAsync(
                $"/api/sensors/{sensorId}/status",
                new ChangeSensorStatusRequest(status));

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);
    }

    private async Task IngestFloatAsync(
        Guid sensorId,
        double value,
        int secondsAfterBase)
    {
        using HttpResponseMessage response =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensorId}/telemetry",
                new IngestTelemetryRequest(
                    _telemetryBaseTime.AddSeconds(secondsAfterBase),
                    value,
                    null,
                    null));

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);
    }

    private async Task AcknowledgeAsync(Guid issueId)
    {
        using HttpResponseMessage response =
            await _client.PostAsync(
                $"/api/dashboard/issues/{issueId}/acknowledge",
                content: null);

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        DashboardIssueResponse acknowledged =
            await response.Content
                .ReadFromJsonAsync<DashboardIssueResponse>()
            ?? throw new InvalidOperationException(
                "Issue acknowledgement response was missing.");

        Assert.Equal(issueId, acknowledged.Id);
        Assert.Equal("Acknowledged", acknowledged.State);
    }

    /// <summary>
    /// Uploads a valid configuration through the actual Part 1 encrypted
    /// attachment endpoint so configuration recovery is genuine.
    /// </summary>
    private async Task UploadConfigurationAsync(Guid sensorId)
    {
        byte[] configuration =
            """
            {
              "sampleRateSeconds": 5,
              "enabled": true
            }
            """u8.ToArray();

        using var form = new MultipartFormDataContent();
        using var fileContent =
            new ByteArrayContent(configuration);

        fileContent.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");

        form.Add(
            fileContent,
            "file",
            "p206-sensor-config.json");

        using HttpResponseMessage response =
            await _client.PostAsync(
                $"/api/sensors/{sensorId}/attachments" +
                "?category=Configuration",
                form);

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);
    }

    /// <summary>
    /// Uses the normal application registrations while providing
    /// test-local encrypted attachment storage.
    ///
    /// The active HashSet, dashboard issue dictionary, telemetry
    /// processing, sensor repository and SignalR infrastructure
    /// are all the genuine production implementations.
    /// </summary>
    private sealed class ActiveConditionApiFactory
        : WebApplicationFactory<Program>
    {
        private readonly string _attachmentPath;
        private readonly string _encryptionKey;

        public ActiveConditionApiFactory(
            string attachmentPath,
            string encryptionKey)
        {
            _attachmentPath = attachmentPath;
            _encryptionKey = encryptionKey;
        }

        protected override void ConfigureWebHost(
            IWebHostBuilder builder)
        {
            builder.UseSetting(
                "Attachments:StoragePath",
                _attachmentPath);

            builder.UseSetting(
                "Attachments:EncryptionKeyBase64",
                _encryptionKey);
        }
    }
}