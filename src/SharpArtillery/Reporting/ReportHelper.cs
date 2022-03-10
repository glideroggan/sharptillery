using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SharpArtillery.Reporting;

internal enum ReportingTemplates
{
    Excel,
    Html
}
internal static class ReportHelper
{
    public static Dictionary<ReportingTemplates, string> ReportingTemplatesLocations = new()
    {
        { ReportingTemplates.Excel, "SharpArtillery.Reporting.Templates.reportTemplate.xlsx" },
        { ReportingTemplates.Html, "SharpArtillery.Reporting.Templates.reportTemplate.html" }
    };
    public static Stream? GetContentFromManifest(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames();
        return assembly.GetManifestResourceStream(resourceName);
    }
}