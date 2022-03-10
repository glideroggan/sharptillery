using System;

internal class Settings
{
    public int? ConstantRps { get; set; }
    public TimeSpan? Duration { get; set; }
    public int? MaxRequests { get; set; }
    public int Vu { get; set; }
    public string? Target { get; set; }
}