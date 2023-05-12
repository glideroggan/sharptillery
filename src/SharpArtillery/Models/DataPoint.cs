using System;
using System.Net;

namespace SharpArtillery.Models;

public struct DataPoint
{
    internal DateTime RequestSentTime;

    /// <summary>
    /// When the response was processed
    /// </summary>
    internal TimeSpan RequestTimeLine;

    internal DateTime RequestReceivedTime;
    internal TimeSpan Latency;
    public string PhaseName;
    public string? Error { get; set; }
    public int ConcurrentRequests { get; set; }
    public int RequestRate { get; set; }

    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan ResponseTime { get; set; }
    public HttpStatusCode Status { get; set; }
    public int Rps { get; set; }
}