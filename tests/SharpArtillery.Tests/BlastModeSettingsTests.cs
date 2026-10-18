using System;
using System.Text.Json;
using System.Threading.Tasks;
using SharpArtillery.Configs;
using SharpArtillery.Models;
using SharpArtillery.YamlConfig;
using Xunit;

namespace SharpArtillery.Tests;

public class BlastModeSettingsTests
{
    private const string Endpoint = "http://stub.test/x";

    private static ArtilleryConfig Flags() => new()
    {
        Target = Endpoint,
        RequestTimeout = TimeSpan.FromSeconds(5)
    };

    [Fact]
    [Trait("slice", "3")]
    public void The_yaml_timeout_replaces_the_dash_t_timeout()
    {
        var config = new ConfigRoot { Settings = new ConfigSettings { Timeout = 1 } };

        var settings = MainService.BlastPhaseSettings(Flags(), config, new Phase { Vu = 1, Requests = 1 },
            Endpoint, "GET", new RequestSpec());

        Assert.Equal(TimeSpan.FromSeconds(1), settings.RequestTimeout);
    }

    [Fact]
    [Trait("slice", "3")]
    public void Without_a_yaml_timeout_the_dash_t_timeout_is_kept()
    {
        var config = new ConfigRoot();

        var settings = MainService.BlastPhaseSettings(Flags(), config, new Phase { Vu = 1, Requests = 1 },
            Endpoint, "GET", new RequestSpec());

        Assert.Equal(TimeSpan.FromSeconds(5), settings.RequestTimeout);
    }

    [Fact]
    [Trait("slice", "3")]
    public async Task The_yaml_body_is_sent_as_typed()
    {
        var config = new ConfigRoot();
        var settings = MainService.BlastPhaseSettings(Flags(), config, new Phase { Vu = 1, Requests = 1 },
            Endpoint, "POST", new RequestSpec { Body = "{'raw': true}" });

        using var req = new RequestTemplate(settings).Build();

        Assert.Equal("{'raw': true}", await req.Content!.ReadAsStringAsync());
    }

    [Fact]
    [Trait("slice", "3")]
    public void The_yaml_method_and_headers_reach_the_settings()
    {
        var config = new ConfigRoot();
        var spec = new RequestSpec { Headers = new() { ["X-Api-Key"] = "abc" } };

        var settings = MainService.BlastPhaseSettings(Flags(), config, new Phase { Vu = 1, Requests = 1 },
            Endpoint, "PUT", spec);

        Assert.Equal("PUT", settings.Method);
        Assert.Equal("abc", settings.Headers!["X-Api-Key"]);
    }

    [Fact]
    [Trait("slice", "3")]
    public void A_body_with_an_apostrophe_inside_double_quotes_is_kept()
    {
        var body = CommandLineParser.ParseBody("{\"name\":\"O'Brien\"}");

        Assert.Equal("O'Brien", JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(body)).GetProperty("name").GetString());
    }

    [Fact]
    [Trait("slice", "3")]
    public void Single_quotes_around_strings_still_become_json()
    {
        var body = CommandLineParser.ParseBody("{'name':'Ann', 'note':\"it's\"}");

        var json = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(body));
        Assert.Equal("Ann", json.GetProperty("name").GetString());
        Assert.Equal("it's", json.GetProperty("note").GetString());
    }
}
