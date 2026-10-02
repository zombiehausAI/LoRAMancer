namespace LoRAMancer.App.Models;

public sealed class PluginManifest {
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string EntryPoint { get; set; } = string.Empty;
    public string PluginType { get; set; } = "Python";
    public string PythonVersion { get; set; } = "3.12";
    public string DirectoryPath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool HasDedicatedVenv { get; set; }
    public bool IsGitRepo { get; set; }
    public string GitRemoteUrl { get; set; } = string.Empty;
    public bool RequiresPyTorch { get; set; }
    public DateTime? LastUpdated { get; set; }
    public string UiSlot { get; set; } = "None";
    public string MenuSection { get; set; } = string.Empty;
    public string NavLabel { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string UiType { get; set; } = "Command";
    public bool IsModal { get; set; }
    public int MenuOrder { get; set; } = 100;
    public int? WebPort { get; set; }
    public string? WebUrl { get; set; }
    public List<PluginConfigField>? ConfigSchema { get; set; }

    public string GetEffectiveSection() {
        string raw = !string.IsNullOrWhiteSpace(MenuSection) ? MenuSection.Trim() : UiSlot.Trim();
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "None", StringComparison.OrdinalIgnoreCase)) {
            return string.Empty;
        }

        return raw.ToLowerInvariant() switch {
            "studioworkshop" or "workshop" => "Studio Workshop",
            "postforge" or "post-forge" or "showcase" => "Post-Forge Showcase",
            "studiomodaltools" or "modal" or "modals" => "Studio Modal Tools",
            "studiopipeline" or "pipeline" => "Studio Pipeline",
            "subsystems" or "system" => "Subsystems",
            _ => raw
        };
    }

    public bool GetIsModal() {
        return IsModal ||
               string.Equals(UiType, "Modal", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(UiSlot, "StudioModalTools", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class PluginConfigField {
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Type { get; set; } = "text"; // "text", "password", "number", "boolean"
    public string Description { get; set; } = string.Empty;
    public string DefaultValue { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
}
