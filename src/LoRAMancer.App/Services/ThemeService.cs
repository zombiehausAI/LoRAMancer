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
}

public sealed class ThemeService {
    private readonly SettingsService? _settingsService;
    public event Action? OnThemeChanged;

    public static readonly IReadOnlyList<ThemeDefinition> AvailableThemes = new List<ThemeDefinition> {
        new() {
            Id = "dark-purple",
            Name = "Catppuccin Purple (Dark)",
            Description = "Default dark aesthetic with vibrant lavender and blue accents",
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
            TextPrimary = "#cdd6f4",
            TextSecondary = "#a6adc8"
        },
        new() {
            Id = "dark-grey",
            Name = "Slate Greys (Dark)",
            Description = "Pure monochrome charcoal and graphite with platinum accents",
            IsDark = true,
            Primary = "#e0e0e0",
            Secondary = "#9e9e9e",
            Tertiary = "#757575",
            Background = "#141414",
            Surface = "#1f1f1f",
            AppbarBackground = "#1f1f1f",
            DrawerBackground = "#141414",
            Overlay = "#2a2a2a",
            Border = "#383838",
            TextPrimary = "#f5f5f5",
            TextSecondary = "#b0b0b0"
        },
        new() {
            Id = "dark-red",
            Name = "Crimson Ruby (Dark)",
            Description = "Deep obsidian with striking crimson red accents",
            IsDark = true,
            Primary = "#f38ba8",
            Secondary = "#f87171",
            Tertiary = "#fb7185",
            Background = "#140b0f",
            Surface = "#1e1017",
            AppbarBackground = "#1e1017",
            DrawerBackground = "#140b0f",
            Overlay = "#291520",
            Border = "#3d1828",
            TextPrimary = "#fde2e4",
            TextSecondary = "#d1a8b0"
        },
        new() {
            Id = "dark-green",
            Name = "Emerald Forest (Dark)",
            Description = "Cyberpunk dark theme with vibrant emerald and mint accents",
            IsDark = true,
            Primary = "#a6e3a1",
            Secondary = "#34d399",
            Tertiary = "#6ee7b7",
            Background = "#0b140e",
            Surface = "#112017",
            AppbarBackground = "#112017",
            DrawerBackground = "#0b140e",
            Overlay = "#182c20",
            Border = "#1e3828",
            TextPrimary = "#e8f5e9",
            TextSecondary = "#a3c9ab"
        },
        new() {
            Id = "dark-blue",
            Name = "Cobalt Sapphire (Dark)",
            Description = "Deep midnight navy with electric blue accents",
            IsDark = true,
            Primary = "#89b4fa",
            Secondary = "#60a5fa",
            Tertiary = "#93c5fd",
            Background = "#0c1222",
            Surface = "#141e34",
            AppbarBackground = "#141e34",
            DrawerBackground = "#0c1222",
            Overlay = "#1c2a47",
            Border = "#203254",
            TextPrimary = "#e0f2fe",
            TextSecondary = "#94a3b8"
        },
        new() {
            Id = "dark-amber",
            Name = "Amber Sunset (Dark)",
            Description = "Rich dark bronze with warm golden amber accents",
            IsDark = true,
            Primary = "#f9e2af",
            Secondary = "#fbbf24",
            Tertiary = "#fcd34d",
            Background = "#141008",
            Surface = "#20190d",
            AppbarBackground = "#20190d",
            DrawerBackground = "#141008",
            Overlay = "#2e2413",
            Border = "#3a2d18",
            TextPrimary = "#fef3c7",
            TextSecondary = "#d5be9b"
        },
        new() {
            Id = "light",
            Name = "Clean Modern (Light)",
            Description = "Soft porcelain surfaces with crisp indigo accents",
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
            TextSecondary = "#475569"
        }
    };

    public ThemeService(SettingsService? settingsService = null) {
        _settingsService = settingsService;
    }

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

    public ThemeDefinition CurrentTheme =>
        AvailableThemes.FirstOrDefault(t => string.Equals(t.Id, CurrentThemeId, StringComparison.OrdinalIgnoreCase))
        ?? AvailableThemes[0];

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
                    DrawerBackground = current.DrawerBackground,
                    TextPrimary = current.TextPrimary,
                    TextSecondary = current.TextSecondary,
                    ActionDefault = current.TextPrimary,
                    DrawerIcon = current.Primary,
                    DrawerText = current.TextPrimary,
                    Divider = current.Border,
                    LinesDefault = current.Border,
                    TableLines = current.Border,
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
                DrawerBackground = current.DrawerBackground,
                TextPrimary = current.TextPrimary,
                TextSecondary = current.TextSecondary,
                ActionDefault = current.TextPrimary,
                DrawerIcon = current.Primary,
                DrawerText = current.TextPrimary,
                Divider = current.Border,
                LinesDefault = current.Border,
                TableLines = current.Border
            }
        };
    }

    public void SetTheme(string themeId) {
        CurrentThemeId = themeId;
        OnThemeChanged?.Invoke();
    }
}
