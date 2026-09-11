using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Dashboard;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;

namespace SmartX.Api.IntegrationTests.Dashboard;

/// <summary>
/// Exercises the Adaptive Anomaly Spotlight through the complete Smart-X
/// HTTP -> Application -> Domain -> Infrastructure pipeline.
///
/// Each test receives an isolated API process and encrypted attachment
/// directory. This prevents dashboard issue state, registered sensors,
/// telemetry or encrypted files from leaking between test cases.
/// </summary>
public sealed class DashboardIssuesEndpointTests
    : IAsyncLifetime
{
    private static readonly string TestEncryptionKey =
        Convert.ToBase64String(
            Enumerable
                .Range(1, 32)
                .Select(
                    static value =>
                        (byte)value)
                .ToArray());

    private readonly string _storagePath =
        Path.Combine(
            Path.GetTempPath(),
            "smartx-dashboard-tests",
            Guid.NewGuid().ToString("N"));

    /*
     * Keep all telemetry comfortably in the past while preserving short
     * intervals required by the rate-of-change anomaly policy.
     */
    private readonly DateTimeOffset _telemetryBaseTime =
        DateTimeOffset.UtcNow.AddMinutes(-2);

    private DashboardApiFactory _factory =
        null!;

    private HttpClient _client =
        null!;

    /// <summary>
    /// Creates one completely isolated real API host for each integration
    /// test.
    /// </summary>
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(
            _storagePath);

        _factory =
            new DashboardApiFactory(
                _storagePath,
                TestEncryptionKey);

        _client =
            _factory.CreateClient(
                new WebApplicationFactoryClientOptions
                {
                    BaseAddress =
                        new Uri(
                            "https://localhost")
                });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Releases HTTP resources and removes encrypted test attachment files.
    /// </summary>
    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();

        if (Directory.Exists(
                _storagePath))
        {
            Directory.Delete(
                _storagePath,
                recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetDashboard_WhenConfigurationMissing_ReturnsWarningIssue()
    {
        SensorResponse sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:E1");

        DashboardSnapshotResponse snapshot =
            await GetDashboardAsync();

        DashboardIssueResponse issue =
            Assert.Single(
                snapshot.Issues,
                static issue =>
                    issue.Type ==
                    "ConfigurationIssue");

        Assert.Equal(
            sensor.Id,
            issue.SensorProfileId);

        Assert.Equal(
            sensor.Name,
            issue.SensorName);

        Assert.Equal(
            "Warning",
            issue.Severity);

        Assert.Equal(
            "Active",
            issue.State);

        Assert.Equal(
            "Attachments",
            issue.Target);

        Assert.Equal(
            1,
            issue.OccurrenceCount);

        Assert.Equal(
            1,
            snapshot.ActiveIssueCount);

        Assert.Equal(
            1,
            snapshot.UnacknowledgedIssueCount);
    }

    [Fact]
    public async Task UploadConfiguration_WhenIssueActive_ResolvesConfigurationIssue()
    {
        SensorResponse sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:E2");

        DashboardSnapshotResponse initialSnapshot =
            await GetDashboardAsync();

        DashboardIssueResponse configurationIssue =
            Assert.Single(
                initialSnapshot.Issues,
                static issue =>
                    issue.Type ==
                    "ConfigurationIssue");

        await UploadConfigurationAsync(
            sensor.Id);

        DashboardSnapshotResponse activeSnapshot =
            await GetDashboardAsync();

        Assert.DoesNotContain(
            activeSnapshot.Issues,
            static issue =>
                issue.Type ==
                "ConfigurationIssue");

        DashboardSnapshotResponse history =
            await GetDashboardAsync(
                includeResolved: true);

        DashboardIssueResponse resolvedIssue =
            Assert.Single(
                history.Issues,
                issue =>
                    issue.Id ==
                    configurationIssue.Id);

        Assert.Equal(
            "Resolved",
            resolvedIssue.State);

        Assert.NotNull(
            resolvedIssue.ResolvedAtUtc);
    }

    [Fact]
    public async Task IngestTelemetry_WhenSensorOffline_MarksOnlineAndCreatesCriticalSpike()
    {
        SensorResponse sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:E3");

        await IngestFloatTelemetryAsync(
            sensor.Id,
            100.0,
            secondsAfterBase: 0);

        SensorResponse onlineSensor =
            await GetSensorAsync(
                sensor.Id);

        Assert.Equal(
            "Online",
            onlineSensor.Status);

        await IngestFloatTelemetryAsync(
            sensor.Id,
            220.0,
            secondsAfterBase: 10);

        DashboardSnapshotResponse snapshot =
            await GetDashboardAsync();

        DashboardIssueResponse spike =
            Assert.Single(
                snapshot.Issues,
                static issue =>
                    issue.Type ==
                    "TelemetrySpike");

        Assert.Equal(
            sensor.Id,
            spike.SensorProfileId);

        Assert.Equal(
            "Critical",
            spike.Severity);

        Assert.Equal(
            "Telemetry",
            spike.Target);

        Assert.Equal(
            "Active",
            spike.State);

        Assert.Contains(
            "100",
            spike.Detail,
            StringComparison.Ordinal);

        Assert.Contains(
            "220",
            spike.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acknowledge_WhenSpikeActive_PreservesIssueAndReducesPriority()
    {
        SensorResponse sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:E4");

        await CreateCriticalSpikeAsync(
            sensor.Id);

        DashboardSnapshotResponse before =
            await GetDashboardAsync();

        DashboardIssueResponse spikeBefore =
            Assert.Single(
                before.Issues,
                static issue =>
                    issue.Type ==
                    "TelemetrySpike");

        var response =
            await _client.PostAsync(
                $"/api/dashboard/issues/{spikeBefore.Id}/acknowledge",
                content: null);

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        DashboardIssueResponse? acknowledged =
            await response.Content
                .ReadFromJsonAsync<
                    DashboardIssueResponse>();

        Assert.NotNull(
            acknowledged);

        Assert.Equal(
            "Acknowledged",
            acknowledged.State);

        Assert.NotNull(
            acknowledged.AcknowledgedAtUtc);

        Assert.True(
            acknowledged.PriorityScore <
            spikeBefore.PriorityScore);

        DashboardSnapshotResponse after =
            await GetDashboardAsync();

        DashboardIssueResponse spikeAfter =
            Assert.Single(
                after.Issues,
                issue =>
                    issue.Id ==
                    spikeBefore.Id);

        /*
         * Acknowledgement changes operator state but must not falsely claim
         * that the physical condition has recovered.
         */
        Assert.Equal(
            "Acknowledged",
            spikeAfter.State);

        Assert.Equal(
            before.ActiveIssueCount,
            after.ActiveIssueCount);

        Assert.Equal(
            before.UnacknowledgedIssueCount - 1,
            after.UnacknowledgedIssueCount);
    }

    [Fact]
    public async Task IngestTelemetry_WhenStableReadingsFollowSpike_ResolvesSpike()
    {
        SensorResponse sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:E5");

        await CreateCriticalSpikeAsync(
            sensor.Id);

        await IngestFloatTelemetryAsync(
            sensor.Id,
            221.0,
            secondsAfterBase: 20);

        DashboardSnapshotResponse afterFirstStable =
            await GetDashboardAsync();

        Assert.Contains(
            afterFirstStable.Issues,
            static issue =>
                issue.Type ==
                "TelemetrySpike");

        await IngestFloatTelemetryAsync(
            sensor.Id,
            222.0,
            secondsAfterBase: 30);

        DashboardSnapshotResponse afterRecovery =
            await GetDashboardAsync();

        Assert.DoesNotContain(
            afterRecovery.Issues,
            static issue =>
                issue.Type ==
                "TelemetrySpike");

        DashboardSnapshotResponse history =
            await GetDashboardAsync(
                includeResolved: true);

        DashboardIssueResponse resolvedSpike =
            Assert.Single(
                history.Issues,
                static issue =>
                    issue.Type ==
                    "TelemetrySpike");

        Assert.Equal(
            "Resolved",
            resolvedSpike.State);

        Assert.NotNull(
            resolvedSpike.ResolvedAtUtc);
    }

    [Fact]
    public async Task ChangeStatus_WhenSensorDisconnectsAndRecovers_UpdatesDashboard()
    {
        SensorResponse sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:E6");

        /*
         * A newly registered Part 1 sensor begins Offline, so first place it
         * Online before deliberately simulating a disconnect.
         */
        await ChangeSensorStatusAsync(
            sensor.Id,
            "Online");

        await ChangeSensorStatusAsync(
            sensor.Id,
            "Offline");

        DashboardSnapshotResponse disconnected =
            await GetDashboardAsync();

        DashboardIssueResponse disconnectIssue =
            Assert.Single(
                disconnected.Issues,
                static issue =>
                    issue.Type ==
                    "SensorDisconnected");

        Assert.Equal(
            "Warning",
            disconnectIssue.Severity);

        Assert.Equal(
            "Sensor",
            disconnectIssue.Target);

        Assert.Equal(
            "Active",
            disconnectIssue.State);

        await ChangeSensorStatusAsync(
            sensor.Id,
            "Online");

        DashboardSnapshotResponse recovered =
            await GetDashboardAsync();

        Assert.DoesNotContain(
            recovered.Issues,
            static issue =>
                issue.Type ==
                "SensorDisconnected");

        DashboardSnapshotResponse history =
            await GetDashboardAsync(
                includeResolved: true);

        DashboardIssueResponse resolvedDisconnect =
            Assert.Single(
                history.Issues,
                issue =>
                    issue.Id ==
                    disconnectIssue.Id);

        Assert.Equal(
            "Resolved",
            resolvedDisconnect.State);
    }

    [Fact]
    public async Task ChangeStatus_WhenWarningEscalatesToFault_UpdatesSingleCriticalIssue()
    {
        SensorResponse sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:E7");

        await ChangeSensorStatusAsync(
            sensor.Id,
            "Online");

        await ChangeSensorStatusAsync(
            sensor.Id,
            "Warning");

        await ChangeSensorStatusAsync(
            sensor.Id,
            "Fault");

        DashboardSnapshotResponse snapshot =
            await GetDashboardAsync();

        DashboardIssueResponse stateIssue =
            Assert.Single(
                snapshot.Issues,
                static issue =>
                    issue.Type ==
                    "SensorStateChange");

        Assert.Equal(
            "Critical",
            stateIssue.Severity);

        Assert.Equal(
            "Sensor",
            stateIssue.Target);

        Assert.Equal(
            2,
            stateIssue.OccurrenceCount);

        Assert.Equal(
            "Sensor fault detected",
            stateIssue.Title);
    }

    [Fact]
    public async Task IngestTelemetry_WhenFailuresRepeat_CreatesRepeatedFailureIssue()
    {
        SensorResponse sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:E8");

        await IngestFloatTelemetryAsync(
            sensor.Id,
            100.0,
            secondsAfterBase: 0);

        await IngestFloatTelemetryAsync(
            sensor.Id,
            130.0,
            secondsAfterBase: 10);

        await IngestFloatTelemetryAsync(
            sensor.Id,
            180.0,
            secondsAfterBase: 20);

        await IngestFloatTelemetryAsync(
            sensor.Id,
            250.0,
            secondsAfterBase: 30);

        DashboardSnapshotResponse snapshot =
            await GetDashboardAsync();

        DashboardIssueResponse spike =
            Assert.Single(
                snapshot.Issues,
                static issue =>
                    issue.Type ==
                    "TelemetrySpike");

        DashboardIssueResponse repeatedFailure =
            Assert.Single(
                snapshot.Issues,
                static issue =>
                    issue.Type ==
                    "RepeatedFailure");

        Assert.Equal(
            3,
            spike.OccurrenceCount);

        Assert.Equal(
            sensor.Id,
            repeatedFailure.SensorProfileId);

        Assert.Equal(
            "Telemetry",
            repeatedFailure.Target);

        Assert.Contains(
            "repeated",
            repeatedFailure.Title,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Acknowledge_WhenIssueMissing_ReturnsNotFoundProblemDetails()
    {
        Guid missingIssueId =
            Guid.NewGuid();

        var response =
            await _client.PostAsync(
                $"/api/dashboard/issues/{missingIssueId}/acknowledge",
                content: null);

        Assert.Equal(
            HttpStatusCode.NotFound,
            response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers
                .ContentType?
                .MediaType);

        ProblemDetails? problem =
            await response.Content
                .ReadFromJsonAsync<
                    ProblemDetails>();

        Assert.NotNull(
            problem);

        Assert.Equal(
            "Dashboard issue not found",
            problem.Title);

        Assert.Equal(
            StatusCodes.Status404NotFound,
            problem.Status);
    }

    [Fact]
    public async Task Acknowledge_WhenIssueAlreadyResolved_ReturnsConflictProblemDetails()
    {
        SensorResponse sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:E9");

        DashboardSnapshotResponse initialSnapshot =
            await GetDashboardAsync();

        DashboardIssueResponse configurationIssue =
            Assert.Single(
                initialSnapshot.Issues,
                static issue =>
                    issue.Type ==
                    "ConfigurationIssue");

        await UploadConfigurationAsync(
            sensor.Id);

        DashboardSnapshotResponse history =
            await GetDashboardAsync(
                includeResolved: true);

        Assert.Contains(
            history.Issues,
            issue =>
                issue.Id ==
                    configurationIssue.Id &&
                issue.State ==
                    "Resolved");

        var response =
            await _client.PostAsync(
                $"/api/dashboard/issues/{configurationIssue.Id}/acknowledge",
                content: null);

        Assert.Equal(
            HttpStatusCode.Conflict,
            response.StatusCode);

        ProblemDetails? problem =
            await response.Content
                .ReadFromJsonAsync<
                    ProblemDetails>();

        Assert.NotNull(
            problem);

        Assert.Equal(
            "Dashboard issue state conflict",
            problem.Title);

        Assert.Equal(
            StatusCodes.Status409Conflict,
            problem.Status);
    }

    [Fact]
    public async Task Negotiate_WhenDashboardHubMapped_ReturnsSignalRTransportInformation()
    {
        using var content =
            new ByteArrayContent([]);

        var response =
            await _client.PostAsync(
                "/hubs/dashboard/negotiate?negotiateVersion=1",
                content);

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        string json =
            await response.Content
                .ReadAsStringAsync();

        using JsonDocument document =
            JsonDocument.Parse(
                json);

        JsonElement root =
            document.RootElement;

        Assert.True(
            root.TryGetProperty(
                "connectionId",
                out JsonElement connectionId));

        Assert.Equal(
            JsonValueKind.String,
            connectionId.ValueKind);

        Assert.False(
            string.IsNullOrWhiteSpace(
                connectionId.GetString()));

        Assert.True(
            root.TryGetProperty(
                "availableTransports",
                out JsonElement transports));

        Assert.Equal(
            JsonValueKind.Array,
            transports.ValueKind);

        Assert.True(
            transports.GetArrayLength() >
            0);
    }

    /// <summary>
    /// Registers one real Float sensor through the public API.
    /// </summary>
    private async Task<SensorResponse> RegisterSensorAsync(
        string macAddress)
    {
        var request =
            new RegisterSensorRequest(
                "Adaptive Dashboard Sensor",
                macAddress,
                "MacAddress",
                new SensorDeploymentLocationRequest(
                    "Cape Town Facility",
                    "Zone A",
                    "Node 18"),
                "Environmental",
                "Float");

        var response =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                request);

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        SensorResponse? sensor =
            await response.Content
                .ReadFromJsonAsync<
                    SensorResponse>();

        return sensor ??
            throw new InvalidOperationException(
                "Sensor registration returned an empty response.");
    }

    /// <summary>
    /// Retrieves one sensor through the public API.
    /// </summary>
    private async Task<SensorResponse> GetSensorAsync(
        Guid sensorProfileId)
    {
        var response =
            await _client.GetAsync(
                $"/api/sensors/{sensorProfileId}");

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        SensorResponse? sensor =
            await response.Content
                .ReadFromJsonAsync<
                    SensorResponse>();

        return sensor ??
            throw new InvalidOperationException(
                "Sensor retrieval returned an empty response.");
    }

    /// <summary>
    /// Sends one strongly typed Float telemetry packet.
    /// </summary>
    private async Task IngestFloatTelemetryAsync(
        Guid sensorProfileId,
        double value,
        int secondsAfterBase)
    {
        var request =
            new IngestTelemetryRequest(
                _telemetryBaseTime.AddSeconds(
                    secondsAfterBase),
                value,
                null,
                null);

        var response =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensorProfileId}/telemetry",
                request);

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);
    }

    /// <summary>
    /// Creates the two-reading sequence used to trigger one critical
    /// rate-of-change anomaly.
    /// </summary>
    private async Task CreateCriticalSpikeAsync(
        Guid sensorProfileId)
    {
        await IngestFloatTelemetryAsync(
            sensorProfileId,
            100.0,
            secondsAfterBase: 0);

        await IngestFloatTelemetryAsync(
            sensorProfileId,
            220.0,
            secondsAfterBase: 10);
    }

    /// <summary>
    /// Applies one explicit Part 1 simulated sensor state and returns the
    /// server-authoritative profile.
    /// </summary>
    private async Task<SensorResponse> ChangeSensorStatusAsync(
        Guid sensorProfileId,
        string status)
    {
        var response =
            await _client.PatchAsJsonAsync(
                $"/api/sensors/{sensorProfileId}/status",
                new ChangeSensorStatusRequest(
                    status));

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        SensorResponse? sensor =
            await response.Content
                .ReadFromJsonAsync<
                    SensorResponse>();

        return sensor ??
            throw new InvalidOperationException(
                "Sensor state update returned an empty response.");
    }

    /// <summary>
    /// Uploads one valid Configuration attachment through the same streaming
    /// encrypted endpoint used by the React client.
    /// </summary>
    private async Task UploadConfigurationAsync(
        Guid sensorProfileId)
    {
        byte[] configuration =
            """
            {
              "sampleRateSeconds": 5,
              "enabled": true
            }
            """u8.ToArray();

        using var form =
            new MultipartFormDataContent();

        using var fileContent =
            new ByteArrayContent(
                configuration);

        fileContent.Headers.ContentType =
            new MediaTypeHeaderValue(
                "application/json");

        form.Add(
            fileContent,
            "file",
            "sensor-config.json");

        var response =
            await _client.PostAsync(
                $"/api/sensors/{sensorProfileId}/attachments" +
                "?category=Configuration",
                form);

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);
    }

    /// <summary>
    /// Retrieves the current or historical dashboard snapshot.
    /// </summary>
    private async Task<DashboardSnapshotResponse> GetDashboardAsync(
        bool includeResolved = false)
    {
        string path =
            includeResolved
                ? "/api/dashboard/issues?includeResolved=true"
                : "/api/dashboard/issues";

        var response =
            await _client.GetAsync(
                path);

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);

        DashboardSnapshotResponse? snapshot =
            await response.Content
                .ReadFromJsonAsync<
                    DashboardSnapshotResponse>();

        return snapshot ??
            throw new InvalidOperationException(
                "Dashboard snapshot returned an empty response.");
    }

    /// <summary>
    /// Configures the real Smart-X application with isolated encrypted
    /// attachment persistence.
    ///
    /// Sensor, telemetry, dashboard issue and SignalR services otherwise use
    /// the normal production registrations from Program.cs.
    /// </summary>
    private sealed class DashboardApiFactory
        : WebApplicationFactory<Program>
    {
        private readonly string _storagePath;
        private readonly string _encryptionKey;

        public DashboardApiFactory(
            string storagePath,
            string encryptionKey)
        {
            _storagePath =
                storagePath;

            _encryptionKey =
                encryptionKey;
        }

        protected override void ConfigureWebHost(
            IWebHostBuilder builder)
        {
            builder.UseSetting(
                "Attachments:StoragePath",
                _storagePath);

            builder.UseSetting(
                "Attachments:EncryptionKeyBase64",
                _encryptionKey);
        }
    }
}