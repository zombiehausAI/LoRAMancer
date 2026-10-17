using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class ImageHarvesterService {
    private readonly HttpClient _httpClient;
    private readonly SettingsService? _settingsService;
    private readonly PluginManagerService? _pluginManagerService;
    private readonly List<HarvestedCandidateItem> _candidates = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _activeSearchCts;

    public IReadOnlyList<HarvestedCandidateItem> Candidates {
        get {
            lock (_lock) {
                return _candidates.ToList();
            }
        }
    }

    public bool IsSearching { get; private set; }
    public bool IsDownloading { get; private set; }
    public bool IsAuditing { get; private set; }

    public event Action? OnStateChanged;

    public ImageHarvesterService(HttpClient? httpClient) : this(null, null, httpClient) { }

    public ImageHarvesterService(SettingsService? settingsService, HttpClient? httpClient) : this(settingsService, null, httpClient) { }

    public ImageHarvesterService(SettingsService? settingsService = null, PluginManagerService? pluginManagerService = null, HttpClient? httpClient = null) {
        _settingsService = settingsService;
        _pluginManagerService = pluginManagerService;

        if (httpClient != null) {
            _httpClient = httpClient;
        } else {
            var handler = new SocketsHttpHandler {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            _httpClient = new HttpClient(handler) {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent")) {
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        }
    }

    /// <summary>
    /// Returns all available search providers (built-in + custom user providers).
    /// </summary>
    public List<HarvestProviderDefinition> GetProviders() {
        var providers = GetDefaultProviders();

        var disabledIds = _settingsService?.Current.HarvestDisabledProviderIds ?? new List<string>();
        var customList = _settingsService?.Current.HarvestCustomProviders ?? new List<HarvestProviderDefinition>();

        foreach (var p in providers) {
            p.IsEnabled = !disabledIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var custom in customList) {
            custom.IsCustom = true;
            custom.IsEnabled = !disabledIds.Contains(custom.Id, StringComparer.OrdinalIgnoreCase);
            providers.Add(custom);
        }

        if (_pluginManagerService != null) {
            var scraperPlugins = _pluginManagerService.Plugins
                .Where(p => p.IsEnabled && (
                    string.Equals(p.UiSlot, "HarvesterScraper", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.MenuSection, "Harvester Scraper", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.PluginType, "HarvesterScraper", StringComparison.OrdinalIgnoreCase)
                ));

            foreach (var plugin in scraperPlugins) {
                string pid = "plugin_" + plugin.Id;
                providers.Add(new HarvestProviderDefinition {
                    Id = pid,
                    Name = plugin.Name,
                    Description = !string.IsNullOrWhiteSpace(plugin.Description) ? plugin.Description : $"Python scraper plugin ({plugin.Name})",
                    Icon = !string.IsNullOrWhiteSpace(plugin.Icon) ? plugin.Icon : "Extension",
                    Engine = HarvestEngine.PythonPlugin,
                    PluginId = plugin.Id,
                    IsEnabled = !disabledIds.Contains(pid, StringComparer.OrdinalIgnoreCase)
                });
            }
        }

        return providers;
    }

    /// <summary>
    /// Toggles a search provider's enabled state and persists to settings.
    /// </summary>
    public async Task SetProviderEnabledAsync(string providerId, bool enabled) {
        if (_settingsService == null) return;

        var disabled = _settingsService.Current.HarvestDisabledProviderIds;
        if (enabled) {
            disabled.RemoveAll(id => string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase));
        } else {
            if (!disabled.Contains(providerId, StringComparer.OrdinalIgnoreCase)) {
                disabled.Add(providerId);
            }
        }

        await _settingsService.SaveSettingsAsync(_settingsService.Current);
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Adds a new custom REST search provider.
    /// </summary>
    public async Task AddCustomProviderAsync(HarvestProviderDefinition provider) {
        if (_settingsService == null) return;
        if (string.IsNullOrWhiteSpace(provider.Id)) {
            provider.Id = "custom_" + Guid.NewGuid().ToString("N")[..8];
        }
        provider.IsCustom = true;
        provider.Engine = HarvestEngine.CustomRest;

        _settingsService.Current.HarvestCustomProviders.Add(provider);
        await _settingsService.SaveSettingsAsync(_settingsService.Current);
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Removes a custom REST search provider.
    /// </summary>
    public async Task RemoveCustomProviderAsync(string providerId) {
        if (_settingsService == null) return;
        _settingsService.Current.HarvestCustomProviders.RemoveAll(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        _settingsService.Current.HarvestDisabledProviderIds.RemoveAll(id => string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase));
        await _settingsService.SaveSettingsAsync(_settingsService.Current);
        OnStateChanged?.Invoke();
    }

    public void CancelSearch() {
        lock (_lock) {
            _activeSearchCts?.Cancel();
            IsSearching = false;
        }
        OnStateChanged?.Invoke();
    }

    public async Task<List<HarvestedCandidateItem>> SearchAsync(HarvestSearchQuery query, CancellationToken cancellationToken = default) {
        CancellationTokenSource linkedCts;
        lock (_lock) {
            try {
                _activeSearchCts?.Cancel();
                _activeSearchCts?.Dispose();
            } catch {
                // Ignore disposal errors
            }
            _activeSearchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts = _activeSearchCts;
            IsSearching = true;
        }
        OnStateChanged?.Invoke();

        var results = new List<HarvestedCandidateItem>();

        try {
            if (query.SelectedProviderIds != null && query.SelectedProviderIds.Count > 0) {
                results = await SearchSelectedProvidersAsync(query, query.SelectedProviderIds, linkedCts.Token);
            } else if (query.SearchAllProviders) {
                results = await SearchAllEnabledProvidersAsync(query, linkedCts.Token);
            } else {
                results = await SearchSingleProviderAsync(query, linkedCts.Token);
            }

            // Deduplicate against catalog history and existing local image files
            string? destFolder = query.CatalogDestinationFolder;
            if (!string.IsNullOrWhiteSpace(destFolder) && Directory.Exists(destFolder)) {
                var historyUrls = LoadCatalogHistoryUrls(destFolder);
                var localNames = GetExistingLocalFileNames(destFolder);

                foreach (var item in results) {
                    bool urlMatches = (!string.IsNullOrWhiteSpace(item.SourceUrl) && historyUrls.Contains(item.SourceUrl)) ||
                                      (!string.IsNullOrWhiteSpace(item.ThumbnailUrl) && historyUrls.Contains(item.ThumbnailUrl));
                    string clean = CleanTitle(item.Title);
                    bool nameMatches = localNames.Contains(clean);

                    if (urlMatches || nameMatches) {
                        item.IsAlreadyDownloaded = true;
                        item.IsSelected = false;
                    }
                }

                if (query.OmitExistingInCatalog) {
                    results.RemoveAll(r => r.IsAlreadyDownloaded);
                }
            }

            lock (_lock) {
                _candidates.Clear();
                _candidates.AddRange(results);
            }
        } catch (OperationCanceledException) {
            // Handled cancellation cleanly
        } finally {
            lock (_lock) {
                IsSearching = false;
            }
            OnStateChanged?.Invoke();
        }

        return results;
    }

    private async Task<List<HarvestedCandidateItem>> SearchSelectedProvidersAsync(HarvestSearchQuery query, List<string> selectedIds, CancellationToken cancellationToken) {
        var all = GetProviders();
        var matching = all.Where(p => selectedIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase) ||
                                      (!string.IsNullOrWhiteSpace(p.PluginId) && selectedIds.Contains(p.PluginId, StringComparer.OrdinalIgnoreCase))).ToList();

        if (matching.Count == 0) return new List<HarvestedCandidateItem>();

        if (matching.Count == 1) {
            var p = matching[0];
            var sub = new HarvestSearchQuery {
                Engine = p.Engine,
                ProviderId = !string.IsNullOrWhiteSpace(p.PluginId) ? p.PluginId : p.Id,
                Query = query.Query,
                Subreddit = query.Subreddit,
                Tags = !string.IsNullOrWhiteSpace(query.Tags) ? query.Tags : query.Query,
                MaxResults = query.MaxResults,
                SafeSearch = query.SafeSearch,
                DirectUrlsText = query.DirectUrlsText,
                MinWidth = query.MinWidth,
                MinHeight = query.MinHeight
            };
            using var singleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            singleCts.CancelAfter(TimeSpan.FromSeconds(20));
            return await Task.Run(async () => {
                try {
                    return await ExecuteProviderSearchAsync(p, sub, singleCts.Token);
                } catch {
                    return new List<HarvestedCandidateItem>();
                }
            }, cancellationToken);
        }

        int perProviderLimit = Math.Max(10, query.MaxResults / Math.Max(1, matching.Count));
        var tasks = new List<Task<List<HarvestedCandidateItem>>>();

        foreach (var p in matching) {
            var subQuery = new HarvestSearchQuery {
                Engine = p.Engine,
                ProviderId = !string.IsNullOrWhiteSpace(p.PluginId) ? p.PluginId : p.Id,
                Query = query.Query,
                Subreddit = query.Subreddit,
                Tags = !string.IsNullOrWhiteSpace(query.Tags) ? query.Tags : query.Query,
                MaxResults = perProviderLimit,
                SafeSearch = query.SafeSearch,
                DirectUrlsText = query.DirectUrlsText,
                MinWidth = query.MinWidth,
                MinHeight = query.MinHeight
            };

            tasks.Add(Task.Run(async () => {
                using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                providerCts.CancelAfter(TimeSpan.FromSeconds(15));
                try {
                    return await ExecuteProviderSearchAsync(p, subQuery, providerCts.Token);
                } catch {
                    return new List<HarvestedCandidateItem>();
                }
            }, cancellationToken));
        }

        var resultsArray = await Task.WhenAll(tasks);
        var merged = new List<HarvestedCandidateItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in resultsArray) {
            foreach (var item in batch) {
                if (!string.IsNullOrWhiteSpace(item.SourceUrl) && seenUrls.Add(item.SourceUrl)) {
                    merged.Add(item);
                }
            }
        }

        return merged;
    }

    private async Task<List<HarvestedCandidateItem>> SearchAllEnabledProvidersAsync(HarvestSearchQuery query, CancellationToken cancellationToken) {
        var providers = GetProviders().Where(p => p.IsEnabled && p.Engine != HarvestEngine.DirectUrls).ToList();
        if (providers.Count == 0) return new List<HarvestedCandidateItem>();

        int perProviderLimit = Math.Max(10, query.MaxResults / Math.Max(1, providers.Count));
        var tasks = new List<Task<List<HarvestedCandidateItem>>>();

        foreach (var p in providers) {
            var subQuery = new HarvestSearchQuery {
                Engine = p.Engine,
                ProviderId = p.Id,
                Query = query.Query,
                Subreddit = query.Subreddit,
                Tags = !string.IsNullOrWhiteSpace(query.Tags) ? query.Tags : query.Query,
                MaxResults = perProviderLimit,
                SafeSearch = query.SafeSearch,
                MinWidth = query.MinWidth,
                MinHeight = query.MinHeight
            };

            tasks.Add(Task.Run(async () => {
                using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                providerCts.CancelAfter(TimeSpan.FromSeconds(12));
                try {
                    return await ExecuteProviderSearchAsync(p, subQuery, providerCts.Token);
                } catch {
                    return new List<HarvestedCandidateItem>();
                }
            }, cancellationToken));
        }

        var resultsArray = await Task.WhenAll(tasks);
        var merged = new List<HarvestedCandidateItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var batch in resultsArray) {
            foreach (var item in batch) {
                if (!string.IsNullOrWhiteSpace(item.SourceUrl) && seenUrls.Add(item.SourceUrl)) {
                    merged.Add(item);
                }
            }
        }

        return merged;
    }

    private async Task<List<HarvestedCandidateItem>> SearchSingleProviderAsync(HarvestSearchQuery query, CancellationToken cancellationToken) {
        HarvestProviderDefinition? provider = null;

        if (!string.IsNullOrWhiteSpace(query.ProviderId)) {
            provider = GetProviders().FirstOrDefault(p => string.Equals(p.Id, query.ProviderId, StringComparison.OrdinalIgnoreCase) && p.Engine == query.Engine);
        }

        provider ??= GetProviders().FirstOrDefault(p => p.Engine == query.Engine)
                     ?? GetDefaultProviders().First();

        using var singleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        singleCts.CancelAfter(TimeSpan.FromSeconds(20));
        return await ExecuteProviderSearchAsync(provider, query, singleCts.Token);
    }

    private async Task<List<HarvestedCandidateItem>> ExecuteProviderSearchAsync(HarvestProviderDefinition provider, HarvestSearchQuery query, CancellationToken cancellationToken) {
        return provider.Engine switch {
            HarvestEngine.DuckDuckGo => string.Equals(provider.Id, "bing", StringComparison.OrdinalIgnoreCase)
                ? await SearchBingImagesAsync(query, cancellationToken)
                : await SearchWebUnifiedAsync(query, cancellationToken),
            HarvestEngine.Reddit => await SearchRedditAsync(query, cancellationToken),
            HarvestEngine.Wikimedia => await SearchWikimediaAsync(query, cancellationToken),
            HarvestEngine.Unsplash => await SearchUnsplashAsync(query, cancellationToken),
            HarvestEngine.Safebooru => await SearchSafebooruAsync(query, cancellationToken),
            HarvestEngine.Danbooru => await SearchDanbooruAsync(query, cancellationToken),
            HarvestEngine.Openverse => await SearchOpenverseAsync(query, cancellationToken),
            HarvestEngine.Flickr => await SearchFlickrAsync(query, cancellationToken),
            HarvestEngine.DirectUrls => ParseDirectUrls(query),
            HarvestEngine.CustomRest => await SearchCustomRestAsync(provider, query, cancellationToken),
            HarvestEngine.PythonPlugin => await SearchPythonPluginAsync(provider, query, cancellationToken),
            _ => await SearchWebUnifiedAsync(query, cancellationToken)
        };
    }

    public async Task<List<HarvestedCandidateItem>> SearchWebUnifiedAsync(HarvestSearchQuery query, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(query.Query)) return new List<HarvestedCandidateItem>();

        var bingTask = SearchBingImagesAsync(query, cancellationToken);
        var ddgTask = SearchDuckDuckGoAsync(query, cancellationToken);

        try {
            await Task.WhenAll(bingTask, ddgTask);
        } catch {
            // Individual scrapers handle their own errors
        }

        var results = new List<HarvestedCandidateItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var bingList = bingTask.IsCompletedSuccessfully ? bingTask.Result : new List<HarvestedCandidateItem>();
        var ddgList = ddgTask.IsCompletedSuccessfully ? ddgTask.Result : new List<HarvestedCandidateItem>();

        int bIdx = 0, dIdx = 0;
        int maxPerEngine = Math.Max(15, (int)(query.MaxResults * 0.65));

        // Interleave results from Bing and DuckDuckGo for rich diversity and high volume
        while ((bIdx < bingList.Count || dIdx < ddgList.Count) && results.Count < query.MaxResults) {
            bool advanced = false;
            if (bIdx < bingList.Count && bIdx < maxPerEngine) {
                var item = bingList[bIdx++];
                if (seenUrls.Add(item.SourceUrl)) {
                    results.Add(item);
                }
                advanced = true;
            }
            if (dIdx < ddgList.Count && dIdx < maxPerEngine && results.Count < query.MaxResults) {
                var item = ddgList[dIdx++];
                if (seenUrls.Add(item.SourceUrl)) {
                    results.Add(item);
                }
                advanced = true;
            }
            if (!advanced) {
                break;
            }
        }

        while (bIdx < bingList.Count && results.Count < query.MaxResults) {
            var item = bingList[bIdx++];
            if (seenUrls.Add(item.SourceUrl)) {
                results.Add(item);
            }
        }
        while (dIdx < ddgList.Count && results.Count < query.MaxResults) {
            var item = ddgList[dIdx++];
            if (seenUrls.Add(item.SourceUrl)) {
                results.Add(item);
            }
        }

        return results;
    }

    public async Task<List<HarvestedCandidateItem>> SearchDuckDuckGoAsync(HarvestSearchQuery query, CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(query.Query)) return new List<HarvestedCandidateItem>();

        var items = new List<HarvestedCandidateItem>();
        try {
            string searchUrl = $"https://duckduckgo.com/?q={Uri.EscapeDataString(query.Query)}";
            using var initRequest = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            initRequest.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            initRequest.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            initRequest.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            var initResponse = await _httpClient.SendAsync(initRequest, cancellationToken);
            string initHtml = await initResponse.Content.ReadAsStringAsync(cancellationToken);

            string? vqd = null;
            var vqdMatch = Regex.Match(initHtml, @"vqd=['""]?([a-zA-Z0-9_-]+)['""]?");
            if (vqdMatch.Success) {
                vqd = vqdMatch.Groups[1].Value;
            }

            if (string.IsNullOrWhiteSpace(vqd)) {
                var vqdAlt = Regex.Match(initHtml, @"[?&""]vqd=([a-zA-Z0-9_-]+)");
                if (vqdAlt.Success) {
                    vqd = vqdAlt.Groups[1].Value;
                }
            }

            if (string.IsNullOrWhiteSpace(vqd)) {
                var vqdJson = Regex.Match(initHtml, @"""vqd"":\s*""([^""]+)""");
                if (vqdJson.Success) {
                    vqd = vqdJson.Groups[1].Value;
                }
            }

            if (!string.IsNullOrWhiteSpace(vqd)) {
                string safeParam = query.SafeSearch ? "1" : "-1";
                string apiUrl = $"https://duckduckgo.com/i.js?l=us-en&o=json&q={Uri.EscapeDataString(query.Query)}&vqd={vqd}&f=,,,&p={safeParam}";

                using var apiRequest = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                apiRequest.Headers.Add("Accept", "application/json");
                apiRequest.Headers.Add("Referer", "https://duckduckgo.com/");
                var apiResponse = await _httpClient.SendAsync(apiRequest, cancellationToken);

                if (apiResponse.IsSuccessStatusCode) {
                    string json = await apiResponse.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("results", out var resultsElem) && resultsElem.ValueKind == JsonValueKind.Array) {
                        foreach (var el in resultsElem.EnumerateArray()) {
                            if (items.Count >= query.MaxResults) break;

                            string imageUrl = el.TryGetProperty("image", out var img) ? img.GetString() ?? "" : "";
                            string thumbUrl = el.TryGetProperty("thumbnail", out var thm) ? thm.GetString() ?? "" : imageUrl;
                            string title = el.TryGetProperty("title", out var ttl) ? ttl.GetString() ?? "" : "Web Image";
                            int width = el.TryGetProperty("width", out var w) && w.TryGetInt32(out int parsedW) ? parsedW : 0;
                            int height = el.TryGetProperty("height", out var h) && h.TryGetInt32(out int parsedH) ? parsedH : 0;

                            bool passesWidth = query.MinWidth <= 0 || width == 0 || width >= query.MinWidth;
                            bool passesHeight = query.MinHeight <= 0 || height == 0 || height >= query.MinHeight;

                            if (!string.IsNullOrWhiteSpace(imageUrl) && passesWidth && passesHeight) {
                                items.Add(new HarvestedCandidateItem {
                                    SourceUrl = imageUrl,
                                    ThumbnailUrl = !string.IsNullOrWhiteSpace(thumbUrl) ? thumbUrl : imageUrl,
                                    Title = CleanTitle(title),
                                    Width = width,
                                    Height = height,
                                    SourceEngine = HarvestEngine.DuckDuckGo,
                                    ProviderId = "duckduckgo",
                                    ProviderName = "DuckDuckGo",
                                    IsSelected = true
                                });
                            }
                        }
                    }
                }
            }
        } catch {
            // Ignore DDG exceptions
        }

        return items;
    }

    public async Task<List<HarvestedCandidateItem>> SearchBingImagesAsync(HarvestSearchQuery query, CancellationToken cancellationToken = default) {
        var items = new List<HarvestedCandidateItem>();
        if (string.IsNullOrWhiteSpace(query.Query)) return items;

        try {
            int count = Math.Clamp(query.MaxResults, 20, 100);
            string safeParam = query.SafeSearch ? "&adlt=strict" : "&adlt=off";
            string url = $"https://www.bing.com/images/search?q={Uri.EscapeDataString(query.Query)}&form=HDRSC2&count={count}{safeParam}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return items;

            string html = await response.Content.ReadAsStringAsync(cancellationToken);
            var matches = Regex.Matches(html, @"class=""iusc""[^>]*m=""(\{.+?\})""[^>]*href=""([^""]+)""");
            if (matches.Count == 0) {
                matches = Regex.Matches(html, @"m=""(\{.+?\})""");
            }

            foreach (Match match in matches) {
                if (items.Count >= query.MaxResults) break;
                if (!match.Success) continue;

                try {
                    string decodedJson = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
                    using var doc = JsonDocument.Parse(decodedJson);
                    var root = doc.RootElement;

                    string murl = root.TryGetProperty("murl", out var mu) ? mu.GetString() ?? "" : "";
                    string turl = root.TryGetProperty("turl", out var tu) ? tu.GetString() ?? "" : "";
                    string title = root.TryGetProperty("t", out var ti) ? ti.GetString() ?? "Web Image" : "Web Image";

                    if (string.IsNullOrWhiteSpace(murl)) continue;

                    int width = 0;
                    int height = 0;
                    if (match.Groups.Count > 2) {
                        string href = System.Net.WebUtility.HtmlDecode(match.Groups[2].Value);
                        var wMatch = Regex.Match(href, @"expw=(\d+)");
                        if (wMatch.Success) int.TryParse(wMatch.Groups[1].Value, out width);
                        var hMatch = Regex.Match(href, @"exph=(\d+)");
                        if (hMatch.Success) int.TryParse(hMatch.Groups[1].Value, out height);
                    }

                    bool passesWidth = query.MinWidth <= 0 || width == 0 || width >= query.MinWidth;
                    bool passesHeight = query.MinHeight <= 0 || height == 0 || height >= query.MinHeight;

                    if (passesWidth && passesHeight) {
                        items.Add(new HarvestedCandidateItem {
                            SourceUrl = murl,
                            ThumbnailUrl = !string.IsNullOrWhiteSpace(turl) ? turl : murl,
                            Title = CleanTitle(title),
                            Width = width,
                            Height = height,
                            SourceEngine = HarvestEngine.DuckDuckGo,
                            ProviderId = "bing",
                            ProviderName = "Bing Images",
                            IsSelected = true
                        });
                    }
                } catch {
                    // Ignore single match parse failure
                }
            }
        } catch {
            // Suppress search errors
        }

        return items;
    }

    public async Task<List<HarvestedCandidateItem>> SearchRedditAsync(HarvestSearchQuery query, CancellationToken cancellationToken = default) {
        var items = new List<HarvestedCandidateItem>();
        string sub = query.Subreddit.Trim();
        if (sub.StartsWith("r/", StringComparison.OrdinalIgnoreCase)) {
            sub = sub[2..];
        }
        if (string.IsNullOrWhiteSpace(sub)) sub = "wallpaper";

        try {
            string url;
            if (!string.IsNullOrWhiteSpace(query.Query)) {
                if (!string.IsNullOrWhiteSpace(query.Subreddit) && !string.Equals(query.Subreddit, "wallpaper", StringComparison.OrdinalIgnoreCase)) {
                    url = $"https://www.reddit.com/r/{sub}/search.json?q={Uri.EscapeDataString(query.Query)}&restrict_sr=1&sort=relevance&limit={Math.Clamp(query.MaxResults, 10, 100)}";
                } else {
                    url = $"https://www.reddit.com/search.json?q={Uri.EscapeDataString(query.Query)}&sort=relevance&limit={Math.Clamp(query.MaxResults, 10, 100)}";
                }
            } else {
                url = $"https://www.reddit.com/r/{sub}/hot.json?limit={Math.Clamp(query.MaxResults, 10, 100)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) {
                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(json) && json.TrimStart().StartsWith("{")) {
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("data", out var dataElem) &&
                        dataElem.TryGetProperty("children", out var childrenElem) &&
                        childrenElem.ValueKind == JsonValueKind.Array) {

                        foreach (var child in childrenElem.EnumerateArray()) {
                            if (items.Count >= query.MaxResults) break;
                            if (!child.TryGetProperty("data", out var post)) continue;

                            string postUrl = post.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                            string title = post.TryGetProperty("title", out var ttl) ? ttl.GetString() ?? "" : "Reddit Post";
                            string thumb = post.TryGetProperty("thumbnail", out var thm) ? thm.GetString() ?? "" : "";

                            int width = 0;
                            int height = 0;
                            if (post.TryGetProperty("preview", out var preview) &&
                                preview.TryGetProperty("images", out var images) &&
                                images.ValueKind == JsonValueKind.Array &&
                                images.GetArrayLength() > 0) {
                                var firstImg = images[0];
                                if (firstImg.TryGetProperty("source", out var source)) {
                                    if (source.TryGetProperty("url", out var srcUrl)) {
                                        string clean = (srcUrl.GetString() ?? "").Replace("&amp;", "&");
                                        if (!string.IsNullOrWhiteSpace(clean)) {
                                            postUrl = clean;
                                        }
                                    }
                                    if (source.TryGetProperty("width", out var w)) width = w.GetInt32();
                                    if (source.TryGetProperty("height", out var h)) height = h.GetInt32();
                                }
                            }

                            bool passesWidth = query.MinWidth <= 0 || width == 0 || width >= query.MinWidth;
                            bool passesHeight = query.MinHeight <= 0 || height == 0 || height >= query.MinHeight;

                            if ((IsDirectImage(postUrl) || postUrl.Contains("i.redd.it") || postUrl.Contains("imgur.com")) && passesWidth && passesHeight) {
                                items.Add(new HarvestedCandidateItem {
                                    SourceUrl = postUrl,
                                    ThumbnailUrl = (!string.IsNullOrWhiteSpace(thumb) && thumb.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ? thumb : postUrl,
                                    Title = CleanTitle(title),
                                    Width = width,
                                    Height = height,
                                    SourceEngine = HarvestEngine.Reddit,
                                    ProviderId = "reddit",
                                    ProviderName = $"Reddit (r/{sub})",
                                    IsSelected = true
                                });
                            }
                        }
                    }
                }
            }
        } catch {
            // Ignore Reddit parsing errors
        }

        // Reddit's unauthenticated JSON API frequently returns HTTP 403 Forbidden.
        // Fallback to searching Reddit media via site: filter so users still receive relevant Reddit images.
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(query.Query)) {
            try {
                string siteFilter = (!string.IsNullOrWhiteSpace(query.Subreddit) && !string.Equals(query.Subreddit, "wallpaper", StringComparison.OrdinalIgnoreCase))
                    ? $"site:reddit.com/r/{sub} {query.Query}"
                    : $"site:reddit.com {query.Query}";

                var fallbackQuery = new HarvestSearchQuery {
                    Query = siteFilter,
                    MaxResults = query.MaxResults,
                    SafeSearch = query.SafeSearch,
                    MinWidth = query.MinWidth,
                    MinHeight = query.MinHeight
                };

                var fallbackItems = await SearchBingImagesAsync(fallbackQuery, cancellationToken);
                foreach (var fb in fallbackItems) {
                    fb.SourceEngine = HarvestEngine.Reddit;
                    fb.ProviderId = "reddit";
                    fb.ProviderName = !string.IsNullOrWhiteSpace(query.Subreddit) ? $"Reddit (r/{sub})" : "Reddit";
                    items.Add(fb);
                }
            } catch {
                // Ignore fallback search errors
            }
        }

        return items;
    }

    public async Task<List<HarvestedCandidateItem>> SearchWikimediaAsync(HarvestSearchQuery query, CancellationToken cancellationToken = default) {
        var items = new List<HarvestedCandidateItem>();
        if (string.IsNullOrWhiteSpace(query.Query)) return items;

        try {
            string url = $"https://commons.wikimedia.org/w/api.php?action=query&generator=search&gsrnamespace=6&gsrsearch={Uri.EscapeDataString(query.Query.Trim())}&gsrlimit={Math.Clamp(query.MaxResults, 10, 50)}&prop=imageinfo&iiprop=url|size|mime&iiurlwidth=400&format=json";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "LoRAMancer/1.0 (https://github.com/dworden42/LoRAMancer; contact@loramancer.local)");
            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return items;

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("query", out var qElem) &&
                qElem.TryGetProperty("pages", out var pagesElem) &&
                pagesElem.ValueKind == JsonValueKind.Object) {

                foreach (var prop in pagesElem.EnumerateObject()) {
                    if (items.Count >= query.MaxResults) break;
                    var page = prop.Value;
                    string title = page.TryGetProperty("title", out var ttl) ? ttl.GetString() ?? "" : "Wikimedia Image";

                    if (page.TryGetProperty("imageinfo", out var infoArr) &&
                        infoArr.ValueKind == JsonValueKind.Array &&
                        infoArr.GetArrayLength() > 0) {
                        var first = infoArr[0];
                        string imgUrl = first.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                        string thumbUrl = first.TryGetProperty("thumburl", out var thm) ? thm.GetString() ?? imgUrl : imgUrl;
                        int width = first.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        int height = first.TryGetProperty("height", out var h) ? h.GetInt32() : 0;

                        bool passesWidth = query.MinWidth <= 0 || width == 0 || width >= query.MinWidth;
                        bool passesHeight = query.MinHeight <= 0 || height == 0 || height >= query.MinHeight;

                        if (!string.IsNullOrWhiteSpace(imgUrl) && passesWidth && passesHeight) {
                            items.Add(new HarvestedCandidateItem {
                                SourceUrl = imgUrl,
                                ThumbnailUrl = !string.IsNullOrWhiteSpace(thumbUrl) ? thumbUrl : imgUrl,
                                Title = CleanTitle(title.Replace("File:", "")),
                                Width = width,
                                Height = height,
                                SourceEngine = HarvestEngine.Wikimedia,
                                ProviderId = "wikimedia",
                                ProviderName = "Wikimedia Commons",
                                IsSelected = true
                            });
                        }
                    }
                }
            }
        } catch {
            // Ignore Wikimedia errors
        }

        return items;
    }

    public async Task<List<HarvestedCandidateItem>> SearchUnsplashAsync(HarvestSearchQuery query, CancellationToken cancellationToken = default) {
        var items = new List<HarvestedCandidateItem>();
        if (string.IsNullOrWhiteSpace(query.Query)) return items;

        try {
            string url = $"https://unsplash.com/napi/search/photos?query={Uri.EscapeDataString(query.Query)}&per_page={Math.Clamp(query.MaxResults, 10, 30)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) {
                string json = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(json) && json.TrimStart().StartsWith("{")) {
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("results", out var resultsElem) && resultsElem.ValueKind == JsonValueKind.Array) {
                        foreach (var el in resultsElem.EnumerateArray()) {
                            if (items.Count >= query.MaxResults) break;

                            string title = "Unsplash Photo";
                            if (el.TryGetProperty("alt_description", out var alt) && !string.IsNullOrWhiteSpace(alt.GetString())) {
                                title = alt.GetString()!;
                            } else if (el.TryGetProperty("description", out var desc) && !string.IsNullOrWhiteSpace(desc.GetString())) {
                                title = desc.GetString()!;
                            }

                            int width = el.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                            int height = el.TryGetProperty("height", out var h) ? h.GetInt32() : 0;

                            string imgUrl = "";
                            string thumbUrl = "";
                            if (el.TryGetProperty("urls", out var urls)) {
                                imgUrl = urls.TryGetProperty("regular", out var reg) ? reg.GetString() ?? "" : "";
                                thumbUrl = urls.TryGetProperty("small", out var sm) ? sm.GetString() ?? imgUrl : imgUrl;
                            }

                            bool passesWidth = query.MinWidth <= 0 || width == 0 || width >= query.MinWidth;
                            bool passesHeight = query.MinHeight <= 0 || height == 0 || height >= query.MinHeight;

                            if (!string.IsNullOrWhiteSpace(imgUrl) && passesWidth && passesHeight) {
                                items.Add(new HarvestedCandidateItem {
                                    SourceUrl = imgUrl,
                                    ThumbnailUrl = !string.IsNullOrWhiteSpace(thumbUrl) ? thumbUrl : imgUrl,
                                    Title = CleanTitle(title),
                                    Width = width,
                                    Height = height,
                                    SourceEngine = HarvestEngine.Unsplash,
                                    ProviderId = "unsplash",
                                    ProviderName = "Unsplash Photography",
                                    IsSelected = true
                                });
                            }
                        }
                    }
                }
            }
        } catch {
            // Ignore Unsplash errors
        }

        // Unsplash blocks unauthenticated API clients with Fastly / Anubis bot challenges (HTTP 401 / 307).
        // Fallback to querying high-resolution curated photography via Openverse's photography index
        // so queries reliably return topic-specific, real-world reference photographs rather than 0 or bot challenges.
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(query.Query)) {
            try {
                string openverseUrl = $"https://api.openverse.org/v1/images/?q={Uri.EscapeDataString(query.Query)}&categories=photograph&page_size={Math.Clamp(query.MaxResults, 10, 50)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, openverseUrl);
                req.Headers.Add("Accept", "application/json");
                req.Headers.TryAddWithoutValidation("User-Agent", "LoRAMancer/1.0 (https://github.com/dworden42/LoRAMancer; contact@loramancer.local)");

                var resp = await _httpClient.SendAsync(req, cancellationToken);
                if (resp.IsSuccessStatusCode) {
                    string ovJson = await resp.Content.ReadAsStringAsync(cancellationToken);
                    using var ovDoc = JsonDocument.Parse(ovJson);

                    if (ovDoc.RootElement.TryGetProperty("results", out var ovResults) && ovResults.ValueKind == JsonValueKind.Array) {
                        foreach (var el in ovResults.EnumerateArray()) {
                            if (items.Count >= query.MaxResults) break;

                            string imgUrl = el.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                            string thumbUrl = el.TryGetProperty("thumbnail", out var thm) ? thm.GetString() ?? imgUrl : imgUrl;
                            string title = el.TryGetProperty("title", out var ttl) ? ttl.GetString() ?? "Photography Reference" : "Photography Reference";
                            int width = el.TryGetProperty("width", out var w) && w.TryGetInt32(out int pw) ? pw : 0;
                            int height = el.TryGetProperty("height", out var h) && h.TryGetInt32(out int ph) ? ph : 0;

                            bool passesWidth = query.MinWidth <= 0 || width == 0 || width >= query.MinWidth;
                            bool passesHeight = query.MinHeight <= 0 || height == 0 || height >= query.MinHeight;

                            if (!string.IsNullOrWhiteSpace(imgUrl) && passesWidth && passesHeight) {
                                items.Add(new HarvestedCandidateItem {
                                    SourceUrl = imgUrl,
                                    ThumbnailUrl = thumbUrl,
                                    Title = CleanTitle(title),
                                    Width = width,
                                    Height = height,
                                    SourceEngine = HarvestEngine.Unsplash,
                                    ProviderId = "unsplash",
                                    ProviderName = "Unsplash Photography",
                                    IsSelected = true
                                });
                            }
                        }
                    }
                }
            } catch {
                // Ignore fallback photography errors
            }
        }

        return items;
    }

    public async Task<List<HarvestedCandidateItem>> SearchSafebooruAsync(HarvestSearchQuery query, CancellationToken cancellationToken = default) {
        var items = new List<HarvestedCandidateItem>();
        string tags = !string.IsNullOrWhiteSpace(query.Tags) ? query.Tags : query.Query;
        if (string.IsNullOrWhiteSpace(tags)) return items;

        try {
            var rawTokens = tags.Split(new[] { ' ', ',', '+' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => Uri.EscapeDataString(t.Trim().ToLowerInvariant().Replace("-", "_")))
                                .ToList();
            if (rawTokens.Count == 0) return items;

            items = await QuerySafebooruApiAsync(string.Join("+", rawTokens), query, cancellationToken);
            // If multi-tag space query returned nothing, attempt with underscore joined tag (e.g. character name)
            if (items.Count == 0 && rawTokens.Count > 1) {
                items = await QuerySafebooruApiAsync(string.Join("_", rawTokens), query, cancellationToken);
            }
        } catch {
            // Ignore Safebooru errors
        }

        return items;
    }

    private async Task<List<HarvestedCandidateItem>> QuerySafebooruApiAsync(string safeTags, HarvestSearchQuery query, CancellationToken cancellationToken) {
        var items = new List<HarvestedCandidateItem>();
        string url = $"https://safebooru.org/index.php?page=dapi&s=post&q=index&json=1&tags={safeTags}&limit={Math.Clamp(query.MaxResults, 10, 100)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return items;

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("[")) return items;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;

        foreach (var el in doc.RootElement.EnumerateArray()) {
            if (items.Count >= query.MaxResults) break;

            string directory = el.TryGetProperty("directory", out var dir) ? dir.GetString() ?? "" : "";
            string image = el.TryGetProperty("image", out var img) ? img.GetString() ?? "" : "";
            string tagStr = el.TryGetProperty("tags", out var t) ? t.GetString() ?? "Safebooru Item" : "Safebooru Item";
            int width = el.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            int height = el.TryGetProperty("height", out var h) ? h.GetInt32() : 0;

            bool passesWidth = query.MinWidth <= 0 || width == 0 || width >= query.MinWidth;
            bool passesHeight = query.MinHeight <= 0 || height == 0 || height >= query.MinHeight;

            if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(image) && passesWidth && passesHeight) {
                string imgUrl = $"https://safebooru.org/images/{directory}/{image}";
                string thumbUrl = $"https://safebooru.org/thumbnails/{directory}/thumbnail_{image}";

                items.Add(new HarvestedCandidateItem {
                    SourceUrl = imgUrl,
                    ThumbnailUrl = thumbUrl,
                    Title = CleanTitle(tagStr.Length > 40 ? tagStr[..40] + "..." : tagStr),
                    Width = width,
                    Height = height,
                    SourceEngine = HarvestEngine.Safebooru,
                    ProviderId = "safebooru",
                    ProviderName = "Safebooru",
                    IsSelected = true
                });
            }
        }
        return items;
    }

    public async Task<List<HarvestedCandidateItem>> SearchDanbooruAsync(HarvestSearchQuery query, CancellationToken cancellationToken = default) {
        var items = new List<HarvestedCandidateItem>();
        string tags = !string.IsNullOrWhiteSpace(query.Tags) ? query.Tags : query.Query;
        if (string.IsNullOrWhiteSpace(tags)) return items;

        try {
            var rawTokens = tags.Split(new[] { ' ', ',', '+' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => Uri.EscapeDataString(t.Trim().ToLowerInvariant().Replace("-", "_")))
                                .ToList();
            if (rawTokens.Count == 0) return items;

            // Danbooru anonymous API limits searches to at most 2 tags (causes TagLimitError if >2)
            var limitedTokens = rawTokens.Take(2);
            string safeTags = string.Join("+", limitedTokens);
            string url = $"https://danbooru.donmai.us/posts.json?tags={safeTags}&limit={Math.Clamp(query.MaxResults, 10, 50)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "LoRAMancer/1.0 (contact@loramancer.local)");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return items;

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("[")) return items;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return items;

            foreach (var el in doc.RootElement.EnumerateArray()) {
                if (items.Count >= query.MaxResults) break;

                string fileUrl = el.TryGetProperty("file_url", out var f) ? f.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(fileUrl) && el.TryGetProperty("large_file_url", out var lf)) {
                    fileUrl = lf.GetString() ?? "";
                }
                string thumbUrl = el.TryGetProperty("preview_file_url", out var pf) ? pf.GetString() ?? fileUrl : fileUrl;
                string tagStr = el.TryGetProperty("tag_string_character", out var tc) && !string.IsNullOrWhiteSpace(tc.GetString())
                    ? tc.GetString()!
                    : (el.TryGetProperty("tag_string", out var ts) ? ts.GetString() ?? "Danbooru Art" : "Danbooru Art");

                int width = el.TryGetProperty("image_width", out var w) ? w.GetInt32() : 0;
                int height = el.TryGetProperty("image_height", out var h) ? h.GetInt32() : 0;

                bool passesWidth = query.MinWidth <= 0 || width == 0 || width >= query.MinWidth;
                bool passesHeight = query.MinHeight <= 0 || height == 0 || height >= query.MinHeight;

                if (!string.IsNullOrWhiteSpace(fileUrl) && passesWidth && passesHeight) {
                    items.Add(new HarvestedCandidateItem {
                        SourceUrl = fileUrl,
                        ThumbnailUrl = thumbUrl,
                        Title = CleanTitle(tagStr.Length > 40 ? tagStr[..40] + "..." : tagStr),
                        Width = width,
                        Height = height,
                        SourceEngine = HarvestEngine.Danbooru,
                        ProviderId = "danbooru",
                        ProviderName = "Danbooru",
                        IsSelected = true
                    });
                }
            }
        } catch {
            // Ignore Danbooru errors
        }

        return items;
    }

    public async Task<List<HarvestedCandidateItem>> SearchOpenverseAsync(HarvestSearchQuery query, CancellationToken cancellationToken = default) {
        var items = new List<HarvestedCandidateItem>();
        if (string.IsNullOrWhiteSpace(query.Query)) return items;

        try {
            string url = $"https://api.openverse.org/v1/images/?q={Uri.EscapeDataString(query.Query)}&page_size={Math.Clamp(query.MaxResults, 10, 50)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "LoRAMancer/1.0 (https://github.com/dworden42/LoRAMancer; contact@loramancer.local)");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return items;

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("results", out var resultsElem) && resultsElem.ValueKind == JsonValueKind.Array) {
                foreach (var el in resultsElem.EnumerateArray()) {
                    if (items.Count >= query.MaxResults) break;

                    string imgUrl = el.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    string thumbUrl = el.TryGetProperty("thumbnail", out var thm) ? thm.GetString() ?? imgUrl : imgUrl;
                    string title = el.TryGetProperty("title", out var ttl) ? ttl.GetString() ?? "Openverse Item" : "Openverse Item";
                    int width = el.TryGetProperty("width", out var w) && w.TryGetInt32(out int pw) ? pw : 0;
                    int height = el.TryGetProperty("height", out var h) && h.TryGetInt32(out int ph) ? ph : 0;

                    bool passesWidth = query.MinWidth <= 0 || width == 0 || width >= query.MinWidth;
                    bool passesHeight = query.MinHeight <= 0 || height == 0 || height >= query.MinHeight;

                    if (!string.IsNullOrWhiteSpace(imgUrl) && passesWidth && passesHeight) {
                        items.Add(new HarvestedCandidateItem {
                            SourceUrl = imgUrl,
                            ThumbnailUrl = thumbUrl,
                            Title = CleanTitle(title),
                            Width = width,
                            Height = height,
                            SourceEngine = HarvestEngine.Openverse,
                            ProviderId = "openverse",
                            ProviderName = "Openverse",
                            IsSelected = true
                        });
                    }
                }
            }
        } catch {
            // Ignore Openverse errors
        }

        return items;
    }

    public async Task<List<HarvestedCandidateItem>> SearchFlickrAsync(HarvestSearchQuery query, CancellationToken cancellationToken = default) {
        var items = new List<HarvestedCandidateItem>();
        string tag = !string.IsNullOrWhiteSpace(query.Query) ? query.Query : query.Tags;
        if (string.IsNullOrWhiteSpace(tag)) return items;

        try {
            var rawTokens = tag.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(t => Uri.EscapeDataString(t.Trim()))
                               .ToList();
            if (rawTokens.Count == 0) return items;

            // First attempt with tagmode=all so images match all keywords
            string safeTag = string.Join(",", rawTokens);
            string url = $"https://www.flickr.com/services/feeds/photos_public.gne?tags={safeTag}&tagmode=all&format=json&nojsoncallback=1";
            items = await QueryFlickrFeedAsync(url, query, cancellationToken);
        } catch {
            // Ignore Flickr errors
        }

        return items;
    }

    private async Task<List<HarvestedCandidateItem>> QueryFlickrFeedAsync(string url, HarvestSearchQuery query, CancellationToken cancellationToken) {
        var items = new List<HarvestedCandidateItem>();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "LoRAMancer/1.0 (https://github.com/dworden42/LoRAMancer; contact@loramancer.local)");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return items;

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("items", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array) {
            foreach (var el in itemsElem.EnumerateArray()) {
                if (items.Count >= query.MaxResults) break;

                string title = el.TryGetProperty("title", out var ttl) ? ttl.GetString() ?? "Flickr Photo" : "Flickr Photo";
                string thumb = "";
                string img = "";

                if (el.TryGetProperty("media", out var med) && med.TryGetProperty("m", out var m)) {
                    thumb = m.GetString() ?? "";
                    img = thumb.Replace("_m.jpg", "_b.jpg").Replace("_m.png", "_b.png");
                }

                if (!string.IsNullOrWhiteSpace(img)) {
                    items.Add(new HarvestedCandidateItem {
                        SourceUrl = img,
                        ThumbnailUrl = !string.IsNullOrWhiteSpace(thumb) ? thumb : img,
                        Title = CleanTitle(title),
                        SourceEngine = HarvestEngine.Flickr,
                        ProviderId = "flickr",
                        ProviderName = "Flickr",
                        IsSelected = true
                    });
                }
            }
        }
        return items;
    }

    public async Task<List<HarvestedCandidateItem>> SearchCustomRestAsync(HarvestProviderDefinition provider, HarvestSearchQuery query, CancellationToken cancellationToken = default) {
        var items = new List<HarvestedCandidateItem>();
        if (string.IsNullOrWhiteSpace(provider.SearchUrlTemplate)) return items;

        try {
            string filledUrl = provider.SearchUrlTemplate
                .Replace("{query}", Uri.EscapeDataString(query.Query))
                .Replace("{limit}", query.MaxResults.ToString())
                .Replace("{page}", "1");

            using var request = new HttpRequestMessage(HttpMethod.Get, filledUrl);
            request.Headers.Add("Accept", "application/json");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return items;

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            JsonElement rootArray = doc.RootElement;
            if (!string.IsNullOrWhiteSpace(provider.JsonResultsPath)) {
                string[] parts = provider.JsonResultsPath.Split('.');
                foreach (var part in parts) {
                    if (rootArray.TryGetProperty(part, out var next)) {
                        rootArray = next;
                    }
                }
            }

            if (rootArray.ValueKind == JsonValueKind.Array) {
                string imgKey = !string.IsNullOrWhiteSpace(provider.JsonImageUrlKey) ? provider.JsonImageUrlKey : "url";
                string thumbKey = !string.IsNullOrWhiteSpace(provider.JsonThumbUrlKey) ? provider.JsonThumbUrlKey : "thumbnail";
                string titleKey = !string.IsNullOrWhiteSpace(provider.JsonTitleKey) ? provider.JsonTitleKey : "title";

                foreach (var el in rootArray.EnumerateArray()) {
                    if (items.Count >= query.MaxResults) break;

                    string imgUrl = el.TryGetProperty(imgKey, out var iu) ? iu.GetString() ?? "" : "";
                    string thumbUrl = el.TryGetProperty(thumbKey, out var tu) ? tu.GetString() ?? imgUrl : imgUrl;
                    string title = el.TryGetProperty(titleKey, out var ttl) ? ttl.GetString() ?? $"{provider.Name} Image" : $"{provider.Name} Image";

                    if (!string.IsNullOrWhiteSpace(imgUrl)) {
                        items.Add(new HarvestedCandidateItem {
                            SourceUrl = imgUrl,
                            ThumbnailUrl = thumbUrl,
                            Title = CleanTitle(title),
                            SourceEngine = HarvestEngine.CustomRest,
                            ProviderId = provider.Id,
                            ProviderName = provider.Name,
                            IsSelected = true
                        });
                    }
                }
            }
        } catch {
            // Ignore custom REST errors
        }

        return items;
    }

    public async Task<List<HarvestedCandidateItem>> SearchPythonPluginAsync(
        HarvestProviderDefinition provider,
        HarvestSearchQuery query,
        CancellationToken cancellationToken = default) {

        var items = new List<HarvestedCandidateItem>();
        if (_pluginManagerService == null) return items;

        string targetId = !string.IsNullOrWhiteSpace(provider.PluginId)
            ? provider.PluginId
            : provider.Id.Replace("plugin_", "");

        var plugin = _pluginManagerService.Plugins.FirstOrDefault(p => string.Equals(p.Id, targetId, StringComparison.OrdinalIgnoreCase));
        if (plugin == null) return items;

        try {
            string escapedQuery = query.Query.Replace("\"", "\\\"");
            string args = $"--query \"{escapedQuery}\" --limit {query.MaxResults} --json";

            using var pluginCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pluginCts.CancelAfter(TimeSpan.FromSeconds(12));
            var (exitCode, stdout, stderr) = await _pluginManagerService.RunPythonPluginScriptAsync(plugin, args, pluginCts.Token);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return items;

            string cleanStdout = stdout.Trim();
            int firstBracket = cleanStdout.IndexOf('[');
            int lastBracket = cleanStdout.LastIndexOf(']');
            if (firstBracket >= 0 && lastBracket > firstBracket) {
                cleanStdout = cleanStdout.Substring(firstBracket, lastBracket - firstBracket + 1);
            }

            using var doc = JsonDocument.Parse(cleanStdout);
            if (doc.RootElement.ValueKind == JsonValueKind.Array) {
                foreach (var el in doc.RootElement.EnumerateArray()) {
                    if (items.Count >= query.MaxResults) break;

                    string sourceUrl = "";
                    if (el.TryGetProperty("sourceUrl", out var su)) sourceUrl = su.GetString() ?? "";
                    else if (el.TryGetProperty("url", out var u)) sourceUrl = u.GetString() ?? "";
                    else if (el.TryGetProperty("image", out var img)) sourceUrl = img.GetString() ?? "";

                    string thumbUrl = "";
                    if (el.TryGetProperty("thumbnailUrl", out var tu)) thumbUrl = tu.GetString() ?? "";
                    else if (el.TryGetProperty("thumbnail", out var thm)) thumbUrl = thm.GetString() ?? "";
                    else if (el.TryGetProperty("thumb", out var th)) thumbUrl = th.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(thumbUrl)) thumbUrl = sourceUrl;

                    string title = "";
                    if (el.TryGetProperty("title", out var ttl)) title = ttl.GetString() ?? "";
                    else if (el.TryGetProperty("name", out var nm)) title = nm.GetString() ?? "";
                    else if (el.TryGetProperty("caption", out var cap)) title = cap.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(title)) title = $"{plugin.Name} Image";

                    int width = 0;
                    if (el.TryGetProperty("width", out var w)) _ = w.TryGetInt32(out width);
                    int height = 0;
                    if (el.TryGetProperty("height", out var h)) _ = h.TryGetInt32(out height);

                    if (!string.IsNullOrWhiteSpace(sourceUrl) && (width == 0 || width >= query.MinWidth) && (height == 0 || height >= query.MinHeight)) {
                        items.Add(new HarvestedCandidateItem {
                            SourceUrl = sourceUrl,
                            ThumbnailUrl = thumbUrl,
                            Title = CleanTitle(title),
                            Width = width,
                            Height = height,
                            SourceEngine = HarvestEngine.PythonPlugin,
                            ProviderId = provider.Id,
                            ProviderName = provider.Name,
                            IsSelected = true
                        });
                    }
                }
            }
        } catch {
            // Suppress plugin execution / json parse errors
        }

        return items;
    }

    public List<HarvestedCandidateItem> ParseDirectUrls(HarvestSearchQuery query) {
        var items = new List<HarvestedCandidateItem>();
        if (string.IsNullOrWhiteSpace(query.DirectUrlsText)) return items;

        var lines = query.DirectUrlsText.Split(new[] { '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        int index = 1;
        foreach (var line in lines) {
            string trimmed = line.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) {
                items.Add(new HarvestedCandidateItem {
                    SourceUrl = trimmed,
                    ThumbnailUrl = trimmed,
                    Title = $"Direct Image {index++}",
                    SourceEngine = HarvestEngine.DirectUrls,
                    ProviderId = "direct",
                    ProviderName = "Direct URLs",
                    IsSelected = true
                });
            }
        }

        return items;
    }

    public async Task<List<string>> DownloadSelectedAsync(
        string destinationFolder,
        string? filenamePrefix = null,
        IProgress<HarvestDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) {

        IsDownloading = true;
        OnStateChanged?.Invoke();

        var downloadedPaths = new List<string>();
        Directory.CreateDirectory(destinationFolder);

        var selectedItems = Candidates.Where(c => c.IsSelected).ToList();
        int total = selectedItems.Count;
        int current = 0;
        string prefix = string.IsNullOrWhiteSpace(filenamePrefix) ? "harvest" : filenamePrefix.Trim();

        using var semaphore = new SemaphoreSlim(4);
        var tasks = new List<Task>();

        foreach (var item in selectedItems) {
            cancellationToken.ThrowIfCancellationRequested();

            tasks.Add(Task.Run(async () => {
                await semaphore.WaitAsync(cancellationToken);
                try {
                    int idx = Interlocked.Increment(ref current);
                    string ext = Path.GetExtension(new Uri(item.SourceUrl).AbsolutePath);
                    if (string.IsNullOrWhiteSpace(ext) || ext.Length > 5) ext = ".png";

                    string sanitizedTitle = Regex.Replace(item.Title, @"[^a-zA-Z0-9_\-]", "_");
                    if (sanitizedTitle.Length > 30) sanitizedTitle = sanitizedTitle[..30];

                    string fileName = $"{prefix}_{idx:D4}_{sanitizedTitle}{ext}";
                    string fullPath = Path.Combine(destinationFolder, fileName);
                    int counter = 1;
                    while (File.Exists(fullPath)) {
                        fileName = $"{prefix}_{idx:D4}_{sanitizedTitle}_{counter}{ext}";
                        fullPath = Path.Combine(destinationFolder, fileName);
                        counter++;
                    }

                    using var response = await _httpClient.GetAsync(item.SourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.IsSuccessStatusCode) {
                        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                        await using var fileStream = File.Create(fullPath);
                        await stream.CopyToAsync(fileStream, cancellationToken);

                        item.DownloadedFilePath = fullPath;
                        item.IsAlreadyDownloaded = true;
                        lock (downloadedPaths) {
                            downloadedPaths.Add(fullPath);
                        }
                    }

                    progress?.Report(new HarvestDownloadProgress {
                        CurrentIndex = current,
                        TotalCount = total,
                        CurrentFile = fileName,
                        IsComplete = current >= total
                    });
                } catch {
                    // Suppress individual download failure
                } finally {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        try {
            await Task.WhenAll(tasks);
        } finally {
            IsDownloading = false;
            OnStateChanged?.Invoke();
        }

        // Record successfully downloaded URLs into catalog history
        var successfulUrls = selectedItems
            .Where(i => !string.IsNullOrWhiteSpace(i.DownloadedFilePath))
            .Select(i => i.SourceUrl)
            .ToList();

        if (successfulUrls.Count > 0) {
            SaveCatalogHistory(destinationFolder, successfulUrls);
        }

        return downloadedPaths;
    }

    public async Task AuditCandidatesWithOllamaAsync(
        string ollamaUrl,
        string modelName,
        string auditPrompt,
        IProgress<HarvestDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) {

        IsAuditing = true;
        OnStateChanged?.Invoke();

        var items = Candidates.Where(c => c.IsSelected).ToList();
        int total = items.Count;
        int current = 0;

        try {
            foreach (var item in items) {
                cancellationToken.ThrowIfCancellationRequested();
                current++;

                try {
                    byte[]? imageBytes = null;
                    string fetchUrl = !string.IsNullOrWhiteSpace(item.ThumbnailUrl) ? item.ThumbnailUrl : item.SourceUrl;
                    using (var resp = await _httpClient.GetAsync(fetchUrl, cancellationToken)) {
                        if (resp.IsSuccessStatusCode) {
                            imageBytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
                        }
                    }

                    if (imageBytes != null && imageBytes.Length > 0) {
                        string base64 = Convert.ToBase64String(imageBytes);

                        var payload = new {
                            model = modelName,
                            prompt = $"{auditPrompt}\nRespond ONLY in JSON format: {{\"flagged\": true/false, \"reason\": \"brief explanation\"}}",
                            images = new[] { base64 },
                            stream = false,
                            format = "json"
                        };

                        string jsonPayload = JsonSerializer.Serialize(payload);
                        using var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
                        string endpoint = $"{ollamaUrl.TrimEnd('/')}/api/generate";
                        using var ollamaResp = await _httpClient.PostAsync(endpoint, content, cancellationToken);

                        if (ollamaResp.IsSuccessStatusCode) {
                            string resultJson = await ollamaResp.Content.ReadAsStringAsync(cancellationToken);
                            using var doc = JsonDocument.Parse(resultJson);
                            if (doc.RootElement.TryGetProperty("response", out var respText)) {
                                string raw = respText.GetString() ?? "";
                                using var parsedDoc = JsonDocument.Parse(raw);
                                bool flagged = parsedDoc.RootElement.TryGetProperty("flagged", out var flg) && flg.GetBoolean();
                                string reason = parsedDoc.RootElement.TryGetProperty("reason", out var rsn) ? rsn.GetString() ?? "" : "";

                                item.AuditStatus = flagged ? OllamaAuditStatus.Flagged : OllamaAuditStatus.Clean;
                                item.AuditReason = reason;
                                if (flagged) {
                                    item.IsSelected = false;
                                }
                            }
                        }
                    }
                } catch (Exception ex) {
                    item.AuditStatus = OllamaAuditStatus.NotAudited;
                    item.AuditReason = ex.Message;
                }

                progress?.Report(new HarvestDownloadProgress {
                    CurrentIndex = current,
                    TotalCount = total,
                    CurrentFile = item.Title,
                    IsComplete = current >= total
                });
            }
        } finally {
            IsAuditing = false;
            OnStateChanged?.Invoke();
        }
    }

    public void SelectAll(bool selected) {
        lock (_lock) {
            foreach (var item in _candidates) {
                item.IsSelected = selected;
            }
        }
        OnStateChanged?.Invoke();
    }

    public void InvertSelection() {
        lock (_lock) {
            foreach (var item in _candidates) {
                item.IsSelected = !item.IsSelected;
            }
        }
        OnStateChanged?.Invoke();
    }

    public void RemoveCandidate(string id) {
        lock (_lock) {
            _candidates.RemoveAll(c => c.Id == id);
        }
        OnStateChanged?.Invoke();
    }

    public void ClearCandidates() {
        lock (_lock) {
            _candidates.Clear();
        }
        OnStateChanged?.Invoke();
    }

    private static List<HarvestProviderDefinition> GetDefaultProviders() {
        return new List<HarvestProviderDefinition> {
            new() {
                Id = "wikimedia",
                Name = "Wikimedia Commons",
                Description = "Millions of public domain and Creative Commons high-res assets",
                Icon = "AccountBalance",
                Engine = HarvestEngine.Wikimedia,
                IsEnabled = true
            },
            new() {
                Id = "safebooru",
                Name = "Safebooru (Anime & Tags)",
                Description = "Safe anime, illustrations, and concept art indexed by tag",
                Icon = "Style",
                Engine = HarvestEngine.Safebooru,
                IsEnabled = true
            },
            new() {
                Id = "openverse",
                Name = "Openverse (CC Index)",
                Description = "Over 700 million openly licensed Creative Commons artworks and photos",
                Icon = "Public",
                Engine = HarvestEngine.Openverse,
                IsEnabled = true
            },
            new() {
                Id = "flickr",
                Name = "Flickr Public Feed",
                Description = "Creative Commons photography stream tagged by keywords",
                Icon = "PhotoLibrary",
                Engine = HarvestEngine.Flickr,
                IsEnabled = true
            },
            new() {
                Id = "reddit",
                Name = "Reddit Feeds",
                Description = "Harvest public posts and high-res images from any subreddit",
                Icon = "Forum",
                Engine = HarvestEngine.Reddit,
                IsEnabled = true
            },
            new() {
                Id = "duckduckgo",
                Name = "Web Image Search (DuckDuckGo + Bing)",
                Description = "High-resolution combined web search with multi-engine deduplication",
                Icon = "Search",
                Engine = HarvestEngine.DuckDuckGo,
                IsEnabled = true
            },
            new() {
                Id = "bing",
                Name = "Bing Images (Direct)",
                Description = "Direct Microsoft Bing high-resolution image index",
                Icon = "ImageSearch",
                Engine = HarvestEngine.DuckDuckGo,
                IsEnabled = true
            },
            new() {
                Id = "unsplash",
                Name = "Unsplash Photography",
                Description = "High-resolution curated photographs and reference aesthetics",
                Icon = "CameraAlt",
                Engine = HarvestEngine.Unsplash,
                IsEnabled = false
            },
            new() {
                Id = "danbooru",
                Name = "Danbooru (Tags)",
                Description = "Tag-based character and artwork database",
                Icon = "Collections",
                Engine = HarvestEngine.Danbooru,
                IsEnabled = true
            },
            new() {
                Id = "direct",
                Name = "Direct URLs",
                Description = "Batch parse and download arbitrary image URL lists",
                Icon = "Link",
                Engine = HarvestEngine.DirectUrls,
                IsEnabled = true
            }
        };
    }

    private static bool IsDirectImage(string url) {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string clean = url.Split('?')[0];
        return clean.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
               clean.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
               clean.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               clean.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanTitle(string raw) {
        if (string.IsNullOrWhiteSpace(raw)) return "Image";
        string unescaped = System.Net.WebUtility.HtmlDecode(raw);
        string cleaned = Regex.Replace(unescaped, @"<[^>]*>", "").Trim();
        if (cleaned.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) {
            cleaned = Path.GetFileNameWithoutExtension(cleaned);
        }
        return string.IsNullOrWhiteSpace(cleaned) ? "Image" : cleaned;
    }

    public static string GetCatalogsFilePath() {
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ".loramancer", "harvest_catalogs.json");
    }

    public async Task<List<HarvestCatalog>> GetCatalogsAsync() {
        string filePath = GetCatalogsFilePath();
        if (!File.Exists(filePath)) return new List<HarvestCatalog>();

        try {
            string json = await File.ReadAllTextAsync(filePath);
            var list = JsonSerializer.Deserialize<List<HarvestCatalog>>(json);
            return list ?? new List<HarvestCatalog>();
        } catch {
            return new List<HarvestCatalog>();
        }
    }

    public async Task SaveCatalogAsync(HarvestCatalog catalog) {
        var catalogs = await GetCatalogsAsync();
        int idx = catalogs.FindIndex(c => string.Equals(c.Id, catalog.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) {
            catalogs[idx] = catalog;
        } else {
            catalogs.Add(catalog);
        }

        string filePath = GetCatalogsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string json = JsonSerializer.Serialize(catalogs, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
        OnStateChanged?.Invoke();
    }

    public async Task DeleteCatalogAsync(string catalogId) {
        var catalogs = await GetCatalogsAsync();
        catalogs.RemoveAll(c => string.Equals(c.Id, catalogId, StringComparison.OrdinalIgnoreCase));

        string filePath = GetCatalogsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string json = JsonSerializer.Serialize(catalogs, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(filePath, json);
        OnStateChanged?.Invoke();
    }

    public async Task<int> SyncCatalogAsync(HarvestCatalog catalog, IProgress<HarvestDownloadProgress>? progress = null, CancellationToken cancellationToken = default) {
        var query = new HarvestSearchQuery {
            Query = catalog.Query,
            SelectedProviderIds = catalog.SelectedProviderIds != null && catalog.SelectedProviderIds.Count > 0
                ? catalog.SelectedProviderIds
                : new List<string> { "duckduckgo" },
            MaxResults = catalog.MaxResultsPerSync > 0 ? catalog.MaxResultsPerSync : 100,
            CatalogId = catalog.Id,
            CatalogDestinationFolder = catalog.DestinationFolder,
            OmitExistingInCatalog = catalog.AutoOmitExisting
        };

        var candidates = await SearchAsync(query, cancellationToken);
        if (candidates.Count == 0) return 0;

        foreach (var c in candidates) {
            c.IsSelected = true;
        }

        var downloaded = await DownloadSelectedAsync(
            catalog.DestinationFolder,
            catalog.Name,
            progress,
            cancellationToken);

        catalog.LastSyncedAt = DateTime.UtcNow;
        catalog.TotalDownloadedCount += downloaded.Count;
        await SaveCatalogAsync(catalog);

        return downloaded.Count;
    }

    public HashSet<string> LoadCatalogHistoryUrls(string destinationFolder) {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder)) return set;

        string historyFile = Path.Combine(destinationFolder, "download_history.json");
        if (File.Exists(historyFile)) {
            try {
                string json = File.ReadAllText(historyFile);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array) {
                    foreach (var el in doc.RootElement.EnumerateArray()) {
                        string? u = el.GetString();
                        if (!string.IsNullOrWhiteSpace(u)) set.Add(u.Trim());
                    }
                } else if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("downloadedUrls", out var arr)) {
                    foreach (var el in arr.EnumerateArray()) {
                        string? u = el.GetString();
                        if (!string.IsNullOrWhiteSpace(u)) set.Add(u.Trim());
                    }
                }
            } catch {
                // Ignore history read error
            }
        }
        return set;
    }

    public void SaveCatalogHistory(string destinationFolder, IEnumerable<string> urls) {
        if (string.IsNullOrWhiteSpace(destinationFolder)) return;
        Directory.CreateDirectory(destinationFolder);
        string historyFile = Path.Combine(destinationFolder, "download_history.json");

        var existing = LoadCatalogHistoryUrls(destinationFolder);
        foreach (var u in urls) {
            if (!string.IsNullOrWhiteSpace(u)) existing.Add(u.Trim());
        }

        try {
            string json = JsonSerializer.Serialize(existing.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(historyFile, json);
        } catch {
            // Ignore history write error
        }
    }

    public HashSet<string> GetExistingLocalFileNames(string destinationFolder) {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder)) return set;
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
        try {
            var files = Directory.EnumerateFiles(destinationFolder);
            foreach (var f in files) {
                if (exts.Contains(Path.GetExtension(f))) {
                    set.Add(Path.GetFileNameWithoutExtension(f));
                }
            }
        } catch { }
        return set;
    }

    public async Task<int> AuditCatalogWithOllamaAsync(
        HarvestCatalog catalog,
        string ollamaUrl,
        string modelName,
        string auditPrompt,
        IProgress<HarvestDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) {

        if (string.IsNullOrWhiteSpace(catalog.DestinationFolder) || !Directory.Exists(catalog.DestinationFolder)) return 0;

        IsAuditing = true;
        OnStateChanged?.Invoke();

        int flaggedCount = 0;
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif" };
        var files = Directory.EnumerateFiles(catalog.DestinationFolder)
            .Where(f => exts.Contains(Path.GetExtension(f)))
            .ToList();

        int total = files.Count;
        int current = 0;
        string quarantineDir = Path.Combine(catalog.DestinationFolder, "_flagged");

        try {
            foreach (var filePath in files) {
                cancellationToken.ThrowIfCancellationRequested();
                current++;
                string fileName = Path.GetFileName(filePath);

                try {
                    byte[] imageBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
                    if (imageBytes.Length > 0) {
                        string base64 = Convert.ToBase64String(imageBytes);

                        var payload = new {
                            model = modelName,
                            prompt = $"{auditPrompt}\nRespond ONLY in JSON format: {{\"flagged\": true/false, \"reason\": \"brief explanation\"}}",
                            images = new[] { base64 },
                            stream = false,
                            format = "json"
                        };

                        string jsonPayload = JsonSerializer.Serialize(payload);
                        using var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
                        string endpoint = $"{ollamaUrl.TrimEnd('/')}/api/generate";
                        using var ollamaResp = await _httpClient.PostAsync(endpoint, content, cancellationToken);

                        if (ollamaResp.IsSuccessStatusCode) {
                            string resultJson = await ollamaResp.Content.ReadAsStringAsync(cancellationToken);
                            using var doc = JsonDocument.Parse(resultJson);
                            if (doc.RootElement.TryGetProperty("response", out var respText)) {
                                string raw = respText.GetString() ?? "";
                                using var parsedDoc = JsonDocument.Parse(raw);
                                bool flagged = parsedDoc.RootElement.TryGetProperty("flagged", out var flg) && flg.GetBoolean();
                                string reason = parsedDoc.RootElement.TryGetProperty("reason", out var rsn) ? rsn.GetString() ?? "" : "";

                                if (flagged) {
                                    flaggedCount++;
                                    Directory.CreateDirectory(quarantineDir);
                                    string destPath = Path.Combine(quarantineDir, fileName);
                                    int counter = 1;
                                    while (File.Exists(destPath)) {
                                        string withoutExt = Path.GetFileNameWithoutExtension(fileName);
                                        string ext = Path.GetExtension(fileName);
                                        destPath = Path.Combine(quarantineDir, $"{withoutExt}_{counter}{ext}");
                                        counter++;
                                    }
                                    File.Move(filePath, destPath);

                                    try {
                                        string notePath = Path.ChangeExtension(destPath, ".flagged.txt");
                                        await File.WriteAllTextAsync(notePath, $"Flagged by {modelName}: {reason}", cancellationToken);
                                    } catch {
                                        // Ignore note write failure
                                    }
                                }
                            }
                        }
                    }
                } catch {
                    // Suppress individual file audit error
                }

                progress?.Report(new HarvestDownloadProgress {
                    CurrentIndex = current,
                    TotalCount = total,
                    CurrentFile = fileName,
                    IsComplete = current >= total
                });
            }

            catalog.TotalDownloadedCount = Math.Max(0, total - flaggedCount);
            await SaveCatalogAsync(catalog);
        } finally {
            IsAuditing = false;
            OnStateChanged?.Invoke();
        }

        return flaggedCount;
    }
}
