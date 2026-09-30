using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

/// <summary>
/// Generates and caches media thumbnails on the fly in the user settings folder (.loramancer/showcase_thumbnails).
/// Supports images, video frames via FFmpeg, and audio waveforms via FFmpeg showwavespic.
/// </summary>
public sealed class ShowcaseThumbnailService {
    private readonly SettingsService _settingsService;
    private readonly string _thumbnailDirectory;
    private readonly ConcurrentDictionary<string, string> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<string>> _inFlightTasks = new(StringComparer.OrdinalIgnoreCase);

    private static string? _cachedFfmpegPath;
    private static bool _ffmpegSearched;

    public ShowcaseThumbnailService(SettingsService settingsService) {
        _settingsService = settingsService;
        _thumbnailDirectory = Path.Combine(_settingsService.SettingsDirectory, "showcase_thumbnails");

        try {
            if (!Directory.Exists(_thumbnailDirectory)) {
                Directory.CreateDirectory(_thumbnailDirectory);
            }
        } catch {
            // Ignore directory creation errors if constrained
        }
    }

    public string ThumbnailDirectory => _thumbnailDirectory;

    public string? GetCachedDataUri(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) {
            return null;
        }
        return _memoryCache.TryGetValue(filePath, out string? uri) ? uri : null;
    }

    public async Task<string> GetOrCreateThumbnailDataUriAsync(ShowcaseMediaItem item, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath)) {
            return string.Empty;
        }

        if (_memoryCache.TryGetValue(item.FilePath, out string? cached)) {
            return cached;
        }

        // Deduplicate concurrent requests for the exact same file
        return await _inFlightTasks.GetOrAdd(item.FilePath, _ => GenerateThumbnailInternalAsync(item, cancellationToken));
    }

    private async Task<string> GenerateThumbnailInternalAsync(ShowcaseMediaItem item, CancellationToken cancellationToken) {
        try {
            string hashKey = ComputeThumbKey(item.FilePath, item.FileSizeBytes, item.CreatedDate);
            string thumbFilePath = Path.Combine(_thumbnailDirectory, $"{hashKey}.jpg");

            // Check if thumbnail was already created on disk previously
            if (File.Exists(thumbFilePath) && new FileInfo(thumbFilePath).Length > 0) {
                byte[] existingBytes = await File.ReadAllBytesAsync(thumbFilePath, cancellationToken);
                string dataUri = $"data:image/jpeg;base64,{Convert.ToBase64String(existingBytes)}";
                _memoryCache[item.FilePath] = dataUri;
                return dataUri;
            }

            bool generated = false;

            if (item.MediaType == ShowcaseMediaType.Image) {
                generated = await GenerateImageThumbnailAsync(item.FilePath, thumbFilePath, cancellationToken);
            } else if (item.MediaType == ShowcaseMediaType.Video) {
                generated = await GenerateVideoThumbnailAsync(item.FilePath, thumbFilePath, cancellationToken);
            } else if (item.MediaType == ShowcaseMediaType.Audio) {
                generated = await GenerateAudioThumbnailAsync(item.FilePath, thumbFilePath, cancellationToken);
            }

            if (generated && File.Exists(thumbFilePath) && new FileInfo(thumbFilePath).Length > 0) {
                byte[] generatedBytes = await File.ReadAllBytesAsync(thumbFilePath, cancellationToken);
                string dataUri = $"data:image/jpeg;base64,{Convert.ToBase64String(generatedBytes)}";
                _memoryCache[item.FilePath] = dataUri;
                return dataUri;
            }

            return string.Empty;
        } catch {
            return string.Empty;
        } finally {
            _inFlightTasks.TryRemove(item.FilePath, out _);
        }
    }

    private async Task<bool> GenerateImageThumbnailAsync(string sourcePath, string destJpgPath, CancellationToken token) {
        // Attempt 1: Native Windows.Graphics.Imaging downscaling
        try {
            await using FileStream stream = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            if (stream.Length == 0) {
                return false;
            }

            using var randomAccessStream = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(stream);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(randomAccessStream);

            uint origW = decoder.PixelWidth;
            uint origH = decoder.PixelHeight;
            const uint maxDim = 380;
            uint targetW = origW;
            uint targetH = origH;

            if (origW > maxDim || origH > maxDim) {
                if (origW >= origH) {
                    targetH = Math.Max(1, (uint)Math.Round((double)origH * maxDim / origW));
                    targetW = maxDim;
                } else {
                    targetW = Math.Max(1, (uint)Math.Round((double)origW * maxDim / origH));
                    targetH = maxDim;
                }
            }

            var transform = new Windows.Graphics.Imaging.BitmapTransform {
                ScaledWidth = targetW,
                ScaledHeight = targetH,
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant
            };

            var pixelData = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                transform,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb
            );

            byte[] pixels = pixelData.DetachPixelData();

            await using FileStream outStream = new(destJpgPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            using var outRandomAccessStream = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(outStream);
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, outRandomAccessStream);

            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                targetW,
                targetH,
                96,
                96,
                pixels
            );

            await encoder.FlushAsync();
            return true;
        } catch {
            // Fall back to FFmpeg if native decoder failed on atypical format
        }

        // Attempt 2: FFmpeg fallback for complex or unusual image formats
        string? ffmpeg = FindFfmpeg();
        if (!string.IsNullOrWhiteSpace(ffmpeg)) {
            return await RunFfmpegAsync(ffmpeg, $"-y -i \"{sourcePath}\" -vf \"scale=380:-1\" -q:v 2 \"{destJpgPath}\"", token);
        }

        return false;
    }

    private async Task<bool> GenerateVideoThumbnailAsync(string sourcePath, string destJpgPath, CancellationToken token) {
        string? ffmpeg = FindFfmpeg();
        if (string.IsNullOrWhiteSpace(ffmpeg)) {
            return false;
        }

        // Try grabbing frame at 1.0s first
        bool success = await RunFfmpegAsync(ffmpeg, $"-y -ss 00:00:01 -i \"{sourcePath}\" -vframes 1 -vf \"scale=380:-1\" -q:v 3 \"{destJpgPath}\"", token);
        if (success && File.Exists(destJpgPath) && new FileInfo(destJpgPath).Length > 0) {
            return true;
        }

        // If 1.0s failed (e.g., video is shorter than 1s), grab at beginning 0.0s
        return await RunFfmpegAsync(ffmpeg, $"-y -ss 00:00:00 -i \"{sourcePath}\" -vframes 1 -vf \"scale=380:-1\" -q:v 3 \"{destJpgPath}\"", token);
    }

    private async Task<bool> GenerateAudioThumbnailAsync(string sourcePath, string destJpgPath, CancellationToken token) {
        string? ffmpeg = FindFfmpeg();
        if (string.IsNullOrWhiteSpace(ffmpeg)) {
            return false;
        }

        // Generate audio waveform picture with showwavespic
        return await RunFfmpegAsync(
            ffmpeg,
            $"-y -i \"{sourcePath}\" -filter_complex \"showwavespic=s=380x190:colors=#cba6f7\" -frames:v 1 \"{destJpgPath}\"",
            token
        );
    }

    private static async Task<bool> RunFfmpegAsync(string ffmpegPath, string arguments, CancellationToken token) {
        try {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            proc.Start();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

            await proc.WaitForExitAsync(timeoutCts.Token);
            return proc.ExitCode == 0;
        } catch {
            return false;
        }
    }

    public static string? FindFfmpeg() {
        if (_ffmpegSearched) {
            return _cachedFfmpegPath;
        }
        _ffmpegSearched = true;

        string[] candidates = new[] {
            @"C:\tools\ffmpeg\bin\ffmpeg.exe",
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
            @"C:\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".loramancer", "bin", "ffmpeg.exe")
        };

        foreach (string candidate in candidates) {
            if (File.Exists(candidate)) {
                _cachedFfmpegPath = candidate;
                return _cachedFfmpegPath;
            }
        }

        try {
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathVar)) {
                foreach (string part in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
                    string candidate = Path.Combine(part.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate)) {
                        _cachedFfmpegPath = candidate;
                        return _cachedFfmpegPath;
                    }
                }
            }
        } catch {
            // Ignore environment path read errors
        }

        return null;
    }

    public async Task<string> GetFullImageDataUriAsync(string filePath, CancellationToken token = default) {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) {
            return string.Empty;
        }

        try {
            byte[] bytes = await File.ReadAllBytesAsync(filePath, token);
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string mime = ext switch {
                ".png" => "image/png",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                _ => "image/jpeg"
            };
            return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
        } catch {
            return string.Empty;
        }
    }

    private static string ComputeThumbKey(string filePath, long size, DateTime created) {
        string raw = $"{filePath.ToLowerInvariant()}_{size}_{created.Ticks}";
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        StringBuilder sb = new();
        for (int i = 0; i < 16; i++) {
            sb.Append(hashBytes[i].ToString("x2"));
        }
        return sb.ToString();
    }
}
