using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SharpArtillery.Configs;
using SharpArtillery.Tests.Stubs;
using SharpArtillery.YamlConfig;
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

    private const string TwoGets = """
        scenarios:
          - name: "s"
            flow:
              - get:
                  url: "/a"
              - get:
                  url: "/b"
        """;

    private const string ThinkFlow = """
        scenarios:
          - name: "s"
            flow:
              - get:
                  url: "/a"
              - think: 1
        """;

    private const string CapturingRequest = """
        scenarios:
          - name: "s"
            flow:
              - get:
                  url: "/a"
                  capture:
                    - json: "$.token"
                      as: "token"
        """;

    // Red for slice 8: a flow run has no rate schedule, so -r was dropped without a word.
    [Fact]
    [Trait("slice", "8")]
    public void Rps_with_a_flow_of_several_steps_is_an_error()
    {
        var path = File(Settings + "\n" + OnePhase + "\n" + TwoGets);

        var error = Assert.Single(Errors("-y", path, "-r", "10"));
        Assert.Contains("-r/--rps", error);
        Assert.Contains("flow", error);
    }

    [Fact]
    [Trait("slice", "8")]
    public void Rps_with_a_think_step_is_an_error()
    {
        var path = File(Settings + "\n" + OnePhase + "\n" + ThinkFlow);

        Assert.Contains("-r/--rps", Assert.Single(Errors("-y", path, "-r", "10")));
    }

    [Fact]
    [Trait("slice", "8")]
    public void Rps_with_a_request_that_captures_is_an_error()
    {
        var path = File(Settings + "\n" + OnePhase + "\n" + CapturingRequest);

        Assert.Contains("-r/--rps", Assert.Single(Errors("-y", path, "-r", "10")));
    }

    // Blast mode is the one YAML run that uses -r, once per phase.
    [Fact]
    [Trait("slice", "8")]
    public void Rps_with_a_single_request_is_accepted()
    {
        var path = File(Settings + "\n" + OnePhase + "\n" + OneGet);

        Assert.Empty(Errors("-y", path, "-r", "10"));
    }

    // The model has no rate key: an rps under settings is refused as an unknown key, so it is never ignored.
    [Fact]
    [Trait("slice", "8")]
    public void A_rate_key_in_settings_is_an_unknown_key_error()
    {
        var path = File("settings:\n  target: \"http://localhost:8080\"\n  rps: 10\n" + OnePhase + "\n" + OneGet);

        var error = Assert.Single(Errors("-y", path));
        Assert.Contains("'rps'", error);
    }

    // Red for slice 3: a phase with neither duration nor requests was accepted at parse time.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("slice", "3")]
    public void A_phase_with_neither_duration_nor_requests_is_an_error_that_names_it(bool flow)
    {
        var path = File(Settings + "\n" + """
            phases:
              - name: "Warm-up"
                vu: 2
            """ + "\n" + (flow ? TwoGets : OneGet));

        var error = Assert.Single(Errors("-y", path));
        Assert.Contains("'Warm-up'", error);
        Assert.Contains("duration or requests", error);
    }

    // Red for slice 3: a phase with no users was accepted at parse time; left out means 0.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("slice", "3")]
    public void A_phase_with_no_users_is_an_error_that_names_it(bool flow)
    {
        var path = File(Settings + "\n" + """
            phases:
              - name: "Load"
                duration: 5
                vu: 0
            """ + "\n" + (flow ? TwoGets : OneGet));

        var error = Assert.Single(Errors("-y", path));
        Assert.Contains("'Load'", error);
        Assert.Contains("vu", error);
    }

    [Fact]
    [Trait("slice", "3")]
    public void A_phase_with_requests_and_no_duration_is_accepted()
    {
        var path = File(Settings + "\n" + """
            phases:
              - name: "Count"
                requests: 5
                vu: 2
            """ + "\n" + TwoGets);

        Assert.Empty(Errors("-y", path));
    }

    // Red for slice 8: a bare settings: key left Settings null, and the run died with a NullReferenceException.
    [Fact]
    [Trait("slice", "8")]
    public void A_bare_settings_key_reads_as_empty_settings()
    {
        var path = File("settings:\n" + OnePhase + "\n" + OneGet);

        var (config, errors) = YamlHelper.Load(path);

        Assert.Empty(errors);
        Assert.NotNull(config!.Settings);
        Assert.Null(config.Settings.Target);
    }

    [Fact]
    [Trait("slice", "8")]
    public async Task A_bare_settings_key_stops_the_run_with_the_missing_target_message()
    {
        var path = File("settings:\n" + OnePhase + "\n" + OneGet);
        var flags = new ArtilleryConfig { YamlConfig = YamlHelper.ReadYaml(path) };
        using var factory = new StubHttpClientFactory(StubHttpHandler.Returning(HttpStatusCode.OK));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MainService(NullLoggerFactory.Instance).Run(flags, factory));
        Assert.Contains("settings.target", error.Message);
    }
}
