using SmartX.Application.Attachments;
using SmartX.Application.Dashboard;
using SmartX.Application.Sensors;
using SmartX.Application.Telemetry;
using SmartX.Domain.Dashboard;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

namespace SmartX.Application.Tests.Dashboard;

/// <summary>
/// Verifies Adaptive Anomaly Spotlight analysis independently from HTTP,
/// SignalR and concrete infrastructure.
///
/// These tests deliberately exercise the real generic telemetry packet types
/// and dashboard issue collection so the production anomaly path remains
/// representative of the actual Smart-X architecture.
/// </summary>
public sealed class AdaptiveAnomalyAnalysisServiceTests
{
    private static readonly DateTimeOffset BaseTime =
        new(
            2026,
            9,
            11,
            14,
            0,
            0,
            TimeSpan.Zero);

    [Fact]
    public async Task AnalyzeLatestTelemetryAsync_WhenFloatSpikeCritical_AddsCriticalIssue()
    {
        SensorProfile sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:B1");

        var telemetry =
            new RecordingTelemetryRepository();

        telemetry.Seed(
            sensor.Id,
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                100.0,
                BaseTime.AddSeconds(-30)),
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                220.0,
                BaseTime.AddSeconds(-10)));

        DashboardIssueCollection issues = new();

        AdaptiveAnomalyAnalysisService service =
            CreateService(
                sensor,
                telemetry,
                new RecordingAttachmentStore(),
                issues);

        await service.AnalyzeLatestTelemetryAsync(
            sensor.Id);

        DashboardIssue issue =
            Assert.Single(
                issues.GetActiveSnapshot(BaseTime));

        Assert.Equal(
            DashboardIssueType.TelemetrySpike,
            issue.Type);

        Assert.Equal(
            DashboardIssueSeverity.Critical,
            issue.Severity);

        Assert.Equal(
            DashboardIssueTarget.Telemetry,
            issue.Target);

        Assert.Contains(
            "100",
            issue.Detail,
            StringComparison.Ordinal);

        Assert.Contains(
            "220",
            issue.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeLatestTelemetryAsync_WhenIntegerChangeWarning_AddsWarningIssue()
    {
        SensorProfile sensor =
            CreateSensor(
                TelemetryDataType.Integer,
                "AA:BB:CC:DD:EE:B2");

        var telemetry =
            new RecordingTelemetryRepository();

        telemetry.Seed(
            sensor.Id,
            new NumericTelemetryPacket<int>(
                sensor.DeviceIdentifier,
                1000,
                BaseTime.AddSeconds(-30)),
            new NumericTelemetryPacket<int>(
                sensor.DeviceIdentifier,
                1250,
                BaseTime.AddSeconds(-10)));

        DashboardIssueCollection issues = new();

        AdaptiveAnomalyAnalysisService service =
            CreateService(
                sensor,
                telemetry,
                new RecordingAttachmentStore(),
                issues);

        await service.AnalyzeLatestTelemetryAsync(
            sensor.Id);

        DashboardIssue issue =
            Assert.Single(
                issues.GetActiveSnapshot(BaseTime));

        Assert.Equal(
            DashboardIssueType.TelemetrySpike,
            issue.Type);

        Assert.Equal(
            DashboardIssueSeverity.Warning,
            issue.Severity);
    }

    [Fact]
    public async Task AnalyzeLatestTelemetryAsync_WhenChangeSmall_DoesNotCreateIssue()
    {
        SensorProfile sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:B3");

        var telemetry =
            new RecordingTelemetryRepository();

        telemetry.Seed(
            sensor.Id,
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                100.0,
                BaseTime.AddSeconds(-30)),
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                110.0,
                BaseTime.AddSeconds(-10)));

        DashboardIssueCollection issues = new();

        AdaptiveAnomalyAnalysisService service =
            CreateService(
                sensor,
                telemetry,
                new RecordingAttachmentStore(),
                issues);

        await service.AnalyzeLatestTelemetryAsync(
            sensor.Id);

        Assert.Empty(
            issues.GetActiveSnapshot(BaseTime));
    }

    [Fact]
    public async Task AnalyzeLatestTelemetryAsync_WhenLargeChangeOccursSlowly_DoesNotCreateSpike()
    {
        SensorProfile sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:B4");

        var telemetry =
            new RecordingTelemetryRepository();

        telemetry.Seed(
            sensor.Id,
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                100.0,
                BaseTime.AddMinutes(-20)),
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                220.0,
                BaseTime.AddMinutes(-10)));

        DashboardIssueCollection issues = new();

        AdaptiveAnomalyAnalysisService service =
            CreateService(
                sensor,
                telemetry,
                new RecordingAttachmentStore(),
                issues);

        await service.AnalyzeLatestTelemetryAsync(
            sensor.Id);

        Assert.Empty(
            issues.GetActiveSnapshot(BaseTime));
    }

    [Fact]
    public async Task AnalyzeLatestTelemetryAsync_WhenRepeatedSpikes_CreatesRepeatedFailure()
    {
        SensorProfile sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:B5");

        var telemetry =
            new RecordingTelemetryRepository();

        telemetry.Seed(
            sensor.Id,
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                100.0,
                BaseTime.AddSeconds(-40)),
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                130.0,
                BaseTime.AddSeconds(-30)));

        DashboardIssueCollection issues = new();

        AdaptiveAnomalyAnalysisService service =
            CreateService(
                sensor,
                telemetry,
                new RecordingAttachmentStore(),
                issues);

        await service.AnalyzeLatestTelemetryAsync(
            sensor.Id);

        telemetry.Append(
            sensor.Id,
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                180.0,
                BaseTime.AddSeconds(-20)));

        await service.AnalyzeLatestTelemetryAsync(
            sensor.Id);

        telemetry.Append(
            sensor.Id,
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                250.0,
                BaseTime.AddSeconds(-10)));

        await service.AnalyzeLatestTelemetryAsync(
            sensor.Id);

        IReadOnlyList<DashboardIssue> active =
            issues.GetActiveSnapshot(
                BaseTime);

        DashboardIssue spike =
            Assert.Single(
                active,
                issue =>
                    issue.Type ==
                    DashboardIssueType.TelemetrySpike);

        DashboardIssue repeatedFailure =
            Assert.Single(
                active,
                issue =>
                    issue.Type ==
                    DashboardIssueType.RepeatedFailure);

        Assert.Equal(
            3,
            spike.OccurrenceCount);

        Assert.Equal(
            DashboardIssueTarget.Telemetry,
            repeatedFailure.Target);
    }

    [Fact]
    public async Task AnalyzeLatestTelemetryAsync_WhenTwoStableReadingsFollowSpike_ResolvesTelemetryIssues()
    {
        SensorProfile sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:B6");

        var telemetry =
            new RecordingTelemetryRepository();

        telemetry.Seed(
            sensor.Id,
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                100.0,
                BaseTime.AddSeconds(-40)),
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                220.0,
                BaseTime.AddSeconds(-30)));

        DashboardIssueCollection issues = new();

        AdaptiveAnomalyAnalysisService service =
            CreateService(
                sensor,
                telemetry,
                new RecordingAttachmentStore(),
                issues);

        await service.AnalyzeLatestTelemetryAsync(
            sensor.Id);

        Assert.Contains(
            issues.GetActiveSnapshot(BaseTime),
            issue =>
                issue.Type ==
                DashboardIssueType.TelemetrySpike);

        telemetry.Append(
            sensor.Id,
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                221.0,
                BaseTime.AddSeconds(-20)));

        await service.AnalyzeLatestTelemetryAsync(
            sensor.Id);

        /*
         * One normal observation is not sufficient. This mirrors the research
         * decision that an issue should remain ranked until recovery has been
         * confirmed rather than disappearing after one packet.
         */
        Assert.Contains(
            issues.GetActiveSnapshot(BaseTime),
            issue =>
                issue.Type ==
                DashboardIssueType.TelemetrySpike);

        telemetry.Append(
            sensor.Id,
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                222.0,
                BaseTime.AddSeconds(-10)));

        await service.AnalyzeLatestTelemetryAsync(
            sensor.Id);

        Assert.DoesNotContain(
            issues.GetActiveSnapshot(BaseTime),
            issue =>
                issue.Type ==
                DashboardIssueType.TelemetrySpike);
    }

    [Fact]
    public async Task AnalyzeLatestTelemetryAsync_ForBooleanSensor_DoesNotCreateNumericSpike()
    {
        SensorProfile sensor =
            CreateSensor(
                TelemetryDataType.Boolean,
                "AA:BB:CC:DD:EE:B7");

        var telemetry =
            new RecordingTelemetryRepository();

        telemetry.Seed(
            sensor.Id,
            new TelemetryPacket<bool>(
                sensor.DeviceIdentifier,
                false,
                BaseTime.AddSeconds(-20)),
            new TelemetryPacket<bool>(
                sensor.DeviceIdentifier,
                true,
                BaseTime.AddSeconds(-10)));

        DashboardIssueCollection issues = new();

        AdaptiveAnomalyAnalysisService service =
            CreateService(
                sensor,
                telemetry,
                new RecordingAttachmentStore(),
                issues);

        await service.AnalyzeLatestTelemetryAsync(
            sensor.Id);

        Assert.DoesNotContain(
            issues.GetActiveSnapshot(BaseTime),
            issue =>
                issue.Type ==
                DashboardIssueType.TelemetrySpike);
    }

    [Fact]
    public async Task AnalyzeConfigurationAsync_WhenConfigurationMissing_CreatesSingleIssue()
    {
        SensorProfile sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:B8");

        DashboardIssueCollection issues = new();

        AdaptiveAnomalyAnalysisService service =
            CreateService(
                sensor,
                new RecordingTelemetryRepository(),
                new RecordingAttachmentStore(),
                issues);

        await service.AnalyzeConfigurationAsync(
            sensor.Id);

        await service.AnalyzeConfigurationAsync(
            sensor.Id);

        DashboardIssue issue =
            Assert.Single(
                issues.GetActiveSnapshot(BaseTime));

        Assert.Equal(
            DashboardIssueType.ConfigurationIssue,
            issue.Type);

        /*
         * Re-checking the same missing state must not pretend that another
         * failure occurred.
         */
        Assert.Equal(
            1,
            issue.OccurrenceCount);

        Assert.Equal(
            DashboardIssueTarget.Attachments,
            issue.Target);
    }

    [Fact]
    public async Task AnalyzeConfigurationAsync_WhenConfigurationAdded_ResolvesIssue()
    {
        SensorProfile sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:B9");

        var attachments =
            new RecordingAttachmentStore();

        DashboardIssueCollection issues = new();

        AdaptiveAnomalyAnalysisService service =
            CreateService(
                sensor,
                new RecordingTelemetryRepository(),
                attachments,
                issues);

        await service.AnalyzeConfigurationAsync(
            sensor.Id);

        attachments.Add(
            new SensorAttachmentMetadata(
                Guid.NewGuid(),
                sensor.Id,
                "sensor.cfg",
                "text/plain",
                SensorAttachmentCategory.Configuration,
                32,
                BaseTime));

        await service.AnalyzeConfigurationAsync(
            sensor.Id);

        Assert.DoesNotContain(
            issues.GetActiveSnapshot(BaseTime),
            issue =>
                issue.Type ==
                DashboardIssueType.ConfigurationIssue);
    }

    [Fact]
    public async Task AnalyzeSensorStateAsync_WhenSensorDisconnects_CreatesDisconnectIssue()
    {
        SensorProfile sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:BA");

        sensor.ChangeStatus(
            SensorStatus.Online,
            BaseTime.AddMinutes(-15));

        SensorStatus previousStatus =
            sensor.Status;

        sensor.ChangeStatus(
            SensorStatus.Offline,
            BaseTime.AddMinutes(-5));

        var telemetry =
            new RecordingTelemetryRepository();

        telemetry.Seed(
            sensor.Id,
            new NumericTelemetryPacket<double>(
                sensor.DeviceIdentifier,
                23.5,
                BaseTime.AddMinutes(-6)));

        DashboardIssueCollection issues = new();

        AdaptiveAnomalyAnalysisService service =
            CreateService(
                sensor,
                telemetry,
                new RecordingAttachmentStore(),
                issues);

        await service.AnalyzeSensorStateAsync(
            sensor.Id,
            previousStatus);

        DashboardIssue issue =
            Assert.Single(
                issues.GetActiveSnapshot(BaseTime));

        Assert.Equal(
            DashboardIssueType.SensorDisconnected,
            issue.Type);

        Assert.Equal(
            DashboardIssueSeverity.Warning,
            issue.Severity);

        Assert.Contains(
            "last telemetry",
            issue.Detail,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            BaseTime.AddMinutes(-5),
            issue.FirstObservedAtUtc);
    }

    [Fact]
    public async Task AnalyzeSensorStateAsync_WhenSensorReturnsOnline_ResolvesDisconnect()
    {
        SensorProfile sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:BB");

        sensor.ChangeStatus(
            SensorStatus.Online,
            BaseTime.AddMinutes(-20));

        sensor.ChangeStatus(
            SensorStatus.Offline,
            BaseTime.AddMinutes(-10));

        DashboardIssueCollection issues = new();

        AdaptiveAnomalyAnalysisService service =
            CreateService(
                sensor,
                new RecordingTelemetryRepository(),
                new RecordingAttachmentStore(),
                issues);

        await service.AnalyzeSensorStateAsync(
            sensor.Id,
            SensorStatus.Online);

        Assert.Contains(
            issues.GetActiveSnapshot(BaseTime),
            issue =>
                issue.Type ==
                DashboardIssueType.SensorDisconnected);

        SensorStatus previousStatus =
            sensor.Status;

        sensor.ChangeStatus(
            SensorStatus.Online,
            BaseTime.AddMinutes(-1));

        await service.AnalyzeSensorStateAsync(
            sensor.Id,
            previousStatus);

        Assert.DoesNotContain(
            issues.GetActiveSnapshot(BaseTime),
            issue =>
                issue.Type ==
                DashboardIssueType.SensorDisconnected);
    }

    [Fact]
    public async Task AnalyzeSensorStateAsync_WhenSensorFaults_CreatesCriticalStateIssue()
    {
        SensorProfile sensor =
            CreateSensor(
                TelemetryDataType.Integer,
                "AA:BB:CC:DD:EE:BC");

        SensorStatus previousStatus =
            sensor.Status;

        sensor.ChangeStatus(
            SensorStatus.Fault,
            BaseTime.AddMinutes(-1));

        DashboardIssueCollection issues = new();

        AdaptiveAnomalyAnalysisService service =
            CreateService(
                sensor,
                new RecordingTelemetryRepository(),
                new RecordingAttachmentStore(),
                issues);

        await service.AnalyzeSensorStateAsync(
            sensor.Id,
            previousStatus);

        DashboardIssue issue =
            Assert.Single(
                issues.GetActiveSnapshot(BaseTime));

        Assert.Equal(
            DashboardIssueType.SensorStateChange,
            issue.Type);

        Assert.Equal(
            DashboardIssueSeverity.Critical,
            issue.Severity);

        Assert.Equal(
            DashboardIssueTarget.Sensor,
            issue.Target);
    }

    [Fact]
    public async Task AnalyzeLatestTelemetryAsync_WhenSensorMissing_ThrowsSensorNotFoundException()
    {
        var service =
            new AdaptiveAnomalyAnalysisService(
                new StubSensorRepository([]),
                new RecordingTelemetryRepository(),
                new RecordingAttachmentStore(),
                new DashboardIssueCollection(),
                new FixedTimeProvider(BaseTime),
                new AdaptiveAnomalyPolicy());

        await Assert.ThrowsAsync<
            SensorNotFoundException>(
            () =>
                service.AnalyzeLatestTelemetryAsync(
                    Guid.NewGuid()));
    }

    [Fact]
    public async Task AnalyzeLatestTelemetryAsync_WhenCancelled_DoesNotCreateIssue()
    {
        SensorProfile sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:BD");

        DashboardIssueCollection issues = new();

        AdaptiveAnomalyAnalysisService service =
            CreateService(
                sensor,
                new RecordingTelemetryRepository(),
                new RecordingAttachmentStore(),
                issues);

        using var cancellationSource =
            new CancellationTokenSource();

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(
            () =>
                service.AnalyzeLatestTelemetryAsync(
                    sensor.Id,
                    cancellationSource.Token));

        Assert.Empty(
            issues.GetActiveSnapshot(BaseTime));
    }

    private static AdaptiveAnomalyAnalysisService CreateService(
        SensorProfile sensor,
        RecordingTelemetryRepository telemetryRepository,
        RecordingAttachmentStore attachmentStore,
        DashboardIssueCollection issues)
    {
        return new AdaptiveAnomalyAnalysisService(
            new StubSensorRepository(
                [sensor]),
            telemetryRepository,
            attachmentStore,
            issues,
            new FixedTimeProvider(BaseTime),
            new AdaptiveAnomalyPolicy());
    }

    private static SensorProfile CreateSensor(
        TelemetryDataType dataType,
        string macAddress)
    {
        return SensorProfile.Create(
            $"Sensor {dataType}",
            DeviceIdentifier.FromMacAddress(
                macAddress),
            new DeploymentLocation(
                "Cape Town Facility",
                "Zone A",
                "Node 1"),
            SensorCategory.Environmental,
            dataType,
            BaseTime.AddHours(-2));
    }

    /// <summary>
    /// Deterministic clock used to make priority and recovery tests repeatable.
    /// </summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(
            DateTimeOffset utcNow)
        {
            _utcNow =
                utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }

    /// <summary>
    /// Minimal in-memory sensor repository used to isolate the analysis
    /// service from concrete infrastructure.
    /// </summary>
    private sealed class StubSensorRepository
        : ISensorRepository
    {
        private readonly Dictionary<Guid, SensorProfile>
            _sensors;

        public StubSensorRepository(
            IEnumerable<SensorProfile> sensors)
        {
            _sensors =
                sensors.ToDictionary(
                    static sensor =>
                        sensor.Id);
        }

        public Task<bool> TryAddAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_sensors.Values.Any(
                    existing =>
                        existing.DeviceIdentifier ==
                        sensor.DeviceIdentifier))
            {
                return Task.FromResult(false);
            }

            _sensors.Add(
                sensor.Id,
                sensor);

            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<SensorProfile>> GetAllAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<
                IReadOnlyList<SensorProfile>>(
                    _sensors.Values.ToArray());
        }

        public Task<SensorProfile?> GetByIdAsync(
            Guid sensorProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _sensors.TryGetValue(
                sensorProfileId,
                out SensorProfile? sensor);

            return Task.FromResult(
                sensor);
        }

        public Task<SensorProfile?> GetByDeviceIdentifierAsync(
            DeviceIdentifier deviceIdentifier,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(deviceIdentifier);

            cancellationToken.ThrowIfCancellationRequested();

            SensorProfile? sensor =
                _sensors.Values.FirstOrDefault(
                    existing => existing.DeviceIdentifier == deviceIdentifier);

            return Task.FromResult(sensor);
        }

        public Task<bool> UpdateAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_sensors.ContainsKey(
                    sensor.Id))
            {
                return Task.FromResult(false);
            }

            _sensors[sensor.Id] =
                sensor;

            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Strongly typed telemetry repository test double.
    ///
    /// Each closed generic payload type remains in its own List so the tests
    /// exercise the same generic contract used by the production repository.
    /// </summary>
    private sealed class RecordingTelemetryRepository
        : ITelemetryRepository
    {
        private readonly Dictionary<
            (Guid SensorId, Type PayloadType),
            object> _stores = [];

        public void Seed<T>(
            Guid sensorProfileId,
            params TelemetryPacket<T>[] packets)
            where T : struct
        {
            _stores[
                (
                    sensorProfileId,
                    typeof(T))] =
                packets.ToList();
        }

        public void Append<T>(
            Guid sensorProfileId,
            TelemetryPacket<T> packet)
            where T : struct
        {
            List<TelemetryPacket<T>> store =
                GetOrCreateStore<T>(
                    sensorProfileId);

            store.Add(
                packet);
        }

        public Task AddAsync<T>(
            Guid sensorProfileId,
            TelemetryPacket<T> packet,
            CancellationToken cancellationToken = default)
            where T : struct
        {
            cancellationToken.ThrowIfCancellationRequested();

            Append(
                sensorProfileId,
                packet);

            return Task.CompletedTask;
        }

        public Task AddBatchAsync<T>(
            Guid sensorProfileId,
            HistoricalTelemetryBuffer<T> history,
            CancellationToken cancellationToken = default)
            where T : struct
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<TelemetryPacket<T>> store =
                GetOrCreateStore<T>(
                    sensorProfileId);

            for (var index = 0;
                 index < history.Count;
                 index++)
            {
                store.Add(
                    history[index]);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TelemetryPacket<T>>> GetRecentAsync<T>(
            Guid sensorProfileId,
            int limit,
            CancellationToken cancellationToken = default)
            where T : struct
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_stores.TryGetValue(
                    (
                        sensorProfileId,
                        typeof(T)),
                    out object? rawStore))
            {
                return Task.FromResult<
                    IReadOnlyList<TelemetryPacket<T>>>(
                        Array.Empty<TelemetryPacket<T>>());
            }

            var store =
                (List<TelemetryPacket<T>>)rawStore;

            int skip =
                Math.Max(
                    0,
                    store.Count - limit);

            IReadOnlyList<TelemetryPacket<T>> result =
                store
                    .Skip(skip)
                    .ToArray();

            return Task.FromResult(
                result);
        }

        private List<TelemetryPacket<T>> GetOrCreateStore<T>(
            Guid sensorProfileId)
            where T : struct
        {
            var key =
                (
                    sensorProfileId,
                    typeof(T));

            if (_stores.TryGetValue(
                    key,
                    out object? existing))
            {
                return (List<TelemetryPacket<T>>)existing;
            }

            var store =
                new List<TelemetryPacket<T>>();

            _stores.Add(
                key,
                store);

            return store;
        }
    }

    /// <summary>
    /// Minimal attachment persistence test double used to simulate the
    /// presence or absence of sensor configuration evidence.
    /// </summary>
    private sealed class RecordingAttachmentStore
        : ISensorAttachmentStore
    {
        private readonly List<SensorAttachmentMetadata>
            _attachments = [];

        public void Add(
            SensorAttachmentMetadata attachment)
        {
            _attachments.Add(
                attachment);
        }

        public async Task<SensorAttachmentMetadata> SaveAsync(
            SensorAttachmentWriteRequest request,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var destination =
                new MemoryStream();

            await content.CopyToAsync(
                destination,
                cancellationToken);

            var metadata =
                new SensorAttachmentMetadata(
                    Guid.NewGuid(),
                    request.SensorProfileId,
                    request.FileName,
                    request.ContentType,
                    request.Category,
                    destination.Length,
                    request.UploadedAtUtc);

            _attachments.Add(
                metadata);

            return metadata;
        }

        public Task<IReadOnlyList<SensorAttachmentMetadata>> ListAsync(
            Guid sensorProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<SensorAttachmentMetadata> result =
                _attachments
                    .Where(
                        attachment =>
                            attachment.SensorProfileId ==
                            sensorProfileId)
                    .ToArray();

            return Task.FromResult(
                result);
        }

        public Task<SensorAttachmentMetadata?> GetMetadataAsync(
            Guid sensorProfileId,
            Guid attachmentId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SensorAttachmentMetadata? result =
                _attachments.FirstOrDefault(
                    attachment =>
                        attachment.SensorProfileId ==
                        sensorProfileId &&
                        attachment.Id ==
                        attachmentId);

            return Task.FromResult(
                result);
        }

        public Task WriteContentAsync(
            Guid sensorProfileId,
            Guid attachmentId,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        }
    }
}