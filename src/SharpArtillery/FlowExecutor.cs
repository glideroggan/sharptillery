using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpArtillery.Models;
using SharpArtillery.YamlConfig;

namespace SharpArtillery;

/// <summary>
/// Executes flow steps for a VirtualUser, handling HTTP requests, 
/// template interpolation, response captures, and think times.
/// </summary>
internal sealed partial class FlowExecutor
{
    private readonly VirtualUser _vu;
    private readonly Stopwatch _globalTimer;
    
    // Regex for {{ variable }} interpolation
    [GeneratedRegex(@"\{\{\s*(\w+)\s*\}\}")]
    private static partial Regex VariablePattern();

    public FlowExecutor(VirtualUser vu, Stopwatch globalTimer)
    {
        _vu = vu;
        _globalTimer = globalTimer;
    }

    /// <summary>
    /// Executes a single flow step and returns a DataPoint with timing info.
    /// </summary>
    public async Task<DataPoint> ExecuteStepAsync(FlowStep step, CancellationToken cancellationToken)
    {
        if (step.IsThink)
        {
            return await ExecuteThinkAsync(step.Think!.Value, cancellationToken);
        }
        
        var httpRequest = step.GetHttpRequest();
        if (httpRequest == null)
        {
            throw new InvalidOperationException("Flow step has no valid action (get/post/put/delete/think)");
        }
        
        return await ExecuteHttpRequestAsync(httpRequest.Value.Method, httpRequest.Value.Spec, cancellationToken);
    }

    /// <summary>
    /// Executes a think/pause step.
    /// </summary>
    private async Task<DataPoint> ExecuteThinkAsync(double seconds, CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;
        var delay = TimeSpan.FromSeconds(seconds);
        
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Test was cancelled during think time
        }
        
        // Think steps don't count as requests - return a marker DataPoint
        return new DataPoint
        {
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            RequestSentTime = startTime,
            RequestReceivedTime = DateTime.UtcNow,
            ResponseTime = delay,
            Latency = TimeSpan.Zero,
            RequestTimeLine = _globalTimer.Elapsed,
            PhaseName = _vu.PhaseName,
            Status = 0, // No HTTP status for think steps
            HasResponse = false,
            // Mark as non-error think step
            Error = null
        };
    }

    /// <summary>
    /// Executes an HTTP request step.
    /// </summary>
    private async Task<DataPoint> ExecuteHttpRequestAsync(
        string method, 
        RequestSpec spec, 
        CancellationToken cancellationToken)
    {
        var dataPoint = new DataPoint
        {
            StartTime = DateTime.UtcNow,
            PhaseName = _vu.PhaseName
        };
        dataPoint.RequestSentTime = dataPoint.StartTime;

        // Build the full URL with interpolation
        var url = _vu.BaseTarget + Interpolate(spec.Url ?? "");
        
        // Create request
        var httpMethod = method.ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            "PATCH" => HttpMethod.Patch,
            "HEAD" => HttpMethod.Head,
            "OPTIONS" => HttpMethod.Options,
            _ => HttpMethod.Get
        };
        
        using var request = new HttpRequestMessage(httpMethod, url);
        
        // Add headers with interpolation
        if (spec.Headers != null)
        {
            foreach (var (key, value) in spec.Headers)
            {
                request.Headers.TryAddWithoutValidation(key, Interpolate(value));
            }
        }
        
        // Add JSON body with interpolation
        if (spec.Json != null)
        {
            var interpolatedJson = InterpolateObject(spec.Json);
            request.Content = JsonContent.Create(interpolatedJson);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            dataPoint.RequestContentLengthBytes = request.Content.Headers.ContentLength;
        }
        else if (!string.IsNullOrEmpty(spec.Body))
        {
            request.Content = new StringContent(Interpolate(spec.Body));
            dataPoint.RequestContentLengthBytes = request.Content.Headers.ContentLength;
        }

        string? responseBody = null;
        
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_vu.RequestTimeout);
            
            using var response = await _vu.HttpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);
            
            dataPoint.Status = response.StatusCode;
            dataPoint.HasResponse = true;
            dataPoint.ProtocolVersion = response.Version.ToString();
            dataPoint.ResponseContentLengthBytes = response.Content.Headers.ContentLength;
            
            // Read response body for captures
            if (spec.Capture != null && spec.Capture.Count > 0)
            {
                responseBody = await response.Content.ReadAsStringAsync(cts.Token);
                ProcessCaptures(spec.Capture, responseBody, response.Headers);
            }
        }
        catch (OperationCanceledException oce) when (!cancellationToken.IsCancellationRequested)
        {
            dataPoint.Status = 0;
            dataPoint.IsTimeout = true;
            dataPoint.ErrorType = "timeout";
            dataPoint.ExceptionType = oce.GetType().FullName;
            dataPoint.Error = oce.Message;
        }
        catch (HttpRequestException e)
        {
            dataPoint.Status = HttpStatusCode.InternalServerError;
            dataPoint.ErrorType = "http_request_exception";
            dataPoint.ExceptionType = e.GetType().FullName;
            dataPoint.Error = e.Message;
        }
        catch (Exception e)
        {
            dataPoint.Status = HttpStatusCode.InternalServerError;
            dataPoint.ErrorType = "exception";
            dataPoint.ExceptionType = e.GetType().FullName;
            dataPoint.Error = e.Message;
        }

        dataPoint.RequestReceivedTime = DateTime.UtcNow;
        dataPoint.EndTime = DateTime.UtcNow;
        dataPoint.ResponseTime = dataPoint.EndTime - dataPoint.StartTime;
        dataPoint.Latency = dataPoint.ResponseTime;
        dataPoint.RequestTimeLine = _globalTimer.Elapsed;

        return dataPoint;
    }

    /// <summary>
    /// Interpolates {{ variable }} placeholders in a string.
    /// </summary>
    public string Interpolate(string template)
    {
        if (string.IsNullOrEmpty(template)) return template;
        
        return VariablePattern().Replace(template, match =>
        {
            var varName = match.Groups[1].Value;
            return _vu.GetVariable(varName) ?? match.Value; // Keep original if not found
        });
    }

    /// <summary>
    /// Interpolates variables in a dictionary (for JSON bodies).
    /// </summary>
    private Dictionary<string, object> InterpolateObject(Dictionary<string, object> source)
    {
        var result = new Dictionary<string, object>();
        foreach (var (key, value) in source)
        {
            result[key] = value switch
            {
                string s => Interpolate(s),
                Dictionary<string, object> nested => InterpolateObject(nested),
                _ => value
            };
        }
        return result;
    }

    /// <summary>
    /// Processes capture specifications and stores extracted values in VU variables.
    /// </summary>
    private void ProcessCaptures(
        List<CaptureSpec> captures, 
        string responseBody, 
        System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        foreach (var capture in captures)
        {
            if (string.IsNullOrEmpty(capture.As)) continue;
            
            string? value = null;
            
            if (!string.IsNullOrEmpty(capture.Json))
            {
                value = ExtractJsonPath(responseBody, capture.Json);
            }
            else if (!string.IsNullOrEmpty(capture.Header))
            {
                if (headers.TryGetValues(capture.Header, out var values))
                {
                    value = string.Join(", ", values);
                }
            }
            
            if (value != null)
            {
                _vu.SetVariable(capture.As, value);
            }
        }
    }

    /// <summary>
    /// Extracts a value from JSON using a simple JSONPath expression.
    /// Supports: $.field, $.nested.field, $.array[0].field
    /// </summary>
    private static string? ExtractJsonPath(string json, string path)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(path)) return null;
        
        try
        {
            using var doc = JsonDocument.Parse(json);
            var element = doc.RootElement;
            
            // Remove leading $. if present
            var normalizedPath = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path;
            if (normalizedPath.StartsWith('$')) normalizedPath = normalizedPath[1..];
            
            var segments = ParseJsonPathSegments(normalizedPath);
            
            foreach (var segment in segments)
            {
                if (segment.IsArrayIndex)
                {
                    if (element.ValueKind != JsonValueKind.Array) return null;
                    if (segment.ArrayIndex < 0 || segment.ArrayIndex >= element.GetArrayLength()) return null;
                    element = element[segment.ArrayIndex];
                }
                else
                {
                    if (element.ValueKind != JsonValueKind.Object) return null;
                    if (!element.TryGetProperty(segment.PropertyName!, out element)) return null;
                }
            }
            
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => null,
                _ => element.GetRawText()
            };
        }
        catch
        {
            return null;
        }
    }

    private record struct PathSegment(string? PropertyName, int ArrayIndex, bool IsArrayIndex);
    
    // Regex for array index: field[0] or [0]
    [GeneratedRegex(@"^(\w+)?\[(\d+)\]$")]
    private static partial Regex ArrayIndexPattern();
    
    private static List<PathSegment> ParseJsonPathSegments(string path)
    {
        var segments = new List<PathSegment>();
        var parts = path.Split('.');
        
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            
            var match = ArrayIndexPattern().Match(part);
            if (match.Success)
            {
                var propName = match.Groups[1].Value;
                var index = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                
                if (!string.IsNullOrEmpty(propName))
                {
                    segments.Add(new PathSegment(propName, 0, false));
                }
                segments.Add(new PathSegment(null, index, true));
            }
            else
            {
                segments.Add(new PathSegment(part, 0, false));
            }
        }
        
        return segments;
    }
}
