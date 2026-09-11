using System.Text;

using SmartX.Application.Attachments;
using SmartX.Infrastructure.Attachments;

namespace SmartX.Infrastructure.Tests.Attachments;

public sealed class FileSystemSensorAttachmentStoreTests
{
    private static readonly string TestKeyBase64 =
        Convert.ToBase64String(
            Enumerable
                .Range(1, 32)
                .Select(value => (byte)value)
                .ToArray());

    [Fact]
    public async Task SaveAndRead_WhenValid_RestoresOriginalContent()
    {
        var root =
            CreateTemporaryDirectory();

        try
        {
            using var cipher =
                new ChunkedAesGcmAttachmentCipher(
                    TestKeyBase64);

            var store =
                new FileSystemSensorAttachmentStore(
                    root,
                    cipher);

            var sensorId =
                Guid.NewGuid();

            var payload =
                Encoding.UTF8.GetBytes(
                    "ESP32 boot completed.");

            var request =
                CreateRequest(sensorId);

            var metadata =
                await store.SaveAsync(
                    request,
                    new MemoryStream(payload));

            using var restored =
                new MemoryStream();

            await store.WriteContentAsync(
                sensorId,
                metadata.Id,
                restored);

            Assert.Equal(
                payload,
                restored.ToArray());

            Assert.Equal(
                payload.LongLength,
                metadata.SizeBytes);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task SaveAsync_WhenValid_StoresEncryptedPayloadOnDisk()
    {
        var root =
            CreateTemporaryDirectory();

        try
        {
            using var cipher =
                new ChunkedAesGcmAttachmentCipher(
                    TestKeyBase64);

            var store =
                new FileSystemSensorAttachmentStore(
                    root,
                    cipher);

            var payload =
                Encoding.UTF8.GetBytes(
                    "plaintext must not be stored directly");

            await store.SaveAsync(
                CreateRequest(Guid.NewGuid()),
                new MemoryStream(payload));

            var payloadFile =
                Assert.Single(
                    Directory.GetFiles(
                        root,
                        "*.payload",
                        SearchOption.AllDirectories));

            var storedBytes =
                await File.ReadAllBytesAsync(
                    payloadFile);

            Assert.False(
                payload.SequenceEqual(storedBytes));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyAttachmentsForRequestedSensor()
    {
        var root =
            CreateTemporaryDirectory();

        try
        {
            using var cipher =
                new ChunkedAesGcmAttachmentCipher(
                    TestKeyBase64);

            var store =
                new FileSystemSensorAttachmentStore(
                    root,
                    cipher);

            var sensorA =
                Guid.NewGuid();

            var sensorB =
                Guid.NewGuid();

            await store.SaveAsync(
                CreateRequest(sensorA),
                new MemoryStream([1, 2, 3]));

            await store.SaveAsync(
                CreateRequest(sensorB),
                new MemoryStream([4, 5, 6]));

            var sensorAAttachments =
                await store.ListAsync(sensorA);

            var attachment =
                Assert.Single(sensorAAttachments);

            Assert.Equal(
                sensorA,
                attachment.SensorProfileId);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static SensorAttachmentWriteRequest CreateRequest(
        Guid sensorId)
    {
        return new SensorAttachmentWriteRequest(
            sensorId,
            "gateway.log",
            "text/plain",
            SensorAttachmentCategory.HardwareLog,
            new DateTimeOffset(
                2026,
                9,
                10,
                18,
                0,
                0,
                TimeSpan.Zero));
    }

    private static string CreateTemporaryDirectory()
    {
        var path =
            Path.Combine(
                Path.GetTempPath(),
                "smartx-tests",
                Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(path);

        return path;
    }

    private static void DeleteTemporaryDirectory(
        string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(
                path,
                recursive: true);
        }
    }
}