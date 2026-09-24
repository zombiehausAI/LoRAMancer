namespace LoRAMancer.App.Models;

public enum TrainingSubjectType {
    Character,
    Style,
    Concept,
    Clothing,
    Custom
}

public sealed class SubjectPreset {
    public TrainingSubjectType SubjectType { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int DefaultDim { get; set; } = 16;
    public int DefaultAlpha { get; set; } = 16;
    public double DefaultLearningRate { get; set; } = 1e-4;
    public int DefaultEpochs { get; set; } = 10;
    public int DefaultRepeats { get; set; } = 10;
    public string RecommendedPromptTemplate { get; set; } = string.Empty;
}

public sealed class DatasetHealthReport {
    public string DatasetDirectory { get; set; } = string.Empty;
    public bool ExtractedFromZip { get; set; }
    public string OriginalZipPath { get; set; } = string.Empty;
    public int TotalImages { get; set; }
    public int TotalCaptions { get; set; }
    public int MissingCaptions { get; set; }
    public int LowResolutionImages { get; set; }
    public int CorruptImages { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> SampleCaptions { get; set; } = new();
    public bool IsValid => TotalImages > 0 && CorruptImages == 0;
}

public sealed class PreFlightCheckResult {
    public bool Passed { get; set; } = true;
    public string GpuName { get; set; } = "AMD Radeon Graphics";
    public double TotalVramGb { get; set; } = 16.0;
    public double EstimatedVramGb { get; set; } = 10.0;
    public bool HasSufficientVram => TotalVramGb >= EstimatedVramGb;
    public double AvailableDiskGb { get; set; } = 50.0;
    public bool HasSufficientDisk => AvailableDiskGb >= 10.0;
    public bool IsRocmConfigured { get; set; } = true;
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public sealed class TrainingEstimates {
    public int TotalSteps { get; set; }
    public TimeSpan EstimatedDuration { get; set; }
    public double EstimatedVramGb { get; set; }
    public double EstimatedOutputSizeMb { get; set; }
    public string StepBreakdown { get; set; } = string.Empty;
}
