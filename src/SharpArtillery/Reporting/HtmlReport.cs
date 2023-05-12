using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace SharpArtillery.Reporting;

internal static class HtmlReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    [RequiresUnreferencedCode("Calls System.Text.Json.JsonSerializer.Serialize<SharpArtillery.YamlConfig.Config>(SharpArtillery.YamlConfig.Config, System.Text.Json.JsonSerializerOptions?)")]
    public static async Task CreateAsync(string outputPath, List<DataPoint> reportData, string url)
    {
        // fix the main graph
        reportData.Sort((a, b) =>
            a.RequestSentTime < b.RequestSentTime ? -1 :
            a.RequestSentTime == b.RequestSentTime ? 0 : 1);
        // reportData.Sort((a, b) =>
        //     a.RequestReceivedTime < b.RequestReceivedTime ? -1 :
        //     a.RequestReceivedTime == b.RequestReceivedTime ? 0 : 1);

        // get content from manifest
        await using var stream =
            ReportHelper.GetContentFromManifest(ReportHelper.ReportingTemplatesLocations[ReportingTemplates.Html]);
        Debug.Assert(stream != null);

        // stream is now the template, so make a new file
        var dest = File.Create(outputPath);
        await stream.CopyToAsync(dest);
        await dest.FlushAsync();
        dest.Close();

        // add info to new file
        var content = await File.ReadAllTextAsync(dest.Name);
        content = content.Replace("<# ENDPOINT #>", url);

        // add settings
        // var settingsJson = JsonSerializer.Serialize(settings, Options);
        // content = content.Replace("<# PUT SETTINGS HERE #>", settingsJson);
        
        // prepare data for html (<# PUT DATA HERE #>)
        var dataStr = PrepareRequestData(reportData);
        content = content.Replace("<# PUT DATA HERE #>", dataStr);

        // write content back and we should be done
        await using var reportFile = File.OpenWrite(dest.Name);
        var byteArr = Encoding.UTF8.GetBytes(content);
        await reportFile.WriteAsync(byteArr);

        Console.Out.WriteLine("Report saved");
    }

    [RequiresUnreferencedCode(
        "Calls System.Text.Json.JsonSerializer.Serialize<System.Collections.Generic.IEnumerable<<anonymous type: int Time, System.TimeSpan Latency, int RequestRate, string Phase>>>(System.Collections.Generic.IEnumerable<<anonymous type: int Time, System.TimeSpan Latency, int RequestRate, string Phase>>, System.Text.Json.JsonSerializerOptions?)")]
    private static string PrepareRequestData(List<DataPoint> requestData)
    {
        var counter = 0;
        var jsonArr = requestData.Select(d => new
        {
            Time = d.RequestTimeLine.TotalSeconds, Latency = d.ResponseTime.TotalMilliseconds,
            Rps = d.Rps, Phase = d.PhaseName
        }).OrderBy(x => x.Time);
        
        var dataStr = JsonSerializer.Serialize(jsonArr, Options);
        return dataStr;
    }
}