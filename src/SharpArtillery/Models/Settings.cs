using System;
using System.Collections.Generic;

namespace SharpArtillery.Configs;

internal sealed class Settings
{
    public int? ConstantRps { get; set; }
    public TimeSpan? Duration { get; set; }
    public TimeSpan? RequestTimeout { get; set; }
    public AutoTuneSettings? AutoTune { get; set; }
    public int? MaxRequests { get; set; }
    /// <summary>
    /// Virtual Users
    /// </summary>
    public int Vu { get; set; }
    public string? Target { get; set; }
    public string? Method { get; init; }
    public Dictionary<string,string>? Headers { get; set; }
    public object? JsonContent { get; set; }

    /// <summary>
    /// A body sent as typed (YAML <c>body:</c>), used when there is no JSON body.
    /// </summary>
    public string? RawBody { get; set; }

    /// <summary>
    /// Name of the current phase (for multi-phase YAML tests).
    /// </summary>
    public string? PhaseName { get; set; }

    /// <summary>
    /// Validates that settings are consistent.
    /// Throws ArgumentException if validation fails.
    /// </summary>
    /// <param name="context">Optional context for error messages (e.g., phase name).</param>
    public void Validate(string? context = null)
    {
        var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";

        if (Vu < 1)
            throw new ArgumentException($"{prefix}Concurrency level must be at least 1.");

        if (Duration == null && MaxRequests == null)
            throw new ArgumentException($"{prefix}Must specify either duration or max requests.");

        if (Duration != null && MaxRequests != null)
            throw new ArgumentException($"{prefix}Duration and max requests are mutually exclusive.");
    }
}

internal sealed record AutoTuneSettings(
    int MaxClients,
    int Step,
    TimeSpan Interval,
    float MaxErrorRatio,
    float MinImprovementRatio,
    int MaxStallIntervals);