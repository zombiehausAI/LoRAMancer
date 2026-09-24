using System.Text.Json;
using System.Text.Json.Serialization;
using LoRAMancer.App.Models;
using MudBlazor;

namespace LoRAMancer.App.Services;

public sealed record ThemeDefinition {
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required bool IsDark { get; init; }
    public required string Primary { get; init; }
    public required string Secondary { get; init; }
    public required string Tertiary { get; init; }
    public required string Background { get; init; }
    public required string Surface { get; init; }
    public required string AppbarBackground { get; init; }
    public required string DrawerBackground { get; init; }
    public required string Overlay { get; init; }
    public required string Border { get; init; }
    public required string TextPrimary { get; init; }
    public required string TextSecondary { get; init; }
    public bool IsCustom { get; init; }

    public string ToJson(bool indented = true) {
        var options = new JsonSerializerOptions {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        return JsonSerializer.Serialize(this, options);
    }

    public static ThemeDefinition? FromJson(string json) {
        try {
            var options = new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true
            };
            return JsonSerializer.Deserialize<ThemeDefinition>(json, options);
        } catch {
            return null;
        }
    }
}

public sealed class ThemeService {
    private static ThemeService? _instance;
    private readonly SettingsService? _settingsService;
    private readonly string _themesDirectory;
    private readonly List<ThemeDefinition> _customThemes = new();
    private readonly object _lock = new();

    public event Action? OnThemeChanged;

    public static IReadOnlyList<ThemeDefinition> AvailableThemes {
        get {
            if (_instance != null) {
                return _instance.GetAvailableThemes();
            }
            return BuiltInThemes;
        }
    }

    public static readonly IReadOnlyList<ThemeDefinition> BuiltInThemes = new List<ThemeDefinition> {
        new() {
            Id = "dark-purple",
            Name = "Catppuccin Mocha (Dark)",
            Description = "Lavender and sky blue accents on rich deep slate",
            IsDark = true,
            Primary = "#cba6f7",
            Secondary = "#89b4fa",
            Tertiary = "#a6e3a1",
            Background = "#11111b",
            Surface = "#181825",
            AppbarBackground = "#181825",
            DrawerBackground = "#11111b",
            Overlay = "#1e1e2e",
            Border = "#313244",
            TextPrimary = "#f1f5f9",
            TextSecondary = "#94a3b8",
            IsCustom = false
        },
        new() {
            Id = "tokyo-night",
            Name = "Tokyo Night (Dark)",
            Description = "Electric neon cyan and indigo over deep midnight navy",
            IsDark = true,
            Primary = "#7aa2f7",
            Secondary = "#bb9af7",
            Tertiary = "#7dcfff",
            Background = "#1a1b26",
            Surface = "#24283b",
            AppbarBackground = "#24283b",
            DrawerBackground = "#1f2335",
            Overlay = "#292e42",
            Border = "#3b4261",
            TextPrimary = "#c0caf5",
            TextSecondary = "#9aa5ce",
            IsCustom = false
        },
        new() {
            Id = "dark-grey",
            Name = "Slate Greys (Dark)",
            Description = "Neutral charcoal and steel with high-contrast platinum text",
            IsDark = true,
            Primary = "#38bdf8",
            Secondary = "#818cf8",
            Tertiary = "#34d399",
            Background = "#0f172a",
            Surface = "#1e293b",
            AppbarBackground = "#1e293b",
            DrawerBackground = "#0f172a",
            Overlay = "#334155",
            Border = "#334155",
            TextPrimary = "#f8fafc",
            TextSecondary = "#94a3b8",
            IsCustom = false
        },
        new() {
            Id = "dark-blue",
            Name = "Cobalt Sapphire (Dark)",
            Description = "Deep midnight navy with high-contrast electric sapphire accents",
            IsDark = true,
            Primary = "#60a5fa",
            Secondary = "#818cf8",
            Tertiary = "#93c5fd",
            Background = "#0b1329",
            Surface = "#111d3d",
            AppbarBackground = "#111d3d",
            DrawerBackground = "#0b1329",
            Overlay = "#18264e",
            Border = "#233566",
            TextPrimary = "#f0f9ff",
            TextSecondary = "#94a3b8",
            IsCustom = false
        },
        new() {
            Id = "dark-red",
            Name = "Crimson Blood (Dark)",
            Description = "Deep obsidian with vivid rose and ruby highlights",
            IsDark = true,
            Primary = "#fb7185",
            Secondary = "#f43f5e",
            Tertiary = "#fecdd3",
            Background = "#120a0d",
            Surface = "#1f1218",
            AppbarBackground = "#1f1218",
            DrawerBackground = "#120a0d",
            Overlay = "#2c1922",
            Border = "#4a2134",
            TextPrimary = "#fde2e4",
            TextSecondary = "#e2b8c2",
            IsCustom = false
        },
        new() {
            Id = "dark-green",
            Name = "Emerald Cyber (Dark)",
            Description = "Matrix carbon with glowing mint and emerald accents",
            IsDark = true,
            Primary = "#10b981",
            Secondary = "#34d399",
            Tertiary = "#6ee7b7",
            Background = "#09140f",
            Surface = "#12231b",
            AppbarBackground = "#12231b",
            DrawerBackground = "#09140f",
            Overlay = "#1a3327",
            Border = "#234736",
            TextPrimary = "#ecfdf5",
            TextSecondary = "#a7f3d0",
            IsCustom = false
        },
        new() {
            Id = "dark-amber",
            Name = "Solar Amber (Dark)",
            Description = "Dark roast espresso with radiant golden amber illumination",
            IsDark = true,
            Primary = "#f59e0b",
            Secondary = "#fbbf24",
            Tertiary = "#fcd34d",
            Background = "#14100b",
            Surface = "#211b13",
            AppbarBackground = "#211b13",
            DrawerBackground = "#14100b",
            Overlay = "#30261b",
            Border = "#483827",
            TextPrimary = "#fffbeb",
            TextSecondary = "#fde68a",
            IsCustom = false
        },
        new() {
            Id = "light",
            Name = "Clean Modern (Light)",
            Description = "Porcelain background with crisp indigo and slate accents",
            IsDark = false,
            Primary = "#6366f1",
            Secondary = "#0284c7",
            Tertiary = "#059669",
            Background = "#f8fafc",
            Surface = "#ffffff",
            AppbarBackground = "#ffffff",
            DrawerBackground = "#f1f5f9",
            Overlay = "#e2e8f0",
            Border = "#cbd5e1",
            TextPrimary = "#0f172a",
            TextSecondary = "#475569",
            IsCustom = false
        }
    };

    public ThemeService(SettingsService? settingsService = null) {
        _instance = this;
        _settingsService = settingsService;

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _themesDirectory = Path.Combine(userProfile, ".LoRAMancer", "themes");
        try {
            Directory.CreateDirectory(_themesDirectory);
        } catch {
            // Ignore directory creation errors
        }

        LoadCustomThemes();
    }

    public IReadOnlyList<ThemeDefinition> GetAvailableThemes() {
        lock (_lock) {
            var list = new List<ThemeDefinition>(BuiltInThemes);
            list.AddRange(_customThemes);
            return list;
        }
    }

    public IReadOnlyList<ThemeDefinition> Themes => AvailableThemes;

    public string CurrentThemeId {
        get => _settingsService?.Current.ThemePreset ?? "dark-purple";
        set {
            if (_settingsService != null && _settingsService.Current.ThemePreset != value) {
                _settingsService.Current.ThemePreset = value;
                _ = _settingsService.SaveSettingsAsync(_settingsService.Current);
                OnThemeChanged?.Invoke();
            }
        }
    }

    public ThemeDefinition CurrentTheme {
        get {
            string currentId = CurrentThemeId;
            return AvailableThemes.FirstOrDefault(t => string.Equals(t.Id, currentId, StringComparison.OrdinalIgnoreCase))
                ?? BuiltInThemes[0];
        }
    }

    public void LoadCustomThemes() {
        lock (_lock) {
            _customThemes.Clear();
            if (Directory.Exists(_themesDirectory)) {
                try {
                    string[] files = Directory.GetFiles(_themesDirectory, "*.json");
                    foreach (string file in files) {
                        try {
                            string json = File.ReadAllText(file);
                            var theme = ThemeDefinition.FromJson(json);
                            if (theme != null && !string.IsNullOrWhiteSpace(theme.Id)) {
                                _customThemes.Add(theme with { IsCustom = true });
                            }
                        } catch {
                            // Skip invalid theme files
                        }
                    }
                } catch {
                    // Suppress directory read errors
                }
            }
        }
    }

    public bool SaveCustomTheme(ThemeDefinition theme, out string? error) {
        error = null;
        if (string.IsNullOrWhiteSpace(theme.Name)) {
            error = "Theme name cannot be empty.";
            return false;
        }

        string id = string.IsNullOrWhiteSpace(theme.Id)
            ? "custom-" + Guid.NewGuid().ToString("N")[..8]
            : theme.Id.Trim().ToLowerInvariant();

        // Disallow overwriting built-in theme IDs
        if (BuiltInThemes.Any(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase))) {
            id = id + "-custom";
        }

        var customTheme = theme with {
            Id = id,
            IsCustom = true
        };

        try {
            Directory.CreateDirectory(_themesDirectory);
            string filePath = Path.Combine(_themesDirectory, $"{id}.json");
            File.WriteAllText(filePath, customTheme.ToJson(indented: true));

            lock (_lock) {
                _customThemes.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
                _customThemes.Add(customTheme);
            }

            OnThemeChanged?.Invoke();
            return true;
        } catch (Exception ex) {
            error = $"Failed to save theme: {ex.Message}";
            return false;
        }
    }

    public bool DeleteCustomTheme(string themeId) {
        lock (_lock) {
            var target = _customThemes.FirstOrDefault(t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase));
            if (target == null) {
                return false;
            }

            try {
                string filePath = Path.Combine(_themesDirectory, $"{target.Id}.json");
                if (File.Exists(filePath)) {
                    File.Delete(filePath);
                }
            } catch {
                // Ignore file delete errors
            }

            _customThemes.Remove(target);

            if (string.Equals(CurrentThemeId, themeId, StringComparison.OrdinalIgnoreCase)) {
                CurrentThemeId = "dark-purple";
            } else {
                OnThemeChanged?.Invoke();
            }

            return true;
        }
    }

    public bool ImportTheme(string json, out ThemeDefinition? imported, out string? error) {
        imported = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json)) {
            error = "Theme JSON cannot be empty.";
            return false;
        }

        ThemeDefinition? parsed = ThemeDefinition.FromJson(json);
        if (parsed == null) {
            error = "Invalid JSON format. Please verify the theme JSON structure.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(parsed.Name)) {
            error = "Theme must contain a valid 'Name' property.";
            return false;
        }

        string baseId = string.IsNullOrWhiteSpace(parsed.Id)
            ? "theme-" + Guid.NewGuid().ToString("N")[..8]
            : parsed.Id.Trim().ToLowerInvariant();

        // Ensure unique ID for imported theme
        string id = baseId;
        int counter = 1;
        while (AvailableThemes.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))) {
            id = $"{baseId}-imported{counter++}";
        }

        var customTheme = parsed with {
            Id = id,
            IsCustom = true
        };

        if (SaveCustomTheme(customTheme, out error)) {
            imported = customTheme;
            CurrentThemeId = id;
            return true;
        }

        return false;
    }

    public string ExportTheme(string themeId) {
        var theme = AvailableThemes.FirstOrDefault(t => string.Equals(t.Id, themeId, StringComparison.OrdinalIgnoreCase))
            ?? CurrentTheme;
        return theme.ToJson(indented: true);
    }

    public MudTheme GetMudTheme() {
        ThemeDefinition current = CurrentTheme;

        if (current.IsDark) {
            return new MudTheme {
                PaletteDark = new PaletteDark {
                    Primary = current.Primary,
                    Secondary = current.Secondary,
                    Tertiary = current.Tertiary,
                    Background = current.Background,
                    Surface = current.Surface,
                    AppbarBackground = current.AppbarBackground,
                    AppbarText = current.TextPrimary,
                    DrawerBackground = current.DrawerBackground,
                    DrawerIcon = current.Primary,
                    DrawerText = current.TextPrimary,
                    TextPrimary = current.TextPrimary,
                    TextSecondary = current.TextSecondary,
                    ActionDefault = current.TextPrimary,
                    Divider = current.Border,
                    LinesDefault = current.Border,
                    LinesInputs = current.Border,
                    TableLines = current.Border,
                    TableHover = "rgba(255, 255, 255, 0.04)",
                    TableStriped = "rgba(255, 255, 255, 0.02)",
                    OverlayDark = "rgba(17, 17, 27, 0.8)"
                }
            };
        }

        return new MudTheme {
            PaletteLight = new PaletteLight {
                Primary = current.Primary,
                Secondary = current.Secondary,
                Tertiary = current.Tertiary,
                Background = current.Background,
                Surface = current.Surface,
                AppbarBackground = current.AppbarBackground,
                AppbarText = current.TextPrimary,
                DrawerBackground = current.DrawerBackground,
                DrawerIcon = current.Primary,
                DrawerText = current.TextPrimary,
                TextPrimary = current.TextPrimary,
                TextSecondary = current.TextSecondary,
                ActionDefault = current.TextPrimary,
                Divider = current.Border,
                LinesDefault = current.Border,
                LinesInputs = current.Border,
                TableLines = current.Border,
                TableHover = "rgba(0, 0, 0, 0.04)",
                TableStriped = "rgba(0, 0, 0, 0.02)"
            }
        };
    }

    public void SetTheme(string themeId) {
        CurrentThemeId = themeId;
        OnThemeChanged?.Invoke();
    }
}
