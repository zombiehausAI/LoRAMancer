using System.Text.Json;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class LoraLibraryService {
    private readonly SafeTensorsMetadataReader _metadataReader;
    private readonly CivitaiService _civitaiService;
    private readonly SettingsService _settingsService;
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;

    public event Action<LoraMetadata>? OnLoraEnriched;

    public LoraLibraryService(
        SafeTensorsMetadataReader metadataReader,
        CivitaiService civitaiService,
        SettingsService settingsService,
        HttpClient? httpClient = null
    ) {
        _metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        _civitaiService = civitaiService ?? throw new ArgumentNullException(nameof(civitaiService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _httpClient = httpClient ?? new HttpClient();

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _cacheDirectory = Path.Combine(userProfile, ".loramancer", "lora_cache");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<List<LoraMetadata>> ScanDirectoryAsync(string directoryPath, Action<string, int, int>? onProgress = null, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!Directory.Exists(directoryPath)) {
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
        }

        string[] files = Directory.GetFiles(directoryPath, "*.safetensors", SearchOption.AllDirectories);
        List<LoraMetadata> result = new();

        for (int i = 0; i < files.Length; i++) {
            cancellationToken.ThrowIfCancellationRequested();
            string file = files[i];

            try {
                LoraMetadata meta = await _metadataReader.ReadMetadataAsync(file, cancellationToken);
                FindLocalThumbnail(meta);
                LoadCachedCivitaiInfo(meta);
                result.Add(meta);
                onProgress?.Invoke(Path.GetFileName(file), i + 1, files.Length);
            } catch {
                // Ignore corrupt or non-LoRA safetensors files
            }
        }

        return result;
    }

    public void FindLocalThumbnail(LoraMetadata meta) {
        string dir = Path.GetDirectoryName(meta.FilePath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(meta.FilePath);

        string[] candidates = {
            Path.Combine(dir, $"{baseName}.png"),
            Path.Combine(dir, $"{baseName}.preview.png"),
            Path.Combine(dir, $"{baseName}.jpg"),
            Path.Combine(dir, $"{baseName}.preview.jpg"),
            Path.Combine(dir, $"{baseName}.webp")
        };

        foreach (string candidate in candidates) {
            if (File.Exists(candidate)) {
                meta.ThumbnailPath = candidate;
                return;
            }
        }
    }

    public async Task<CivitaiModelVersionInfo?> EnrichFromCivitaiAsync(LoraMetadata meta, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(meta);

        if (meta.CivitaiInfo != null) {
            return meta.CivitaiInfo;
        }

        try {
            if (string.IsNullOrWhiteSpace(meta.Sha256Hash)) {
                meta.Sha256Hash = await _civitaiService.ComputeFileSha256Async(meta.FilePath, cancellationToken: cancellationToken);
            }

            string cacheFile = Path.Combine(_cacheDirectory, $"{meta.Sha256Hash}.json");
            if (File.Exists(cacheFile)) {
                string json = await File.ReadAllTextAsync(cacheFile, cancellationToken);
                CivitaiModelVersionInfo? cached = JsonSerializer.Deserialize<CivitaiModelVersionInfo>(json);
                if (cached != null) {
                    meta.CivitaiInfo = cached;
                    if (cached.TrainedWords != null && cached.TrainedWords.Count > 0) {
                        meta.TrainedWords = cached.TrainedWords;
                    }
                    ApplyCachedThumbnail(meta);
                    OnLoraEnriched?.Invoke(meta);
                    return cached;
                }
            }

            CivitaiModelVersionInfo? info = await _civitaiService.LookupByHashAsync(meta.Sha256Hash, cancellationToken);
            if (info != null) {
                meta.CivitaiInfo = info;
                if (info.TrainedWords != null && info.TrainedWords.Count > 0) {
                    meta.TrainedWords = info.TrainedWords;
                }

                // Cache metadata
                await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(info), cancellationToken);

                // Download & cache remote thumbnail if no local image exists
                if (string.IsNullOrWhiteSpace(meta.ThumbnailPath) && !string.IsNullOrWhiteSpace(info.PreviewImageUrl)) {
                    await DownloadAndCacheThumbnailAsync(meta, info.PreviewImageUrl, cancellationToken);
                }

                OnLoraEnriched?.Invoke(meta);
                return info;
            }
        } catch {
            // Suppress lookup errors
        }

        return null;
    }

    private void LoadCachedCivitaiInfo(LoraMetadata meta) {
        if (string.IsNullOrWhiteSpace(meta.Sha256Hash)) {
            return;
        }

        string cacheFile = Path.Combine(_cacheDirectory, $"{meta.Sha256Hash}.json");
        if (File.Exists(cacheFile)) {
            try {
                string json = File.ReadAllText(cacheFile);
                var cached = JsonSerializer.Deserialize<CivitaiModelVersionInfo>(json);
                if (cached != null) {
                    meta.CivitaiInfo = cached;
                    if (cached.TrainedWords != null && cached.TrainedWords.Count > 0) {
                        meta.TrainedWords = cached.TrainedWords;
                    }
                    ApplyCachedThumbnail(meta);
                }
            } catch {
                // Ignore parse errors
            }
        }
    }

    private void ApplyCachedThumbnail(LoraMetadata meta) {
        if (!string.IsNullOrWhiteSpace(meta.ThumbnailPath)) {
            return;
        }

        if (!string.IsNullOrWhiteSpace(meta.Sha256Hash)) {
            string cachedThumb = Path.Combine(_cacheDirectory, $"{meta.Sha256Hash}.png");
            if (File.Exists(cachedThumb)) {
                meta.ThumbnailPath = cachedThumb;
            }
        }
    }

    private async Task DownloadAndCacheThumbnailAsync(LoraMetadata meta, string imageUrl, CancellationToken cancellationToken) {
        try {
            string destination = Path.Combine(_cacheDirectory, $"{meta.Sha256Hash}.png");
            byte[] imageBytes = await _httpClient.GetByteArrayAsync(imageUrl, cancellationToken);
            await File.WriteAllBytesAsync(destination, imageBytes, cancellationToken);
            meta.ThumbnailPath = destination;
        } catch {
            // Ignore thumbnail download errors
        }
    }
}
