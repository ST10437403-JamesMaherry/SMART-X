using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using SmartX.Contracts.Attachments;

namespace SmartX.Api.IntegrationTests.Attachments;

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

    private AttachmentApiFactory _factory = null!;

    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_storagePath);

        _factory =
            new AttachmentApiFactory(
                _storagePath,
                TestEncryptionKey);

        _client =
            _factory.CreateClient();

        return Task.CompletedTask;
    }

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
    public async Task UploadAndDownload_WhenValid_RoundTripsEncryptedAttachment()
    {
        var sensorId =
            Guid.NewGuid();

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
                $"/api/sensors/{sensorId}/attachments" +
                "?category=HardwareLog",
                form);

        Assert.Equal(
            HttpStatusCode.Created,
            uploadResponse.StatusCode);

        var metadata =
            await uploadResponse.Content
                .ReadFromJsonAsync<
                    SensorAttachmentResponse>();

        Assert.NotNull(metadata);

        Assert.Equal(
            sensorId,
            metadata.SensorProfileId);

        Assert.Equal(
            "gateway.log",
            metadata.FileName);

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
            plaintext.SequenceEqual(encryptedBytes));

        var downloadResponse =
            await _client.GetAsync(
                $"/api/sensors/{sensorId}/attachments/" +
                $"{metadata.Id}");

        Assert.Equal(
            HttpStatusCode.OK,
            downloadResponse.StatusCode);

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
        var sensorId =
            Guid.NewGuid();

        using var form =
            CreateFileUpload(
                "configuration=true"u8.ToArray(),
                "device.cfg",
                "application/octet-stream");

        var uploadResponse =
            await _client.PostAsync(
                $"/api/sensors/{sensorId}/attachments" +
                "?category=Configuration",
                form);

        uploadResponse.EnsureSuccessStatusCode();

        var list =
            await _client.GetFromJsonAsync<
                SensorAttachmentResponse[]>(
                    $"/api/sensors/{sensorId}/attachments");

        var attachment =
            Assert.Single(list!);

        Assert.Equal(
            sensorId,
            attachment.SensorProfileId);

        Assert.Equal(
            "Configuration",
            attachment.Category);
    }

    [Fact]
    public async Task Upload_WhenExtensionUnsupported_ReturnsBadRequest()
    {
        var sensorId =
            Guid.NewGuid();

        using var form =
            CreateFileUpload(
                [1, 2, 3, 4],
                "malware.exe",
                "application/octet-stream");

        var response =
            await _client.PostAsync(
                $"/api/sensors/{sensorId}/attachments" +
                "?category=HardwareLog",
                form);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);
    }

    [Fact]
    public async Task Upload_WhenMultipartContainsNoFile_ReturnsBadRequest()
    {
        var sensorId =
            Guid.NewGuid();

        using var form =
            new MultipartFormDataContent();

        form.Add(
            new StringContent("not-a-file"),
            "description");

        var response =
            await _client.PostAsync(
                $"/api/sensors/{sensorId}/attachments" +
                "?category=HardwareLog",
                form);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            response.StatusCode);
    }

    private static MultipartFormDataContent CreateFileUpload(
        byte[] content,
        string fileName,
        string contentType)
    {
        var form =
            new MultipartFormDataContent();

        var fileContent =
            new ByteArrayContent(content);

        fileContent.Headers.ContentType =
            new MediaTypeHeaderValue(contentType);

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
            _storagePath = storagePath;
            _encryptionKey = encryptionKey;
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