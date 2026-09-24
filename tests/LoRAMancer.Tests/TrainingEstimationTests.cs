using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public sealed class TrainingEstimationTests {
    [Fact]
    public void CalculateEstimates_FluxDev_ReturnsAccurateMetrics() {
        ModelArchitectureRegistry registry = new();
        TrainingEstimationService estimator = new(registry);

        TrainingEstimates estimates = estimator.CalculateEstimates(
            baseModelDisplayName: "FLUX.1 Dev",
            imageCount: 20,
            repeats: 10,
            epochs: 10,
            batchSize: 1,
            networkDim: 16
        );

        Assert.Equal(2000, estimates.TotalSteps);
        Assert.True(estimates.EstimatedVramGb >= 13.0);
        Assert.True(estimates.EstimatedOutputSizeMb > 150.0);
        Assert.True(estimates.EstimatedDuration.TotalMinutes > 0);
    }

    [Fact]
    public void GetSubjectPresets_ContainsAllDefaultTypes() {
        ModelArchitectureRegistry registry = new();
        TrainingEstimationService estimator = new(registry);

        var presets = estimator.GetSubjectPresets();

        Assert.Contains(presets, p => p.SubjectType == TrainingSubjectType.Character);
        Assert.Contains(presets, p => p.SubjectType == TrainingSubjectType.Style);
        Assert.Contains(presets, p => p.SubjectType == TrainingSubjectType.Concept);
        Assert.Contains(presets, p => p.SubjectType == TrainingSubjectType.Clothing);
    }

    [Fact]
    public async Task DatasetInspector_InspectAndPrepend_WorksCorrectly() {
        DatasetInspectorService inspector = new();
        string tempDir = Path.Combine(Path.GetTempPath(), "loramancer_dataset_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try {
            string img1 = Path.Combine(tempDir, "img01.png");
            string img2 = Path.Combine(tempDir, "img02.png");
            await File.WriteAllBytesAsync(img1, new byte[20480]); // 20KB
            await File.WriteAllBytesAsync(img2, new byte[20480]); // 20KB

            string txt1 = Path.Combine(tempDir, "img01.txt");
            await File.WriteAllTextAsync(txt1, "a photo of a person");

            DatasetHealthReport report = await inspector.InspectDatasetAsync(tempDir);

            Assert.Equal(2, report.TotalImages);
            Assert.Equal(1, report.TotalCaptions);
            Assert.Equal(1, report.MissingCaptions);

            int modified = await inspector.PrependTriggerWordAsync(tempDir, "ohwx character");
            Assert.Equal(2, modified);

            string updatedTxt1 = await File.ReadAllTextAsync(txt1);
            Assert.StartsWith("ohwx character, a photo of a person", updatedTxt1);

            string txt2 = Path.Combine(tempDir, "img02.txt");
            Assert.True(File.Exists(txt2));
            string updatedTxt2 = await File.ReadAllTextAsync(txt2);
            Assert.Equal("ohwx character", updatedTxt2);
        } finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void AmdEnvironmentInfo_ReadyForTraining_EvaluatesAccurately() {
        AmdEnvironmentInfo amdInfo = new() {
            DetectedVendor = HardwareVendor.Amd,
            VenvPath = "C:\\test\\.venv",
            HasRocmSupport = true
        };
        Assert.True(amdInfo.IsReadyForTraining);

        AmdEnvironmentInfo nvidiaInfo = new() {
            DetectedVendor = HardwareVendor.Nvidia,
            VenvPath = "C:\\test\\.venv",
            HasRocmSupport = false
        };
        Assert.True(nvidiaInfo.IsReadyForTraining);

        AmdEnvironmentInfo unconfiguredInfo = new() {
            DetectedVendor = HardwareVendor.Amd,
            VenvPath = string.Empty,
            HasRocmSupport = false
        };
        Assert.False(unconfiguredInfo.IsReadyForTraining);
    }

    [Fact]
    public async Task DatasetInspector_ZipDataset_AutoExtractsAndInspects() {
        DatasetInspectorService inspector = new();
        string tempWorkDir = Path.Combine(Path.GetTempPath(), "loramancer_zip_test_" + Guid.NewGuid());
        Directory.CreateDirectory(tempWorkDir);
        string zipPath = Path.Combine(tempWorkDir, "test_dataset.zip");

        try {
            // Create a small zip file containing an image and caption
            using (var zipStream = new FileStream(zipPath, FileMode.Create))
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create)) {
                var imgEntry = archive.CreateEntry("sample01.png");
                using (var entryStream = imgEntry.Open()) {
                    byte[] dummyImg = new byte[25000];
                    await entryStream.WriteAsync(dummyImg);
                }

                var txtEntry = archive.CreateEntry("sample01.txt");
                using (var entryStream = txtEntry.Open())
                using (var writer = new StreamWriter(entryStream)) {
                    await writer.WriteAsync("sks person, looking at viewer");
                }
            }

            var report = await inspector.InspectDatasetAsync(zipPath);

            Assert.True(report.ExtractedFromZip);
            Assert.Equal(zipPath, report.OriginalZipPath);
            Assert.Equal(1, report.TotalImages);
            Assert.Equal(1, report.TotalCaptions);
            Assert.True(Directory.Exists(report.DatasetDirectory));
            Assert.True(File.Exists(Path.Combine(report.DatasetDirectory, "sample01.png")));
            Assert.True(File.Exists(Path.Combine(report.DatasetDirectory, "sample01.txt")));
        } finally {
            if (Directory.Exists(tempWorkDir)) {
                Directory.Delete(tempWorkDir, true);
            }
        }
    }
}
