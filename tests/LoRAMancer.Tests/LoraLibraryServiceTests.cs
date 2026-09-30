using LoRAMancer.App.Engines;
using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public sealed class LoraLibraryServiceTests {
    [Fact]
    public void FindLocalThumbnail_FindsMatchingPreviewImage() {
        string tempDir = Path.Combine(Path.GetTempPath(), $"thumb_test_{Guid.NewGuid():N}");
        string tempSettingsPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(tempDir);

        try {
            string loraPath = Path.Combine(tempDir, "cyberpunk_style.safetensors");
            string previewPath = Path.Combine(tempDir, "cyberpunk_style.preview.png");

            File.WriteAllText(loraPath, "dummy");
            File.WriteAllText(previewPath, "image_data");

            LoraMetadata meta = new() {
                FileName = "cyberpunk_style.safetensors",
                FilePath = loraPath
            };

            SettingsService settings = new(null, tempSettingsPath);
            CivitaiService civitai = new(settings);
            SafeTensorsMetadataReader reader = new();
            LoraLibraryService service = new(reader, civitai, settings);

            service.FindLocalThumbnail(meta);

            Assert.NotNull(meta.ThumbnailPath);
            Assert.Equal(previewPath, meta.ThumbnailPath);
        } finally {
            if (Directory.Exists(tempDir)) {
                for (int attempt = 0; attempt < 5; attempt++) {
                    try {
                        Directory.Delete(tempDir, recursive: true);
                        break;
                    } catch (IOException) {
                        Thread.Sleep(50);
                    } catch (UnauthorizedAccessException) {
                        Thread.Sleep(50);
                    }
                }
            }
            if (File.Exists(tempSettingsPath)) {
                try {
                    File.Delete(tempSettingsPath);
                } catch {
                    // Suppress transient lock on temp settings file
                }
            }
        }
    }

    [Fact]
    public void LoraMetadata_FormattedSize_CalculatesAccurately() {
        LoraMetadata meta = new() {
            FileSizeBytes = 150 * 1024 * 1024 // 150 MB
        };

        Assert.Equal("150.0 MB", meta.FormattedSize);
    }

    [Fact]
    public async Task ScanDirectoryStreamAsync_EmptyDirectory_ReturnsZeroDiscovered() {
        string tempDir = Path.Combine(Path.GetTempPath(), $"stream_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try {
            SettingsService settings = new(null);
            CivitaiService civitai = new(settings);
            SafeTensorsMetadataReader reader = new();
            LoraLibraryService service = new(reader, civitai, settings);

            List<LoraMetadata> streamed = new();
            int count = await service.ScanDirectoryStreamAsync(tempDir, meta => {
                streamed.Add(meta);
                return Task.CompletedTask;
            });

            Assert.Equal(0, count);
            Assert.Empty(streamed);
        } finally {
            if (Directory.Exists(tempDir)) {
                for (int attempt = 0; attempt < 5; attempt++) {
                    try {
                        Directory.Delete(tempDir, recursive: true);
                        break;
                    } catch (IOException) {
                        Thread.Sleep(50);
                    } catch (UnauthorizedAccessException) {
                        Thread.Sleep(50);
                    }
                }
            }
        }
    }

    [Fact]
    public async Task LoraDatabaseService_ExportAndAnalyze_ValidatesSchemaParity() {
        string tempDir = Path.Combine(Path.GetTempPath(), $"db_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try {
            using var dbService = new LoraDatabaseService();
            await dbService.EnsureInitializedAsync();

            string exportPath = Path.Combine(tempDir, "export_test.db");
            await dbService.ExportDatabaseAsync(exportPath);

            Assert.True(File.Exists(exportPath));
            Assert.True(new FileInfo(exportPath).Length > 0);

            var analysis = await dbService.AnalyzeDatabaseForImportAsync(exportPath);
            Assert.True(analysis.IsValid);
            Assert.Contains("Loras", analysis.MatchingTables);
            Assert.Contains("GalleryMedia", analysis.MatchingTables);
            Assert.Empty(analysis.MissingColumnsInDestination);
        } finally {
            if (Directory.Exists(tempDir)) {
                for (int attempt = 0; attempt < 5; attempt++) {
                    try {
                        Directory.Delete(tempDir, recursive: true);
                        break;
                    } catch {
                        Thread.Sleep(50);
                    }
                }
            }
        }
    }

    [Fact]
    public async Task LoraDatabaseService_ImportAndMerge_ResolvesColumnsAndRecords() {
        string tempDir = Path.Combine(Path.GetTempPath(), $"merge_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try {
            using var dbService = new LoraDatabaseService();
            await dbService.EnsureInitializedAsync();

            string exportPath = Path.Combine(tempDir, "source_to_merge.db");
            await dbService.ExportDatabaseAsync(exportPath);

            string dynamicCol = $"CustomCol_{Guid.NewGuid():N}";

            // Add an extra column and a row to source_to_merge.db
            using (var srcConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={exportPath};")) {
                await srcConn.OpenAsync();
                using var alterCmd = srcConn.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE Categories ADD COLUMN {dynamicCol} TEXT;";
                await alterCmd.ExecuteNonQueryAsync();

                using var insertCmd = srcConn.CreateCommand();
                insertCmd.CommandText = $@"
                    INSERT OR REPLACE INTO Categories (Id, Name, Color, Icon, Description, CreatedAtUtc, UpdatedAtUtc, {dynamicCol})
                    VALUES ('test_cat_id', 'Test Category Special', '#ff0055', 'star', 'Imported Category', '2026-09-30T00:00:00Z', '2026-09-30T00:00:00Z', 'custom-value');
                ";
                await insertCmd.ExecuteNonQueryAsync();
            }

            var analysis = await dbService.AnalyzeDatabaseForImportAsync(exportPath);
            Assert.True(analysis.IsValid);
            Assert.True(analysis.MissingColumnsInDestination.ContainsKey("Categories"));
            Assert.Contains(dynamicCol, analysis.MissingColumnsInDestination["Categories"]);

            var (tables, rows, resolutions) = await dbService.ImportAndMergeDatabaseAsync(exportPath, autoAddMissingColumns: true);
            Assert.True(tables > 0);
            Assert.True(rows > 0);
            Assert.Contains(resolutions, r => r.Contains(dynamicCol));

            // Verify row exists in current database
            var categories = await dbService.GetCategoriesAsync();
            Assert.Contains(categories, c => c.Name == "Test Category Special");
        } finally {
            if (Directory.Exists(tempDir)) {
                for (int attempt = 0; attempt < 5; attempt++) {
                    try {
                        Directory.Delete(tempDir, recursive: true);
                        break;
                    } catch {
                        Thread.Sleep(50);
                    }
                }
            }
        }
    }
}
