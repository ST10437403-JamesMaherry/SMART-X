using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Attachments;
using SmartX.Contracts.Sensors;

namespace SmartX.Api.IntegrationTests.Attachments;

/// <summary>
/// Exercises the secure attachment pipeline through the real sensor API,
/// Application services, encrypted filesystem persistence and download path.
/// </summary>
public sealed class SensorAttachmentsEndpointTests
    : IAsyncLifetime
{
    private static readonly string TestEncryptionKey =
        Convert.ToBase64String(
            Enumerable
                .Range(1, 32)
                .Select(value => (byte)value)
                .ToArray());

    private readonly string _storagePath =
        Path.Combine(
            Path.GetTempPath(),
            "smartx-api-tests",
            Guid.NewGuid().ToString("N"));

    private AttachmentApiFactory _factory =
        null!;

    private HttpClient _client =
        null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(
            _storagePath);

        _factory =
            new AttachmentApiFactory(
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
    public async Task UploadAndDownload_WhenValid_RoundTripsEncryptedAttachment()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:B0");

        var plaintext =
            Encoding.UTF8.GetBytes(
                "ESP32 boot: all systems operational.");

        using var form =
            CreateFileUpload(
                plaintext,
                "gateway.log",
                "text/plain");

        var uploadResponse =
            await _client.PostAsync(
                $"/api/sensors/{sensor.Id}/attachments" +
                "?category=HardwareLog",
                form);

        Assert.Equal(
            HttpStatusCode.Created,
            uploadResponse.StatusCode);

        var metadata =
            await uploadResponse.Content
                .ReadFromJsonAsync<
                    SensorAttachmentResponse>();

        Assert.NotNull(
            metadata);

        Assert.Equal(
            sensor.Id,
            metadata.SensorProfileId);

        Assert.Equal(
            "gateway.log",
            metadata.FileName);

        // Persistent payload must contain encrypted data rather than the
        // original plaintext bytes.
        var storedPayload =
            Assert.Single(
                Directory.GetFiles(
                    _storagePath,
                    "*.payload",
                    SearchOption.AllDirectories));

        var encryptedBytes =
            await File.ReadAllBytesAsync(
                storedPayload);

        Assert.False(
            plaintext.SequenceEqual(
                encryptedBytes));

        var downloadResponse =
            await _client.GetAsync(
                $"/api/sensors/{sensor.Id}/attachments/" +
                $"{metadata.Id}");

        Assert.Equal(
            HttpStatusCode.OK,
            downloadResponse.StatusCode);

        Assert.Equal(
            "no-store",
            downloadResponse.Headers.CacheControl?.ToString());

        Assert.True(
            downloadResponse.Headers.Contains(
                "X-Content-Type-Options"));

        var downloaded =
            await downloadResponse.Content
                .ReadAsByteArrayAsync();

        Assert.Equal(
            plaintext,
            downloaded);
    }

    [Fact]
    public async Task List_WhenAttachmentExists_ReturnsSensorMetadata()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:B1");

        using var form =
            CreateFileUpload(
                "configuration=true"u8.ToArray(),
                "device.cfg",
                "application/octet-stream");

        var uploadResponse =
            await _client.PostAsync(
                $"/api/sensors/{sensor.Id}/attachments" +
                "?category=Configuration",
                form);

        uploadResponse
            .EnsureSuccessStatusCode();

        var list =
            await _client.GetFromJsonAsync<
                SensorAttachmentResponse[]>(
                    $"/api/sensors/{sensor.Id}/attachments");

        var attachment =
            Assert.Single(
                list!);

        Assert.Equal(
            sensor.Id,
            attachment.SensorProfileId);

        Assert.Equal(
            "Configuration",
            attachment.Category);
    }

    [Fact]
    public async Task Upload_WhenSensorDoesNotExist_ReturnsNotFoundProblemDetails()
    {
        var missingSensorId =
            Guid.NewGuid();

        using var form =
            CreateFileUpload(
                "gateway started"u8.ToArray(),
                "gateway.log",
                "text/plain");

        var response =
            await _client.PostAsync(
                $"/api/sensors/{missingSensorId}/attachments" +
                "?category=HardwareLog",
                form);

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
            "Sensor not found",
            problem.Title);

        Assert.Empty(
            Directory.GetFiles(
                _storagePath,
                "*",
                SearchOption.AllDirectories));
    }

    [Fact]
    public async Task List_WhenSensorDoesNotExist_ReturnsNotFound()
    {
        var response =
            await _client.GetAsync(
                $"/api/sensors/{Guid.NewGuid()}/attachments");

        Assert.Equal(
            HttpStatusCode.NotFound,
            response.StatusCode);
    }

    [Fact]
    public async Task Upload_WhenValidPngSignature_StoresDeploymentPhoto()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:B2");

        byte[] pngPayload =
        [
            0x89,
            0x50,
            0x4E,
            0x47,
            0x0D,
            0x0A,
            0x1A,
            0x0A,
            0x01,
            0x02,
            0x03,
            0x04
        ];

        using var form =
            CreateFileUpload(
                pngPayload,
                "deployment.png",
                "image/png");

        var response =
            await _client.PostAsync(
                $"/api/sensors/{sensor.Id}/attachments" +
                "?category=DeploymentPhoto",
                form);

        Assert.Equal(
            HttpStatusCode.Created,
            response.StatusCode);

        var metadata =
            await response.Content
                .ReadFromJsonAsync<
                    SensorAttachmentResponse>();

        Assert.NotNull(
            metadata);

        Assert.Equal(
            "DeploymentPhoto",
            metadata.Category);

        var download =
            await _client.GetByteArrayAsync(
                $"/api/sensors/{sensor.Id}/attachments/" +
                $"{metadata.Id}");

        Assert.Equal(
            pngPayload,
            download);
    }

    [Fact]
    public async Task Upload_WhenPngSignatureInvalid_ReturnsBadRequestAndStoresNothing()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:B3");

        using var form =
            CreateFileUpload(
                "Definitely not a PNG"u8.ToArray(),
                "fake.png",
                "image/png");

        var response =
            await _client.PostAsync(
                $"/api/sensors/{sensor.Id}/attachments" +
                "?category=DeploymentPhoto",
                form);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        var problem =
            await response.Content
                .ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(
            problem);

        Assert.Contains(
            "PNG",
            problem.Detail,
            StringComparison.OrdinalIgnoreCase);

        Assert.Empty(
            Directory.GetFiles(
                _storagePath,
                "*",
                SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Upload_WhenPhotoMimeDoesNotMatchExtension_ReturnsBadRequest()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:B4");

        byte[] jpegPayload =
        [
            0xFF,
            0xD8,
            0xFF,
            0xE0
        ];

        using var form =
            CreateFileUpload(
                jpegPayload,
                "fake.png",
                "image/jpeg");

        var response =
            await _client.PostAsync(
                $"/api/sensors/{sensor.Id}/attachments" +
                "?category=DeploymentPhoto",
                form);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        Assert.Empty(
            Directory.GetFiles(
                _storagePath,
                "*",
                SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Upload_WhenExtensionUnsupported_ReturnsBadRequest()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:B5");

        using var form =
            CreateFileUpload(
                [1, 2, 3, 4],
                "malware.exe",
                "application/octet-stream");

        var response =
            await _client.PostAsync(
                $"/api/sensors/{sensor.Id}/attachments" +
                "?category=HardwareLog",
                form);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);
    }

    [Fact]
    public async Task Upload_WhenMultipartContainsNoFile_ReturnsBadRequest()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:B6");

        using var form =
            new MultipartFormDataContent();

        form.Add(
            new StringContent(
                "not-a-file"),
            "description");

        var response =
            await _client.PostAsync(
                $"/api/sensors/{sensor.Id}/attachments" +
                "?category=HardwareLog",
                form);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);
    }

    [Fact]
    public async Task Upload_WhenFileEmpty_ReturnsBadRequestAndStoresNothing()
    {
        var sensor =
            await RegisterSensorAsync(
                "AA:BB:CC:DD:EE:B7");

        using var form =
            CreateFileUpload(
                [],
                "gateway.log",
                "text/plain");

        var response =
            await _client.PostAsync(
                $"/api/sensors/{sensor.Id}/attachments" +
                "?category=HardwareLog",
                form);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);

        Assert.Empty(
            Directory.GetFiles(
                _storagePath,
                "*",
                SearchOption.AllDirectories));
    }

    private async Task<SensorResponse> RegisterSensorAsync(
        string macAddress)
    {
        var request =
            new RegisterSensorRequest(
                "Attachment Test Sensor",
                macAddress,
                "MacAddress",
                new SensorDeploymentLocationRequest(
                    "Facility A",
                    "Zone 1",
                    "Node 1"),
                "Environmental",
                "Float");

        var response =
            await _client.PostAsJsonAsync(
                "/api/sensors",
                request);

        response
            .EnsureSuccessStatusCode();

        var sensor =
            await response.Content
                .ReadFromJsonAsync<
                    SensorResponse>();

        return sensor ??
            throw new InvalidOperationException(
                "Sensor registration returned an empty response.");
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

    private sealed class AttachmentApiFactory
        : WebApplicationFactory<Program>
    {
        private readonly string _storagePath;
        private readonly string _encryptionKey;

        public AttachmentApiFactory(
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