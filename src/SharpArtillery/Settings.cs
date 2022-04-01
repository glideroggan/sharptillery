using System;
using System.Collections.Generic;

internal class Settings
{
    public int? ConstantRps { get; set; }
    public TimeSpan? Duration { get; set; }
    public int? MaxRequests { get; set; }
    public int Vu { get; set; }
    public string? Target { get; set; }
    public string? Method { get; init; }
    public Dictionary<string,string>? Headers { get; set; }
    public object? JsonContent { get; set; }
}