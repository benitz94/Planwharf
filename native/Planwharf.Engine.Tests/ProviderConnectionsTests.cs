using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ProviderConnectionsTests
{
    private static JsonObject Input() => JsonNode.Parse("""{"id":"deepseek-api","label":" DeepSeek ","adapter":"api","baseUrl":"https://api.example.invalid","authEnv":"TEST_KEY","apiProtocol":"openai","modelIds":["exact-model","another-model"],"toolsEnabled":true} """)!.AsObject();
    private static JsonObject Cli()
    {
        var input = Input(); input["adapter"] = "configured-cli"; input["cliCommand"] = "/synthetic/cli";
        input["cliArgs"] = new JsonArray("--model", "{model}"); input["yoloArgs"] = new JsonArray(); input["restrictedArgs"] = new JsonArray(); input["outputProtocol"] = "codex-jsonl";
        return input;
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Connection_PreservesExactIdsRolesAndUnverifiedAvailability(bool tools)
    {
        var input = Input(); input["toolsEnabled"] = tools;
        var before = input.ToJsonString();
        var provider = ProviderConnections.Connection(input);
        Assert.Equal(before, input.ToJsonString());
        Assert.Equal("DeepSeek", provider["label"]!.GetValue<string>());
        Assert.False(provider["policy"]!["alwaysApprove"]!.GetValue<bool>());
        Assert.Equal("deepseek-api", provider["quotaPoolId"]!.GetValue<string>());
        Assert.True(provider["enabled"]!.GetValue<bool>());
        Assert.Equal(2, provider["models"]!.AsArray().Count);
        foreach (var model in provider["models"]!.AsArray())
        {
            Assert.Equal("deepseek-api:" + model!["cli"]!.GetValue<string>(), model["id"]!.GetValue<string>());
            foreach (var role in new[] { "orchestratorEligible", "workerEligible", "reviewerEligible" }) Assert.Equal(tools, model[role]!.GetValue<bool>());
            Assert.False(model["capabilities"]!["canResearch"]!.GetValue<bool>());
            Assert.False(model["availabilityVerified"]!.GetValue<bool>());
            Assert.Equal("Configured route; account access and model inference have not been verified.", model["availabilityWarning"]!.GetValue<string>());
        }
    }
    [Theory]
    [InlineData("apiKey", "\"synthetic-secret\"", "Invalid connection fields. Store an environment variable name, never an API key.")]
    [InlineData("id", "\"../bad\"", "Use a unique lowercase provider ID and a label.")]
    [InlineData("label", "\" \"", "Use a unique lowercase provider ID and a label.")]
    [InlineData("adapter", "\"unknown\"", "Unsupported connection adapter.")]
    [InlineData("modelIds", "[]", "Provide unique exact model IDs without whitespace.")]
    [InlineData("modelIds", "[\"a\",\"a\"]", "Provide unique exact model IDs without whitespace.")]
    [InlineData("modelIds", "[\"white space\"]", "Provide unique exact model IDs without whitespace.")]
    [InlineData("modelIds", "[\"a\\uFEFFb\"]", "Provide unique exact model IDs without whitespace.")]
    [InlineData("modelIds", "[null]", "Provide unique exact model IDs without whitespace.")]
    [InlineData("toolsEnabled", "null", "Declare whether these models support tools.")]
    [InlineData("toolsEnabled", "1", "Declare whether these models support tools.")]
    public void Connection_RejectsSecretsAmbiguousIdsAndMalformedFields(string key, string json, string error)
    {
        var input = Input(); input[key] = JsonNode.Parse(json);
        Assert.Equal(error, Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(input)).Message);
    }
    [Fact]
    public void Connection_PreservesValidationOrder()
    {
        var input = Input(); input["id"] = "BAD"; input["adapter"] = "bad"; input["toolsEnabled"] = null; input["modelIds"] = null;
        Assert.StartsWith("Use a unique", Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(input)).Message);
        input["id"] = "valid";
        Assert.Equal("Unsupported connection adapter.", Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(input)).Message);
        input["adapter"] = "api";
        Assert.StartsWith("Provide unique", Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(input)).Message);
        input["modelIds"] = new JsonArray("exact");
        Assert.StartsWith("Declare whether", Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(input)).Message);
    }
    [Fact]
    public void Connection_CoversEveryLengthBoundaryAndUnicode()
    {
        var input = Input(); input["id"] = new string('a', 64); input["label"] = new string('日', 100);
        var models = new JsonArray();
        for (var i = 0; i < 100; i++) models.Add((JsonNode?)JsonValue.Create(i == 0 ? new string('m', 200) : "m" + i));
        input["modelIds"] = models;
        Assert.Equal(100, ProviderConnections.Connection(input)["models"]!.AsArray().Count);
        foreach (var (key, value) in new[] { ("id", new string('a', 65)), ("label", new string('日', 101)) })
        {
            var copy = (JsonObject)input.DeepClone(); copy[key] = value;
            Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(copy));
        }
        models.Add((JsonNode?)JsonValue.Create("last")); Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(input));
        input["modelIds"] = new JsonArray(new string('m', 201)); Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(input));
        input["modelIds"] = new JsonArray("Upper/日本語", "upper/日本語");
        Assert.Equal(2, ProviderConnections.Connection(input)["models"]!.AsArray().Count);
        input["label"] = "\ufeffName\ufeff";
        Assert.Equal("Name", ProviderConnections.Connection(input)["label"]!.GetValue<string>());
    }
    [Fact]
    public void Connection_CliRequiresExplicitArraysPlaceholderAndProtocol()
    {
        var input = Cli(); var provider = ProviderConnections.Connection(input);
        Assert.True(JsonNode.DeepEquals(input["cliArgs"], provider["cliArgs"]));
        provider["cliArgs"]![0] = "changed"; Assert.Equal("--model", input["cliArgs"]![0]!.GetValue<string>());
        foreach (var key in new[] { "cliArgs", "yoloArgs", "restrictedArgs" })
        {
            foreach (var json in new[] { "null", "[1]", "[\"bad\\narg\"]" })
            {
                input = Cli(); input[key] = JsonNode.Parse(json);
                Assert.Equal(key + " must be an explicit JSON array of arguments.", Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(input)).Message);
            }
        }
        input = Cli(); input["cliArgs"] = new JsonArray();
        Assert.Equal("CLI arguments must include {model}.", Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(input)).Message);
        input = Cli(); input["outputProtocol"] = "text";
        Assert.Equal("Select a supported structured output protocol.", Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(input)).Message);
        input = Cli(); input["cliCommand"] = "bad\0";
        Assert.Equal("Provide a native CLI executable.", Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(input)).Message);
    }
    [Fact]
    public void Connection_PreservesSourceRegexCoercionAndRejectsNonObjects()
    {
        foreach (var input in new JsonNode?[] { null, new JsonArray(1), JsonValue.Create("invalid"), JsonValue.Create(1) })
            Assert.Equal("Invalid connection fields. Store an environment variable name, never an API key.", Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(input)).Message);
        Assert.Equal("Use a unique lowercase provider ID and a label.", Assert.Throws<InvalidOperationException>(() => ProviderConnections.Connection(new JsonArray())).Message);
        var obj = Input(); obj["id"] = new JsonArray("route");
        var provider = ProviderConnections.Connection(obj);
        Assert.True(JsonNode.DeepEquals(obj["id"], provider["id"]));
        Assert.Equal("route:exact-model", provider["models"]![0]!["id"]!.GetValue<string>());
    }
}
