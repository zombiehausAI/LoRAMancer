using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoRAMancer.App.Services;

public enum StudioStage {
    Curate,
    Train,
    Lab,
    Test,
    Vault
}

public sealed class StudioProjectState {
    public string ProjectName { get; set; } = "Untitled Concept";
    public string DatasetFolder { get; set; } = string.Empty;
    public string TriggerWord { get; set; } = string.Empty;
    public string BaseModel { get; set; } = "Flux.1-Dev";
    public string ActiveLoraPath { get; set; } = string.Empty;
    public StudioStage ActiveStage { get; set; } = StudioStage.Vault;
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
}

public sealed class StudioSessionService {
    private readonly string _sessionFilePath;
    private readonly object _lock = new();
    private StudioProjectState _currentProject;

    public event Action? OnSessionChanged;

    public StudioProjectState Current => _currentProject;

    public string ProjectName => _currentProject.ProjectName;
    public string DatasetFolder => _currentProject.DatasetFolder;
    public string TriggerWord => _currentProject.TriggerWord;
    public string BaseModel => _currentProject.BaseModel;
    public string ActiveLoraPath => _currentProject.ActiveLoraPath;
    public StudioStage ActiveStage => _currentProject.ActiveStage;

    public StudioSessionService(string? customSessionPath = null) {
        if (!string.IsNullOrWhiteSpace(customSessionPath)) {
            _sessionFilePath = customSessionPath;
        } else {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string baseDir = Path.Combine(userProfile, ".loramancer");
            if (!Directory.Exists(baseDir)) {
                Directory.CreateDirectory(baseDir);
            }
            _sessionFilePath = Path.Combine(baseDir, "studio_session.json");
        }

        _currentProject = LoadSession();
    }

    public void UpdateProject(
        string? projectName = null,
        string? datasetFolder = null,
        string? triggerWord = null,
        string? baseModel = null,
        string? activeLoraPath = null,
        StudioStage? stage = null
    ) {
        lock (_lock) {
            if (projectName != null) _currentProject.ProjectName = projectName;
            if (datasetFolder != null) _currentProject.DatasetFolder = datasetFolder;
            if (triggerWord != null) _currentProject.TriggerWord = triggerWord;
            if (baseModel != null) _currentProject.BaseModel = baseModel;
            if (activeLoraPath != null) _currentProject.ActiveLoraPath = activeLoraPath;
            if (stage != null) _currentProject.ActiveStage = stage.Value;

            _currentProject.LastModified = DateTime.UtcNow;
            SaveSession();
        }

        OnSessionChanged?.Invoke();
    }

    public void SetStage(StudioStage stage) {
        lock (_lock) {
            _currentProject.ActiveStage = stage;
            SaveSession();
        }
        OnSessionChanged?.Invoke();
    }

    public void SetDatasetFolder(string folder) {
        lock (_lock) {
            _currentProject.DatasetFolder = folder;
            SaveSession();
        }
        OnSessionChanged?.Invoke();
    }

    public void SetActiveLora(string loraPath, string? baseModel = null) {
        lock (_lock) {
            _currentProject.ActiveLoraPath = loraPath;
            if (!string.IsNullOrWhiteSpace(baseModel)) {
                _currentProject.BaseModel = baseModel;
            }
            SaveSession();
        }
        OnSessionChanged?.Invoke();
    }

    public void SetBaseModel(string baseModel) {
        lock (_lock) {
            _currentProject.BaseModel = baseModel;
            SaveSession();
        }
        OnSessionChanged?.Invoke();
    }

    public void SetTriggerWord(string triggerWord) {
        lock (_lock) {
            _currentProject.TriggerWord = triggerWord;
            SaveSession();
        }
        OnSessionChanged?.Invoke();
    }

    public void ResetProject(string projectName = "New Concept") {
        lock (_lock) {
            _currentProject = new StudioProjectState {
                ProjectName = projectName,
                LastModified = DateTime.UtcNow
            };
            SaveSession();
        }
        OnSessionChanged?.Invoke();
    }

    private StudioProjectState LoadSession() {
        if (!File.Exists(_sessionFilePath)) {
            return new StudioProjectState();
        }

        try {
            string json = File.ReadAllText(_sessionFilePath);
            var state = JsonSerializer.Deserialize<StudioProjectState>(json, new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true
            });
            return state ?? new StudioProjectState();
        } catch {
            return new StudioProjectState();
        }
    }

    private void SaveSession() {
        try {
            string json = JsonSerializer.Serialize(_currentProject, new JsonSerializerOptions {
                WriteIndented = true
            });
            File.WriteAllText(_sessionFilePath, json);
        } catch {
            // Non-critical background save failure suppression
        }
    }
}
