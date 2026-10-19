using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SharpArtillery.Reporting;

internal enum ReportingTemplates
{
    Html,
    ChartJs
}
internal static class ReportHelper
{
    public static Dictionary<ReportingTemplates, string> ReportingTemplatesLocations = new()
    {
        { ReportingTemplates.Html, "SharpArtillery.Reporting.Templates.reportTemplate.html" },
        { ReportingTemplates.ChartJs, "SharpArtillery.Reporting.Templates.chart.umd.min.js" }
    };
    public static Stream? GetContentFromManifest(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames();
        return assembly.GetManifestResourceStream(resourceName);
    }
}