using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.Tests;

public sealed class RemoteEngineModelsTests {
    [Fact]
    public void ServerHealthDto_SerializesAndDeserializesCorrectly() {
        ServerHealthDto original = new() {
            Status = "Training",
            MachineName = "AIPC-4090",
            OsDescription = "Microsoft Windows 11 Pro",
            GpuInfo = "NVIDIA GeForce RTX 4090 (24GB)",
            PyTorchVersion = "2.6.0+cu124",
            IsTraining = true,
            ActiveRunName = "flux_mystic_v1",
            CurrentStep = 450,
            TotalSteps = 1500,
            CurrentLoss = 0.0421f
        };

        string json = JsonSerializer.Serialize(original);
        ServerHealthDto? deserialized = JsonSerializer.Deserialize<ServerHealthDto>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Training", deserialized.Status);
        Assert.Equal("AIPC-4090", deserialized.MachineName);
        Assert.Equal("NVIDIA GeForce RTX 4090 (24GB)", deserialized.GpuInfo);
        Assert.Equal(450, deserialized.CurrentStep);
        Assert.Equal(1500, deserialized.TotalSteps);
        Assert.Equal(0.0421f, deserialized.CurrentLoss, 4);
    }

    [Fact]
    public void StartRemoteTrainingRequest_SerializesWithDefaults() {
        StartRemoteTrainingRequest request = new() {
            RunName = "cyberpunk_style",
            BaseArchitecture = "sdxl",
            ConfigYaml = "sample: config",
            TriggerWord = "cyb3rpunk style",
            Steps = 2000
        };

        string json = JsonSerializer.Serialize(request);
        StartRemoteTrainingRequest? result = JsonSerializer.Deserialize<StartRemoteTrainingRequest>(json);

        Assert.NotNull(result);
        Assert.Equal("cyberpunk_style", result.RunName);
        Assert.Equal("sdxl", result.BaseArchitecture);
        Assert.Equal("cyb3rpunk style", result.TriggerWord);
        Assert.Equal(2000, result.Steps);
    }

    [Fact]
    public void TrainingTelemetryDto_SerializesEventTypes() {
        TrainingTelemetryDto telemetry = new() {
            EventType = "step",
            Step = 100,
            TotalSteps = 1000,
            Loss = 0.089f,
            Message = "Step 100/1000 complete"
        };

        string json = JsonSerializer.Serialize(telemetry);
        TrainingTelemetryDto? result = JsonSerializer.Deserialize<TrainingTelemetryDto>(json);

        Assert.NotNull(result);
        Assert.Equal("step", result.EventType);
        Assert.Equal(100, result.Step);
        Assert.Equal(1000, result.TotalSteps);
        Assert.Equal(0.089f, result.Loss, 3);
        Assert.Equal("Step 100/1000 complete", result.Message);
    }
}
