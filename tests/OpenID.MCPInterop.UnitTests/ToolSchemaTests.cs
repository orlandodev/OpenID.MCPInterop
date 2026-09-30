using System.Text.Json;
using OpenID.MCPInterop.Client.Helpers;
using OpenID.MCPInterop.Client.Rendering;
using Xunit;

namespace OpenID.MCPInterop.UnitTests;

/// <summary>
/// Covers reading a third-party tool's inputSchema into form fields and
/// validating submitted values against it before CallToolAsync, so a bad
/// value is explained in the log rather than surfacing as a server error.
/// </summary>
public sealed class ToolSchemaTests
{
    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "query":    { "type": "string", "description": "Search text" },
            "limit":    { "type": ["integer", "null"], "default": 10 },
            "mode":     { "enum": ["fast", "thorough"] },
            "strict":   { "anyOf": [ { "type": "boolean" }, { "type": "null" } ] },
            "filters":  { "properties": { "tag": { "type": "string" } } },
            "ids":      { "items": { "type": "string" } }
          },
          "required": ["query", "mode"]
        }
        """;

    [Theory]
    [InlineData("query", "string")]
    [InlineData("limit", "integer")]
    [InlineData("mode", "string")]
    [InlineData("strict", "boolean")]
    [InlineData("filters", "object")]
    [InlineData("ids", "array")]
    public void ParseSchemaFields_NonTrivialTypeShapes_ResolveToSingleType(string name, string expectedType)
    {
        var field = ClientHtmlRenderer.ParseSchemaFields(Schema).Single(f => f.Name == name);

        Assert.Equal(expectedType, field.Type);
    }

    [Fact]
    public void ParseSchemaFields_DescriptionEnumAndDefault_AreCaptured()
    {
        var fields = ClientHtmlRenderer.ParseSchemaFields(Schema).ToDictionary(f => f.Name);

        Assert.Equal("Search text", fields["query"].Description);
        Assert.Equal(["fast", "thorough"], fields["mode"].EnumValues);
        Assert.Equal("10", fields["limit"].DefaultValue);
        Assert.True(fields["mode"].Required);
    }

    [Fact]
    public void Build_ValidValues_ConvertsToDeclaredJsonTypes()
    {
        var fields = ClientHtmlRenderer.ParseSchemaFields(Schema);
        var values = new Dictionary<string, string>
        {
            ["query"] = "hello",
            ["limit"] = "5",
            ["mode"] = "fast",
            ["strict"] = "true",
            ["filters"] = """{"tag":"x"}""",
            ["ids"] = """["a"]""",
        };

        var arguments = ToolArgumentBuilder.Build(values, fields);

        Assert.Equal("hello", arguments["query"]);
        Assert.Equal(5L, arguments["limit"]);
        Assert.Equal(true, arguments["strict"]);
        Assert.Equal(JsonValueKind.Object, ((JsonElement)arguments["filters"]!).ValueKind);
        Assert.Equal(JsonValueKind.Array, ((JsonElement)arguments["ids"]!).ValueKind);
    }

    [Fact]
    public void Build_BlankOptionalField_IsOmittedNotSentAsEmptyString()
    {
        var fields = ClientHtmlRenderer.ParseSchemaFields(Schema);
        var values = new Dictionary<string, string> { ["query"] = "q", ["mode"] = "fast", ["limit"] = "  " };

        var arguments = ToolArgumentBuilder.Build(values, fields);

        Assert.False(arguments.ContainsKey("limit"));
    }

    [Theory]
    [InlineData("mode", "", "is required")]
    [InlineData("mode", "slow", "must be one of")]
    [InlineData("limit", "2.5", "whole number")]
    [InlineData("strict", "yes", "true or false")]
    [InlineData("filters", "[1]", "JSON object")]
    [InlineData("ids", "{not json", "valid JSON")]
    public void Build_ValueTheSchemaWouldReject_ThrowsFormatExceptionNamingTheProblem(string name, string value, string expectedMessage)
    {
        var fields = ClientHtmlRenderer.ParseSchemaFields(Schema);
        var values = new Dictionary<string, string> { ["query"] = "q", ["mode"] = "fast", [name] = value };

        var exception = Assert.Throws<FormatException>(() => ToolArgumentBuilder.Build(values, fields));

        Assert.Contains(expectedMessage, exception.Message);
    }
}
