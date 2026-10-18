using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace SharpArtillery.Tests;

public class HeaderParsingTests
{
    private static Dictionary<string, string> Parse(params string[] headers)
    {
        var target = new Dictionary<string, string>();
        CommandLineParser.ParseHeaders(headers, target);
        return target;
    }

    [Fact]
    public void Name_and_value_are_split_at_the_colon_and_trimmed()
    {
        var headers = Parse(" X-Api-Key :  abc 123 ");

        Assert.Equal("abc 123", headers["X-Api-Key"]);
    }

    [Fact]
    public void Only_the_first_colon_splits()
    {
        var headers = Parse("Referer:http://example.com:8080/x");

        Assert.Equal("http://example.com:8080/x", headers["Referer"]);
    }

    [Fact]
    public void Several_headers_are_all_kept()
    {
        var headers = Parse("A:1", "B:2");

        Assert.Equal(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }, headers);
    }

    [Fact]
    public void A_repeated_name_keeps_the_last_value()
    {
        var headers = Parse("A:1", "A:2");

        Assert.Equal("2", headers["A"]);
    }

    [Fact]
    public void An_empty_value_is_kept()
    {
        var headers = Parse("A:");

        Assert.Equal("", headers["A"]);
    }

    // Today a header without a colon, or with nothing before it, is dropped without a word.
    // Slice 3 turns the missing colon into an error.
    [Theory]
    [InlineData("NoColon")]
    [InlineData(":value")]
    public void An_entry_without_a_name_is_skipped(string header)
    {
        Assert.Empty(Parse(header));
    }

    [Fact]
    public void No_headers_leaves_the_target_empty()
    {
        var target = new Dictionary<string, string>();

        CommandLineParser.ParseHeaders(null, target);

        Assert.Empty(target);
    }
}

public class CommandLineValidatorTests
{
    private static List<string> Errors(params string[] args)
    {
        using var host = new HostBuilder().Build();
        var root = CommandLineParser.BuildRootCommand(host);
        return root.Parse(args).Errors.Select(e => e.Message).ToList();
    }

    [Theory]
    [InlineData("-t", "http://localhost:8080/x", "-n", "10")]
    [InlineData("-t", "https://localhost/x", "-d", "00:00:05")]
    [InlineData("-y", "config.yaml")]
    [InlineData("-t", "http://localhost/x", "-d", "00:00:05", "-A")]
    [InlineData("-t", "http://localhost/x", "-n", "1", "-o", "report", "-e", "html")]
    public void Valid_arguments_have_no_errors(params string[] args)
    {
        Assert.Empty(Errors(args));
    }

    [Fact]
    public void Requests_and_duration_together_are_an_error()
    {
        var errors = Errors("-t", "http://localhost/x", "-n", "10", "-d", "00:00:05");

        Assert.Contains("Cannot use -n/--requests and -d/--duration together. Choose one.", errors);
    }

    [Fact]
    public void Neither_requests_duration_nor_yaml_is_an_error()
    {
        var errors = Errors("-t", "http://localhost/x");

        Assert.Contains("Must specify -n/--requests, -d/--duration, or -y/--yaml.", errors);
    }

    [Fact]
    public void A_missing_target_without_yaml_is_an_error()
    {
        var errors = Errors("-n", "10");

        Assert.Contains("Target URL (-t/--target) is required unless using YAML config (-y/--yaml).", errors);
    }

    [Fact]
    public void A_relative_target_is_an_error()
    {
        var errors = Errors("-t", "localhost/x", "-n", "10");

        Assert.Contains("Invalid target URL: 'localhost/x'. Expected http(s)://host/path.", errors);
    }

    [Fact]
    public void A_non_http_scheme_is_an_error()
    {
        var errors = Errors("-t", "ftp://localhost/x", "-n", "10");

        Assert.Contains("Invalid URL scheme: 'ftp'. Use http or https.", errors);
    }

    [Fact]
    public void Autotune_without_duration_is_an_error()
    {
        var errors = Errors("-y", "config.yaml", "-A");

        Assert.Contains("Auto-tune (-A/--autotune) requires duration mode (-d/--duration).", errors);
    }

    [Fact]
    public void Autotune_with_requests_is_an_error()
    {
        var errors = Errors("-t", "http://localhost/x", "-n", "10", "-A");

        Assert.Contains("Auto-tune (-A/--autotune) is not supported with max requests (-n/--requests).", errors);
    }

    // Red for slice 3: today `-e excel` passes the parser, maps to .xlsx and throws
    // NotImplementedException in MainService.WriteReportAsync after the whole run.
    [Fact(Skip = "slice 3")]
    [Trait("slice", "3")]
    public void An_unsupported_report_type_is_a_parse_error()
    {
        var errors = Errors("-t", "http://localhost/x", "-n", "1", "-o", "report", "-e", "excel");

        Assert.NotEmpty(errors);
    }
}
