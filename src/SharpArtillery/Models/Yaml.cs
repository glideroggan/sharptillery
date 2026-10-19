using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SharpArtillery.YamlConfig;

internal sealed partial class YamlHelper
{
    /// <summary>
    /// Loads a YAML file that <see cref="Load"/> has found no errors in.
    /// </summary>
    internal static ConfigRoot ReadYaml(string path)
    {
        var (config, errors) = Load(path);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        return config!;
    }

    /// <summary>
    /// Reads the YAML file, or says why it cannot run as written. The model has a property only for
    /// keys the run uses, so any other key (Artillery's arrival-rate phases, <c>excludeGraph</c>, a
    /// misspelling) is an error that names the key and its line instead of being ignored.
    /// </summary>
    internal static (ConfigRoot? Config, List<string> Errors) Load(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            return (null, [$"YAML file '{path}' cannot be read: {e.Message}"]);
        }

        ConfigRoot root;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            // An empty file has no root; the run then reports what is missing.
            root = deserializer.Deserialize<ConfigRoot?>(text) ?? new ConfigRoot();
            // A bare "settings:" key deserializes to null. It reads as empty settings, so the run reports
            // the missing target instead of dereferencing null.
            root.Settings ??= new ConfigSettings();
        }
        catch (YamlException e)
        {
            return (null, [DescribeYamlError(path, e)]);
        }

        return (root, UnsupportedValues(path, root));
    }

    /// <summary>
    /// What the user can do about a key the model has no property for. Keys not listed get no hint.
    /// </summary>
    private static readonly Dictionary<string, string> KeyHints = new(StringComparer.Ordinal)
    {
        ["arrivalRate"] = ArrivalHint,
        ["arrivalCount"] = ArrivalHint,
        ["rampTo"] = ArrivalHint,
        ["maxVusers"] = ArrivalHint,
        ["excludeGraph"] = "Every phase is in the report."
    };

    private const string ArrivalHint =
        "A phase runs a fixed number of users: give it vu, with duration or requests.";

    [GeneratedRegex(@"Property '(?<key>[^']+)' not found on type '(?<type>[^']+)'")]
    private static partial Regex UnknownProperty();

    private static string DescribeYamlError(string path, YamlException e)
    {
        var where = $"YAML file '{path}' line {e.Start.Line}";
        var unknown = UnknownProperty().Match(e.Message);
        if (unknown.Success)
        {
            var key = unknown.Groups["key"].Value;
            var hint = KeyHints.TryGetValue(key, out var text) ? " " + text : "";
            return $"{where}: '{key}' is not supported in {Section(unknown.Groups["type"].Value)}.{hint}";
        }

        // A value of the wrong type (vu: many) is wrapped; the innermost message names the value.
        Exception inner = e;
        while (inner.InnerException != null) inner = inner.InnerException;
        return $"{where}: {inner.Message}";
    }

    private static string Section(string typeName) =>
        typeName[(typeName.LastIndexOf('.') + 1)..] switch
        {
            nameof(ConfigRoot) => "the top level",
            nameof(ConfigSettings) => "settings",
            nameof(Phase) => "a phase",
            nameof(Scenario) => "a scenario",
            nameof(FlowStep) => "a flow step",
            nameof(RequestSpec) => "a request",
            nameof(CaptureSpec) => "a capture",
            var other => other
        };

    /// <summary>
    /// Keys the model reads but the run cannot honour as written.
    /// </summary>
    private static List<string> UnsupportedValues(string path, ConfigRoot root)
    {
        var errors = new List<string>();

        if (root.Scenarios is { Count: > 1 } scenarios)
        {
            errors.Add($"YAML file '{path}': {scenarios.Count} scenarios found, but only one scenario is " +
                       "supported (weighted scenarios are not). Keep one under scenarios.");
        }

        if (root.Settings?.Report is { } report && !IsSupportedReportType(report))
        {
            errors.Add($"YAML file '{path}': settings.report: Unsupported report type: '{report}'. Use html or csv.");
        }

        // Every run sends its requests to settings.target, so a file without one cannot run.
        if (string.IsNullOrEmpty(root.Settings?.Target))
        {
            errors.Add($"YAML file '{path}': settings.target is missing. Give the base URL there, " +
                       "for example target: \"http://localhost:8080\".");
        }

        // A bare "-" in a flow is an empty step: it is neither a request nor a think, so the run cannot use it.
        if (root.Scenarios is { Count: > 0 } scenarioList && scenarioList[0]?.Flow is { } steps)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i] is null)
                    errors.Add($"YAML file '{path}': Flow step {i + 1} is empty. Give it get, post, put, delete or think.");
            }
        }

        // A phase needs users and an end. A request count of zero or less is no count, as in the run.
        var phases = root.GetPhases() ?? new List<Phase>();
        for (var i = 0; i < phases.Count; i++)
        {
            var phase = phases[i];
            var label = phase?.Name is { Length: > 0 } name ? $"Phase '{name}'" : $"Phase {i + 1} (unnamed)";
            if (phase is null)
            {
                errors.Add($"YAML file '{path}': Phase {i + 1} is empty. Give it vu, and duration or requests.");
                continue;
            }

            if (phase.Vu < 1)
                errors.Add($"YAML file '{path}': {label} needs vu of at least 1.");
            if (phase.Duration == null && phase.Requests is not > 0)
                errors.Add($"YAML file '{path}': {label} needs duration or requests.");
            // Blast mode would refuse this only at run time, after the earlier phases had sent their requests.
            if (phase.Duration != null && phase.Requests is > 0)
                errors.Add($"YAML file '{path}': {label} has both duration and requests. Give it one.");
        }

        return errors;
    }

    /// <summary>
    /// The report types a run can write, for <c>-e</c> and <c>settings.report</c> alike.
    /// </summary>
    internal static bool IsSupportedReportType(string type) =>
        string.Equals(type, "html", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "csv", StringComparison.OrdinalIgnoreCase);

    internal static string GetReportExtension(string val) =>
        val.ToLowerInvariant() switch
        {
            "html" => ".html",
            "csv" => ".csv",
            // Every value reaching here passed IsSupportedReportType, so an unknown one is a bug, not a format.
            _ => throw new ArgumentException($"No report file for report type '{val}'.", nameof(val))
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
    /// Report type, as <c>-e</c>: "html" or "csv". A typed <c>-e</c> wins; <c>-o</c> still turns the report on.
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
    /// Accepted for Artillery files; with one scenario allowed it has no effect.
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
