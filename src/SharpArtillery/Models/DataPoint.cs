using System;
using System.Net;

namespace SharpArtillery.Models;

/// <summary>
/// One finished request. A run keeps every one in memory until the report is written, so this
/// holds only what the console summary and the report read: one timeline point and one latency,
/// no wall-clock times or copies of them.
/// </summary>
public struct DataPoint
{
    /// <summary>
    /// When the response was processed, from the start of the run.
    /// </summary>
    internal TimeSpan RequestTimeLine { get; set; }

    public TimeSpan ResponseTime { get; set; }
    public string? PhaseName { get; set; }
    public string? Error { get; set; }
    public string? ErrorType { get; set; }
    public string? ExceptionType { get; set; }
    public long? ResponseContentLengthBytes { get; set; }
    public HttpStatusCode Status { get; set; }
    public int Rps { get; set; }
    public bool HasResponse { get; set; }
    public bool IsTimeout { get; set; }

    /// <summary>
    /// Returns true if this data point represents an error (non-2xx status or has error message).
    /// </summary>
    public bool IsError() =>
        !string.IsNullOrEmpty(Error) || (int)Status < 200 || (int)Status >= 300;
}
