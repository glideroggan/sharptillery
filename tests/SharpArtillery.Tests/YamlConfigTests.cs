using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace SharpArtillery.Tests;

/// <summary>
/// The YAML file is checked when the command line is parsed: a key the run would ignore is an
/// error that names it, before any request is sent.
/// </summary>
public sealed class YamlConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sharptillery-tests", Guid.NewGuid().ToString("N"));

    public YamlConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string yaml)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".yaml");
        System.IO.File.WriteAllText(path, yaml);
        return path;
    }

    private static List<string> Errors(params string[] args)
    {
        using var host = new HostBuilder().Build();
        var root = CommandLineParser.BuildRootCommand(host);
        return root.Parse(args).Errors.Select(e => e.Message).ToList();
    }

    private const string Settings = """
        settings:
          target: "http://localhost:8080"
        """;

    private const string OneGet = """
        scenarios:
          - name: "s"
            flow:
              - get:
                  url: "/x"
        """;

    private const string OnePhase = """
        phases:
          - duration: 5
            vu: 2
        """;

    [Fact]
    [Trait("slice", "4")]
    public void The_sample_files_have_no_errors()
    {
        var blast = File(Settings + "\n  timeout: 5\n" + OnePhase + "\n" + OneGet);
        var flow = File(Settings + "\n" + OnePhase + """

            scenarios:
              - name: "flow"
                flow:
                  - post:
                      url: "/login"
                      json:
                        user: "a"
                      headers:
                        Content-Type: "application/json"
                      capture:
                        - json: "$.token"
                          as: "token"
                  - think: 1
                  - get:
                      url: "/me"
                      headers:
                        Authorization: "Bearer {{ token }}"
            """);

        Assert.Empty(Errors("-y", blast));
        Assert.Empty(Errors("-y", flow));
    }

    [Fact]
    [Trait("slice", "4")]
    public void The_shipped_config_yaml_has_no_errors()
    {
        // Copied next to the tests by the tool project.
        Assert.Empty(Errors("-y", Path.Combine(AppContext.BaseDirectory, "config.yaml")));
    }

    // Red for slice 4: only scenarios[0] ran; the others and their weights were ignored without a word.
    [Fact]
    [Trait("slice", "4")]
    public void Several_scenarios_are_an_error()
    {
        var path = File(Settings + "\n" + OnePhase + """

            scenarios:
              - name: "a"
                weight: 3
                flow:
                  - get:
                      url: "/a"
              - name: "b"
                weight: 1
                flow:
                  - get:
                      url: "/b"
            """);

        var error = Assert.Single(Errors("-y", path));
        Assert.Contains("scenario", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2", error);
    }

    // Red for slice 4: excludeGraph was read and never used, so the phase stayed in the report.
    [Fact]
    [Trait("slice", "4")]
    public void Exclude_graph_is_an_error()
    {
        var path = File(Settings + """

            phases:
              - name: "warm-up"
                duration: 5
                vu: 2
                excludeGraph: true
            """ + "\n" + OneGet);

        var error = Assert.Single(Errors("-y", path));
        Assert.Contains("excludeGraph", error);
        Assert.Contains("line", error);
    }

    // Red for slice 4: Artillery's arrival phases crashed the load with YamlDotNet's stack trace.
    [Theory]
    [InlineData("arrivalRate: 5")]
    [InlineData("rampTo: 50")]
    [InlineData("arrivalCount: 10")]
    [Trait("slice", "4")]
    public void An_arrival_rate_phase_is_an_error_that_names_the_key(string line)
    {
        var path = File(Settings + "\nphases:\n  - duration: 5\n    vu: 2\n    " + line + "\n" + OneGet);

        var error = Assert.Single(Errors("-y", path));
        Assert.Contains(line.Split(':')[0], error);
        Assert.Contains("vu", error);
    }

    [Fact]
    [Trait("slice", "4")]
    public void An_unknown_key_is_an_error_that_names_it_and_its_line()
    {
        var path = File(Settings + "\n" + OnePhase + """

            scenarios:
              - name: "s"
                flow:
                  - get:
                      url: "/x"
                      qs:
                        page: 1
            """);

        var error = Assert.Single(Errors("-y", path));
        Assert.Contains("'qs'", error);
        // settings (2 lines), phases (3), then scenarios: qs is line 11.
        Assert.Contains("line 11:", error);
    }

    // Red for slice 4: settings.report was read and never used.
    [Theory]
    [InlineData("excel")]
    [InlineData("pdf")]
    [Trait("slice", "4")]
    public void An_unsupported_settings_report_is_an_error(string report)
    {
        var path = File(Settings + "\n  report: \"" + report + "\"\n" + OnePhase + "\n" + OneGet);

        var error = Assert.Single(Errors("-y", path));
        Assert.Contains("settings.report", error);
        Assert.Contains($"'{report}'", error);
    }

    [Theory]
    [InlineData("html", "excel", "html")]
    [InlineData(null, "html", "html")]
    [InlineData(null, null, "html")]
    [Trait("slice", "4")]
    public void A_typed_extension_wins_over_settings_report(string? typed, string? report, string expected)
    {
        var yaml = new YamlConfig.ConfigRoot { Settings = new YamlConfig.ConfigSettings { Report = report } };

        Assert.Equal(expected, CommandLineParser.ReportType(typed, yaml));
    }

    [Fact]
    [Trait("slice", "4")]
    public void Settings_report_html_is_accepted()
    {
        var path = File(Settings + "\n  report: \"HTML\"\n" + OnePhase + "\n" + OneGet);

        Assert.Empty(Errors("-y", path, "-o", "report"));
    }

    [Fact]
    [Trait("slice", "6")]
    public void Settings_report_csv_is_accepted()
    {
        var path = File(Settings + "\n  report: \"csv\"\n" + OnePhase + "\n" + OneGet);

        Assert.Empty(Errors("-y", path, "-o", "report"));
    }

    [Fact]
    [Trait("slice", "4")]
    public void A_missing_file_is_an_error_not_a_crash()
    {
        var error = Assert.Single(Errors("-y", Path.Combine(_dir, "missing.yaml")));
        Assert.Contains("missing.yaml", error);
    }
}
