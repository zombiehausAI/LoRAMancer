using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.Tests;

public sealed class SafeTensorsMetadataReaderTests {
    [Fact]
    public async Task ReadMetadataAsync_ValidHeader_ExtractsHyperparameters() {
        // Arrange
        SafeTensorsMetadataReader reader = new();
        string tempFile = Path.GetTempFileName();

        try {
            var headerDict = new Dictionary<string, object> {
                ["__metadata__"] = new Dictionary<string, string> {
                    ["ss_network_dim"] = "32",
                    ["ss_network_alpha"] = "16.0",
                    ["ss_learning_rate"] = "0.0002",
                    ["ss_optimizer"] = "adamw8bit",
                    ["ss_base_model_version"] = "FLUX.1-dev",
                    ["ss_epoch"] = "12",
                    ["ss_max_train_steps"] = "2400"
                }
            };

            byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(headerDict));
            byte[] headerSizeBytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(headerSizeBytes, (ulong)jsonBytes.Length);

            await using (FileStream fs = new(tempFile, FileMode.Create, FileAccess.Write)) {
                await fs.WriteAsync(headerSizeBytes);
                await fs.WriteAsync(jsonBytes);
                // Append dummy weight bytes
                await fs.WriteAsync(new byte[1024]);
            }

            // Act
            LoraMetadata result = await reader.ReadMetadataAsync(tempFile);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(32, result.NetworkDim);
            Assert.Equal(16.0, result.NetworkAlpha);
            Assert.Equal(0.0002, result.LearningRate);
            Assert.Equal("adamw8bit", result.Optimizer);
            Assert.Equal("FLUX.1-dev", result.BaseModel);
            Assert.Equal(12, result.Epochs);
            Assert.Equal(2400, result.TotalSteps);
        } finally {
            if (File.Exists(tempFile)) {
                File.Delete(tempFile);
            }
        }
    }
}
