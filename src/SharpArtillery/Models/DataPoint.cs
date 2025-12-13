using System;
using System.Net;

namespace SharpArtillery.Models;

public struct DataPoint
{
    internal DateTime RequestSentTime { get; set; }

    /// <summary>
    /// When the response was processed
    /// </summary>
    internal TimeSpan RequestTimeLine { get; set; }

    internal DateTime RequestReceivedTime;
    internal TimeSpan Latency { get; set; }
    public string? PhaseName { get; set; }
    public string? Error { get; set; }
    public int ConcurrentRequests { get; set; }
    public int RequestRate { get; set; }

    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan ResponseTime { get; set; }
    public HttpStatusCode Status { get; set; }
    public int Rps { get; set; }

    public bool HasResponse { get; set; }
    public bool IsTimeout { get; set; }
    public string? ErrorType { get; set; }
    public string? ExceptionType { get; set; }
    public string? ProtocolVersion { get; set; }
    public long? RequestContentLengthBytes { get; set; }
    public long? ResponseContentLengthBytes { get; set; }
}