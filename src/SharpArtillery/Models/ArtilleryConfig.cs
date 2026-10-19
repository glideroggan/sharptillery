using System;
using System.Collections.Generic;
using SharpArtillery.Models;
using SharpArtillery.YamlConfig;

namespace SharpArtillery.Configs;

public class ArtilleryConfig
{
    // CLI-specific properties (not mapped to Settings)
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

    /// <summary>
    /// Converts CLI config to runtime Settings.
    /// Centralizes the mapping logic previously scattered in MainService.
    /// </summary>
    /// <param name="targetOverride">Optional target override (e.g., from YAML phase)</param>
    /// <param name="vuOverride">Optional VU override (e.g., from YAML phase)</param>
    /// <param name="durationOverride">Optional duration override (e.g., from YAML phase)</param>
    /// <param name="maxRequestsOverride">Optional max requests override (e.g., from YAML phase)</param>
    /// <param name="phaseName">Optional phase name for multi-phase YAML tests</param>
    /// <param name="methodOverride">Optional HTTP method override (e.g., from YAML flow step)</param>
    /// <param name="headersOverride">Optional headers override (e.g., from YAML flow step)</param>
    /// <param name="jsonContentOverride">Optional JSON body override (e.g., from YAML flow step)</param>
    /// <param name="rawBodyOverride">Optional raw body override (e.g., from a YAML blast step's body)</param>
    internal Settings ToSettings(
        string? targetOverride = null,
        int? vuOverride = null,
        TimeSpan? durationOverride = null,
        int? maxRequestsOverride = null,
        string? phaseName = null,
        string? methodOverride = null,
        Dictionary<string, string>? headersOverride = null,
        object? jsonContentOverride = null,
        string? rawBodyOverride = null)
    {
        // Merge headers: YAML headers override/extend CLI headers
        var mergedHeaders = new Dictionary<string, string>(Headers);
        if (headersOverride != null)
        {
            foreach (var kvp in headersOverride)
                mergedHeaders[kvp.Key] = kvp.Value;
        }
        
        return new Settings
        {
            Target = targetOverride ?? Target,
            Vu = vuOverride ?? Vu,
            Duration = durationOverride ?? Duration,
            MaxRequests = maxRequestsOverride ?? MaxRequests,
            Method = methodOverride ?? Method,
            Headers = mergedHeaders,
            JsonContent = jsonContentOverride ?? JsonContent,
            RawBody = rawBodyOverride,
            ConstantRps = ConstantRps,
            RequestTimeout = RequestTimeout,
            PhaseName = phaseName,
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
