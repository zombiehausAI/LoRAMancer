using System.Text;
using System.Text.RegularExpressions;

namespace LoRAMancer.App.Services;

public record TagFrequencyEntry(string Tag, int Count, double Percentage);

public record AspectRatioBucketSummary(string BucketName, string RatioLabel, int Count, double Percentage);

public sealed class DatasetCuratorItem {
    public string ImagePath { get; set; } = string.Empty;
    public string? CaptionPath { get; set; }
    public string CaptionText { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string AspectRatioLabel { get; set; } = "Unknown";
    public string BucketName { get; set; } = "Square";
    public long FileSizeBytes { get; set; }
    public bool IsCorrupt { get; set; }
    public string? ErrorMessage { get; set; }

    public string FormattedDimensions => Width > 0 && Height > 0 ? $"{Width} x {Height}" : "Unknown";

    public string FormattedSize {
        get {
            if (FileSizeBytes <= 0) {
                return "-";
            }
            double kb = FileSizeBytes / 1024.0;
            return kb >= 1024.0 ? $"{(kb / 1024.0):F1} MB" : $"{kb:F0} KB";
        }
    }
}

public sealed class DatasetCuratorReport {
    public string DirectoryPath { get; set; } = string.Empty;
    public List<DatasetCuratorItem> Items { get; set; } = new();
    public List<TagFrequencyEntry> TagFrequencies { get; set; } = new();
    public List<AspectRatioBucketSummary> Buckets { get; set; } = new();
    public int TotalImages => Items.Count;
    public int TotalCaptions => Items.Count(i => !string.IsNullOrWhiteSpace(i.CaptionText));
    public int MissingCaptions => Items.Count(i => string.IsNullOrWhiteSpace(i.CaptionText));
    public int CorruptCount => Items.Count(i => i.IsCorrupt);
}

public sealed class DatasetCuratorService {
    private static readonly HashSet<string> ValidImageExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp"
    };

    public async Task<DatasetCuratorReport> ScanDatasetAsync(string datasetDir, CancellationToken cancellationToken = default) {
        var report = new DatasetCuratorReport { DirectoryPath = datasetDir };
        if (string.IsNullOrWhiteSpace(datasetDir) || !Directory.Exists(datasetDir)) {
            return report;
        }

        string[] files = Directory.GetFiles(datasetDir, "*.*", SearchOption.AllDirectories);
        var imageFiles = files.Where(f => ValidImageExtensions.Contains(Path.GetExtension(f))).OrderBy(f => f).ToList();

        var items = new List<DatasetCuratorItem>();
        foreach (var imgPath in imageFiles) {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new DatasetCuratorItem { ImagePath = imgPath };

            try {
                var fileInfo = new FileInfo(imgPath);
                item.FileSizeBytes = fileInfo.Length;

                if (fileInfo.Length < 100) {
                    item.IsCorrupt = true;
                    item.ErrorMessage = "File size too small (empty or corrupt header)";
                } else {
                    var (w, h) = TryReadImageDimensions(imgPath);
                    item.Width = w;
                    item.Height = h;

                    if (w > 0 && h > 0) {
                        double ratio = (double)w / h;
                        if (ratio >= 0.95 && ratio <= 1.05) {
                            item.BucketName = "Square (1:1)";
                            item.AspectRatioLabel = "1:1";
                        } else if (ratio < 0.95) {
                            if (ratio <= 0.6) {
                                item.BucketName = "Ultra-Tall (9:16)";
                                item.AspectRatioLabel = "9:16";
                            } else if (ratio <= 0.75) {
                                item.BucketName = "Portrait (2:3)";
                                item.AspectRatioLabel = "2:3";
                            } else {
                                item.BucketName = "Portrait (4:5 / 3:4)";
                                item.AspectRatioLabel = "3:4";
                            }
                        } else {
                            if (ratio >= 1.7) {
                                item.BucketName = "Ultra-Wide (16:9)";
                                item.AspectRatioLabel = "16:9";
                            } else if (ratio >= 1.4) {
                                item.BucketName = "Landscape (3:2)";
                                item.AspectRatioLabel = "3:2";
                            } else {
                                item.BucketName = "Landscape (4:3 / 5:4)";
                                item.AspectRatioLabel = "4:3";
                            }
                        }
                    } else {
                        item.IsCorrupt = true;
                        item.ErrorMessage = "Unrecognized or unsupported image header format";
                    }
                }
            } catch (Exception ex) {
                item.IsCorrupt = true;
                item.ErrorMessage = ex.Message;
            }

            // Find caption
            string baseNoExt = Path.Combine(Path.GetDirectoryName(imgPath) ?? datasetDir, Path.GetFileNameWithoutExtension(imgPath));
            string txtFile = baseNoExt + ".txt";
            string capFile = baseNoExt + ".caption";

            if (File.Exists(txtFile)) {
                item.CaptionPath = txtFile;
                item.CaptionText = (await File.ReadAllTextAsync(txtFile, cancellationToken)).Trim();
            } else if (File.Exists(capFile)) {
                item.CaptionPath = capFile;
                item.CaptionText = (await File.ReadAllTextAsync(capFile, cancellationToken)).Trim();
            }

            items.Add(item);
        }

        report.Items = items;
        report.TagFrequencies = ComputeTagFrequencies(items);
        report.Buckets = ComputeBucketSummaries(items);
        return report;
    }

    public List<TagFrequencyEntry> ComputeTagFrequencies(IEnumerable<DatasetCuratorItem> items) {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int totalItemsWithTags = 0;

        foreach (var item in items) {
            if (string.IsNullOrWhiteSpace(item.CaptionText)) {
                continue;
            }

            totalItemsWithTags++;
            var tags = ParseTags(item.CaptionText);
            var seenInItem = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in tags) {
                if (seenInItem.Add(t)) {
                    dict[t] = dict.GetValueOrDefault(t, 0) + 1;
                }
            }
        }

        return dict
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Select(kv => new TagFrequencyEntry(
                kv.Key,
                kv.Value,
                totalItemsWithTags > 0 ? (double)kv.Value / totalItemsWithTags * 100.0 : 0
            ))
            .ToList();
    }

    public List<AspectRatioBucketSummary> ComputeBucketSummaries(IEnumerable<DatasetCuratorItem> items) {
        var list = items.Where(i => !i.IsCorrupt && i.Width > 0).ToList();
        int total = list.Count;
        if (total == 0) {
            return new();
        }

        return list
            .GroupBy(i => i.BucketName)
            .Select(g => new AspectRatioBucketSummary(
                g.Key,
                g.First().AspectRatioLabel,
                g.Count(),
                (double)g.Count() / total * 100.0
            ))
            .OrderByDescending(b => b.Count)
            .ToList();
    }

    public async Task<int> BatchFindReplaceAsync(string datasetDir, string findText, string replaceText, bool matchCase = false, bool useRegex = false, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(datasetDir) || !Directory.Exists(datasetDir) || string.IsNullOrEmpty(findText)) {
            return 0;
        }

        int modifiedCount = 0;
        string[] txtFiles = Directory.GetFiles(datasetDir, "*.txt", SearchOption.AllDirectories);

        Regex? regex = null;
        if (useRegex) {
            var options = matchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
            regex = new Regex(findText, options);
        }

        foreach (var file in txtFiles) {
            cancellationToken.ThrowIfCancellationRequested();
            string content = await File.ReadAllTextAsync(file, cancellationToken);
            string newContent;

            if (useRegex && regex != null) {
                newContent = regex.Replace(content, replaceText);
            } else {
                var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                newContent = ReplaceString(content, findText, replaceText, comparison);
            }

            if (newContent != content) {
                await File.WriteAllTextAsync(file, newContent, cancellationToken);
                modifiedCount++;
            }
        }

        return modifiedCount;
    }

    public async Task<int> BatchPrependTagAsync(string datasetDir, string tag, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(datasetDir) || !Directory.Exists(datasetDir) || string.IsNullOrWhiteSpace(tag)) {
            return 0;
        }

        string cleanTag = tag.Trim().TrimEnd(',');
        int modifiedCount = 0;
        string[] imgFiles = Directory.GetFiles(datasetDir, "*.*", SearchOption.AllDirectories)
            .Where(f => ValidImageExtensions.Contains(Path.GetExtension(f))).ToArray();

        foreach (var img in imgFiles) {
            cancellationToken.ThrowIfCancellationRequested();
            string dir = Path.GetDirectoryName(img) ?? datasetDir;
            string txtPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(img) + ".txt");

            if (File.Exists(txtPath)) {
                string current = (await File.ReadAllTextAsync(txtPath, cancellationToken)).Trim();
                var tags = ParseTags(current);
                if (!tags.Any(t => string.Equals(t, cleanTag, StringComparison.OrdinalIgnoreCase))) {
                    string newText = string.IsNullOrEmpty(current) ? cleanTag : $"{cleanTag}, {current}";
                    await File.WriteAllTextAsync(txtPath, newText, cancellationToken);
                    modifiedCount++;
                }
            } else {
                await File.WriteAllTextAsync(txtPath, cleanTag, cancellationToken);
                modifiedCount++;
            }
        }

        return modifiedCount;
    }

    public async Task<int> BatchAppendTagAsync(string datasetDir, string tag, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(datasetDir) || !Directory.Exists(datasetDir) || string.IsNullOrWhiteSpace(tag)) {
            return 0;
        }

        string cleanTag = tag.Trim().TrimEnd(',');
        int modifiedCount = 0;
        string[] imgFiles = Directory.GetFiles(datasetDir, "*.*", SearchOption.AllDirectories)
            .Where(f => ValidImageExtensions.Contains(Path.GetExtension(f))).ToArray();

        foreach (var img in imgFiles) {
            cancellationToken.ThrowIfCancellationRequested();
            string dir = Path.GetDirectoryName(img) ?? datasetDir;
            string txtPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(img) + ".txt");

            if (File.Exists(txtPath)) {
                string current = (await File.ReadAllTextAsync(txtPath, cancellationToken)).Trim();
                var tags = ParseTags(current);
                if (!tags.Any(t => string.Equals(t, cleanTag, StringComparison.OrdinalIgnoreCase))) {
                    string newText = string.IsNullOrEmpty(current) ? cleanTag : $"{current}, {cleanTag}";
                    await File.WriteAllTextAsync(txtPath, newText, cancellationToken);
                    modifiedCount++;
                }
            } else {
                await File.WriteAllTextAsync(txtPath, cleanTag, cancellationToken);
                modifiedCount++;
            }
        }

        return modifiedCount;
    }

    public async Task<int> BatchDeleteTagAsync(string datasetDir, string tagToRemove, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(datasetDir) || !Directory.Exists(datasetDir) || string.IsNullOrWhiteSpace(tagToRemove)) {
            return 0;
        }

        string cleanTarget = tagToRemove.Trim();
        int modifiedCount = 0;
        string[] txtFiles = Directory.GetFiles(datasetDir, "*.txt", SearchOption.AllDirectories);

        foreach (var file in txtFiles) {
            cancellationToken.ThrowIfCancellationRequested();
            string current = await File.ReadAllTextAsync(file, cancellationToken);
            var tags = ParseTags(current);

            var filtered = tags.Where(t => !string.Equals(t, cleanTarget, StringComparison.OrdinalIgnoreCase)).ToList();
            if (filtered.Count != tags.Count) {
                string newContent = string.Join(", ", filtered);
                await File.WriteAllTextAsync(file, newContent, cancellationToken);
                modifiedCount++;
            }
        }

        return modifiedCount;
    }

    public async Task SaveItemCaptionAsync(DatasetCuratorItem item, string newCaption, CancellationToken cancellationToken = default) {
        string path = item.CaptionPath ?? Path.Combine(
            Path.GetDirectoryName(item.ImagePath) ?? "",
            Path.GetFileNameWithoutExtension(item.ImagePath) + ".txt"
        );

        await File.WriteAllTextAsync(path, newCaption.Trim(), cancellationToken);
        item.CaptionPath = path;
        item.CaptionText = newCaption.Trim();
    }

    public async Task<string> CreateCaptionBackupAsync(string datasetDir, CancellationToken cancellationToken = default) {
        string backupDir = Path.Combine(datasetDir, $".captions_backup_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(backupDir);

        string[] txtFiles = Directory.GetFiles(datasetDir, "*.txt", SearchOption.TopDirectoryOnly);
        foreach (var file in txtFiles) {
            cancellationToken.ThrowIfCancellationRequested();
            string dest = Path.Combine(backupDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }

        return backupDir;
    }

    public static List<string> ParseTags(string caption) {
        if (string.IsNullOrWhiteSpace(caption)) {
            return new();
        }

        return caption
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
    }

    private static string ReplaceString(string str, string oldValue, string newValue, StringComparison comparison) {
        StringBuilder sb = new StringBuilder();
        int previousIndex = 0;
        int index = str.IndexOf(oldValue, comparison);
        while (index != -1) {
            sb.Append(str.AsSpan(previousIndex, index - previousIndex));
            sb.Append(newValue);
            previousIndex = index + oldValue.Length;
            index = str.IndexOf(oldValue, previousIndex, comparison);
        }
        sb.Append(str.AsSpan(previousIndex));
        return sb.ToString();
    }

    private static (int width, int height) TryReadImageDimensions(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);

        byte[] header = reader.ReadBytes(16);
        if (header.Length < 16) {
            return (0, 0);
        }

        // PNG signature: 89 50 4E 47 0D 0A 1A 0A
        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) {
            stream.Seek(16, SeekOrigin.Begin);
            byte[] dimBytes = reader.ReadBytes(8);
            if (dimBytes.Length == 8) {
                int w = (dimBytes[0] << 24) | (dimBytes[1] << 16) | (dimBytes[2] << 8) | dimBytes[3];
                int h = (dimBytes[4] << 24) | (dimBytes[5] << 16) | (dimBytes[6] << 8) | dimBytes[7];
                return (w, h);
            }
        }

        // JPEG: starts with FF D8
        if (header[0] == 0xFF && header[1] == 0xD8) {
            stream.Seek(2, SeekOrigin.Begin);
            while (stream.Position < stream.Length - 8) {
                byte markerLead = reader.ReadByte();
                if (markerLead != 0xFF) {
                    continue;
                }
                byte marker = reader.ReadByte();
                while (marker == 0xFF) {
                    marker = reader.ReadByte();
                }

                if (marker == 0xC0 || marker == 0xC1 || marker == 0xC2) {
                    // SOF marker found
                    reader.ReadInt16(); // skip length
                    reader.ReadByte();  // skip precision
                    int h = (reader.ReadByte() << 8) | reader.ReadByte();
                    int w = (reader.ReadByte() << 8) | reader.ReadByte();
                    return (w, h);
                }

                if (marker == 0xD9 || marker == 0xDA) {
                    break; // End of image or start of scan
                }

                // Skip marker segment
                byte lenH = reader.ReadByte();
                byte lenL = reader.ReadByte();
                int len = (lenH << 8) | lenL;
                if (len < 2) {
                    break;
                }
                stream.Seek(len - 2, SeekOrigin.Current);
            }
        }

        // WebP: RIFF ... WEBP
        if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
            header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50) {
            stream.Seek(12, SeekOrigin.Begin);
            byte[] vp8Chunk = reader.ReadBytes(4);
            string chunkType = Encoding.ASCII.GetString(vp8Chunk);

            if (chunkType == "VP8 ") {
                stream.Seek(23, SeekOrigin.Begin);
                byte[] dims = reader.ReadBytes(4);
                if (dims.Length == 4) {
                    int w = (dims[0] | (dims[1] << 8)) & 0x3FFF;
                    int h = (dims[2] | (dims[3] << 8)) & 0x3FFF;
                    return (w, h);
                }
            } else if (chunkType == "VP8L") {
                stream.Seek(21, SeekOrigin.Begin);
                byte[] b = reader.ReadBytes(4);
                if (b.Length == 4) {
                    int w = 1 + (((b[1] & 0x3F) << 8) | b[0]);
                    int h = 1 + (((b[3] & 0xF) << 10) | (b[2] << 2) | ((b[1] & 0xC0) >> 6));
                    return (w, h);
                }
            } else if (chunkType == "VP8X") {
                stream.Seek(24, SeekOrigin.Begin);
                byte[] dims = reader.ReadBytes(6);
                if (dims.Length == 6) {
                    int w = 1 + (dims[0] | (dims[1] << 8) | (dims[2] << 16));
                    int h = 1 + (dims[3] | (dims[4] << 8) | (dims[5] << 16));
                    return (w, h);
                }
            }
        }

        return (0, 0);
    }
}
