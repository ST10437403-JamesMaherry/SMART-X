using System.Text;

using SmartX.Application.Attachments;
using SmartX.Infrastructure.Attachments;

namespace SmartX.Infrastructure.Tests.Attachments;

/// <summary>
/// Verifies that cancelled attachment persistence cannot leave partial
/// encrypted payload or metadata files behind.
/// </summary>
public sealed class FileSystemAttachmentCleanupTests
{
    private static readonly string TestKeyBase64 =
        Convert.ToBase64String(
            Enumerable
                .Range(1, 32)
                .Select(value => (byte)value)
                .ToArray());

    [Fact]
    public async Task SaveAsync_WhenCancelled_LeavesNoPartialFiles()
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                "smartx-cleanup-tests",
                Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(
            root);

        try
        {
            using var cipher =
                new ChunkedAesGcmAttachmentCipher(
                    TestKeyBase64);

            var store =
                new FileSystemSensorAttachmentStore(
                    root,
                    cipher);

            var request =
                new SensorAttachmentWriteRequest(
                    Guid.NewGuid(),
                    "gateway.log",
                    "text/plain",
                    SensorAttachmentCategory.HardwareLog,
                    DateTimeOffset.UtcNow);

            using var cancellationSource =
                new CancellationTokenSource();

            cancellationSource.Cancel();

            await Assert.ThrowsAnyAsync<
                OperationCanceledException>(
                () => store.SaveAsync(
                    request,
                    new MemoryStream(
                        Encoding.UTF8.GetBytes(
                            "Smart-X hardware log")),
                    cancellationSource.Token));

            var remainingFiles =
                Directory.GetFiles(
                    root,
                    "*",
                    SearchOption.AllDirectories);

            Assert.Empty(
                remainingFiles);
        }
        finally
        {
            if (Directory.Exists(
                    root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }
}