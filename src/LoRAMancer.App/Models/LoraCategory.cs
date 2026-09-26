namespace LoRAMancer.App.Models;

public sealed class LoraCategory {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#89b4fa";
    public string? Icon { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public static List<LoraCategory> GetDefaultCategories() {
        return new List<LoraCategory> {
            new() {
                Id = "char",
                Name = "Character",
                Color = "#89b4fa",
                Icon = MudBlazor.Icons.Material.Filled.Person,
                Description = "Characters, persons, faces, and original character (OC) models"
            },
            new() {
                Id = "style",
                Name = "Style",
                Color = "#cba6f7",
                Icon = MudBlazor.Icons.Material.Filled.Palette,
                Description = "Art styles, aesthetics, anime, painterly, and rendering techniques"
            },
            new() {
                Id = "concept",
                Name = "Concept",
                Color = "#a6e3a1",
                Icon = MudBlazor.Icons.Material.Filled.Lightbulb,
                Description = "Themes, visual effects, lighting, dynamic angles, and abstract concepts"
            },
            new() {
                Id = "clothing",
                Name = "Clothing",
                Color = "#f9e2af",
                Icon = MudBlazor.Icons.Material.Filled.Checkroom,
                Description = "Outfits, fashion, armor, footwear, accessories, and costumes"
            },
            new() {
                Id = "pose",
                Name = "Pose",
                Color = "#fab387",
                Icon = MudBlazor.Icons.Material.Filled.SportsGymnastics,
                Description = "Action poses, body anatomy, gestures, and camera perspectives"
            },
            new() {
                Id = "environment",
                Name = "Environment",
                Color = "#94e2d5",
                Icon = MudBlazor.Icons.Material.Filled.Landscape,
                Description = "Backgrounds, scenery, architecture, nature, and interiors"
            },
            new() {
                Id = "vehicle",
                Name = "Vehicle",
                Color = "#f38ba8",
                Icon = MudBlazor.Icons.Material.Filled.DirectionsCar,
                Description = "Vehicles, mecha, ships, robots, and machinery"
            },
            new() {
                Id = "enhancement",
                Name = "Enhancement",
                Color = "#b4befe",
                Icon = MudBlazor.Icons.Material.Filled.AutoFixHigh,
                Description = "Detail enhancers, quality boosters, negative correction, and utilities"
            }
        };
    }
}
