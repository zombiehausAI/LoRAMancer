using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public sealed class LoraLibraryServiceTests {
    [Fact]
    public void FindLocalThumbnail_FindsMatchingPreviewImage() {
        string tempDir = Path.Combine(Path.GetTempPath(), $"thumb_test_{Guid.NewGuid():N}");
        string tempSettingsPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(tempDir);

        try {
            string loraPath = Path.Combine(tempDir, "cyberpunk_style.safetensors");
            string previewPath = Path.Combine(tempDir, "cyberpunk_style.preview.png");

            File.WriteAllText(loraPath, "dummy");
            File.WriteAllText(previewPath, "image_data");

            LoraMetadata meta = new() {
                FileName = "cyberpunk_style.safetensors",
                FilePath = loraPath
            };

            SettingsService settings = new(null, tempSettingsPath);
            CivitaiService civitai = new(settings);
            SafeTensorsMetadataReader reader = new();
            LoraLibraryService service = new(reader, civitai, settings);

            service.FindLocalThumbnail(meta);

            Assert.NotNull(meta.ThumbnailPath);
            Assert.Equal(previewPath, meta.ThumbnailPath);
        } finally {
            if (Directory.Exists(tempDir)) {
                for (int attempt = 0; attempt < 5; attempt++) {
                    try {
                        Directory.Delete(tempDir, recursive: true);
                        break;
                    } catch (IOException) {
                        Thread.Sleep(50);
                    } catch (UnauthorizedAccessException) {
                        Thread.Sleep(50);
                    }
                }
            }
            if (File.Exists(tempSettingsPath)) {
                try {
                    File.Delete(tempSettingsPath);
                } catch {
                    // Suppress transient lock on temp settings file
                }
            }
        }
    }

    [Fact]
    public void LoraMetadata_FormattedSize_CalculatesAccurately() {
        LoraMetadata meta = new() {
            FileSizeBytes = 150 * 1024 * 1024 // 150 MB
        };

        Assert.Equal("150.0 MB", meta.FormattedSize);
    }

    [Fact]
    public async Task ScanDirectoryStreamAsync_EmptyDirectory_ReturnsZeroDiscovered() {
        string tempDir = Path.Combine(Path.GetTempPath(), $"stream_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try {
            SettingsService settings = new(null);
            CivitaiService civitai = new(settings);
            SafeTensorsMetadataReader reader = new();
            LoraLibraryService service = new(reader, civitai, settings);

            List<LoraMetadata> streamed = new();
            int count = await service.ScanDirectoryStreamAsync(tempDir, meta => {
                streamed.Add(meta);
                return Task.CompletedTask;
            });

            Assert.Equal(0, count);
            Assert.Empty(streamed);
        } finally {
            if (Directory.Exists(tempDir)) {
                for (int attempt = 0; attempt < 5; attempt++) {
                    try {
                        Directory.Delete(tempDir, recursive: true);
                        break;
                    } catch (IOException) {
                        Thread.Sleep(50);
                    } catch (UnauthorizedAccessException) {
                        Thread.Sleep(50);
                    }
                }
            }
        }
    }
}
