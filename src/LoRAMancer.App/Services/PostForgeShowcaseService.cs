using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class PostForgeShowcaseService {
    private readonly SettingsService _settingsService;
    private readonly LoraLibraryService? _loraLibraryService;
    private readonly LoraDatabaseService? _databaseService;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, ShowcaseMediaItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private List<ShowcaseMediaItem> _cachedSnapshot = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int count, DateTime timestamp)> _dirCountCache = new();
    private readonly List<ShowcaseLibrary> _libraries = new();
    private readonly List<string> _customTags = new();
    private readonly List<string> _customCategories = new();
    private readonly string _cacheFilePath;
    private CancellationTokenSource? _scanCts;
    private Task? _runningScanTask;

    public bool IsScanning { get; private set; }
    public string ScanCurrentFile { get; private set; } = string.Empty;
    public int ScanCurrentIndex { get; private set; }
    public int ScanTotalCount { get; private set; }

    public event Action? OnShowcaseUpdated;
    public event Action<string, int, int>? OnScanProgress;
    public event Action? OnScanCompleted;

    public IReadOnlyList<ShowcaseMediaItem> Items => _cachedSnapshot;

    public IReadOnlyList<ShowcaseLibrary> Libraries => _libraries;
    public IReadOnlyList<string> CustomTags => _customTags;
    public IReadOnlyList<string> CustomCategories => _customCategories;
    public LoraLibraryService? LibraryService => _loraLibraryService;

    public PostForgeShowcaseService(SettingsService settingsService, LoraLibraryService? loraLibraryService = null, LoraDatabaseService? databaseService = null) {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _loraLibraryService = loraLibraryService;
        _databaseService = databaseService;
        _cacheFilePath = Path.Combine(_settingsService.SettingsDirectory, "showcase_cache.json");

        InitializeDefaults();
        _ = LoadCacheAsync();
    }

    private void InitializeDefaults() {
        var settings = _settingsService.Current;
        if (settings.ShowcaseCustomTags?.Count > 0) {
            _customTags.AddRange(settings.ShowcaseCustomTags);
        } else {
            _customTags.AddRange(new[] { "Favorites", "Best Likeness", "Experimental", "Overbaked", "Keeper", "Wallpaper", "Fix Hands" });
        }

        if (settings.ShowcaseCustomCategories?.Count > 0) {
            _customCategories.AddRange(settings.ShowcaseCustomCategories);
        } else {
            _customCategories.AddRange(new[] { "Portraits", "Styles", "Landscapes", "Characters", "Objects", "Concepts", "Animations", "Soundscapes" });
        }
    }

    public async Task LoadCacheAsync(CancellationToken cancellationToken = default) {
        await _lock.WaitAsync(cancellationToken);
        try {
            bool loadedFromDb = false;
            if (_databaseService != null) {
                try {
                    var dbItems = await _databaseService.GetAllGalleryMediaAsync(cancellationToken);
                    if (dbItems.Count > 0) {
                        lock (_items) {
                            _items.Clear();
                            foreach (var item in dbItems) {
                                if (File.Exists(item.FilePath)) {
                                    _items[item.FilePath] = item;
                                }
                            }
                        }
                        UpdateSnapshot();
                        loadedFromDb = true;
                    }
                } catch {
                    // Fallback to json cache if db query fails
                }
            }

            if (!loadedFromDb && File.Exists(_cacheFilePath)) {
                try {
                    string json = await File.ReadAllTextAsync(_cacheFilePath, cancellationToken);
                    var cached = JsonSerializer.Deserialize<List<ShowcaseMediaItem>>(json, new JsonSerializerOptions {
                        PropertyNameCaseInsensitive = true
                    });
                    if (cached != null) {
                        lock (_items) {
                            _items.Clear();
                            foreach (var item in cached) {
                                if (File.Exists(item.FilePath)) {
                                    _items[item.FilePath] = item;
                                }
                            }
                        }
                        UpdateSnapshot();

                        // Migrate JSON cache into SQLite
                        if (_databaseService != null && _items.Count > 0) {
                            _ = _databaseService.UpsertGalleryMediaBatchAsync(_items.Values.ToList(), CancellationToken.None);
                        }
                    }
                } catch {
                    // Ignore cache read failures
                }
            }
        } finally {
            _lock.Release();
        }
        OnShowcaseUpdated?.Invoke();
    }

    public async Task SaveCacheAsync(CancellationToken cancellationToken = default) {
        await _lock.WaitAsync(cancellationToken);
        try {
            List<ShowcaseMediaItem> snapshot;
            lock (_items) {
                snapshot = _items.Values.ToList();
            }

            if (_databaseService != null && snapshot.Count > 0) {
                try {
                    await _databaseService.UpsertGalleryMediaBatchAsync(snapshot, cancellationToken);
                } catch {
                    // Ignore db write errors
                }
            }

            string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(_cacheFilePath, json, cancellationToken);
        } catch {
            // Ignore cache write errors
        } finally {
            _lock.Release();
        }
    }

    public List<string> GetConfiguredDirectories() {
        var settings = _settingsService.Current;
        if (settings.ShowcaseDirectories == null) {
            settings.ShowcaseDirectories = new List<string>();
        }

        // Initialize default outputs on first run if list is empty
        if (settings.ShowcaseDirectories.Count == 0) {
            string repoOutputs = Path.Combine(Directory.GetCurrentDirectory(), "outputs");
            if (Directory.Exists(repoOutputs)) {
                settings.ShowcaseDirectories.Add(repoOutputs);
            }
            string appOutputs = Path.Combine(AppContext.BaseDirectory, "outputs");
            if (Directory.Exists(appOutputs) && !settings.ShowcaseDirectories.Contains(appOutputs, StringComparer.OrdinalIgnoreCase)) {
                settings.ShowcaseDirectories.Add(appOutputs);
            }
            if (settings.ShowcaseDirectories.Count > 0) {
                _ = _settingsService.SaveSettingsAsync(settings);
            }
        }

        return settings.ShowcaseDirectories
            .Where(d => Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task AddFolderAsync(string directoryPath, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) {
            return;
        }

        var settings = _settingsService.Current;
        if (settings.ShowcaseDirectories == null) {
            settings.ShowcaseDirectories = new List<string>();
        }

        if (!settings.ShowcaseDirectories.Contains(directoryPath, StringComparer.OrdinalIgnoreCase)) {
            settings.ShowcaseDirectories.Add(directoryPath);
            await _settingsService.SaveSettingsAsync(settings);
        }

        _ = StartBackgroundScanAsync();
    }

    public async Task RemoveFolderAsync(string directoryPath, CancellationToken cancellationToken = default) {
        var settings = _settingsService.Current;
        if (settings.ShowcaseDirectories.RemoveAll(d => string.Equals(d, directoryPath, StringComparison.OrdinalIgnoreCase)) > 0) {
            await _settingsService.SaveSettingsAsync(settings);
        }

        lock (_items) {
            var toRemove = _items.Keys.Where(k => k.StartsWith(directoryPath, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var k in toRemove) {
                _items.Remove(k);
            }
        }

        if (_databaseService != null) {
            try {
                await _databaseService.RemoveGalleryMediaByFolderAsync(directoryPath, cancellationToken);
            } catch { }
        }

        UpdateSnapshot();
        await SaveCacheAsync(cancellationToken);
        OnShowcaseUpdated?.Invoke();
    }

    /// <summary>
    /// Starts a non-blocking background scan of all configured media directories.
    /// Runs out-of-process from the UI thread with cancellation and throttled progress reporting.
    /// </summary>
    public Task StartBackgroundScanAsync() {
        if (IsScanning) {
            return _runningScanTask ?? Task.CompletedTask;
        }

        IsScanning = true;
        ScanCurrentIndex = 0;
        ScanTotalCount = 0;
        ScanCurrentFile = string.Empty;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        _runningScanTask = Task.Run(async () => {
            OnScanProgress?.Invoke(string.Empty, 0, 0);

            try {
                await ScanAllAsync(null, token);
            } catch (OperationCanceledException) {
                // Background scan safely cancelled
            } catch {
                // Absorb error to protect background loop
            } finally {
                IsScanning = false;
                OnScanCompleted?.Invoke();
                OnShowcaseUpdated?.Invoke();
            }
        });

        return _runningScanTask;
    }

    /// <summary>
    /// Cancels any currently running background scan.
    /// </summary>
    public void CancelScan() {
        try {
            _scanCts?.Cancel();
        } catch { }
    }

    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase) {
        ".git", ".github", ".vs", "bin", "obj", "node_modules", ".venv", "venv", "__pycache__", ".cache"
    };

    private static IEnumerable<string> EnumerateMediaFilesSafely(string rootDir) {
        EnumerationOptions options = new() {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };

        Queue<string> dirsToVisit = new();
        dirsToVisit.Enqueue(rootDir);

        while (dirsToVisit.Count > 0) {
            string currentDir = dirsToVisit.Dequeue();

            IEnumerable<string> files;
            try {
                files = Directory.EnumerateFiles(currentDir, "*.*", options);
            } catch {
                continue;
            }

            foreach (string file in files) {
                if (MediaMetadataExtractor.IsSupportedMedia(Path.GetExtension(file))) {
                    yield return file;
                }
            }

            IEnumerable<string> subDirs;
            try {
                subDirs = Directory.EnumerateDirectories(currentDir, "*", options);
            } catch {
                continue;
            }

            foreach (string subDir in subDirs) {
                string name = Path.GetFileName(subDir);
                if (!ExcludedFolderNames.Contains(name) && !name.StartsWith('.')) {
                    dirsToVisit.Enqueue(subDir);
                }
            }
        }
    }

    public async Task ScanAllAsync(Action<string, int, int>? onProgress = null, CancellationToken cancellationToken = default) {
        if (_items.Count == 0 && _databaseService != null) {
            try {
                await LoadCacheAsync(cancellationToken);
            } catch { }
        }

        var dirs = GetConfiguredDirectories();
        List<string> candidateFiles = new();
        HashSet<string> candidateFilesSet = new(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs) {
            if (!Directory.Exists(dir)) {
                continue;
            }

            try {
                foreach (string file in EnumerateMediaFilesSafely(dir)) {
                    if (candidateFilesSet.Add(file)) {
                        candidateFiles.Add(file);
                    }
                }
            } catch {
                // Ignore inaccessible directories
            }
        }

        int total = candidateFiles.Count;
        ScanTotalCount = total;
        ScanCurrentIndex = 0;

        // Identify files that are not yet cached
        List<string> filesNeedingExtraction = new();
        lock (_items) {
            foreach (var file in candidateFiles) {
                if (!_items.ContainsKey(file)) {
                    filesNeedingExtraction.Add(file);
                }
            }
        }

        int current = total - filesNeedingExtraction.Count;
        ScanCurrentIndex = current;
        DateTime lastProgressReport = DateTime.MinValue;

        // Perform parallel metadata extraction for newly discovered media files
        if (filesNeedingExtraction.Count > 0) {
            int maxConcurrency = Math.Clamp(Environment.ProcessorCount, 4, 16);
            using SemaphoreSlim throttler = new(maxConcurrency, maxConcurrency);
            List<ShowcaseMediaItem> incrementalBatch = new();
            object batchLock = new();

            var tasks = filesNeedingExtraction.Select(async file => {
                await throttler.WaitAsync(cancellationToken);
                try {
                    cancellationToken.ThrowIfCancellationRequested();
                    var extracted = await MediaMetadataExtractor.ExtractAsync(file, cancellationToken);

                    lock (_items) {
                        _items[file] = extracted;
                    }

                    int cur = Interlocked.Increment(ref current);
                    ScanCurrentIndex = cur;
                    ScanCurrentFile = Path.GetFileName(file);

                    bool shouldReport = cur == 1 || cur == total || (DateTime.UtcNow - lastProgressReport).TotalMilliseconds >= 250;
                    if (shouldReport) {
                        lastProgressReport = DateTime.UtcNow;
                        onProgress?.Invoke(ScanCurrentFile, cur, total);
                        OnScanProgress?.Invoke(ScanCurrentFile, cur, total);
                    }

                    bool shouldFlush = false;
                    List<ShowcaseMediaItem>? flushBatch = null;
                    lock (batchLock) {
                        incrementalBatch.Add(extracted);
                        if (incrementalBatch.Count >= 25) {
                            flushBatch = incrementalBatch.ToList();
                            incrementalBatch.Clear();
                            shouldFlush = true;
                        }
                    }

                    if (shouldFlush && flushBatch != null && _databaseService != null) {
                        try {
                            await _databaseService.UpsertGalleryMediaBatchAsync(flushBatch, cancellationToken);
                        } catch { }
                        UpdateSnapshot();
                        OnShowcaseUpdated?.Invoke();
                    }
                } finally {
                    throttler.Release();
                }
            });

            await Task.WhenAll(tasks);

            // Flush remaining items
            if (incrementalBatch.Count > 0 && _databaseService != null) {
                try {
                    await _databaseService.UpsertGalleryMediaBatchAsync(incrementalBatch, cancellationToken);
                } catch { }
                incrementalBatch.Clear();
            }
        }

        // Clean up items for files deleted from disk using the fast candidate set
        lock (_items) {
            var dead = _items.Keys
                .Where(k => dirs.Any(d => k.StartsWith(d, StringComparison.OrdinalIgnoreCase)) && !candidateFilesSet.Contains(k))
                .ToList();
            foreach (var d in dead) {
                _items.Remove(d);
                if (_databaseService != null) {
                    _ = _databaseService.RemoveGalleryMediaAsync(d, CancellationToken.None);
                }
            }
        }

        UpdateSnapshot();
        await SaveCacheAsync(cancellationToken);
        OnShowcaseUpdated?.Invoke();
    }

    /// <summary>
    /// Gets the persistent sort order for a given folder path (or root if null/empty).
    /// </summary>
    public string GetFolderSortOrder(string? folderPath) {
        var settings = _settingsService.Current;
        string key = string.IsNullOrWhiteSpace(folderPath) ? "_root_" : folderPath.Trim();
        if (settings.ShowcaseFolderSortOrders != null && settings.ShowcaseFolderSortOrders.TryGetValue(key, out string? order) && !string.IsNullOrWhiteSpace(order)) {
            return order;
        }
        return !string.IsNullOrWhiteSpace(settings.ShowcaseDefaultSort) ? settings.ShowcaseDefaultSort : "newest";
    }

    /// <summary>
    /// Sets and saves the persistent sort order for a given folder path across user sessions.
    /// </summary>
    public async Task SetFolderSortOrderAsync(string? folderPath, string sortBy) {
        if (string.IsNullOrWhiteSpace(sortBy)) return;
        var settings = _settingsService.Current;
        settings.ShowcaseFolderSortOrders ??= new(StringComparer.OrdinalIgnoreCase);
        string key = string.IsNullOrWhiteSpace(folderPath) ? "_root_" : folderPath.Trim();
        settings.ShowcaseFolderSortOrders[key] = sortBy.Trim();
        await _settingsService.SaveSettingsAsync(settings);
    }

    public async Task ToggleFavoriteAsync(string filePath, CancellationToken cancellationToken = default) {
        lock (_items) {
            if (_items.TryGetValue(filePath, out var item)) {
                item.IsFavorite = !item.IsFavorite;
            }
        }
        if (_databaseService != null) {
            try {
                await _databaseService.ToggleGalleryFavoriteAsync(filePath, cancellationToken);
            } catch { }
        }
        await SaveCacheAsync(cancellationToken);
        OnShowcaseUpdated?.Invoke();
    }

    public async Task UpdateMediaItemAsync(ShowcaseMediaItem item, CancellationToken cancellationToken = default) {
        lock (_items) {
            _items[item.FilePath] = item;
        }
        if (_databaseService != null) {
            try {
                await _databaseService.UpdateGalleryMediaItemAsync(item, cancellationToken);
            } catch { }
        }
        await SaveCacheAsync(cancellationToken);
        OnShowcaseUpdated?.Invoke();
    }

    public async Task AddCustomTagAsync(string tag) {
        if (string.IsNullOrWhiteSpace(tag)) {
            return;
        }
        string clean = tag.Trim();
        if (!_customTags.Contains(clean, StringComparer.OrdinalIgnoreCase)) {
            _customTags.Add(clean);
            var settings = _settingsService.Current;
            settings.ShowcaseCustomTags = _customTags.ToList();
            await _settingsService.SaveSettingsAsync(settings);
            OnShowcaseUpdated?.Invoke();
        }
    }

    public async Task AddCustomCategoryAsync(string category) {
        if (string.IsNullOrWhiteSpace(category)) {
            return;
        }
        string clean = category.Trim();
        if (!_customCategories.Contains(clean, StringComparer.OrdinalIgnoreCase)) {
            _customCategories.Add(clean);
            var settings = _settingsService.Current;
            settings.ShowcaseCustomCategories = _customCategories.ToList();
            await _settingsService.SaveSettingsAsync(settings);
            OnShowcaseUpdated?.Invoke();
        }
    }

    public List<LoraMetadata> FindMatchingLorasForMedia(ShowcaseMediaItem item) {
        if (_loraLibraryService == null || item.UsedLoras.Count == 0) {
            return new();
        }

        var allLoras = _loraLibraryService.Items;
        var matches = new List<LoraMetadata>();

        foreach (string used in item.UsedLoras) {
            string clean = Path.GetFileNameWithoutExtension(used).Trim();
            var found = allLoras.FirstOrDefault(l =>
                Path.GetFileNameWithoutExtension(l.FilePath).Equals(clean, StringComparison.OrdinalIgnoreCase) ||
                l.FileName.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
                l.FileName.Equals($"{clean}.safetensors", StringComparison.OrdinalIgnoreCase));

            if (found != null && !matches.Any(m => m.FilePath.Equals(found.FilePath, StringComparison.OrdinalIgnoreCase))) {
                matches.Add(found);
            }
        }

        return matches;
    }

    public async Task<int> AssignImageAsLoraPreviewAsync(
        ShowcaseMediaItem item,
        IEnumerable<LoraMetadata> targetLoras,
        bool copyAlongsideLora = true,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(targetLoras);

        if (!File.Exists(item.FilePath)) {
            return 0;
        }

        int count = 0;
        foreach (var lora in targetLoras) {
            try {
                if (_loraLibraryService != null) {
                    bool ok = await _loraLibraryService.AssignPreviewImageAsync(lora, item.FilePath, copyAlongsideLora);
                    if (ok) {
                        count++;
                    }
                } else if (copyAlongsideLora && File.Exists(lora.FilePath)) {
                    string loraDir = Path.GetDirectoryName(lora.FilePath) ?? string.Empty;
                    string loraBaseName = Path.GetFileNameWithoutExtension(lora.FilePath);
                    string ext = Path.GetExtension(item.FilePath);
                    if (string.IsNullOrWhiteSpace(ext)) {
                        ext = ".png";
                    }
                    string dest = Path.Combine(loraDir, $"{loraBaseName}.preview{ext}");
                    File.Copy(item.FilePath, dest, overwrite: true);
                    lora.ThumbnailPath = dest;
                    count++;
                }

                string loraName = Path.GetFileNameWithoutExtension(lora.FileName);
                if (string.IsNullOrWhiteSpace(loraName)) {
                    loraName = Path.GetFileNameWithoutExtension(lora.FilePath);
                }

                if (!item.AssociatedLoraNames.Contains(loraName, StringComparer.OrdinalIgnoreCase)) {
                    item.AssociatedLoraNames.Add(loraName);
                }
                if (!item.AssociatedLoraFilePaths.Contains(lora.FilePath, StringComparer.OrdinalIgnoreCase)) {
                    item.AssociatedLoraFilePaths.Add(lora.FilePath);
                }
            } catch {
                // Ignore single LoRA assignment failures
            }
        }

        if (count > 0) {
            lock (_items) {
                _items[item.FilePath] = item;
            }
            await SaveCacheAsync(cancellationToken);
            OnShowcaseUpdated?.Invoke();
        }

        return count;
    }

    public List<ShowcaseFolderNode> GetChildFolders(string? parentFolder, IEnumerable<ShowcaseMediaItem>? mediaSubset = null) {
        var mediaList = (mediaSubset ?? Items).ToList();
        var result = new List<ShowcaseFolderNode>();

        if (string.IsNullOrWhiteSpace(parentFolder)) {
            var roots = GetConfiguredDirectories();
            foreach (var root in roots) {
                string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var itemsInRoot = mediaList.Where(i => string.Equals(i.FolderPath, root, StringComparison.OrdinalIgnoreCase) || i.FolderPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
                int directCount = mediaList.Count(i => string.Equals(i.FolderPath, root, StringComparison.OrdinalIgnoreCase));

                int subDirCount = GetCachedSubdirCount(root);

                var previewItem = itemsInRoot.FirstOrDefault(i => i.MediaType == ShowcaseMediaType.Image) ?? itemsInRoot.FirstOrDefault();
                string? preview = previewItem?.FileUrl;
                string? previewPath = previewItem?.FilePath;

                result.Add(new ShowcaseFolderNode {
                    FullPath = root,
                    Name = Path.GetFileName(root) is string n && !string.IsNullOrWhiteSpace(n) ? n : root,
                    DirectMediaCount = directCount,
                    TotalMediaCount = itemsInRoot.Count,
                    SubfolderCount = subDirCount,
                    PreviewFileUrl = preview,
                    PreviewFilePath = previewPath
                });
            }
            return result;
        }

        var subdirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(parentFolder)) {
            try {
                foreach (string dir in Directory.GetDirectories(parentFolder)) {
                    subdirs.Add(dir);
                }
            } catch {
                // Ignore filesystem access errors
            }
        }

        string parentPrefix = parentFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var item in mediaList) {
            if (item.FolderPath.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase)) {
                string rel = item.FolderPath.Substring(parentPrefix.Length);
                string[] parts = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0) {
                    subdirs.Add(Path.Combine(parentFolder, parts[0]));
                }
            }
        }

        foreach (string sub in subdirs.OrderBy(s => Path.GetFileName(s))) {
            string subPrefix = sub.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var subItems = mediaList.Where(i => string.Equals(i.FolderPath, sub, StringComparison.OrdinalIgnoreCase) || i.FolderPath.StartsWith(subPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            int directCount = mediaList.Count(i => string.Equals(i.FolderPath, sub, StringComparison.OrdinalIgnoreCase));

            int childSubCount = GetCachedSubdirCount(sub);

            var subPreviewItem = subItems.FirstOrDefault(i => i.MediaType == ShowcaseMediaType.Image) ?? subItems.FirstOrDefault();
            string? preview = subPreviewItem?.FileUrl;
            string? previewPath = subPreviewItem?.FilePath;

            result.Add(new ShowcaseFolderNode {
                FullPath = sub,
                Name = Path.GetFileName(sub),
                DirectMediaCount = directCount,
                TotalMediaCount = subItems.Count,
                SubfolderCount = childSubCount,
                PreviewFileUrl = preview,
                PreviewFilePath = previewPath
            });
        }

        return result;
    }

    public List<ShowcaseBreadcrumb> GetBreadcrumbs(string? currentFolder) {
        var crumbs = new List<ShowcaseBreadcrumb> {
            new ShowcaseBreadcrumb { Name = "All Libraries", FullPath = null }
        };

        if (string.IsNullOrWhiteSpace(currentFolder)) {
            return crumbs;
        }

        var roots = GetConfiguredDirectories();
        string? matchedRoot = roots.FirstOrDefault(r => currentFolder.StartsWith(r, StringComparison.OrdinalIgnoreCase));

        if (matchedRoot != null) {
            crumbs.Add(new ShowcaseBreadcrumb {
                Name = Path.GetFileName(matchedRoot) is string rn && !string.IsNullOrWhiteSpace(rn) ? rn : matchedRoot,
                FullPath = matchedRoot
            });

            if (!string.Equals(matchedRoot, currentFolder, StringComparison.OrdinalIgnoreCase)) {
                string rel = currentFolder.Substring(matchedRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string[] parts = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

                string accum = matchedRoot;
                foreach (string part in parts) {
                    accum = Path.Combine(accum, part);
                    crumbs.Add(new ShowcaseBreadcrumb {
                        Name = part,
                        FullPath = accum
                    });
                }
            }
        } else {
            string[] parts = currentFolder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            string accum = "";
            foreach (string part in parts) {
                accum = string.IsNullOrWhiteSpace(accum) ? part : Path.Combine(accum, part);
                crumbs.Add(new ShowcaseBreadcrumb {
                    Name = part,
                    FullPath = accum
                });
            }
        }

        return crumbs;
    }

    private void UpdateSnapshot() {
        lock (_items) {
            _cachedSnapshot = _items.Values.OrderByDescending(i => i.CreatedDate).ToList();
        }
    }

    private int GetCachedSubdirCount(string dir) {
        if (string.IsNullOrWhiteSpace(dir)) return 0;
        if (_dirCountCache.TryGetValue(dir, out var entry) && (DateTime.UtcNow - entry.timestamp).TotalSeconds < 30) {
            return entry.count;
        }
        int count = 0;
        try {
            if (Directory.Exists(dir)) {
                count = Directory.GetDirectories(dir).Length;
            }
        } catch { }
        _dirCountCache[dir] = (count, DateTime.UtcNow);
        return count;
    }
}

