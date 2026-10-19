using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SharpArtillery.Models;
using SharpArtillery.Reporting;
using Xunit;

namespace SharpArtillery.Tests;

// A report opened without a network had no charts: they came from Google Charts on gstatic.com
// (sharptillery#583 S5). The chart library now ships inside the report.
public partial class HtmlReportTemplateTests
{
    // Anything the browser would fetch on its own: a script or image source, a stylesheet link,
    // a CSS import or url().
    [GeneratedRegex("""<script\b[^>]*\bsrc\s*=|<link\b|<img\b|<iframe\b|@import|\burl\(""",RegexOptions.IgnoreCase)]
    private static partial Regex ExternalLoad();

    private static string Template()
    {
        using var stream = ReportHelper.GetContentFromManifest(
            ReportHelper.ReportingTemplatesLocations[ReportingTemplates.Html]);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_template_loads_nothing_from_outside()
    {
        var template = Template();

        Assert.DoesNotMatch(ExternalLoad(), template);
        Assert.DoesNotContain("gstatic", template, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("google.charts", template, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_written_report_carries_chart_js_and_loads_nothing_from_outside()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sharptillery-report-{Guid.NewGuid():N}.html");
        var data = new List<DataPoint>
        {
            new()
            {
                Status = HttpStatusCode.OK, HasResponse = true, PhaseName = "A",
                ResponseTime = TimeSpan.FromMilliseconds(12), RequestTimeLine = TimeSpan.FromSeconds(1)
            }
        };
        try
        {
            await HtmlReport.CreateAsync(path, data, "http://stub.test/x");
            var report = await File.ReadAllTextAsync(path);

            Assert.Contains("Chart.js v4", report, StringComparison.Ordinal);
            Assert.Contains("Released under the MIT License", report, StringComparison.Ordinal);
            Assert.DoesNotContain("<#", report, StringComparison.Ordinal);
            Assert.DoesNotMatch(ExternalLoad(), report);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
