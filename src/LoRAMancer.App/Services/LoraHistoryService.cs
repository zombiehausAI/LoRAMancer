using System.Text.Json;
using LoRAMancer.App.Models;

namespace LoRAMancer.App.Services;

public sealed class LoraHistoryService {
    private readonly string _historyDirectory;
    private readonly string _historyFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<LoraHistoryRecord>? _cachedRecords;

    public event Action? OnHistoryChanged;

    public LoraHistoryService(string? customHistoryDir = null) {
        if (!string.IsNullOrWhiteSpace(customHistoryDir)) {
            _historyDirectory = customHistoryDir;
        } else {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _historyDirectory = Path.Combine(userProfile, ".loramancer", "history");
        }
        _historyFilePath = Path.Combine(_historyDirectory, "history.json");
    }

    public async Task<IReadOnlyList<LoraHistoryRecord>> GetHistoryAsync() {
        await _lock.WaitAsync();
        try {
            if (_cachedRecords != null) {
                return _cachedRecords.OrderByDescending(r => r.CompletedAt).ToList();
            }

            if (!File.Exists(_historyFilePath)) {
                _cachedRecords = new List<LoraHistoryRecord>();
                return _cachedRecords;
            }

            string json = await File.ReadAllTextAsync(_historyFilePath);
            var records = JsonSerializer.Deserialize<List<LoraHistoryRecord>>(json, new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true
            });

            _cachedRecords = records ?? new List<LoraHistoryRecord>();
            return _cachedRecords.OrderByDescending(r => r.CompletedAt).ToList();
        } finally {
            _lock.Release();
        }
    }

    public async Task<LoraHistoryRecord?> GetRecordByIdAsync(string id) {
        var list = await GetHistoryAsync();
        return list.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task AddOrUpdateRecordAsync(LoraHistoryRecord record) {
        ArgumentNullException.ThrowIfNull(record);

        await _lock.WaitAsync();
        try {
            Directory.CreateDirectory(_historyDirectory);

            if (_cachedRecords == null) {
                if (File.Exists(_historyFilePath)) {
                    string json = await File.ReadAllTextAsync(_historyFilePath);
                    _cachedRecords = JsonSerializer.Deserialize<List<LoraHistoryRecord>>(json) ?? new List<LoraHistoryRecord>();
                } else {
                    _cachedRecords = new List<LoraHistoryRecord>();
                }
            }

            int existingIdx = _cachedRecords.FindIndex(r => string.Equals(r.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0) {
                _cachedRecords[existingIdx] = record;
            } else {
                _cachedRecords.Insert(0, record);
            }

            string serialized = JsonSerializer.Serialize(_cachedRecords, new JsonSerializerOptions {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(_historyFilePath, serialized);
        } finally {
            _lock.Release();
        }

        OnHistoryChanged?.Invoke();
    }

    public async Task<bool> DeleteRecordAsync(string id, bool deleteFilesFromDisk = false) {
        await _lock.WaitAsync();
        LoraHistoryRecord? target = null;
        try {
            if (_cachedRecords == null && File.Exists(_historyFilePath)) {
                string json = await File.ReadAllTextAsync(_historyFilePath);
                _cachedRecords = JsonSerializer.Deserialize<List<LoraHistoryRecord>>(json);
            }

            if (_cachedRecords != null) {
                target = _cachedRecords.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (target != null) {
                    _cachedRecords.Remove(target);
                    string serialized = JsonSerializer.Serialize(_cachedRecords, new JsonSerializerOptions {
                        WriteIndented = true
                    });
                    await File.WriteAllTextAsync(_historyFilePath, serialized);
                }
            }
        } finally {
            _lock.Release();
        }

        if (target != null) {
            if (deleteFilesFromDisk && !string.IsNullOrWhiteSpace(target.OutputFilePath) && File.Exists(target.OutputFilePath)) {
                try {
                    File.Delete(target.OutputFilePath);
                } catch {
                    // Suppress deletion error
                }
            }
            OnHistoryChanged?.Invoke();
            return true;
        }

        return false;
    }

    public async Task<bool> ToggleFavoriteAsync(string id) {
        var record = await GetRecordByIdAsync(id);
        if (record == null) {
            return false;
        }

        record.IsFavorite = !record.IsFavorite;
        await AddOrUpdateRecordAsync(record);
        return true;
    }

    public async Task<bool> UpdateNotesAsync(string id, string notes) {
        var record = await GetRecordByIdAsync(id);
        if (record == null) {
            return false;
        }

        record.Notes = notes;
        await AddOrUpdateRecordAsync(record);
        return true;
    }
}
