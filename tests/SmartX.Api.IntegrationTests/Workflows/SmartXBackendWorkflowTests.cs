using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Attachments;
using SmartX.Contracts.Sensors;
using SmartX.Contracts.Telemetry;

namespace SmartX.Api.IntegrationTests.Workflows;

/// <summary>
/// Exercises the completed Smart-X Part 1 backend as one integrated system.
///
/// These tests deliberately cross controller and feature boundaries. They
/// verify that sensor registration, sensor updates, strongly typed telemetry,
/// encrypted attachments and ProblemDetails responses operate correctly when
/// used together through the public HTTP API.
/// </summary>
public sealed class SmartXBackendWorkflowTests
    : IAsyncLifetime
{
    private static readonly string TestEncryptionKey =
        Convert.ToBase64String(
            Enumerable
                .Range(1, 32)
                .Select(value => (byte)value)
                .ToArray());

    private static readonly DateTimeOffset BaseTime =
        new(
            2026,
            9,
            11,
            12,
            0,
            0,
            TimeSpan.Zero);

    private readonly string _storagePath =
        Path.Combine(
            Path.GetTempPath(),
            "smartx-workflow-tests",
            Guid.NewGuid().ToString("N"));

    private BackendApiFactory _factory =
        null!;

    private HttpClient _client =
        null!;

    /// <summary>
    /// Creates a completely isolated Smart-X API instance for each workflow
    /// test, including a dedicated encrypted attachment storage directory.
    /// </summary>
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(
            _storagePath);

        _factory =
            new BackendApiFactory(
                _storagePath,
                TestEncryptionKey);

        _client =
            _factory.CreateClient(
                new WebApplicationFactoryClientOptions
                {
                    BaseAddress =
                        new Uri("https://localhost")
                });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes all test HTTP and filesystem resources.
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
    public async Task CompleteBackendWorkflow_WhenValid_PreservesResourcesAcrossFeatures()
    {
        // -----------------------------------------------------------------
        // 1. Register a real sensor.
        // -----------------------------------------------------------------

        var registered =
            await RegisterSensorAsync(
                "Greenhouse Sensor",
                "AA:BB:CC:DD:EE:C0",
                "Environmental",
                "Float");

        Assert.NotEqual(
            Guid.Empty,
            registered.Id);

        Assert.Equal(
            "AA:BB:CC:DD:EE:C0",
            registered.DeviceIdentifier);

        // -----------------------------------------------------------------
        // 2. Ingest strongly typed Float telemetry.
        // -----------------------------------------------------------------

        var firstTelemetry =
            await IngestFloatTelemetryAsync(
                registered.Id,
                21.50,
                BaseTime);

        var secondTelemetry =
            await IngestFloatTelemetryAsync(
                registered.Id,
                22.25,
                BaseTime.AddMinutes(1));

        Assert.Equal(
            "Float",
            firstTelemetry.TelemetryDataType);

        Assert.Equal(
            21.50,
            firstTelemetry.FloatValue);

        Assert.Equal(
            22.25,
            secondTelemetry.FloatValue);

        // -----------------------------------------------------------------
        // 3. Attach a hardware log.
        // -----------------------------------------------------------------

        var logContent =
            Encoding.UTF8.GetBytes(
                "ESP32 boot complete. Wi-Fi connected.");

        var attachment =
            await UploadHardwareLogAsync(
                registered.Id,
                logContent,
                "gateway.log");

        Assert.Equal(
            registered.Id,
            attachment.SensorProfileId);

        Assert.Equal(
            "HardwareLog",
            attachment.Category);

        // -----------------------------------------------------------------
        // 4. Update mutable sensor metadata.
        //
        // The telemetry data type intentionally remains Float so existing
        // typed telemetry continues to represent the current sensor stream.
        // -----------------------------------------------------------------

        var updateRequest =
            new UpdateSensorRequest(
                "Updated Greenhouse Sensor",
                new SensorDeploymentLocationRequest(
                    "Cape Town Facility",
                    "Greenhouse B",
                    "Node-07"),
                "Environmental",
                "Float");

        var updateResponse =
            await _client.PutAsJsonAsync(
                $"/api/sensors/{registered.Id}",
                updateRequest);

        Assert.Equal(
            HttpStatusCode.OK,
            updateResponse.StatusCode);

        var updated =
            await updateResponse.Content
                .ReadFromJsonAsync<SensorResponse>();

        Assert.NotNull(
            updated);

        Assert.Equal(
            registered.Id,
            updated.Id);

        // Physical device identity must survive profile updates unchanged.
        Assert.Equal(
            registered.DeviceIdentifier,
            updated.DeviceIdentifier);

        Assert.Equal(
            "Updated Greenhouse Sensor",
            updated.Name);

        Assert.Equal(
            "Cape Town Facility",
            updated.DeploymentLocation.Facility);

        Assert.Equal(
            "Greenhouse B",
            updated.DeploymentLocation.Zone);

        Assert.Equal(
            "Node-07",
            updated.DeploymentLocation.NodeId);

        // -----------------------------------------------------------------
        // 5. Confirm telemetry survived the profile update.
        // -----------------------------------------------------------------

        var telemetryResponse =
            await _client.GetAsync(
                $"/api/sensors/{registered.Id}/telemetry?limit=10");

        Assert.Equal(
            HttpStatusCode.OK,
            telemetryResponse.StatusCode);

        var telemetry =
            await telemetryResponse.Content
                .ReadFromJsonAsync<TelemetryReadingResponse[]>();

        Assert.NotNull(
            telemetry);

        Assert.Equal(
            2,
            telemetry.Length);

        Assert.Equal(
            21.50,
            telemetry[0].FloatValue);

        Assert.Equal(
            22.25,
            telemetry[1].FloatValue);

        // -----------------------------------------------------------------
        // 6. Confirm attachment metadata survived the profile update.
        // -----------------------------------------------------------------

        var attachments =
            await _client.GetFromJsonAsync<
                SensorAttachmentResponse[]>(
                    $"/api/sensors/{registered.Id}/attachments");

        Assert.NotNull(
            attachments);

        var storedAttachment =
            Assert.Single(
                attachments);

        Assert.Equal(
            attachment.Id,
            storedAttachment.Id);

        // -----------------------------------------------------------------
        // 7. Download through the real AES-GCM decryption pipeline.
        // -----------------------------------------------------------------

        var downloadResponse =
            await _client.GetAsync(
                $"/api/sensors/{registered.Id}/attachments/" +
                $"{attachment.Id}");

        Assert.Equal(
            HttpStatusCode.OK,
            downloadResponse.StatusCode);

        var downloadedContent =
            await downloadResponse.Content
                .ReadAsByteArrayAsync();

        Assert.Equal(
            logContent,
            downloadedContent);

        // -----------------------------------------------------------------
        // 8. Verify encryption at rest and generated physical storage names.
        // -----------------------------------------------------------------

        var payloadFiles =
            Directory.GetFiles(
                _storagePath,
                "*.payload",
                SearchOption.AllDirectories);

        var payloadPath =
            Assert.Single(
                payloadFiles);

        var encryptedContent =
            await File.ReadAllBytesAsync(
                payloadPath);

        Assert.False(
            logContent.SequenceEqual(
                encryptedContent));

        var allPhysicalFiles =
            Directory.GetFiles(
                _storagePath,
                "*",
                SearchOption.AllDirectories);

        // The client-supplied filename is metadata only. It must never become
        // the physical persistent filename.
        Assert.DoesNotContain(
    allPhysicalFiles,
    path =>
        string.Equals(
            Path.GetFileName(path),
            "gateway.log",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MultipleSensors_WhenUsedAcrossFeatures_KeepResourcesIsolated()
    {
        var floatSensor =
            await RegisterSensorAsync(
                "Temperature Sensor",
                "AA:BB:CC:DD:EE:C1",
                "Environmental",
                "Float");

        var integerSensor =
            await RegisterSensorAsync(
                "Power Sensor",
                "AA:BB:CC:DD:EE:C2",
                "PowerConsumption",
                "Integer");

        // Store different strongly typed telemetry streams.
        await IngestFloatTelemetryAsync(
            floatSensor.Id,
            19.75,
            BaseTime);

        await IngestIntegerTelemetryAsync(
            integerSensor.Id,
            4200,
            BaseTime);

        // Store an independent attachment for each sensor.
        var firstAttachment =
            await UploadHardwareLogAsync(
                floatSensor.Id,
                "temperature gateway log"u8.ToArray(),
                "temperature.log");

        var secondAttachment =
            await UploadHardwareLogAsync(
                integerSensor.Id,
                "power gateway log"u8.ToArray(),
                "power.log");

        // -----------------------------------------------------------------
        // Telemetry must remain isolated by sensor.
        // -----------------------------------------------------------------

        var floatTelemetry =
            await _client.GetFromJsonAsync<
                TelemetryReadingResponse[]>(
                    $"/api/sensors/{floatSensor.Id}/telemetry?limit=10");

        var integerTelemetry =
            await _client.GetFromJsonAsync<
                TelemetryReadingResponse[]>(
                    $"/api/sensors/{integerSensor.Id}/telemetry?limit=10");

        Assert.NotNull(
            floatTelemetry);

        Assert.NotNull(
            integerTelemetry);

        var floatReading =
            Assert.Single(
                floatTelemetry);

        var integerReading =
            Assert.Single(
                integerTelemetry);

        Assert.Equal(
            "Float",
            floatReading.TelemetryDataType);

        Assert.Equal(
            19.75,
            floatReading.FloatValue);

        Assert.Null(
            floatReading.IntegerValue);

        Assert.Equal(
            "Integer",
            integerReading.TelemetryDataType);

        Assert.Equal(
            4200,
            integerReading.IntegerValue);

        Assert.Null(
            integerReading.FloatValue);

        // -----------------------------------------------------------------
        // Attachment metadata must also remain isolated by sensor.
        // -----------------------------------------------------------------

        var firstAttachments =
            await _client.GetFromJsonAsync<
                SensorAttachmentResponse[]>(
                    $"/api/sensors/{floatSensor.Id}/attachments");

        var secondAttachments =
            await _client.GetFromJsonAsync<
                SensorAttachmentResponse[]>(
                    $"/api/sensors/{integerSensor.Id}/attachments");

        Assert.NotNull(
            firstAttachments);

        Assert.NotNull(
            secondAttachments);

        var firstStoredAttachment =
            Assert.Single(
                firstAttachments);

        var secondStoredAttachment =
            Assert.Single(
                secondAttachments);

        Assert.Equal(
            firstAttachment.Id,
            firstStoredAttachment.Id);

        Assert.Equal(
            floatSensor.Id,
            firstStoredAttachment.SensorProfileId);

        Assert.Equal(
            secondAttachment.Id,
            secondStoredAttachment.Id);

        Assert.Equal(
            integerSensor.Id,
            secondStoredAttachment.SensorProfileId);

        Assert.NotEqual(
            firstStoredAttachment.Id,
            secondStoredAttachment.Id);
    }

    [Fact]
    public async Task UnknownSensor_WhenUsedAcrossFeatures_ReturnsConsistentNotFoundProblems()
    {
        var missingSensorId =
            Guid.NewGuid();

        // -----------------------------------------------------------------
        // Telemetry ingestion must reject unknown sensor references.
        // -----------------------------------------------------------------

        var telemetryResponse =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{missingSensorId}/telemetry",
                new IngestTelemetryRequest(
                    BaseTime,
                    20.5,
                    null,
                    null));

        await AssertSensorNotFoundAsync(
            telemetryResponse,
            missingSensorId);

        // -----------------------------------------------------------------
        // Telemetry retrieval must follow the same 404 contract.
        // -----------------------------------------------------------------

        var telemetryReadResponse =
            await _client.GetAsync(
                $"/api/sensors/{missingSensorId}/telemetry");

        await AssertSensorNotFoundAsync(
            telemetryReadResponse,
            missingSensorId);

        // -----------------------------------------------------------------
        // Attachment upload must reject the same unknown sensor before
        // anything is written to encrypted storage.
        // -----------------------------------------------------------------

        using var form =
            CreateFileUpload(
                "orphan attachment"u8.ToArray(),
                "orphan.log",
                "text/plain");

        var attachmentResponse =
            await _client.PostAsync(
                $"/api/sensors/{missingSensorId}/attachments" +
                "?category=HardwareLog",
                form);

        await AssertSensorNotFoundAsync(
            attachmentResponse,
            missingSensorId);

        // -----------------------------------------------------------------
        // Attachment listing also requires a real parent sensor.
        // -----------------------------------------------------------------

        var attachmentListResponse =
            await _client.GetAsync(
                $"/api/sensors/{missingSensorId}/attachments");

        await AssertSensorNotFoundAsync(
            attachmentListResponse,
            missingSensorId);

        Assert.Empty(
            Directory.GetFiles(
                _storagePath,
                "*",
                SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DuplicateRegistration_WhenRejected_DoesNotDamageExistingSensorResources()
    {
        var sensor =
            await RegisterSensorAsync(
                "Original Sensor",
                "AA:BB:CC:DD:EE:C3",
                "Environmental",
                "Float");

        await IngestFloatTelemetryAsync(
            sensor.Id,
            25.25,
            BaseTime);

        var attachment =
            await UploadHardwareLogAsync(
                sensor.Id,
                "original sensor log"u8.ToArray(),
                "original.log");

        // Equivalent textual representation of the same MAC address.
        var duplicateRequest =
            new RegisterSensorRequest(
                "Duplicate Sensor",
                "aa-bb-cc-dd-ee-c3",
                "MacAddress",
                new SensorDeploymentLocationRequest(
                    "Facility B",
                    "Zone 2",
                    "Node 2"),
                "Environmental",
                "Float");

        var duplicateResponse =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                duplicateRequest);

        Assert.Equal(
            HttpStatusCode.Conflict,
            duplicateResponse.StatusCode);

        var duplicateProblem =
            await duplicateResponse.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(
            duplicateProblem);

        Assert.Equal(
            "Duplicate sensor identifier",
            duplicateProblem.Title);

        // The failed duplicate registration must not affect resources already
        // associated with the legitimate sensor.
        var telemetry =
            await _client.GetFromJsonAsync<
                TelemetryReadingResponse[]>(
                    $"/api/sensors/{sensor.Id}/telemetry?limit=10");

        Assert.NotNull(
            telemetry);

        Assert.Equal(
            25.25,
            Assert.Single(telemetry).FloatValue);

        var attachments =
            await _client.GetFromJsonAsync<
                SensorAttachmentResponse[]>(
                    $"/api/sensors/{sensor.Id}/attachments");

        Assert.NotNull(
            attachments);

        Assert.Equal(
            attachment.Id,
            Assert.Single(attachments).Id);

        var sensors =
            await _client.GetFromJsonAsync<
                SensorResponse[]>(
                    "/api/sensors");

        Assert.NotNull(
            sensors);

        // This test's isolated backend should still contain only the original
        // registration.
        Assert.Single(
            sensors);

        Assert.Equal(
            sensor.Id,
            sensors[0].Id);
    }

    private async Task<SensorResponse> RegisterSensorAsync(
        string name,
        string macAddress,
        string category,
        string telemetryDataType)
    {
        var request =
            new RegisterSensorRequest(
                name,
                macAddress,
                "MacAddress",
                new SensorDeploymentLocationRequest(
                    "Facility A",
                    "Zone 1",
                    "Node 1"),
                category,
                telemetryDataType);

        var response =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                request);

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        var sensor =
            await response.Content
                .ReadFromJsonAsync<SensorResponse>();

        return sensor ??
            throw new InvalidOperationException(
                "Sensor registration returned an empty response.");
    }

    private async Task<TelemetryReadingResponse>
        IngestFloatTelemetryAsync(
            Guid sensorProfileId,
            double value,
            DateTimeOffset capturedAtUtc)
    {
        var request =
            new IngestTelemetryRequest(
                capturedAtUtc,
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

        var telemetry =
            await response.Content
                .ReadFromJsonAsync<TelemetryReadingResponse>();

        return telemetry ??
            throw new InvalidOperationException(
                "Telemetry ingestion returned an empty response.");
    }

    private async Task<TelemetryReadingResponse>
        IngestIntegerTelemetryAsync(
            Guid sensorProfileId,
            int value,
            DateTimeOffset capturedAtUtc)
    {
        var request =
            new IngestTelemetryRequest(
                capturedAtUtc,
                null,
                value,
                null);

        var response =
            await _client.PostAsJsonAsync(
                $"/api/sensors/{sensorProfileId}/telemetry",
                request);

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        var telemetry =
            await response.Content
                .ReadFromJsonAsync<TelemetryReadingResponse>();

        return telemetry ??
            throw new InvalidOperationException(
                "Telemetry ingestion returned an empty response.");
    }

    private async Task<SensorAttachmentResponse>
        UploadHardwareLogAsync(
            Guid sensorProfileId,
            byte[] content,
            string fileName)
    {
        using var form =
            CreateFileUpload(
                content,
                fileName,
                "text/plain");

        var response =
            await _client.PostAsync(
                $"/api/sensors/{sensorProfileId}/attachments" +
                "?category=HardwareLog",
                form);

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        var attachment =
            await response.Content
                .ReadFromJsonAsync<SensorAttachmentResponse>();

        return attachment ??
            throw new InvalidOperationException(
                "Attachment upload returned an empty response.");
    }

    private static MultipartFormDataContent CreateFileUpload(
        byte[] content,
        string fileName,
        string contentType)
    {
        var form =
            new MultipartFormDataContent();

        var fileContent =
            new ByteArrayContent(
                content);

        fileContent.Headers.ContentType =
            new MediaTypeHeaderValue(
                contentType);

        form.Add(
            fileContent,
            "file",
            fileName);

        return form;
    }

    private static async Task AssertSensorNotFoundAsync(
        HttpResponseMessage response,
        Guid expectedSensorId)
    {
        Assert.Equal(
            HttpStatusCode.NotFound,
            response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(
            problem);

        Assert.Equal(
            StatusCodes.Status404NotFound,
            problem.Status);

        Assert.Equal(
            "Sensor not found",
            problem.Title);

        Assert.Contains(
            expectedSensorId.ToString(),
            problem.Detail,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Configures the real Smart-X application with an isolated encrypted
    /// attachment directory and deterministic test-only encryption key.
    ///
    /// Sensor and telemetry repositories use the normal production DI
    /// registrations from Program.cs.
    /// </summary>
    private sealed class BackendApiFactory
        : WebApplicationFactory<Program>
    {
        private readonly string _storagePath;
        private readonly string _encryptionKey;

        public BackendApiFactory(
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