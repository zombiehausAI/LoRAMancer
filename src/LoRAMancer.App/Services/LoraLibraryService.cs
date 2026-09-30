using System.Text.Json;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class LoraLibraryService {
    private readonly SafeTensorsMetadataReader _metadataReader;
    private readonly CivitaiService _civitaiService;
    private readonly LoraMetadataAggregatorService _aggregatorService;
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

    private readonly List<LoraLibrary> _libraries = new();
    public IReadOnlyList<LoraLibrary> Libraries {
        get {
            lock (_lock) {
                return _libraries.ToList();
            }
        }
    }
    public LoraLibrary? ActiveLibrary { get; private set; }

    public IReadOnlyList<LoraMetadata> Items {
        get {
            lock (_lock) {
                return _items.ToList();
            }
        }
    }

    private readonly List<LoraCategory> _categories = new();
    public IReadOnlyList<LoraCategory> Categories {
        get {
            lock (_lock) {
                return _categories.ToList();
            }
        }
    }

    private readonly List<LoraCollection> _collections = new();
    public IReadOnlyList<LoraCollection> Collections {
        get {
            lock (_lock) {
                return _collections.ToList();
            }
        }
    }

    public event Action? OnLibraryUpdated;
    public event Action? OnLibrariesChanged;
    public event Action? OnCategoriesChanged;
    public event Action? OnCollectionsChanged;
    public event Action<string, int, int>? OnScanProgress;
    public event Action<bool>? OnScanStateChanged;
    public event Action<bool>? OnEnrichStateChanged;
    public event Action<string, int, int>? OnEnrichProgress;
    public event Action<LoraMetadata>? OnLoraEnriched;

    public LoraMetadataAggregatorService Aggregator => _aggregatorService;

    public LoraLibraryService(
        SafeTensorsMetadataReader metadataReader,
        CivitaiService civitaiService,
        SettingsService settingsService,
        LoraDatabaseService? databaseService = null,
        HttpClient? httpClient = null,
        LoraMetadataAggregatorService? aggregatorService = null
    ) {
        _metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        _civitaiService = civitaiService ?? throw new ArgumentNullException(nameof(civitaiService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _databaseService = databaseService ?? new LoraDatabaseService();
        _httpClient = httpClient ?? new HttpClient();
        _aggregatorService = aggregatorService ?? new LoraMetadataAggregatorService(
            _civitaiService,
            new HuggingFaceService(_settingsService, _httpClient),
            new DanbooruTagService(_httpClient),
            _settingsService,
            _httpClient
        );

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

        // Instant initialization of libraries from persistent SQLite library - no automatic rescanning!
        _ = InitializeLibrariesAsync();
    }

    public async Task InitializeLibrariesAsync() {
        try {
            var dbLibs = await _databaseService.GetLibrariesAsync();
            var dbCats = await _databaseService.GetCategoriesAsync();
            var dbCols = await _databaseService.GetCollectionsAsync();
            lock (_lock) {
                _libraries.Clear();
                _libraries.AddRange(dbLibs);
                _categories.Clear();
                _categories.AddRange(dbCats);
                _collections.Clear();
                _collections.AddRange(dbCols);
            }
            OnCategoriesChanged?.Invoke();
            OnCollectionsChanged?.Invoke();

            if (_libraries.Count == 0) {
                string defaultPath = !string.IsNullOrWhiteSpace(RootFolder) && Directory.Exists(RootFolder)
                    ? RootFolder
                    : (!string.IsNullOrWhiteSpace(_settingsService.Current.LoraStorageDirectory) && Directory.Exists(_settingsService.Current.LoraStorageDirectory)
                        ? _settingsService.Current.LoraStorageDirectory
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "models", "loras"));

                var defaultLib = new LoraLibrary {
                    Id = "default",
                    Name = "Main Library",
                    FolderPath = defaultPath,
                    Description = "Primary LoRA collection",
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                await _databaseService.UpsertLibraryAsync(defaultLib);
                lock (_lock) {
                    _libraries.Add(defaultLib);
                }
            } else {
                var defaultLib = _libraries.FirstOrDefault(l => string.Equals(l.Id, "default", StringComparison.OrdinalIgnoreCase));
                if (defaultLib != null && (!Directory.Exists(defaultLib.FolderPath) || string.IsNullOrWhiteSpace(defaultLib.FolderPath)) && !string.IsNullOrWhiteSpace(_settingsService.Current.LoraStorageDirectory) && Directory.Exists(_settingsService.Current.LoraStorageDirectory)) {
                    defaultLib.FolderPath = _settingsService.Current.LoraStorageDirectory;
                    await _databaseService.UpsertLibraryAsync(defaultLib);
                }
            }

            string savedActiveId = _settingsService.Current.LastActiveLibraryId;
            var targetLib = _libraries.FirstOrDefault(l => string.Equals(l.Id, savedActiveId, StringComparison.OrdinalIgnoreCase))
                ?? _libraries.FirstOrDefault();

            if (targetLib != null) {
                await SwitchLibraryAsync(targetLib.Id);
            }
        } catch {
            await LoadFromDatabaseAsync();
        }
    }

    public async Task SwitchLibraryAsync(string libraryId) {
        LoraLibrary? target;
        lock (_lock) {
            target = _libraries.FirstOrDefault(l => string.Equals(l.Id, libraryId, StringComparison.OrdinalIgnoreCase));
        }

        if (target == null) {
            return;
        }

        ActiveLibrary = target;
        RootFolder = target.FolderPath;
        CurrentFolder = target.FolderPath;
        _settingsService.Current.LastActiveLibraryId = target.Id;
        _settingsService.Current.LoraStorageDirectory = target.FolderPath;
        _settingsService.Current.LastSubfolderPath = target.FolderPath;
        _ = _settingsService.SaveSettingsAsync(_settingsService.Current);

        await LoadFromDatabaseAsync();
        await RefreshLibrariesAsync();
    }

    public async Task RefreshLibrariesAsync() {
        try {
            var dbLibs = await _databaseService.GetLibrariesAsync();
            lock (_lock) {
                _libraries.Clear();
                _libraries.AddRange(dbLibs);
                if (ActiveLibrary != null) {
                    var updatedActive = _libraries.FirstOrDefault(l => string.Equals(l.Id, ActiveLibrary.Id, StringComparison.OrdinalIgnoreCase));
                    if (updatedActive != null) {
                        ActiveLibrary = updatedActive;
                    }
                }
            }
            OnLibrariesChanged?.Invoke();
        } catch {
            // Suppress background reload errors
        }
    }

    public async Task<LoraLibrary> CreateLibraryAsync(string name, string folderPath, string? description = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var lib = new LoraLibrary {
            Id = "lib-" + Guid.NewGuid().ToString("N")[..8],
            Name = name.Trim(),
            FolderPath = folderPath.Trim(),
            Description = description?.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        await _databaseService.UpsertLibraryAsync(lib);
        await RefreshLibrariesAsync();
        return _libraries.FirstOrDefault(l => string.Equals(l.Id, lib.Id, StringComparison.OrdinalIgnoreCase)) ?? lib;
    }

    public async Task UpdateLibraryAsync(LoraLibrary library) {
        ArgumentNullException.ThrowIfNull(library);
        library.UpdatedAtUtc = DateTime.UtcNow;
        await _databaseService.UpsertLibraryAsync(library);
        if (ActiveLibrary != null && string.Equals(ActiveLibrary.Id, library.Id, StringComparison.OrdinalIgnoreCase)) {
            ActiveLibrary = library;
            RootFolder = library.FolderPath;
        }
        await RefreshLibrariesAsync();
    }

    public async Task DeleteLibraryAsync(string libraryId) {
        await _databaseService.DeleteLibraryAsync(libraryId);
        await RefreshLibrariesAsync();

        if (ActiveLibrary != null && string.Equals(ActiveLibrary.Id, libraryId, StringComparison.OrdinalIgnoreCase)) {
            var fallback = _libraries.FirstOrDefault();
            if (fallback != null) {
                await SwitchLibraryAsync(fallback.Id);
            } else {
                ActiveLibrary = null;
                await LoadFromDatabaseAsync();
            }
        }
    }

    public async Task LoadFromDatabaseAsync() {
        try {
            List<LoraMetadata> dbItems;
            if (ActiveLibrary != null) {
                dbItems = await _databaseService.GetByLibraryAsync(ActiveLibrary.Id, ActiveLibrary.FolderPath);
            } else if (!string.IsNullOrWhiteSpace(RootFolder)) {
                dbItems = await _databaseService.GetByLibraryAsync(null, RootFolder);
            } else {
                dbItems = await _databaseService.GetAllAsync();
            }

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

        if (ActiveLibrary != null && !string.Equals(ActiveLibrary.FolderPath, rootFolder, StringComparison.OrdinalIgnoreCase)) {
            ActiveLibrary.FolderPath = rootFolder;
            _ = Task.Run(async () => {
                try {
                    await _databaseService.UpsertLibraryAsync(ActiveLibrary);
                    await RefreshLibrariesAsync();
                } catch {
                    // Suppress background save errors
                }
            });
        }
    }

    public void SetCurrentSubfolder(string currentFolder) {
        CurrentFolder = currentFolder;
        _settingsService.Current.LastSubfolderPath = currentFolder;
        _ = _settingsService.SaveSettingsAsync(_settingsService.Current);
        OnLibraryUpdated?.Invoke();
    }

    public void AddOrUpdateLora(LoraMetadata meta) {
        if (string.IsNullOrWhiteSpace(meta.LibraryId) && ActiveLibrary != null) {
            meta.LibraryId = ActiveLibrary.Id;
        }
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

    public async Task SetLoraCategoryAsync(LoraMetadata meta, string? category) {
        ArgumentNullException.ThrowIfNull(meta);
        meta.Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        await _databaseService.SetLoraCategoryAsync(meta.FilePath, meta.Category);
        OnLibraryUpdated?.Invoke();
    }

    public async Task SetLoraTagsAsync(LoraMetadata meta, IEnumerable<string> tags) {
        ArgumentNullException.ThrowIfNull(meta);
        meta.Tags = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        await _databaseService.SetLoraTagsAsync(meta.FilePath, meta.Tags);
        OnLibraryUpdated?.Invoke();
    }

    public async Task SetLoraCategoryAndTagsAsync(LoraMetadata meta, string? category, IEnumerable<string> tags) {
        ArgumentNullException.ThrowIfNull(meta);
        meta.Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        meta.Tags = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        await _databaseService.SetLoraCategoryAndTagsAsync(meta.FilePath, meta.Category, meta.Tags);
        OnLibraryUpdated?.Invoke();
    }

    public async Task<List<LoraCategory>> LoadCategoriesAsync() {
        var cats = await _databaseService.GetCategoriesAsync();
        lock (_lock) {
            _categories.Clear();
            _categories.AddRange(cats);
        }
        OnCategoriesChanged?.Invoke();
        return cats;
    }

    public async Task SaveCategoryAsync(LoraCategory category) {
        ArgumentNullException.ThrowIfNull(category);
        await _databaseService.SaveCategoryAsync(category);
        await LoadCategoriesAsync();
    }

    public async Task DeleteCategoryAsync(string id, string? reassignTo = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await _databaseService.DeleteCategoryAsync(id, reassignTo);
        await LoadCategoriesAsync();
        // Refresh currently loaded items from DB to reflect reassigned categories
        await LoadFromDatabaseAsync();
    }

    public async Task<List<LoraCollection>> LoadCollectionsAsync() {
        var cols = await _databaseService.GetCollectionsAsync();
        lock (_lock) {
            _collections.Clear();
            _collections.AddRange(cols);
        }
        OnCollectionsChanged?.Invoke();
        return cols;
    }

    public async Task<LoraCollection> CreateCollectionAsync(string name, string? description = null, string? color = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var col = new LoraCollection {
            Id = "col-" + Guid.NewGuid().ToString("N")[..8],
            Name = name.Trim(),
            Description = description?.Trim(),
            Color = color?.Trim() ?? "#cba6f7",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await _databaseService.UpsertCollectionAsync(col);
        await LoadCollectionsAsync();
        return col;
    }

    public async Task UpdateCollectionAsync(LoraCollection collection) {
        ArgumentNullException.ThrowIfNull(collection);
        collection.UpdatedAtUtc = DateTime.UtcNow;
        await _databaseService.UpsertCollectionAsync(collection);
        await LoadCollectionsAsync();
    }

    public async Task DeleteCollectionAsync(string id) {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await _databaseService.DeleteCollectionAsync(id);
        await LoadCollectionsAsync();
    }

    public async Task AddLoraToCollectionAsync(string collectionId, LoraMetadata lora) {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(lora);
        await AddLoraToCollectionAsync(collectionId, lora.FilePath);
    }

    public async Task AddLoraToCollectionAsync(string collectionId, string filePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await _databaseService.AddToCollectionAsync(collectionId, filePath);
        await LoadCollectionsAsync();
    }

    public async Task RemoveLoraFromCollectionAsync(string collectionId, LoraMetadata lora) {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentNullException.ThrowIfNull(lora);
        await RemoveLoraFromCollectionAsync(collectionId, lora.FilePath);
    }

    public async Task RemoveLoraFromCollectionAsync(string collectionId, string filePath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await _databaseService.RemoveFromCollectionAsync(collectionId, filePath);
        await LoadCollectionsAsync();
    }

    public async Task<List<string>> GetCollectionsForLoraAsync(LoraMetadata lora) {
        if (lora == null || string.IsNullOrWhiteSpace(lora.FilePath)) return new List<string>();
        return await GetCollectionsForLoraAsync(lora.FilePath);
    }

    public async Task<List<string>> GetCollectionsForLoraAsync(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) return new List<string>();
        return await _databaseService.GetCollectionsForLoraAsync(filePath);
    }

    public async Task<List<LoraMetadata>> GetCollectionLorasAsync(string collectionId) {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        return await _databaseService.GetCollectionLorasAsync(collectionId);
    }

    public async Task<string> MoveLoraToLibraryAsync(LoraMetadata lora, LoraLibrary targetLibrary) {
        ArgumentNullException.ThrowIfNull(lora);
        ArgumentNullException.ThrowIfNull(targetLibrary);

        string oldFilePath = lora.FilePath;
        string targetFolder = targetLibrary.FolderPath;

        string newFilePath = await _databaseService.MoveLoraFileAsync(oldFilePath, targetFolder, targetLibrary.Id);

        lora.FilePath = newFilePath;
        lora.FileName = Path.GetFileName(newFilePath);
        lora.LibraryId = targetLibrary.Id;
        FindLocalThumbnail(lora);

        lock (_lock) {
            int idx = _items.FindIndex(x => x.FilePath.Equals(oldFilePath, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) {
                _items[idx] = lora;
            }
        }

        await RefreshLibrariesAsync();
        OnLibraryUpdated?.Invoke();
        return newFilePath;
    }

    public async Task RefreshSingleLoraAsync(LoraMetadata meta) {
        ArgumentNullException.ThrowIfNull(meta);
        if (File.Exists(meta.FilePath)) {
            try {
                LoraMetadata updated = await _metadataReader.ReadMetadataAsync(meta.FilePath);
                updated.LibraryId = meta.LibraryId ?? ActiveLibrary?.Id;
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

                DateTime lastProgressReport = DateTime.MinValue;

                for (int i = 0; i < files.Length; i++) {
                    if (token.IsCancellationRequested) {
                        break;
                    }

                    string file = files[i];
                    CurrentScanningFile = Path.GetFileName(file);
                    ScannedCount = i + 1;

                    // Throttle scan progress events to at most once per 250ms to keep UI thread 100% fluid
                    if (i == 0 || i == files.Length - 1 || (DateTime.UtcNow - lastProgressReport).TotalMilliseconds >= 250) {
                        lastProgressReport = DateTime.UtcNow;
                        OnScanProgress?.Invoke(CurrentScanningFile, ScannedCount, TotalFiles);
                    }

                    // Periodically yield execution to keep background scanning truly non-blocking
                    if (i % 30 == 0) {
                        await Task.Delay(1, token);
                    }

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
                        meta.LibraryId = ActiveLibrary?.Id;
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
                await RefreshLibrariesAsync();
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

    public async Task<bool> AssignPreviewImageAsync(LoraMetadata meta, string imageSourcePath, bool copyAlongsideLora = true) {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageSourcePath);

        if (!File.Exists(imageSourcePath) || !File.Exists(meta.FilePath)) {
            return false;
        }

        string targetThumbnailPath = imageSourcePath;
        if (copyAlongsideLora) {
            string loraDir = Path.GetDirectoryName(meta.FilePath) ?? string.Empty;
            string loraBaseName = Path.GetFileNameWithoutExtension(meta.FilePath);
            string ext = Path.GetExtension(imageSourcePath);
            if (string.IsNullOrWhiteSpace(ext)) {
                ext = ".png";
            }
            string dest = Path.Combine(loraDir, $"{loraBaseName}.preview{ext}");
            File.Copy(imageSourcePath, dest, overwrite: true);
            targetThumbnailPath = dest;
        }

        meta.ThumbnailPath = targetThumbnailPath;
        await _databaseService.UpsertSingleAsync(meta);
        AddOrUpdateLora(meta);
        OnLibraryUpdated?.Invoke();
        return true;
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

            AggregatedLoraEnrichmentResult result = await _aggregatorService.EnrichAsync(meta, cancellationToken);
            if (meta.CivitaiInfo != null) {
                // Cache metadata on disk and in SQLite
                await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(meta.CivitaiInfo), cancellationToken);

                if (string.IsNullOrWhiteSpace(meta.ThumbnailPath) && !string.IsNullOrWhiteSpace(meta.CivitaiInfo.PreviewImageUrl)) {
                    await DownloadAndCacheThumbnailAsync(meta, meta.CivitaiInfo.PreviewImageUrl, cancellationToken);
                }

                await _databaseService.UpsertSingleAsync(meta);
                OnLoraEnriched?.Invoke(meta);
                return meta.CivitaiInfo;
            }
        } catch {
            // Suppress lookup errors
        }

        return null;
    }

    public async Task<AggregatedLoraEnrichmentResult> EnrichMultiProviderAsync(LoraMetadata meta, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(meta);

        if (string.IsNullOrWhiteSpace(meta.Sha256Hash) && File.Exists(meta.FilePath)) {
            try {
                meta.Sha256Hash = await _civitaiService.ComputeFileSha256Async(meta.FilePath, cancellationToken: cancellationToken);
            } catch {
                // Ignore hash computation failure
            }
        }

        AggregatedLoraEnrichmentResult result = await _aggregatorService.EnrichAsync(meta, cancellationToken);
        if (meta.CivitaiInfo != null && !string.IsNullOrWhiteSpace(meta.Sha256Hash)) {
            string cacheFile = Path.Combine(_cacheDirectory, $"{meta.Sha256Hash}.json");
            await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(meta.CivitaiInfo), cancellationToken);
        }

        await _databaseService.UpsertSingleAsync(meta);
        OnLoraEnriched?.Invoke(meta);
        return result;
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
