using System.Text;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public sealed class CivitaiServiceTests {
    [Fact]
    public async Task ComputeFileSha256Async_ComputesExpectedHash() {
        string tempFile = Path.Combine(Path.GetTempPath(), $"civitai_test_{Guid.NewGuid():N}.safetensors");
        byte[] content = Encoding.UTF8.GetBytes("test content for safetensors model hash calculation");

        try {
            await File.WriteAllBytesAsync(tempFile, content);

            SettingsService settingsService = new(null, Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json"));
            CivitaiService service = new(settingsService);

            string hash = await service.ComputeFileSha256Async(tempFile);

            Assert.NotNull(hash);
            Assert.Equal(64, hash.Length);
            Assert.Matches("^[A-F0-9]{64}$", hash);
        } finally {
            if (File.Exists(tempFile)) {
                File.Delete(tempFile);
            }
        }
    }
}
