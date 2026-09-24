using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public sealed class LoraLibraryServiceTests {
    [Fact]
    public void FindLocalThumbnail_FindsMatchingPreviewImage() {
        string tempDir = Path.Combine(Path.GetTempPath(), $"thumb_test_{Guid.NewGuid():N}");
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

            SettingsService settings = new(null, Path.Combine(tempDir, "settings.json"));
            CivitaiService civitai = new(settings);
            SafeTensorsMetadataReader reader = new();
            LoraLibraryService service = new(reader, civitai, settings);

            service.FindLocalThumbnail(meta);

            Assert.NotNull(meta.ThumbnailPath);
            Assert.Equal(previewPath, meta.ThumbnailPath);
        } finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
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
}
