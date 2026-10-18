using System.Diagnostics;
using Xunit;

namespace SharpArtillery.Tests;

public class InterpolateTests
{
    private static FlowExecutor ExecutorWith(params (string Name, string Value)[] variables)
    {
        var vu = new VirtualUser(0, "http://stub.test");
        foreach (var (name, value) in variables)
            vu.SetVariable(name, value);
        return new FlowExecutor(vu, new Stopwatch());
    }

    [Theory]
    [InlineData("Bearer {{ token }}", "Bearer abc")]
    [InlineData("Bearer {{token}}", "Bearer abc")]
    [InlineData("{{  token  }}", "abc")]
    [InlineData("/users/{{ id }}/orders/{{ token }}", "/users/7/orders/abc")]
    [InlineData("no placeholders", "no placeholders")]
    public void Known_variables_are_replaced(string template, string expected)
    {
        var executor = ExecutorWith(("token", "abc"), ("id", "7"));

        Assert.Equal(expected, executor.Interpolate(template));
    }

    [Fact]
    public void An_unknown_variable_is_left_as_written()
    {
        var executor = ExecutorWith(("token", "abc"));

        Assert.Equal("/x/{{ missing }}", executor.Interpolate("/x/{{ missing }}"));
    }

    [Fact]
    public void Only_word_characters_make_a_name()
    {
        var executor = ExecutorWith(("a", "1"));

        Assert.Equal("{{ a-b }}", executor.Interpolate("{{ a-b }}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void An_empty_template_comes_back_unchanged(string? template)
    {
        var executor = ExecutorWith();

        Assert.Equal(template, executor.Interpolate(template!));
    }
}

public class ExtractJsonPathTests
{
    private const string Json = """
        {
          "token": "abc",
          "count": 42,
          "ratio": 1.5,
          "ok": true,
          "off": false,
          "none": null,
          "data": { "id": "u-1", "nested": { "deep": "x" } },
          "items": [ { "name": "first" }, { "name": "second" } ],
          "obj": { "a": 1 }
        }
        """;

    [Theory]
    [InlineData("$.token", "abc")]
    [InlineData("token", "abc")]
    [InlineData("$.count", "42")]
    [InlineData("$.ratio", "1.5")]
    [InlineData("$.ok", "true")]
    [InlineData("$.off", "false")]
    [InlineData("$.data.id", "u-1")]
    [InlineData("$.data.nested.deep", "x")]
    [InlineData("$.items[1].name", "second")]
    [InlineData("$.obj", "{ \"a\": 1 }")]
    public void A_path_finds_its_value(string path, string expected)
    {
        Assert.Equal(expected, FlowExecutor.ExtractJsonPath(Json, path));
    }

    [Fact]
    public void A_root_array_index_works()
    {
        Assert.Equal("b", FlowExecutor.ExtractJsonPath("""["a","b"]""", "$[1]"));
    }

    [Theory]
    [InlineData("$.none")]
    [InlineData("$.missing")]
    [InlineData("$.items[5].name")]
    [InlineData("$.token[0]")]
    [InlineData("$.items.name")]
    public void A_path_that_finds_nothing_gives_null(string path)
    {
        Assert.Null(FlowExecutor.ExtractJsonPath(Json, path));
    }

    [Theory]
    [InlineData("not json", "$.token")]
    [InlineData("", "$.token")]
    [InlineData("""{"token":"abc"}""", "")]
    public void Bad_input_gives_null(string json, string path)
    {
        Assert.Null(FlowExecutor.ExtractJsonPath(json, path));
    }
}
