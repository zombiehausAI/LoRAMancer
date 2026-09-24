using LoRAMancer.App.Models;
using LoRAMancer.App.Services;

namespace LoRAMancer.Tests;

public sealed class LoraHistoryServiceTests {
    [Fact]
    public async Task AddOrUpdateRecordAsync_PersistsAndLoadsRecords() {
        string tempDir = Path.Combine(Path.GetTempPath(), $"history_test_{Guid.NewGuid():N}");

        try {
            LoraHistoryService service = new(tempDir);
            LoraHistoryRecord record = new() {
                Id = "lora_test_01",
                Name = "Cyberpunk Samurai",
                BaseArchitecture = "flux1",
                TriggerWord = "cyb3rpunk samurai",
                Steps = 1500,
                FinalLoss = 0.0412,
                CompletedAt = DateTime.UtcNow
            };

            await service.AddOrUpdateRecordAsync(record);

            var history = await service.GetHistoryAsync();
            Assert.Single(history);
            Assert.Equal("Cyberpunk Samurai", history[0].Name);
            Assert.Equal("cyb3rpunk samurai", history[0].TriggerWord);
            Assert.Equal(0.0412, history[0].FinalLoss, 4);
        } finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ToggleFavoriteAsync_TogglesFlagCorrectly() {
        string tempDir = Path.Combine(Path.GetTempPath(), $"history_test_{Guid.NewGuid():N}");

        try {
            LoraHistoryService service = new(tempDir);
            LoraHistoryRecord record = new() {
                Id = "lora_fav_01",
                Name = "Gothic Portrait",
                IsFavorite = false
            };

            await service.AddOrUpdateRecordAsync(record);
            bool toggledOn = await service.ToggleFavoriteAsync("lora_fav_01");
            Assert.True(toggledOn);

            var updated = await service.GetRecordByIdAsync("lora_fav_01");
            Assert.NotNull(updated);
            Assert.True(updated.IsFavorite);

            bool toggledOff = await service.ToggleFavoriteAsync("lora_fav_01");
            Assert.True(toggledOff);

            var finalRecord = await service.GetRecordByIdAsync("lora_fav_01");
            Assert.NotNull(finalRecord);
            Assert.False(finalRecord.IsFavorite);
        } finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UpdateNotesAsync_UpdatesUserNotes() {
        string tempDir = Path.Combine(Path.GetTempPath(), $"history_test_{Guid.NewGuid():N}");

        try {
            LoraHistoryService service = new(tempDir);
            LoraHistoryRecord record = new() {
                Id = "lora_notes_01",
                Name = "Watercolor Landscape",
                Notes = "Initial run"
            };

            await service.AddOrUpdateRecordAsync(record);
            await service.UpdateNotesAsync("lora_notes_01", "Best with CFG 3.5, Euler sampler");

            var updated = await service.GetRecordByIdAsync("lora_notes_01");
            Assert.NotNull(updated);
            Assert.Equal("Best with CFG 3.5, Euler sampler", updated.Notes);
        } finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DeleteRecordAsync_RemovesRecordFromHistory() {
        string tempDir = Path.Combine(Path.GetTempPath(), $"history_test_{Guid.NewGuid():N}");

        try {
            LoraHistoryService service = new(tempDir);
            LoraHistoryRecord record = new() {
                Id = "lora_del_01",
                Name = "To Be Deleted"
            };

            await service.AddOrUpdateRecordAsync(record);
            var initialList = await service.GetHistoryAsync();
            Assert.Single(initialList);

            bool deleted = await service.DeleteRecordAsync("lora_del_01");
            Assert.True(deleted);

            var remaining = await service.GetHistoryAsync();
            Assert.Empty(remaining);
        } finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RecordDoesNotExpire_PreservesRecordsAcrossReloads() {
        string tempDir = Path.Combine(Path.GetTempPath(), $"history_test_{Guid.NewGuid():N}");

        try {
            // Simulate saving a record from 120 days ago (far beyond Civitai's 30-day expiration window)
            LoraHistoryService initialInstance = new(tempDir);
            DateTime oldDate = DateTime.UtcNow.AddDays(-120);

            LoraHistoryRecord oldRecord = new() {
                Id = "lora_old_vault",
                Name = "Archived Masterpiece",
                BaseArchitecture = "sdxl",
                CompletedAt = oldDate,
                FinalLoss = 0.038
            };

            await initialInstance.AddOrUpdateRecordAsync(oldRecord);

            // Fresh service instance simulating app restart days/months later
            LoraHistoryService reloadedInstance = new(tempDir);
            var history = await reloadedInstance.GetHistoryAsync();

            Assert.Single(history);
            Assert.Equal("lora_old_vault", history[0].Id);
            Assert.Equal("Archived Masterpiece", history[0].Name);
            Assert.Equal(oldDate, history[0].CompletedAt);
        } finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
