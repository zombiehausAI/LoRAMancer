using System.IO.Compression;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class DatasetInspectorService {
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".png", ".jpg", ".jpeg", ".webp"
    };

    public Task<string> EnsureExtractedDatasetDirectoryAsync(string inputPath, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(inputPath)) {
            return Task.FromResult(string.Empty);
        }

        // If path is a zip archive, automatically extract it into user profile .loramancer/extracted_datasets/<name>
        if (File.Exists(inputPath) && inputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string baseExtractDir = Path.Combine(userProfile, ".loramancer", "extracted_datasets");
            string safeName = Path.GetFileNameWithoutExtension(inputPath);
            string targetDir = Path.Combine(baseExtractDir, safeName);

            Directory.CreateDirectory(targetDir);

            bool needsExtract = !Directory.EnumerateFileSystemEntries(targetDir).Any() ||
                                File.GetLastWriteTimeUtc(inputPath) > Directory.GetLastWriteTimeUtc(targetDir);

            if (needsExtract) {
                ZipFile.ExtractToDirectory(inputPath, targetDir, overwriteFiles: true);
            }

            return Task.FromResult(targetDir);
        }

        return Task.FromResult(inputPath);
    }

    public async Task<DatasetHealthReport> InspectDatasetAsync(string inputPath) {
        if (string.IsNullOrWhiteSpace(inputPath)) {
            return new DatasetHealthReport {
                Warnings = { "No dataset directory or ZIP archive provided." }
            };
        }

        bool isZip = File.Exists(inputPath) && inputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        string directoryPath = await EnsureExtractedDatasetDirectoryAsync(inputPath);

        DatasetHealthReport report = new() {
            DatasetDirectory = directoryPath,
            ExtractedFromZip = isZip,
            OriginalZipPath = isZip ? inputPath : string.Empty
        };

        if (!Directory.Exists(directoryPath)) {
            report.Warnings.Add(isZip ? "Failed to extract dataset from ZIP archive." : "Selected dataset directory does not exist.");
            return report;
        }

        string[] allFiles = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories);
        List<string> imageFiles = allFiles.Where(f => ImageExtensions.Contains(Path.GetExtension(f))).ToList();
        report.TotalImages = imageFiles.Count;

        if (report.TotalImages == 0) {
            report.Warnings.Add("No images (.png, .jpg, .webp) found in the dataset directory.");
            return report;
        }

        if (report.TotalImages < 10) {
            report.Warnings.Add($"Small dataset ({report.TotalImages} images). Recommended: at least 15–30 images for characters/concepts, 40+ for styles.");
        }

        int captionCount = 0;
        int missingCaptions = 0;

        foreach (string img in imageFiles) {
            string baseNoExt = Path.Combine(Path.GetDirectoryName(img) ?? directoryPath, Path.GetFileNameWithoutExtension(img));
            string txtPath = baseNoExt + ".txt";
            string captionPath = baseNoExt + ".caption";

            string? activeCaptionFile = File.Exists(txtPath) ? txtPath : (File.Exists(captionPath) ? captionPath : null);

            if (activeCaptionFile != null) {
                captionCount++;
                if (report.SampleCaptions.Count < 5) {
                    try {
                        string text = File.ReadAllText(activeCaptionFile).Trim();
                        if (!string.IsNullOrEmpty(text)) {
                            report.SampleCaptions.Add(text);
                        }
                    } catch {
                        // Ignore read errors for sample display
                    }
                }
            } else {
                missingCaptions++;
            }

            // Check file size heuristic (<15KB usually indicates low resolution or corrupt thumbnail)
            try {
                FileInfo info = new(img);
                if (info.Length < 15 * 1024) {
                    report.LowResolutionImages++;
                }
            } catch {
                report.CorruptImages++;
            }
        }

        report.TotalCaptions = captionCount;
        report.MissingCaptions = missingCaptions;

        if (missingCaptions > 0) {
            report.Warnings.Add($"{missingCaptions} image(s) lack accompanying .txt caption files. You can use 'Prepend Trigger Word' to generate them.");
        }

        if (report.LowResolutionImages > 0) {
            report.Warnings.Add($"{report.LowResolutionImages} image(s) appear very small (<15 KB). Upscaling or filtering out low-res files is recommended.");
        }

        return report;
    }

    public async Task<int> PrependTriggerWordAsync(string inputPath, string triggerWord) {
        string directoryPath = await EnsureExtractedDatasetDirectoryAsync(inputPath);
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath) || string.IsNullOrWhiteSpace(triggerWord)) {
            return 0;
        }

        string cleanTrigger = triggerWord.Trim().TrimEnd(',');
        string[] allFiles = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories);
        List<string> imageFiles = allFiles.Where(f => ImageExtensions.Contains(Path.GetExtension(f))).ToList();
        int modifiedCount = 0;

        foreach (string img in imageFiles) {
            string dir = Path.GetDirectoryName(img) ?? directoryPath;
            string txtPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(img) + ".txt");

            if (File.Exists(txtPath)) {
                string currentText = (await File.ReadAllTextAsync(txtPath)).Trim();
                if (!currentText.StartsWith(cleanTrigger, StringComparison.OrdinalIgnoreCase)) {
                    string newText = string.IsNullOrEmpty(currentText)
                        ? cleanTrigger
                        : $"{cleanTrigger}, {currentText}";
                    await File.WriteAllTextAsync(txtPath, newText);
                    modifiedCount++;
                }
            } else {
                await File.WriteAllTextAsync(txtPath, cleanTrigger);
                modifiedCount++;
            }
        }

        return modifiedCount;
    }
}
