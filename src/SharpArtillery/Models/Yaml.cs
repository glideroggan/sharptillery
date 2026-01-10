using System.Collections.Generic;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SharpArtillery.YamlConfig;

internal sealed class YamlHelper
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

/// <summary>
/// Root configuration loaded from YAML.
/// </summary>
public class ConfigRoot
{
    public ConfigSettings Settings { get; set; } = new();
    
    /// <summary>
    /// Top-level phases array (preferred location).
    /// </summary>
    public List<Phase>? Phases { get; set; }
    
    public List<Scenario>? Scenarios { get; set; }
    
    /// <summary>
    /// Gets phases from top-level or falls back to settings.phases for backward compatibility.
    /// </summary>
    public List<Phase>? GetPhases() => Phases ?? Settings.Phases;
}

/// <summary>
/// Global settings for the load test.
/// </summary>
public class ConfigSettings
{
    /// <summary>
    /// Base URL for all requests (e.g., "http://localhost:8080").
    /// </summary>
    public string? Target { get; set; }
    
    /// <summary>
    /// Report format: "html" or "excel".
    /// </summary>
    public string? Report { get; set; }
    
    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int? Timeout { get; set; }
    
    /// <summary>
    /// Legacy: request timeout (use Timeout instead).
    /// </summary>
    public int? RequestTimeoutSeconds { get; set; }
    
    /// <summary>
    /// Legacy: phases nested under settings (use top-level phases instead).
    /// </summary>
    public List<Phase>? Phases { get; set; }
    
    /// <summary>
    /// Gets the effective timeout in seconds.
    /// </summary>
    public int? GetTimeout() => Timeout ?? RequestTimeoutSeconds;
}

/// <summary>
/// A load test phase with specific VU count and duration.
/// </summary>
public class Phase
{
    /// <summary>
    /// Human-readable name for the phase (e.g., "Warm-up", "Load").
    /// </summary>
    public string? Name { get; set; }
    
    /// <summary>
    /// Duration in seconds (mutually exclusive with Requests).
    /// </summary>
    public int? Duration { get; set; }
    
    /// <summary>
    /// Virtual Users (concurrent clients) for this phase.
    /// </summary>
    public int Vu { get; set; }
    
    /// <summary>
    /// Max requests for this phase (mutually exclusive with Duration).
    /// </summary>
    public int? Requests { get; set; }
    
    /// <summary>
    /// Whether to exclude this phase from graphs (e.g., warm-up phases).
    /// </summary>
    public bool ExcludeGraph { get; set; }
}

/// <summary>
/// A scenario defines a flow of requests that each VU executes.
/// </summary>
public class Scenario
{
    /// <summary>
    /// Human-readable name for the scenario.
    /// </summary>
    public string? Name { get; set; }
    
    /// <summary>
    /// Weight for scenario selection (future: for weighted random selection).
    /// </summary>
    public int Weight { get; set; } = 1;
    
    /// <summary>
    /// Sequence of flow steps to execute.
    /// </summary>
    public List<FlowStep>? Flow { get; set; }
}

/// <summary>
/// A single step in a flow. Exactly one of the properties should be set.
/// </summary>
public class FlowStep
{
    /// <summary>
    /// HTTP GET request.
    /// </summary>
    public RequestSpec? Get { get; set; }
    
    /// <summary>
    /// HTTP POST request.
    /// </summary>
    public RequestSpec? Post { get; set; }
    
    /// <summary>
    /// HTTP PUT request.
    /// </summary>
    public RequestSpec? Put { get; set; }
    
    /// <summary>
    /// HTTP DELETE request.
    /// </summary>
    public RequestSpec? Delete { get; set; }
    
    /// <summary>
    /// Think time in seconds (pause between requests).
    /// </summary>
    public double? Think { get; set; }
    
    /// <summary>
    /// Gets the HTTP method and request spec, or null if this is a think step.
    /// </summary>
    public (string Method, RequestSpec Spec)? GetHttpRequest()
    {
        if (Get != null) return ("GET", Get);
        if (Post != null) return ("POST", Post);
        if (Put != null) return ("PUT", Put);
        if (Delete != null) return ("DELETE", Delete);
        return null;
    }
    
    /// <summary>
    /// Returns true if this is a think/pause step.
    /// </summary>
    public bool IsThink => Think.HasValue;
}

/// <summary>
/// Specification for an HTTP request within a flow.
/// </summary>
public class RequestSpec
{
    /// <summary>
    /// URL path (appended to settings.target).
    /// Supports {{ variable }} interpolation.
    /// </summary>
    public string? Url { get; set; }
    
    /// <summary>
    /// Request headers.
    /// Values support {{ variable }} interpolation.
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }
    
    /// <summary>
    /// JSON body (for POST/PUT). Will be serialized to JSON.
    /// Values support {{ variable }} interpolation.
    /// </summary>
    public Dictionary<string, object>? Json { get; set; }
    
    /// <summary>
    /// Raw body string (alternative to Json).
    /// Supports {{ variable }} interpolation.
    /// </summary>
    public string? Body { get; set; }
    
    /// <summary>
    /// Capture values from the response for use in subsequent requests.
    /// </summary>
    public List<CaptureSpec>? Capture { get; set; }
}

/// <summary>
/// Specification for capturing a value from a response.
/// </summary>
public class CaptureSpec
{
    /// <summary>
    /// JSONPath expression to extract value (e.g., "$.token", "$.data.id").
    /// </summary>
    public string? Json { get; set; }
    
    /// <summary>
    /// Header name to capture.
    /// </summary>
    public string? Header { get; set; }
    
    /// <summary>
    /// Variable name to store the captured value.
    /// </summary>
    public string? As { get; set; }
}
