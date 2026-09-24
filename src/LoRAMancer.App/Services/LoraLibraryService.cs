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
    private readonly List<LoraMetadata> _items = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _scanCts;

    public string RootFolder { get; private set; } = string.Empty;
    public string CurrentFolder { get; set; } = string.Empty;
    public bool IsScanning { get; private set; }
    public int ScannedCount { get; private set; }
    public int TotalFiles { get; private set; }
    public string CurrentScanningFile { get; private set; } = string.Empty;

    public IReadOnlyList<LoraMetadata> Items {
        get {
            lock (_lock) {
                return _items.ToList();
            }
        }
    }

    public event Action? OnLibraryUpdated;
    public event Action<string, int, int>? OnScanProgress;
    public event Action<bool>? OnScanStateChanged;
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

        // Restore previously selected folder across sessions
        string savedRoot = _settingsService.Current.LoraStorageDirectory;
        if (!string.IsNullOrWhiteSpace(savedRoot) && Directory.Exists(savedRoot)) {
            RootFolder = savedRoot;
            string savedCurrent = _settingsService.Current.LastSubfolderPath;
            CurrentFolder = (!string.IsNullOrWhiteSpace(savedCurrent) && Directory.Exists(savedCurrent)) ? savedCurrent : savedRoot;
            StartScan(savedRoot, forceClear: false);
        }
    }

    public void SetFolders(string rootFolder, string? currentFolder = null) {
        RootFolder = rootFolder;
        CurrentFolder = currentFolder ?? rootFolder;
        _settingsService.Current.LoraStorageDirectory = rootFolder;
        _settingsService.Current.LastSubfolderPath = CurrentFolder;
        _ = _settingsService.SaveSettingsAsync(_settingsService.Current);
    }

    public void SetCurrentSubfolder(string currentFolder) {
        CurrentFolder = currentFolder;
        _settingsService.Current.LastSubfolderPath = currentFolder;
        _ = _settingsService.SaveSettingsAsync(_settingsService.Current);
        OnLibraryUpdated?.Invoke();
    }

    public void AddOrUpdateLora(LoraMetadata meta) {
        lock (_lock) {
            _items.RemoveAll(x => x.FilePath.Equals(meta.FilePath, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, meta);
        }
        OnLibraryUpdated?.Invoke();
    }

    public void StartScan(string directoryPath, bool forceClear = true) {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!Directory.Exists(directoryPath)) {
            return;
        }

        SetFolders(directoryPath, CurrentFolder.StartsWith(directoryPath, StringComparison.OrdinalIgnoreCase) ? CurrentFolder : directoryPath);

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        CancellationToken token = _scanCts.Token;

        if (forceClear) {
            lock (_lock) {
                _items.Clear();
            }
            OnLibraryUpdated?.Invoke();
        }

        IsScanning = true;
        ScannedCount = 0;
        TotalFiles = 0;
        CurrentScanningFile = "Discovering files recursively...";
        OnScanStateChanged?.Invoke(true);

        _ = Task.Run(async () => {
            try {
                await ScanDirectoryStreamAsync(
                    directoryPath,
                    meta => {
                        lock (_lock) {
                            int existingIndex = _items.FindIndex(x => x.FilePath.Equals(meta.FilePath, StringComparison.OrdinalIgnoreCase));
                            if (existingIndex >= 0) {
                                _items[existingIndex] = meta;
                            } else {
                                _items.Add(meta);
                            }
                        }
                        OnLibraryUpdated?.Invoke();
                        return Task.CompletedTask;
                    },
                    (file, current, total) => {
                        CurrentScanningFile = file;
                        ScannedCount = current;
                        TotalFiles = total;
                        OnScanProgress?.Invoke(file, current, total);
                    },
                    token
                );
            } catch (OperationCanceledException) {
                // Background scan stopped
            } catch {
                // Suppress unexpected scan errors
            } finally {
                IsScanning = false;
                OnScanStateChanged?.Invoke(false);
            }
        }, token);
    }

    public void CancelScan() {
        _scanCts?.Cancel();
    }

    public async Task<int> ScanDirectoryStreamAsync(
        string directoryPath,
        Func<LoraMetadata, Task> onItemDiscovered,
        Action<string, int, int>? onProgress = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!Directory.Exists(directoryPath)) {
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
        }

        string[] files = Directory.GetFiles(directoryPath, "*.safetensors", SearchOption.AllDirectories);
        int discoveredCount = 0;

        for (int i = 0; i < files.Length; i++) {
            if (cancellationToken.IsCancellationRequested) {
                break;
            }

            string file = files[i];

            try {
                LoraMetadata meta = await _metadataReader.ReadMetadataAsync(file, cancellationToken);
                FindLocalThumbnail(meta);
                LoadCachedCivitaiInfo(meta);
                discoveredCount++;
                if (onItemDiscovered != null) {
                    await onItemDiscovered(meta);
                }
            } catch {
                // Ignore corrupt or non-LoRA safetensors files
            } finally {
                onProgress?.Invoke(Path.GetFileName(file), i + 1, files.Length);
            }
        }

        return discoveredCount;
    }

    public async Task<List<LoraMetadata>> ScanDirectoryAsync(
        string directoryPath,
        Action<string, int, int>? onProgress = null,
        CancellationToken cancellationToken = default
    ) {
        List<LoraMetadata> result = new();
        await ScanDirectoryStreamAsync(
            directoryPath,
            meta => {
                result.Add(meta);
                return Task.CompletedTask;
            },
            onProgress,
            cancellationToken
        );
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
            using HttpRequestMessage request = new(HttpMethod.Get, imageUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "LoRAMancer/1.0 (Windows NT 10.0; Win64; x64)");

            string apiKey = _settingsService.Current.CivitaiApiKey;
            if (!string.IsNullOrWhiteSpace(apiKey) && imageUrl.Contains("civitai", StringComparison.OrdinalIgnoreCase)) {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) {
                byte[] imageBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                await File.WriteAllBytesAsync(destination, imageBytes, cancellationToken);
                meta.ThumbnailPath = destination;
            }
        } catch {
            // Ignore thumbnail download errors
        }
    }
}
