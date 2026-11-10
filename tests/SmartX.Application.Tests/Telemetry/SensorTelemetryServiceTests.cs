using SmartX.Application.Dashboard;
using SmartX.Application.Realtime;
using SmartX.Application.Sensors;
using SmartX.Application.Telemetry;
using SmartX.Application.Tests.Realtime;
using SmartX.Contracts.Telemetry;
using SmartX.Domain.Sensors;
using SmartX.Domain.Telemetry;

namespace SmartX.Application.Tests.Telemetry;

/// <summary>
/// Verifies strongly typed telemetry orchestration independently from HTTP
/// and concrete Infrastructure persistence.
/// </summary>
public sealed class SensorTelemetryServiceTests
{
    private static readonly DateTimeOffset BaseTime =
        new(
            2026,
            9,
            11,
            9,
            0,
            0,
            TimeSpan.Zero);

    [Fact]
    public async Task IngestAsync_ForFloatSensor_StoresDoublePacket()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:41");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        var response =
            await service.IngestAsync(
                sensor.Id,
                new IngestTelemetryRequest(
                    BaseTime,
                    24.75,
                    null,
                    null));

        Assert.Equal(
            typeof(double),
            telemetryRepository.LastPayloadType);

        Assert.IsType<
            NumericTelemetryPacket<double>>(
            telemetryRepository.LastPacket);

        Assert.Equal(
            24.75,
            response.FloatValue);

        Assert.Equal(
            "Float",
            response.TelemetryDataType);
    }

    [Fact]
    public async Task IngestAsync_ForIntegerSensor_StoresIntegerPacket()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Integer,
                "AA:BB:CC:DD:EE:42");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        var response =
            await service.IngestAsync(
                sensor.Id,
                new IngestTelemetryRequest(
                    BaseTime,
                    null,
                    4200,
                    null));

        Assert.Equal(
            typeof(int),
            telemetryRepository.LastPayloadType);

        Assert.IsType<
            NumericTelemetryPacket<int>>(
            telemetryRepository.LastPacket);

        Assert.Equal(
            4200,
            response.IntegerValue);

        Assert.Equal(
            "Integer",
            response.TelemetryDataType);
    }

    [Fact]
    public async Task IngestAsync_ForBooleanSensor_StoresBooleanPacket()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Boolean,
                "AA:BB:CC:DD:EE:43");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        var response =
            await service.IngestAsync(
                sensor.Id,
                new IngestTelemetryRequest(
                    BaseTime,
                    null,
                    null,
                    false));

        Assert.Equal(
            typeof(bool),
            telemetryRepository.LastPayloadType);

        Assert.IsType<
            TelemetryPacket<bool>>(
            telemetryRepository.LastPacket);

        Assert.False(
            response.BooleanValue);
    }

    [Fact]
    public async Task IngestAsync_WhenSensorMissing_ThrowsSensorNotFoundException()
    {
        var service =
            CreateService(null, new RecordingTelemetryRepository());

        var missingId =
            Guid.NewGuid();

        var exception =
            await Assert.ThrowsAsync<
                SensorNotFoundException>(
                () => service.IngestAsync(
                    missingId,
                    new IngestTelemetryRequest(
                        BaseTime,
                        20.0,
                        null,
                        null)));

        Assert.Equal(
            missingId,
            exception.SensorProfileId);
    }

    [Fact]
    public async Task IngestAsync_WhenPayloadTypeDoesNotMatchSensor_RejectsRequest()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:44");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        var exception =
            await Assert.ThrowsAsync<
                InvalidTelemetryRequestException>(
                () => service.IngestAsync(
                    sensor.Id,
                    new IngestTelemetryRequest(
                        BaseTime,
                        null,
                        25,
                        null)));

        Assert.Contains(
            "FloatValue",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            0,
            telemetryRepository.AddCallCount);
    }

    [Fact]
    public async Task IngestAsync_WhenMultiplePayloadsProvided_RejectsRequest()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Integer,
                "AA:BB:CC:DD:EE:45");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        await Assert.ThrowsAsync<
            InvalidTelemetryRequestException>(
            () => service.IngestAsync(
                sensor.Id,
                new IngestTelemetryRequest(
                    BaseTime,
                    25.5,
                    25,
                    null)));

        Assert.Equal(
            0,
            telemetryRepository.AddCallCount);
    }

    [Fact]
    public async Task IngestAsync_WhenTimestampMissing_RejectsBeforePersistence()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Boolean,
                "AA:BB:CC:DD:EE:46");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        var exception =
            await Assert.ThrowsAsync<
                InvalidTelemetryRequestException>(
                () => service.IngestAsync(
                    sensor.Id,
                    new IngestTelemetryRequest(
                        null,
                        null,
                        null,
                        true)));

        Assert.Contains(
            "timestamp",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            0,
            telemetryRepository.AddCallCount);
    }

    [Fact]
    public async Task IngestAsync_WhenFloatNonFinite_UsesDomainValidation()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:47");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        await Assert.ThrowsAsync<
            SmartX.Domain.Exceptions.InvalidTelemetryPacketException>(
            () => service.IngestAsync(
                sensor.Id,
                new IngestTelemetryRequest(
                    BaseTime,
                    double.NaN,
                    null,
                    null)));

        Assert.Equal(
            0,
            telemetryRepository.AddCallCount);
    }

    [Fact]
    public async Task IngestBatchAsync_WhenUnevenFloatBatches_PreservesOrder()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:48");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        IngestTelemetryRequest?[]?[] batches =
        [
            [
                FloatReading(10.0, 0),
                FloatReading(11.0, 1)
            ],
            [
                FloatReading(20.0, 2)
            ],
            [
                FloatReading(30.0, 3),
                FloatReading(31.0, 4),
                FloatReading(32.0, 5)
            ]
        ];

        var response =
            await service.IngestBatchAsync(
                sensor.Id,
                new IngestTelemetryBatchRequest(
                    batches));

        Assert.Equal(
            3,
            response.BatchCount);

        Assert.Equal(
            6,
            response.AcceptedPacketCount);

        var stored =
            telemetryRepository
                .GetStored<double>(
                    sensor.Id);

        Assert.Equal(
            [10.0, 11.0, 20.0, 30.0, 31.0, 32.0],
            stored
                .Select(packet => packet.Payload)
                .ToArray());

        Assert.Equal(
            3,
            telemetryRepository.LastBatchCount);
    }

    [Fact]
    public async Task IngestBatchAsync_WithEmptyBatch_PreservesBatchBoundary()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Boolean,
                "AA:BB:CC:DD:EE:49");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        IngestTelemetryRequest?[]?[] batches =
        [
            [
                BooleanReading(false, 0)
            ],
            [],
            [
                BooleanReading(true, 1)
            ]
        ];

        var response =
            await service.IngestBatchAsync(
                sensor.Id,
                new IngestTelemetryBatchRequest(
                    batches));

        Assert.Equal(
            3,
            response.BatchCount);

        Assert.Equal(
            2,
            response.AcceptedPacketCount);

        Assert.Equal(
            3,
            telemetryRepository.LastBatchCount);
    }

    [Fact]
    public async Task IngestBatchAsync_WhenInnerBatchNull_RejectsRequest()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Integer,
                "AA:BB:CC:DD:EE:4A");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        IngestTelemetryRequest?[]?[] batches =
        [
            [
                IntegerReading(10, 0)
            ],
            null
        ];

        var exception =
            await Assert.ThrowsAsync<
                InvalidTelemetryRequestException>(
                () => service.IngestBatchAsync(
                    sensor.Id,
                    new IngestTelemetryBatchRequest(
                        batches)));

        Assert.Contains(
            "batch 1",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            0,
            telemetryRepository.BatchAddCallCount);
    }

    [Fact]
    public async Task IngestBatchAsync_WhenBatchesNull_RejectsRequest()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:4B");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        await Assert.ThrowsAsync<
            InvalidTelemetryRequestException>(
            () => service.IngestBatchAsync(
                sensor.Id,
                new IngestTelemetryBatchRequest(
                    null)));

        Assert.Equal(
            0,
            telemetryRepository.BatchAddCallCount);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsOnlyRequestedRecentPackets()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Integer,
                "AA:BB:CC:DD:EE:4C");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        await telemetryRepository.AddAsync(
            sensor.Id,
            new NumericTelemetryPacket<int>(
                sensor.DeviceIdentifier,
                10,
                BaseTime));

        await telemetryRepository.AddAsync(
            sensor.Id,
            new NumericTelemetryPacket<int>(
                sensor.DeviceIdentifier,
                20,
                BaseTime.AddSeconds(1)));

        await telemetryRepository.AddAsync(
            sensor.Id,
            new NumericTelemetryPacket<int>(
                sensor.DeviceIdentifier,
                30,
                BaseTime.AddSeconds(2)));

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        var result =
            await service.GetRecentAsync(
                sensor.Id,
                limit: 2);

        Assert.Equal(
            2,
            result.Count);

        Assert.Equal(
            20,
            result[0].IntegerValue);

        Assert.Equal(
            30,
            result[1].IntegerValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task GetRecentAsync_WhenLimitInvalid_RejectsRequest(
        int limit)
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:4D");

        var service =
            CreateService(
                sensor,
                new RecordingTelemetryRepository());

        await Assert.ThrowsAsync<
            InvalidTelemetryRequestException>(
            () => service.GetRecentAsync(
                sensor.Id,
                limit));
    }

    [Fact]
    public async Task IngestAsync_WhenCancelled_DoesNotPersistTelemetry()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Float,
                "AA:BB:CC:DD:EE:4E");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        using var cancellationSource =
            new CancellationTokenSource();

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(
            () => service.IngestAsync(
                sensor.Id,
                FloatReading(
                    22.5,
                    0),
                cancellationSource.Token));

        Assert.Equal(
            0,
            telemetryRepository.AddCallCount);
    }

    [Fact]
    public async Task IngestAsync_PropagatesCancellationTokenToRepository()
    {
        var sensor =
            CreateSensor(
                TelemetryDataType.Boolean,
                "AA:BB:CC:DD:EE:4F");

        var telemetryRepository =
            new RecordingTelemetryRepository();

        var service =
            CreateService(
                sensor,
                telemetryRepository);

        using var cancellationSource =
            new CancellationTokenSource();

        await service.IngestAsync(
            sensor.Id,
            BooleanReading(
                true,
                0),
            cancellationSource.Token);

        Assert.Equal(
            cancellationSource.Token,
            telemetryRepository.LastCancellationToken);
    }

    private static SensorTelemetryService CreateService(
        SensorProfile? sensor,
        RecordingTelemetryRepository telemetryRepository)
    {
        return new SensorTelemetryService(
            new StubSensorRepository(sensor),
            telemetryRepository,
            new TelemetryAcceptanceService(
                telemetryRepository,
                new RecordingTelemetryWorkBuffer(),
                new TelemetryIngestionGate(),
                new TelemetryAnomalyClassifier(new AdaptiveAnomalyPolicy()),
                new RecordingTelemetryAcceptanceObserver(),
                TimeProvider.System));
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
                "Facility A",
                "Zone 1",
                "Node 1"),
            SensorCategory.Environmental,
            dataType,
            BaseTime.AddHours(-1));
    }

    private static IngestTelemetryRequest FloatReading(
        double value,
        int secondsAfterBase)
    {
        return new IngestTelemetryRequest(
            BaseTime.AddSeconds(
                secondsAfterBase),
            value,
            null,
            null);
    }

    private static IngestTelemetryRequest IntegerReading(
        int value,
        int secondsAfterBase)
    {
        return new IngestTelemetryRequest(
            BaseTime.AddSeconds(
                secondsAfterBase),
            null,
            value,
            null);
    }

    private static IngestTelemetryRequest BooleanReading(
        bool value,
        int secondsAfterBase)
    {
        return new IngestTelemetryRequest(
            BaseTime.AddSeconds(
                secondsAfterBase),
            null,
            null,
            value);
    }

    /// <summary>
    /// Minimal sensor repository used to isolate SensorTelemetryService.
    /// </summary>
    private sealed class StubSensorRepository
        : ISensorRepository
    {
        private readonly SensorProfile? _sensor;

        public StubSensorRepository(
            SensorProfile? sensor)
        {
            _sensor = sensor;
        }

        public Task<bool> TryAddAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(false);
        }

        public Task<IReadOnlyList<SensorProfile>> GetAllAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<SensorProfile> result =
                _sensor is null
                    ? Array.Empty<SensorProfile>()
                    : [_sensor];

            return Task.FromResult(result);
        }

        public Task<SensorProfile?> GetByIdAsync(
            Guid sensorProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SensorProfile? result =
                _sensor is not null &&
                _sensor.Id == sensorProfileId
                    ? _sensor
                    : null;

            return Task.FromResult(result);
        }

        public Task<SensorProfile?> GetByDeviceIdentifierAsync(
            DeviceIdentifier deviceIdentifier,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(deviceIdentifier);

            cancellationToken.ThrowIfCancellationRequested();

            SensorProfile? sensor =
                _sensor is not null &&
                _sensor.DeviceIdentifier == deviceIdentifier
                    ? _sensor
                    : null;

            return Task.FromResult(sensor);
        }

        public Task<bool> UpdateAsync(
            SensorProfile sensor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Strongly typed in-memory test double for ITelemetryRepository.
    ///
    /// The fake retains each generic store separately so unit tests can
    /// verify the actual closed generic type selected by the service.
    /// </summary>
    private sealed class RecordingTelemetryRepository
        : ITelemetryRepository
    {
        private readonly Dictionary<
            (Guid SensorId, Type PayloadType),
            object> _stores = [];

        public int AddCallCount
        {
            get;
            private set;
        }

        public int BatchAddCallCount
        {
            get;
            private set;
        }

        public int? LastBatchCount
        {
            get;
            private set;
        }

        public Type? LastPayloadType
        {
            get;
            private set;
        }

        public object? LastPacket
        {
            get;
            private set;
        }

        public CancellationToken LastCancellationToken
        {
            get;
            private set;
        }

        public Task AddAsync<T>(
            Guid sensorProfileId,
            TelemetryPacket<T> packet,
            CancellationToken cancellationToken = default)
            where T : struct
        {
            ArgumentNullException.ThrowIfNull(packet);

            cancellationToken.ThrowIfCancellationRequested();

            AddCallCount++;

            LastPayloadType =
                typeof(T);

            LastPacket =
                packet;

            LastCancellationToken =
                cancellationToken;

            GetOrCreateStore<T>(
                    sensorProfileId)
                .Add(packet);

            return Task.CompletedTask;
        }

        public Task AddBatchAsync<T>(
            Guid sensorProfileId,
            HistoricalTelemetryBuffer<T> history,
            CancellationToken cancellationToken = default)
            where T : struct
        {
            ArgumentNullException.ThrowIfNull(history);

            cancellationToken.ThrowIfCancellationRequested();

            BatchAddCallCount++;

            LastPayloadType =
                typeof(T);

            LastBatchCount =
                history.BatchCount;

            LastCancellationToken =
                cancellationToken;

            GetOrCreateStore<T>(
                    sensorProfileId)
                .AddRange(history);

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TelemetryPacket<T>>> GetRecentAsync<T>(
            Guid sensorProfileId,
            int limit,
            CancellationToken cancellationToken = default)
            where T : struct
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastPayloadType =
                typeof(T);

            LastCancellationToken =
                cancellationToken;

            var store =
                GetOrCreateStore<T>(
                    sensorProfileId);

            var skip =
                Math.Max(
                    0,
                    store.Count - limit);

            IReadOnlyList<TelemetryPacket<T>> result =
                store
                    .Skip(skip)
                    .ToArray();

            return Task.FromResult(result);
        }

        public IReadOnlyList<TelemetryPacket<T>> GetStored<T>(
            Guid sensorProfileId)
            where T : struct
        {
            return GetOrCreateStore<T>(
                    sensorProfileId)
                .ToArray();
        }

        /// <summary>
        /// Test-double implementation of the range contract. These unit
        /// tests verify service orchestration, not AVL complexity; the
        /// production InMemoryTelemetryRepository uses a real AVL index.
        /// </summary>
        public Task<IReadOnlyList<TelemetryPacket<T>>> GetRangeAsync<T>(
            Guid sensorProfileId,
            DateTimeOffset fromInclusiveUtc,
            DateTimeOffset toInclusiveUtc,
            int limit,
            CancellationToken cancellationToken = default)
            where T : struct
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (limit < 1 || limit > AvlTelemetryIndex<T>.MaxRangeLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }

            if (fromInclusiveUtc > toInclusiveUtc)
            {
                throw new ArgumentException(
                    "Range start must not exceed range end.",
                    nameof(fromInclusiveUtc));
            }

            // LINQ is appropriate in this small, deterministic test double;
            // equal timestamps retain their initial ingestion order.
            IReadOnlyList<TelemetryPacket<T>> results =
                GetOrCreateStore<T>(sensorProfileId)
                    .Where(packet => packet.CapturedAtUtc >= fromInclusiveUtc &&
                                     packet.CapturedAtUtc <= toInclusiveUtc)
                    .OrderBy(packet => packet.CapturedAtUtc)
                    .Take(limit)
                    .ToArray();

            return Task.FromResult(results);
        }

        /// <summary>
        /// Implements the new continuation contract for the test double.
        /// Production queries are served by the repository's persistent AVL.
        /// </summary>
        public Task<AvlRangePage<T>> GetRangePageAsync<T>(
            Guid sensorProfileId,
            DateTimeOffset fromInclusiveUtc,
            DateTimeOffset toInclusiveUtc,
            int limit,
            DateTimeOffset? afterTimestampUtc = null,
            int? afterTimestampSequence = null,
            CancellationToken cancellationToken = default)
            where T : struct
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = new AvlTelemetryIndex<T>();
            foreach (TelemetryPacket<T> packet in GetOrCreateStore<T>(sensorProfileId))
            {
                index.Insert(packet);
            }

            return Task.FromResult(index.QueryRangePage(
                fromInclusiveUtc, toInclusiveUtc, limit,
                afterTimestampUtc, afterTimestampSequence));
        }

        private List<TelemetryPacket<T>> GetOrCreateStore<T>(
            Guid sensorProfileId)
            where T : struct
        {
            var key =
                (
                    SensorId: sensorProfileId,
                    PayloadType: typeof(T)
                );

            if (_stores.TryGetValue(
                    key,
                    out var existing))
            {
                return (List<TelemetryPacket<T>>)existing;
            }

            var created =
                new List<TelemetryPacket<T>>();

            _stores.Add(
                key,
                created);

            return created;
        }
    }
}