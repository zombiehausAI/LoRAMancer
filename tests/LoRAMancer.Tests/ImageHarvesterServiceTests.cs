using System.Net;
using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public sealed class ImageHarvesterServiceTests {
    [Fact]
    public void ParseDirectUrls_ExtractsValidHttpAndHttpsImageUrls() {
        var service = new ImageHarvesterService();
        var query = new HarvestSearchQuery {
            Engine = HarvestEngine.DirectUrls,
            DirectUrlsText = """
            https://example.com/images/cat.png
            http://example.com/photos/dog.jpg
            not_a_valid_url
            https://example.com/art/cyberpunk.webp
            """
        };

        var results = service.ParseDirectUrls(query);

        Assert.Equal(3, results.Count);
        Assert.Equal("https://example.com/images/cat.png", results[0].SourceUrl);
        Assert.Equal("http://example.com/photos/dog.jpg", results[1].SourceUrl);
        Assert.Equal("https://example.com/art/cyberpunk.webp", results[2].SourceUrl);
        Assert.True(results.All(r => r.IsSelected));
        Assert.True(results.All(r => r.SourceEngine == HarvestEngine.DirectUrls));
    }

    [Fact]
    public async Task SelectionHelpers_ManageCandidatesCorrectly() {
        var service = new ImageHarvesterService();
        var query = new HarvestSearchQuery {
            Engine = HarvestEngine.DirectUrls,
            DirectUrlsText = "https://example.com/1.png https://example.com/2.png https://example.com/3.png"
        };

        var results = service.ParseDirectUrls(query);
        // Load into candidates by searching DirectUrls
        await service.SearchAsync(query);

        Assert.Equal(3, service.Candidates.Count);
        Assert.True(service.Candidates.All(c => c.IsSelected));

        // Deselect all
        service.SelectAll(false);
        Assert.True(service.Candidates.All(c => !c.IsSelected));

        // Invert selection
        service.InvertSelection();
        Assert.True(service.Candidates.All(c => c.IsSelected));

        // Remove one candidate
        string firstId = service.Candidates[0].Id;
        service.RemoveCandidate(firstId);
        Assert.Equal(2, service.Candidates.Count);
        Assert.DoesNotContain(service.Candidates, c => c.Id == firstId);

        // Clear candidates
        service.ClearCandidates();
        Assert.Empty(service.Candidates);
    }

    [Fact]
    public async Task DownloadSelectedAsync_DownloadsAndReportsProgress() {
        // Arrange custom HTTP handler returning dummy image bytes
        byte[] dummyPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var mockHandler = new MockHttpHandler(dummyPng);
        using var client = new HttpClient(mockHandler);
        var service = new ImageHarvesterService(client);

        string tempDir = Path.Combine(Path.GetTempPath(), $"harvester_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try {
            var query = new HarvestSearchQuery {
                Engine = HarvestEngine.DirectUrls,
                DirectUrlsText = "https://example.com/img1.png\nhttps://example.com/img2.jpg"
            };
            await service.SearchAsync(query);

            int progressReports = 0;
            var progress = new Progress<HarvestDownloadProgress>(_ => {
                progressReports++;
            });

            // Act
            var downloaded = await service.DownloadSelectedAsync(tempDir, "test_prefix", progress);

            // Assert
            Assert.Equal(2, downloaded.Count);
            foreach (var path in downloaded) {
                Assert.True(File.Exists(path));
                var bytes = await File.ReadAllBytesAsync(path);
                Assert.Equal(dummyPng, bytes);
            }
        } finally {
            if (Directory.Exists(tempDir)) {
                try {
                    Directory.Delete(tempDir, recursive: true);
                } catch { }
            }
        }
    }

    [Fact]
    public async Task SearchAsync_HandlesEmptyQueryGracefully() {
        var service = new ImageHarvesterService();
        var emptyQuery = new HarvestSearchQuery {
            Engine = HarvestEngine.DuckDuckGo,
            Query = ""
        };

        var results = await service.SearchAsync(emptyQuery);
        Assert.Empty(results);
    }

    [Fact]
    public void GetProviders_ReturnsBuiltInProviders() {
        var service = new ImageHarvesterService();
        var providers = service.GetProviders();

        Assert.True(providers.Count >= 8);
        Assert.Contains(providers, p => p.Id == "duckduckgo");
        Assert.Contains(providers, p => p.Id == "reddit");
        Assert.Contains(providers, p => p.Id == "wikimedia");
        Assert.Contains(providers, p => p.Id == "unsplash");
        Assert.Contains(providers, p => p.Id == "safebooru");
        Assert.Contains(providers, p => p.Id == "danbooru");
        Assert.Contains(providers, p => p.Id == "openverse");
        Assert.Contains(providers, p => p.Id == "flickr");
        Assert.Contains(providers, p => p.Id == "direct");
        Assert.True(providers.All(p => p.IsEnabled));
    }

    [Fact]
    public async Task CustomProvider_CanBeAddedAndRemoved() {
        string tempSettingsDir = Path.Combine(Path.GetTempPath(), $"settings_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempSettingsDir);
        try {
            string settingsFile = Path.Combine(tempSettingsDir, "settings.json");
            var settingsService = new SettingsService(null, settingsFile);
            var service = new ImageHarvesterService(settingsService);

            var custom = new HarvestProviderDefinition {
                Id = "custom_danbooru_mirror",
                Name = "Danbooru Mirror",
                SearchUrlTemplate = "https://example.com/api?q={query}",
                JsonImageUrlKey = "url"
            };

            await service.AddCustomProviderAsync(custom);

            var providers = service.GetProviders();
            Assert.Contains(providers, p => p.Id == "custom_danbooru_mirror" && p.IsCustom);

            await service.RemoveCustomProviderAsync("custom_danbooru_mirror");

            providers = service.GetProviders();
            Assert.DoesNotContain(providers, p => p.Id == "custom_danbooru_mirror");
        } finally {
            if (Directory.Exists(tempSettingsDir)) {
                try {
                    Directory.Delete(tempSettingsDir, true);
                } catch { }
            }
        }
    }

    [Fact]
    public async Task SearchCustomRestAsync_ParsesJsonPayload() {
        string json = """
        [
            { "url": "https://example.com/custom1.png", "thumbnail": "https://example.com/t1.png", "title": "Custom 1" },
            { "url": "https://example.com/custom2.png", "thumbnail": "https://example.com/t2.png", "title": "Custom 2" }
        ]
        """;

        var mockHandler = new MockHttpHandler(System.Text.Encoding.UTF8.GetBytes(json));
        using var client = new HttpClient(mockHandler);
        var service = new ImageHarvesterService(client);

        var provider = new HarvestProviderDefinition {
            Id = "test_custom",
            Name = "Test Custom",
            SearchUrlTemplate = "https://example.com/search?q={query}",
            JsonImageUrlKey = "url",
            JsonThumbUrlKey = "thumbnail",
            JsonTitleKey = "title"
        };

        var query = new HarvestSearchQuery {
            Query = "art",
            MaxResults = 10
        };

        var results = await service.SearchCustomRestAsync(provider, query);

        Assert.Equal(2, results.Count);
        Assert.Equal("https://example.com/custom1.png", results[0].SourceUrl);
        Assert.Equal("https://example.com/t1.png", results[0].ThumbnailUrl);
        Assert.Equal("Custom 1", results[0].Title);
        Assert.Equal("test_custom", results[0].ProviderId);
    }

    [Fact]
    public async Task GetProviders_DiscoversScraperPluginsFromPluginManagerService() {
        ProcessRunner runner = new();
        using PluginManagerService pluginService = new(runner);
        await pluginService.DiscoverAndInitializePluginsAsync();

        var service = new ImageHarvesterService(null, pluginService, null);
        var providers = service.GetProviders();

        Assert.Contains(providers, p => p.Engine == HarvestEngine.PythonPlugin && p.Id == "plugin_civitai-scraper");
        var civitai = providers.First(p => p.Id == "plugin_civitai-scraper");
        Assert.Equal("Civitai Community Showcase", civitai.Name);
        Assert.Equal("civitai-scraper", civitai.PluginId);
    }

    [Fact]
    public void CatalogHistory_SavesAndLoadsDownloadHistoryCorrectly() {
        var service = new ImageHarvesterService();
        string tempDir = Path.Combine(Path.GetTempPath(), $"catalog_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try {
            var urls = new[] { "https://example.com/img1.png", "https://example.com/img2.jpg" };
            service.SaveCatalogHistory(tempDir, urls);

            var loaded = service.LoadCatalogHistoryUrls(tempDir);
            Assert.Equal(2, loaded.Count);
            Assert.Contains("https://example.com/img1.png", loaded);
            Assert.Contains("https://example.com/img2.jpg", loaded);

            // Append another URL and ensure no duplicate entries
            service.SaveCatalogHistory(tempDir, new[] { "https://example.com/img2.jpg", "https://example.com/img3.webp" });
            var updated = service.LoadCatalogHistoryUrls(tempDir);
            Assert.Equal(3, updated.Count);
            Assert.Contains("https://example.com/img3.webp", updated);
        } finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task SearchAsync_WithCatalogDeduplication_OmitsExistingImages() {
        var service = new ImageHarvesterService();
        string tempDir = Path.Combine(Path.GetTempPath(), $"catalog_dedup_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try {
            // Seed history with img1.png
            service.SaveCatalogHistory(tempDir, new[] { "https://example.com/img1.png" });

            var query = new HarvestSearchQuery {
                Engine = HarvestEngine.DirectUrls,
                DirectUrlsText = "https://example.com/img1.png\nhttps://example.com/img2.png\nhttps://example.com/img3.png",
                CatalogDestinationFolder = tempDir,
                OmitExistingInCatalog = true
            };

            var results = await service.SearchAsync(query);

            // img1.png should be omitted because it's already in history!
            Assert.Equal(2, results.Count);
            Assert.DoesNotContain(results, r => r.SourceUrl == "https://example.com/img1.png");
            Assert.Contains(results, r => r.SourceUrl == "https://example.com/img2.png");
            Assert.Contains(results, r => r.SourceUrl == "https://example.com/img3.png");
        } finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private sealed class MockHttpHandler : HttpMessageHandler {
        private readonly byte[] _content;

        public MockHttpHandler(byte[] content) {
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var response = new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new ByteArrayContent(_content)
            };
            return Task.FromResult(response);
        }
    }
}
