using System.Collections.Generic;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SharpArtillery.YamlConfig;

internal class YamlHelper
{
    internal static ConfigRoot ReadYaml(string path)
    {
        var configInput = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        var root = deserializer.Deserialize<ConfigRoot>(configInput);
        return root;
    }

    internal static string GetReportExtension(string val) =>
        val.ToLowerInvariant() switch
        {
            "excel" => ".xlsx",
            "html" => ".html",
            _ => ".xlsx"
        };
}

public class ConfigRoot
{
    public Config Settings { get; set; }
    public List<Scenario>? Scenarios { get; set; }
}

public class Config
{
    public string? Target { get; set; }
    public string? Report { get; set; }
    public List<Phase>? Phases { get; set; }
}

public class Scenario
{
    public string? Name { get; set; }
    public List<Flow>? Flow { get; set; }
}

public class Flow
{
    public Get? Get { get; set; }
}

public class Get
{
    public string? Url { get; set; }
}


public class Phase
{
    public int Duration { get; set; }
    public int ArrivalRate { get; set; }
    public string? Name { get; set; }
    public bool ExcludeGraph { get; set; }
    public int? RampUp { get; set; }
    
    public int Vu { get; set; }
    public int Requests { get; set; }
}