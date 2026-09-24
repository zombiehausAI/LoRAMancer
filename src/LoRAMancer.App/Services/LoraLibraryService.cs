using System.Text.Json;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class LoraLibraryService {
    private readonly SafeTensorsMetadataReader _metadataReader;
    private readonly CivitaiService _civitaiService;
    private readonly SettingsService _settingsService;
    private readonly LoraDatabaseService _databaseService;
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly List<LoraMetadata> _items = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _enrichCts;

    public string RootFolder { get; private set; } = string.Empty;
    public string CurrentFolder { get; set; } = string.Empty;
    public bool IsScanning { get; private set; }
    public int ScannedCount { get; private set; }
    public int TotalFiles { get; private set; }
    public string CurrentScanningFile { get; private set; } = string.Empty;

    public bool IsEnriching { get; private set; }
    public int EnrichCompleted { get; private set; }
    public int EnrichTotal { get; private set; }
    public string CurrentEnrichingFile { get; private set; } = string.Empty;

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
    public event Action<bool>? OnEnrichStateChanged;
    public event Action<string, int, int>? OnEnrichProgress;
    public event Action<LoraMetadata>? OnLoraEnriched;

    public LoraLibraryService(
        SafeTensorsMetadataReader metadataReader,
        CivitaiService civitaiService,
        SettingsService settingsService,
        LoraDatabaseService? databaseService = null,
        HttpClient? httpClient = null
    ) {
        _metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        _civitaiService = civitaiService ?? throw new ArgumentNullException(nameof(civitaiService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _databaseService = databaseService ?? new LoraDatabaseService();
        _httpClient = httpClient ?? new HttpClient();

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string primaryCacheDir = Path.Combine(userProfile, ".LoRAMancer", "lora_cache");
        string legacyCacheDir = Path.Combine(userProfile, ".loramancer", "lora_cache");
        _cacheDirectory = Directory.Exists(primaryCacheDir) || !Directory.Exists(legacyCacheDir)
            ? primaryCacheDir
            : legacyCacheDir;
        Directory.CreateDirectory(_cacheDirectory);

        // Restore previously selected folder paths across sessions
        string savedRoot = _settingsService.Current.LoraStorageDirectory;
        if (!string.IsNullOrWhiteSpace(savedRoot) && Directory.Exists(savedRoot)) {
            RootFolder = savedRoot;
            string savedCurrent = _settingsService.Current.LastSubfolderPath;
            CurrentFolder = (!string.IsNullOrWhiteSpace(savedCurrent) && Directory.Exists(savedCurrent)) ? savedCurrent : savedRoot;
        }

        // Instant initialization from persistent SQLite library - no automatic rescanning!
        _ = LoadFromDatabaseAsync();
    }

    public async Task LoadFromDatabaseAsync() {
        try {
            var dbItems = await _databaseService.GetAllAsync();
            lock (_lock) {
                _items.Clear();
                _items.AddRange(dbItems);
            }
            OnLibraryUpdated?.Invoke();
        } catch {
            // Non-critical fallback
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
        _ = _databaseService.UpsertSingleAsync(meta);
        OnLibraryUpdated?.Invoke();
    }

    public async Task ToggleFavoriteAsync(LoraMetadata meta) {
        ArgumentNullException.ThrowIfNull(meta);
        meta.IsFavorite = !meta.IsFavorite;
        await _databaseService.SetFavoriteAsync(meta.FilePath, meta.IsFavorite);
        OnLibraryUpdated?.Invoke();
    }

    public async Task SetUserBaseModelAsync(LoraMetadata meta, string? baseModel) {
        ArgumentNullException.ThrowIfNull(meta);
        meta.UserBaseModel = string.IsNullOrWhiteSpace(baseModel) ? null : baseModel.Trim();
        await _databaseService.SetUserBaseModelAsync(meta.FilePath, meta.UserBaseModel);
        OnLibraryUpdated?.Invoke();
    }

    public async Task RefreshSingleLoraAsync(LoraMetadata meta) {
        ArgumentNullException.ThrowIfNull(meta);
        if (File.Exists(meta.FilePath)) {
            try {
                LoraMetadata updated = await _metadataReader.ReadMetadataAsync(meta.FilePath);
                updated.IsFavorite = meta.IsFavorite;
                updated.UserBaseModel = meta.UserBaseModel;
                updated.CivitaiInfo = meta.CivitaiInfo;
                updated.LastModifiedUtc = File.GetLastWriteTimeUtc(meta.FilePath);
                FindLocalThumbnail(updated);
                LoadCachedCivitaiInfo(updated);
                await _databaseService.UpsertSingleAsync(updated);
                AddOrUpdateLora(updated);
            } catch {
                // Ignore single file parse errors
            }
        }
    }

    public void StartScan(string directoryPath, bool forceClear = false) {
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
        CurrentScanningFile = "Indexing directory files...";
        OnScanStateChanged?.Invoke(true);

        _ = Task.Run(async () => {
            try {
                var signatures = await _databaseService.GetFileSignaturesAsync();
                string[] files = Directory.GetFiles(directoryPath, "*.safetensors", SearchOption.AllDirectories);
                TotalFiles = files.Length;

                List<LoraMetadata> batchToSave = new();

                for (int i = 0; i < files.Length; i++) {
                    if (token.IsCancellationRequested) {
                        break;
                    }

                    string file = files[i];
                    CurrentScanningFile = Path.GetFileName(file);
                    ScannedCount = i + 1;
                    OnScanProgress?.Invoke(CurrentScanningFile, ScannedCount, TotalFiles);

                    try {
                        DateTime diskTime = File.GetLastWriteTimeUtc(file);
                        long diskSize = new FileInfo(file).Length;

                        // Smart Differential Scan: Skip disk header parsing if already indexed & unchanged
                        if (signatures.TryGetValue(file, out var sig) && sig.LastModified == diskTime && sig.Size == diskSize) {
                            lock (_lock) {
                                if (!_items.Any(x => x.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase))) {
                                    // Item in DB but not yet in memory
                                    var existing = _items.FirstOrDefault(x => x.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase));
                                    if (existing == null) {
                                        // Will be populated from DB reload at completion
                                    }
                                }
                            }
                            continue;
                        }

                        LoraMetadata meta = await _metadataReader.ReadMetadataAsync(file, token);
                        meta.LastModifiedUtc = diskTime;
                        FindLocalThumbnail(meta);
                        LoadCachedCivitaiInfo(meta);

                        // Preserve existing user customizations if present
                        lock (_lock) {
                            var prev = _items.FirstOrDefault(x => x.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase));
                            if (prev != null) {
                                meta.IsFavorite = prev.IsFavorite;
                                meta.UserBaseModel = prev.UserBaseModel;
                                if (meta.CivitaiInfo == null && prev.CivitaiInfo != null) {
                                    meta.CivitaiInfo = prev.CivitaiInfo;
                                }
                            }
                        }

                        batchToSave.Add(meta);

                        lock (_lock) {
                            int existingIndex = _items.FindIndex(x => x.FilePath.Equals(meta.FilePath, StringComparison.OrdinalIgnoreCase));
                            if (existingIndex >= 0) {
                                _items[existingIndex] = meta;
                            } else {
                                _items.Add(meta);
                            }
                        }

                        if (batchToSave.Count >= 25) {
                            await _databaseService.UpsertBatchAsync(batchToSave);
                            batchToSave.Clear();
                            OnLibraryUpdated?.Invoke();
                        }
                    } catch {
                        // Ignore corrupt or non-LoRA safetensors
                    }
                }

                if (batchToSave.Count > 0) {
                    await _databaseService.UpsertBatchAsync(batchToSave);
                    batchToSave.Clear();
                }

                // Clean up any files that were deleted from disk
                await _databaseService.DeleteMissingInFolderAsync(directoryPath, files);
                await LoadFromDatabaseAsync();
            } catch (OperationCanceledException) {
                // Background scan stopped
            } catch {
                // Suppress unexpected scan errors
            } finally {
                IsScanning = false;
                OnScanStateChanged?.Invoke(false);
                OnLibraryUpdated?.Invoke();
            }
        }, token);
    }

    public void CancelScan() {
        _scanCts?.Cancel();
    }

    public void StartBackgroundEnrichment(IEnumerable<LoraMetadata>? targetItems = null, bool overwriteExisting = false) {
        if (IsEnriching) {
            return;
        }

        List<LoraMetadata> queue;
        lock (_lock) {
            var source = targetItems ?? _items;
            queue = source
                .Where(x => overwriteExisting || x.CivitaiInfo == null)
                .ToList();
        }

        if (queue.Count == 0) {
            return;
        }

        _enrichCts?.Cancel();
        _enrichCts?.Dispose();
        _enrichCts = new CancellationTokenSource();
        CancellationToken token = _enrichCts.Token;

        IsEnriching = true;
        EnrichTotal = queue.Count;
        EnrichCompleted = 0;
        CurrentEnrichingFile = queue[0].FileName;
        OnEnrichStateChanged?.Invoke(true);

        _ = Task.Run(async () => {
            try {
                for (int i = 0; i < queue.Count; i++) {
                    if (token.IsCancellationRequested) {
                        break;
                    }

                    var lora = queue[i];
                    CurrentEnrichingFile = lora.FileName;
                    OnEnrichProgress?.Invoke(lora.FileName, EnrichCompleted, EnrichTotal);

                    try {
                        await EnrichFromCivitaiAsync(lora, token);
                    } catch {
                        // Suppress per-file Civitai lookup errors so the queue continues
                    }

                    EnrichCompleted = i + 1;
                    OnEnrichProgress?.Invoke(lora.FileName, EnrichCompleted, EnrichTotal);

                    // Respect rate limiting: short pause between requests
                    try {
                        await Task.Delay(200, token);
                    } catch (OperationCanceledException) {
                        break;
                    }
                }
            } catch (OperationCanceledException) {
                // Background enrichment stopped by user
            } catch {
                // Suppress background errors
            } finally {
                IsEnriching = false;
                OnEnrichStateChanged?.Invoke(false);
                OnLibraryUpdated?.Invoke();
            }
        }, token);
    }

    public void CancelEnrichment() {
        _enrichCts?.Cancel();
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
                meta.LastModifiedUtc = File.GetLastWriteTimeUtc(file);
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
                    await _databaseService.UpsertSingleAsync(meta);
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

                // Cache metadata on disk and in SQLite
                await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(info), cancellationToken);

                // Download & cache remote thumbnail if no local image exists
                if (string.IsNullOrWhiteSpace(meta.ThumbnailPath) && !string.IsNullOrWhiteSpace(info.PreviewImageUrl)) {
                    await DownloadAndCacheThumbnailAsync(meta, info.PreviewImageUrl, cancellationToken);
                }

                await _databaseService.UpsertSingleAsync(meta);
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
