using System;
using System.Collections.Generic;
using SharpArtillery.YamlConfig;

namespace SharpArtillery.Configs;

public class ArtilleryConfig
{
    public bool ShowInfo { get; set; }
    public bool Quit { get; set; }
    
    
    public string? Yaml { get; set; }
    public string? Target { get; set; }
    public ConfigRoot? YamlConfig { get; set; }
    public TimeSpan? Duration { get; set; }
    public int? RequestRate { get; set; }

    public int? MaxRequests { get; set; }
    public int Clients { get; set; }
    public ReportSettings ReportSettings { get; } = new();
    public int ConstantRps { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Method { get; set; }
    public object? JsonContent { get; set; }
}