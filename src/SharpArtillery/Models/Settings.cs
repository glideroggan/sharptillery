using System;
using System.Collections.Generic;

namespace SharpArtillery.Configs;

internal class Settings
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
}

internal sealed record AutoTuneSettings(
    int MaxClients,
    int Step,
    TimeSpan Interval,
    float MaxErrorRatio,
    float MinImprovementRatio,
    int MaxStallIntervals);