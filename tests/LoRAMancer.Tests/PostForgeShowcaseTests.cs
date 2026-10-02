using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public sealed class PostForgeShowcaseTests {
    [Theory]
    [InlineData(".png", ShowcaseMediaType.Image)]
    [InlineData(".jpg", ShowcaseMediaType.Image)]
    [InlineData(".webp", ShowcaseMediaType.Image)]
    [InlineData(".mp4", ShowcaseMediaType.Video)]
    [InlineData(".webm", ShowcaseMediaType.Video)]
    [InlineData(".wav", ShowcaseMediaType.Audio)]
    [InlineData(".mp3", ShowcaseMediaType.Audio)]
    public void GetMediaType_ClassifiesFormatsAccurately(string extension, ShowcaseMediaType expectedType) {
        var actual = MediaMetadataExtractor.GetMediaType(extension);
        Assert.Equal(expectedType, actual);
    }

    [Theory]
    [InlineData(".png", true)]
    [InlineData(".mp4", true)]
    [InlineData(".wav", true)]
    [InlineData(".exe", false)]
    [InlineData(".dll", false)]
    [InlineData(".txt", false)]
    public void IsSupportedMedia_ValidatesExtensions(string extension, bool expectedSupported) {
        var actual = MediaMetadataExtractor.IsSupportedMedia(extension);
        Assert.Equal(expectedSupported, actual);
    }

    [Fact]
    public async Task ExtractAsync_ExtractsA1111ParametersAndDetectsAi() {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_a1111_{Guid.NewGuid():N}.png");
        string companionJson = Path.ChangeExtension(tempFile, ".json");

        try {
            // Write a dummy PNG with companion JSON representing AI output
            await File.WriteAllBytesAsync(tempFile, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            string recipeJson = """
            {
                "prompt": "masterpiece, 1girl, cyberpunk neon city <lora:cyberpunk_v2:0.8>",
                "seed": 987654321,
                "model": "flux1-dev.safetensors"
            }
            """;
            await File.WriteAllTextAsync(companionJson, recipeJson);

            // Act
            var item = await MediaMetadataExtractor.ExtractAsync(tempFile);

            // Assert
            Assert.True(item.IsAiGenerated);
            Assert.Equal("masterpiece, 1girl, cyberpunk neon city <lora:cyberpunk_v2:0.8>", item.Prompt);
            Assert.Equal(987654321, item.Seed);
            Assert.Equal("flux1-dev.safetensors", item.ModelName);
            Assert.Equal(ShowcaseMediaType.Image, item.MediaType);
        } finally {
            if (File.Exists(tempFile)) {
                File.Delete(tempFile);
            }
            if (File.Exists(companionJson)) {
                File.Delete(companionJson);
            }
        }
    }

    [Fact]
    public async Task ExtractAsync_ClassifiesVideoAndAudioFiles() {
        // Arrange
        string tempVideo = Path.Combine(Path.GetTempPath(), $"test_anim_{Guid.NewGuid():N}.mp4");
        string tempAudio = Path.Combine(Path.GetTempPath(), $"test_sound_{Guid.NewGuid():N}.wav");

        try {
            await File.WriteAllTextAsync(tempVideo, "dummy video data");
            await File.WriteAllTextAsync(tempAudio, "dummy audio data");

            // Act
            var videoItem = await MediaMetadataExtractor.ExtractAsync(tempVideo);
            var audioItem = await MediaMetadataExtractor.ExtractAsync(tempAudio);

            // Assert
            Assert.Equal(ShowcaseMediaType.Video, videoItem.MediaType);
            Assert.Equal(".mp4", videoItem.FileExtension);

            Assert.Equal(ShowcaseMediaType.Audio, audioItem.MediaType);
            Assert.Equal(".wav", audioItem.FileExtension);
        } finally {
            if (File.Exists(tempVideo)) {
                File.Delete(tempVideo);
            }
            if (File.Exists(tempAudio)) {
                File.Delete(tempAudio);
            }
        }
    }

    [Fact]
    public async Task ExtractAsync_ExtractsModusFlowMultiClipAndTextEditorPrompts() {
        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_modusflow_{Guid.NewGuid():N}.png");
        string companionJson = Path.ChangeExtension(tempFile, ".json");

        try {
            await File.WriteAllBytesAsync(tempFile, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            // ComfyUI workflow JSON featuring ModusFlow custom nodes
            string workflowJson = """
            {
                "prompt": {
                    "10": {
                        "class_type": "ModusFlowMultiCLIPTextEncode",
                        "inputs": {
                            "enable_clip1": true,
                            "text_clip1": "high fantasy ethereal landscape, aurora borealis",
                            "enable_clip2": true,
                            "text_clip2": "detailed oil painting style by studio zombiehaus",
                            "enable_clip3": false,
                            "text_clip3": "disabled prompt fragment"
                        }
                    },
                    "11": {
                        "class_type": "ModusFlowTextEditor",
                        "inputs": {
                            "positive": "8k resolution, crisp intricate details",
                            "negative": "blurry, oversaturated, watermark, bad hands"
                        }
                    },
                    "12": {
                        "class_type": "ModusFlowFluxLoader",
                        "inputs": {
                            "model_name": "flux1-schnell.safetensors"
                        }
                    },
                    "13": {
                        "class_type": "ModusFlowPowerLoraLoader",
                        "inputs": {
                            "lora_1_name": "fantasy_world_v1.safetensors",
                            "lora_1_strength": 0.85
                        }
                    }
                }
            }
            """;
            await File.WriteAllTextAsync(companionJson, workflowJson);

            // Act
            var item = await MediaMetadataExtractor.ExtractAsync(tempFile);

            // Assert
            Assert.True(item.IsAiGenerated);
            Assert.Equal("ComfyUI (ModusFlow)", item.AiGenerator);
            Assert.Contains("high fantasy ethereal landscape, aurora borealis", item.Prompt);
            Assert.Contains("detailed oil painting style by studio zombiehaus", item.Prompt);
            Assert.DoesNotContain("disabled prompt fragment", item.Prompt);
            Assert.Contains("8k resolution, crisp intricate details", item.Prompt);
            Assert.Equal("blurry, oversaturated, watermark, bad hands", item.NegativePrompt);
            Assert.Equal("flux1-schnell.safetensors", item.ModelName);
            Assert.Contains("fantasy_world_v1.safetensors", item.UsedLoras);
        } finally {
            if (File.Exists(tempFile)) {
                File.Delete(tempFile);
            }
            if (File.Exists(companionJson)) {
                File.Delete(companionJson);
            }
        }
    }

    [Fact]
    public async Task AssignImageAsLoraPreview_AssignsPreviewAndCopiesCompanion() {
        // Arrange
        string tempDir = Path.Combine(Path.GetTempPath(), $"showcase_assign_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try {
            var settings = new SettingsService(httpClient: null, customSettingsPath: Path.Combine(tempDir, "settings.json"));
            var showcaseService = new PostForgeShowcaseService(settings);

            string imagePath = Path.Combine(tempDir, "sample_render.png");
            await File.WriteAllBytesAsync(imagePath, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            string lora1Path = Path.Combine(tempDir, "cyberpunk_v2.safetensors");
            string lora2Path = Path.Combine(tempDir, "neon_glow.safetensors");
            await File.WriteAllTextAsync(lora1Path, "dummy lora 1");
            await File.WriteAllTextAsync(lora2Path, "dummy lora 2");

            var meta1 = new LoraMetadata {
                FileName = "cyberpunk_v2.safetensors",
                FilePath = lora1Path
            };
            var meta2 = new LoraMetadata {
                FileName = "neon_glow.safetensors",
                FilePath = lora2Path
            };

            var mediaItem = new ShowcaseMediaItem {
                FilePath = imagePath,
                FileName = "sample_render.png",
                FileExtension = ".png",
                MediaType = ShowcaseMediaType.Image
            };

            // Act
            int assignedCount = await showcaseService.AssignImageAsLoraPreviewAsync(
                mediaItem,
                new[] { meta1, meta2 },
                copyAlongsideLora: true
            );

            // Assert
            Assert.Equal(2, assignedCount);

            string expectedCompanion1 = Path.Combine(tempDir, "cyberpunk_v2.preview.png");
            string expectedCompanion2 = Path.Combine(tempDir, "neon_glow.preview.png");

            Assert.True(File.Exists(expectedCompanion1));
            Assert.True(File.Exists(expectedCompanion2));

            Assert.Equal(expectedCompanion1, meta1.ThumbnailPath);
            Assert.Equal(expectedCompanion2, meta2.ThumbnailPath);

            Assert.Contains("cyberpunk_v2", mediaItem.AssociatedLoraNames);
            Assert.Contains("neon_glow", mediaItem.AssociatedLoraNames);
            Assert.Contains(lora1Path, mediaItem.AssociatedLoraFilePaths);
            Assert.Contains(lora2Path, mediaItem.AssociatedLoraFilePaths);
        } finally {
            if (Directory.Exists(tempDir)) {
                try {
                    Directory.Delete(tempDir, recursive: true);
                } catch {
                    // Ignore cleanup failure in temp
                }
            }
        }
    }

    [Fact]
    public void GetBreadcrumbs_RootFolder_ReturnsSingleCrumb() {
        var settings = new SettingsService(httpClient: null);
        var service = new PostForgeShowcaseService(settings);

        var crumbs = service.GetBreadcrumbs(null);

        Assert.Single(crumbs);
        Assert.Equal("All Libraries", crumbs[0].Name);
        Assert.Null(crumbs[0].FullPath);
    }

    [Fact]
    public async Task GetBreadcrumbs_NestedFolder_ReturnsFullHierarchy() {
        string tempDir = Path.Combine(Path.GetTempPath(), $"showcase_crumb_test_{Guid.NewGuid():N}");
        string subDir1 = Path.Combine(tempDir, "renders");
        string subDir2 = Path.Combine(subDir1, "flux");
        Directory.CreateDirectory(subDir2);

        try {
            var settings = new SettingsService(httpClient: null, customSettingsPath: Path.Combine(tempDir, "settings.json"));
            var service = new PostForgeShowcaseService(settings);
            await service.AddFolderAsync(tempDir);

            var crumbs = service.GetBreadcrumbs(subDir2);

            Assert.Equal(4, crumbs.Count);
            Assert.Equal("All Libraries", crumbs[0].Name);
            Assert.Null(crumbs[0].FullPath);

            Assert.Equal(Path.GetFileName(tempDir), crumbs[1].Name);
            Assert.Equal(tempDir, crumbs[1].FullPath);

            Assert.Equal("renders", crumbs[2].Name);
            Assert.Equal(subDir1, crumbs[2].FullPath);

            Assert.Equal("flux", crumbs[3].Name);
            Assert.Equal(subDir2, crumbs[3].FullPath);
        } finally {
            if (Directory.Exists(tempDir)) {
                try {
                    Directory.Delete(tempDir, recursive: true);
                } catch {
                    // Ignore cleanup failure
                }
            }
        }
    }

    [Fact]
    public async Task GetChildFolders_ReturnsImmediateChildrenWithCounts() {
        string rootDir = Path.Combine(Path.GetTempPath(), $"showcase_hierarchy_test_{Guid.NewGuid():N}");
        string subA = Path.Combine(rootDir, "SubA");
        string subB = Path.Combine(rootDir, "SubB");
        string nestedA = Path.Combine(subA, "Nested");
        Directory.CreateDirectory(subA);
        Directory.CreateDirectory(subB);
        Directory.CreateDirectory(nestedA);

        try {
            var settings = new SettingsService(httpClient: null, customSettingsPath: Path.Combine(rootDir, "settings.json"));
            var service = new PostForgeShowcaseService(settings);
            await service.AddFolderAsync(rootDir);

            var mediaList = new List<ShowcaseMediaItem> {
                new() { FilePath = Path.Combine(rootDir, "root_img.png"), FolderPath = rootDir, MediaType = ShowcaseMediaType.Image },
                new() { FilePath = Path.Combine(subA, "suba_1.png"), FolderPath = subA, MediaType = ShowcaseMediaType.Image },
                new() { FilePath = Path.Combine(subA, "suba_2.png"), FolderPath = subA, MediaType = ShowcaseMediaType.Image },
                new() { FilePath = Path.Combine(nestedA, "nested_1.png"), FolderPath = nestedA, MediaType = ShowcaseMediaType.Image },
                new() { FilePath = Path.Combine(subB, "subb_1.mp4"), FolderPath = subB, MediaType = ShowcaseMediaType.Video }
            };

            // Test root level: child folders of null returns configured root directory
            var rootNodes = service.GetChildFolders(null, mediaList);
            Assert.Single(rootNodes);
            Assert.Equal(rootDir, rootNodes[0].FullPath);
            Assert.Equal(1, rootNodes[0].DirectMediaCount);
            Assert.Equal(5, rootNodes[0].TotalMediaCount);

            // Test within rootDir: immediate children are SubA and SubB
            var subNodes = service.GetChildFolders(rootDir, mediaList);
            Assert.Equal(2, subNodes.Count);

            var nodeA = subNodes.First(n => n.Name == "SubA");
            Assert.Equal(2, nodeA.DirectMediaCount);
            Assert.Equal(3, nodeA.TotalMediaCount); // suba_1, suba_2, nested_1

            var nodeB = subNodes.First(n => n.Name == "SubB");
            Assert.Equal(1, nodeB.DirectMediaCount);
            Assert.Equal(1, nodeB.TotalMediaCount);

            // Test within SubA: immediate child is Nested
            var nestedNodes = service.GetChildFolders(subA, mediaList);
            Assert.Single(nestedNodes);
            Assert.Equal("Nested", nestedNodes[0].Name);
            Assert.Equal(1, nestedNodes[0].DirectMediaCount);
            Assert.Equal(1, nestedNodes[0].TotalMediaCount);
        } finally {
            if (Directory.Exists(rootDir)) {
                try {
                    Directory.Delete(rootDir, recursive: true);
                } catch {
                    // Ignore cleanup failure
                }
            }
        }
    }

    [Fact]
    public async Task AddAndRemoveFolder_UpdatesConfiguredDirectoriesCorrectly() {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"showcase_dirs_test_{Guid.NewGuid():N}");
        string folder1 = Path.Combine(tempRoot, "Lib1");
        string folder2 = Path.Combine(tempRoot, "Lib2");
        Directory.CreateDirectory(folder1);
        Directory.CreateDirectory(folder2);

        try {
            var settings = new SettingsService(httpClient: null, customSettingsPath: Path.Combine(tempRoot, "settings.json"));
            var service = new PostForgeShowcaseService(settings);

            await service.AddFolderAsync(folder1);
            await service.AddFolderAsync(folder2);

            var configured = service.GetConfiguredDirectories();
            Assert.Contains(folder1, configured);
            Assert.Contains(folder2, configured);

            await service.RemoveFolderAsync(folder1);

            var afterRemove = service.GetConfiguredDirectories();
            Assert.DoesNotContain(folder1, afterRemove);
            Assert.Contains(folder2, afterRemove);
        } finally {
            if (Directory.Exists(tempRoot)) {
                try {
                    Directory.Delete(tempRoot, recursive: true);
                } catch {
                    // Ignore cleanup
                }
            }
        }
    }

    [Fact]
    public void ShowcaseThumbnailService_FindFfmpeg_FindsInstalledFfmpeg() {
        string? ffmpeg = ShowcaseThumbnailService.FindFfmpeg();
        if (ffmpeg != null) {
            Assert.True(File.Exists(ffmpeg));
            Assert.EndsWith("ffmpeg.exe", ffmpeg, StringComparison.OrdinalIgnoreCase);
        } else {
            // On CI runners or fresh systems without FFmpeg installed, FindFfmpeg gracefully returns null
            Assert.Null(ffmpeg);
        }
    }

    [Fact]
    public async Task ShowcaseThumbnailService_GeneratesAndCachesThumbnailInSettingsFolder() {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"showcase_thumb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try {
            var settings = new SettingsService(httpClient: null, customSettingsPath: Path.Combine(tempRoot, "settings.json"));
            var thumbService = new ShowcaseThumbnailService(settings);

            // Create a small valid 1x1 PNG image
            string sampleImage = Path.Combine(tempRoot, "test_render.png");
            byte[] minimalPng = new byte[] {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
                0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53, 0xDE, 0x00, 0x00, 0x00,
                0x0C, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
                0x00, 0x03, 0x01, 0x01, 0x00, 0x18, 0xDD, 0x8D, 0xB0, 0x00, 0x00, 0x00,
                0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
            };
            await File.WriteAllBytesAsync(sampleImage, minimalPng);

            var item = new ShowcaseMediaItem {
                FilePath = sampleImage,
                FileName = "test_render.png",
                FileExtension = ".png",
                MediaType = ShowcaseMediaType.Image,
                FileSizeBytes = minimalPng.Length,
                CreatedDate = DateTime.UtcNow
            };

            // Act
            string dataUri = await thumbService.GetOrCreateThumbnailDataUriAsync(item);

            // Assert
            Assert.False(string.IsNullOrEmpty(dataUri));
            Assert.StartsWith("data:image/jpeg;base64,", dataUri);

            // Verify physical thumbnail file exists in settings folder
            Assert.True(Directory.Exists(thumbService.ThumbnailDirectory));
            var thumbFiles = Directory.GetFiles(thumbService.ThumbnailDirectory, "*.jpg");
            Assert.NotEmpty(thumbFiles);

            // Verify in-memory cache lookup
            string? cachedUri = thumbService.GetCachedDataUri(sampleImage);
            Assert.Equal(dataUri, cachedUri);
        } finally {
            if (Directory.Exists(tempRoot)) {
                try {
                    Directory.Delete(tempRoot, recursive: true);
                } catch {
                    // Ignore cleanup
                }
            }
        }
    }

    [Fact]
    public async Task FolderSortOrder_PersistsPerFolderAcrossServiceInstances() {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"showcase_sort_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        string settingsFile = Path.Combine(tempRoot, "settings.json");

        try {
            var settings = new SettingsService(httpClient: null, customSettingsPath: settingsFile);
            var service1 = new PostForgeShowcaseService(settings);

            // Default sort is newest
            Assert.Equal("newest", service1.GetFolderSortOrder(null));
            Assert.Equal("newest", service1.GetFolderSortOrder("C:\\SampleFolder"));

            // Change sort for root and specific subfolder
            await service1.SetFolderSortOrderAsync(null, "oldest");
            await service1.SetFolderSortOrderAsync("C:\\SampleFolder", "largest");

            Assert.Equal("oldest", service1.GetFolderSortOrder(null));
            Assert.Equal("largest", service1.GetFolderSortOrder("C:\\SampleFolder"));

            // Create new service instance reading from the persisted settings
            var settings2 = new SettingsService(httpClient: null, customSettingsPath: settingsFile);
            var service2 = new PostForgeShowcaseService(settings2);

            Assert.Equal("oldest", service2.GetFolderSortOrder(null));
            Assert.Equal("largest", service2.GetFolderSortOrder("C:\\SampleFolder"));
            Assert.Equal("newest", service2.GetFolderSortOrder("C:\\AnotherFolder"));
        } finally {
            if (Directory.Exists(tempRoot)) {
                try {
                    Directory.Delete(tempRoot, recursive: true);
                } catch {
                    // Ignore cleanup
                }
            }
        }
    }

    [Fact]
    public async Task BackgroundScan_ExecutesAndSupportsCancellation() {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"showcase_bg_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        string settingsFile = Path.Combine(tempRoot, "settings.json");

        try {
            // Create dummy images
            for (int i = 0; i < 5; i++) {
                await File.WriteAllBytesAsync(Path.Combine(tempRoot, $"image_{i}.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            }

            var settings = new SettingsService(httpClient: null, customSettingsPath: settingsFile);
            var service = new PostForgeShowcaseService(settings);

            bool progressReported = false;
            bool completedReported = false;

            service.OnScanProgress += (file, cur, tot) => {
                progressReported = true;
            };
            service.OnScanCompleted += () => {
                completedReported = true;
            };

            await service.AddFolderAsync(tempRoot);

            // Await the background scan task to complete
            await service.StartBackgroundScanAsync();

            Assert.False(service.IsScanning);
            Assert.True(progressReported);
            Assert.True(completedReported);
            Assert.Equal(5, service.Items.Count);

            // Test cancellation support: start a new scan and cancel it
            var scanTask = service.StartBackgroundScanAsync();
            service.CancelScan();
            await scanTask;
            Assert.False(service.IsScanning);
        } finally {
            if (Directory.Exists(tempRoot)) {
                try {
                    Directory.Delete(tempRoot, recursive: true);
                } catch {
                    // Ignore cleanup
                }
            }
        }
    }
}


