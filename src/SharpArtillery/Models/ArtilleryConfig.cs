using System;
using System.Collections.Generic;
using SharpArtillery.Models;
using SharpArtillery.YamlConfig;

namespace SharpArtillery.Configs;

public class ArtilleryConfig
{
    // CLI-specific properties (not mapped to Settings)
    public bool ShowInfo { get; set; }
    public bool Quit { get; set; }
    public string? Yaml { get; set; }
    public ConfigRoot? YamlConfig { get; set; }
    public ReportSettings ReportSettings { get; } = new();

    // Shared properties (mapped to Settings)
    public string? Target { get; set; }
    public int Vu { get; set; } = 1;
    public TimeSpan? Duration { get; set; }
    public TimeSpan? RequestTimeout { get; set; }
    public int? MaxRequests { get; set; }
    public int? ConstantRps { get; set; }
    public string? Method { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public object? JsonContent { get; set; }

    // AutoTune properties (flattened for CLI, combined into AutoTuneSettings)
    public bool AutoTuneMaxRps { get; set; }
    public int? AutoTuneMaxClients { get; set; }
    public int? AutoTuneStep { get; set; }
    public TimeSpan? AutoTuneInterval { get; set; }
    public float? AutoTuneMaxErrorRatio { get; set; }

    // Unused legacy property
    public int? RequestRate { get; set; }

    /// <summary>
    /// Converts CLI config to runtime Settings.
    /// Centralizes the mapping logic previously scattered in MainService.
    /// </summary>
    /// <param name="targetOverride">Optional target override (e.g., from YAML phase)</param>
    /// <param name="vuOverride">Optional VU override (e.g., from YAML phase)</param>
    /// <param name="durationOverride">Optional duration override (e.g., from YAML phase)</param>
    /// <param name="maxRequestsOverride">Optional max requests override (e.g., from YAML phase)</param>
    internal Settings ToSettings(
        string? targetOverride = null,
        int? vuOverride = null,
        TimeSpan? durationOverride = null,
        int? maxRequestsOverride = null)
    {
        return new Settings
        {
            Target = targetOverride ?? Target,
            Vu = vuOverride ?? Vu,
            Duration = durationOverride ?? Duration,
            MaxRequests = maxRequestsOverride ?? MaxRequests,
            Method = Method,
            Headers = Headers,
            JsonContent = JsonContent,
            ConstantRps = ConstantRps,
            RequestTimeout = RequestTimeout,
            AutoTune = AutoTuneMaxRps
                ? new AutoTuneSettings(
                    MaxClients: AutoTuneMaxClients ?? 256,
                    Step: AutoTuneStep ?? 5,
                    Interval: AutoTuneInterval ?? TimeSpan.FromSeconds(5),
                    MaxErrorRatio: AutoTuneMaxErrorRatio ?? 0.02f,
                    MinImprovementRatio: 0.01f,
                    MaxStallIntervals: 3)
                : null
        };
    }
}
