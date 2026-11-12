using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class StructuredFinalTests
{
    [Fact]
    public void Only_adapters_with_a_verified_format_use_final_output()
    {
        Assert.True(StructuredFinal.SupportsFinalResult(new JsonObject { ["adapter"] = "claude" }));
        foreach (var provider in new[] { "grok", "codex", "antigravity", "api" })
            Assert.False(StructuredFinal.SupportsFinalResult(new JsonObject { ["provider"] = provider }));
        Assert.True(StructuredFinal.SupportsFinalResult(new JsonObject { ["adapter"] = "", ["provider"] = "claude" }));
        Assert.False(StructuredFinal.SupportsFinalResult(null));
    }

    [Fact]
    public void The_schema_pins_the_attempt_identity()
    {
        var schema = StructuredFinal.ResultSchema(JsonNode.Parse("""{"resultId":"R","attemptId":"A","subTaskId":"S","megaTaskId":"M"}""")!.AsObject());
        Assert.Equal(["A"], schema["properties"]!["attemptId"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["COMPLETE", "FAILED", "BLOCKED"], schema["properties"]!["status"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(["resultId", "attemptId", "subTaskId", "megaTaskId", "status", "summary", "filesChanged", "tests", "blockers", "nextAction"], schema["required"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void Reads_the_Claude_result_event_and_refuses_error_results()
    {
        const string success = """{"type":"result","is_error":false,"structured_output":{"status":"COMPLETE","summary":"probe ok"}}""";
        Assert.Equal("probe ok", StructuredFinal.ReadFinalResult("claude", "{\"type\":\"system\"}\r\n" + success + "\r\n")!["summary"]!.GetValue<string>());
        Assert.Null(StructuredFinal.ReadFinalResult("claude", success.Replace("false", "true")));
        Assert.Null(StructuredFinal.ReadFinalResult("claude", "{\"type\":\"result\",\"is_error\":false}"));
        Assert.Null(StructuredFinal.ReadFinalResult("claude", success + "\n{\"type\":\"result\",\"is_error\":true}"));
        Assert.NotNull(StructuredFinal.ReadFinalResult("claude", success + "\nbad json"));
        Assert.NotNull(StructuredFinal.ReadFinalResult("claude", "\uFEFF" + success + "\uFEFF"));
    }

    [Theory]
    [InlineData("claude", "{\"status\":\"COMPLETE\",\"summary\":\"probe ok\"}")]
    [InlineData("claude", "plain text")]
    public void Free_text_is_never_read_as_a_result(string adapter, string text) => Assert.Null(StructuredFinal.ReadFinalResult(adapter, text));

    [Theory]
    [InlineData("grok", "{\"structuredOutput\":{}}")]
    [InlineData("claude", "")]
    [InlineData("claude", null)]
    [InlineData("claude", "   \r\n\t  ")]
    [InlineData("Claude", "{\"type\":\"result\",\"is_error\":false,\"structured_output\":{}}")]
    public void Other_adapters_and_empty_output_yield_nothing(string adapter, string? text) => Assert.Null(StructuredFinal.ReadFinalResult(adapter, text));

    [Fact]
    public void Later_success_recovers_from_an_earlier_error_event()
    {
        const string error = """{"type":"result","is_error":true,"structured_output":{"summary":"bad"}}""";
        const string success = """{"type":"result","is_error":false,"structured_output":{"summary":"ok"}}""";
        Assert.Equal("ok", StructuredFinal.ReadFinalResult("claude", error + "\n" + success)!["summary"]!.GetValue<string>());
        Assert.Null(StructuredFinal.ReadFinalResult("claude", success + "\n" + error));
        Assert.Equal("kept", StructuredFinal.ReadFinalResult("claude", """{"type":"result","structured_output":{"summary":"kept"}}""")!["summary"]!.GetValue<string>());
        Assert.Null(StructuredFinal.ReadFinalResult("claude", """{"type":"result","is_error":"false","structured_output":{"summary":"x"}}"""));
        Assert.Equal(0, StructuredFinal.ReadFinalResult("claude", """{"type":"result","is_error":0,"structured_output":0}""")!.GetValue<int>());
    }

    [Fact]
    public void Adapter_falls_back_to_provider_only_when_adapter_is_falsy()
    {
        Assert.True(StructuredFinal.SupportsFinalResult(new JsonObject { ["adapter"] = "claude", ["provider"] = "grok" }));
        Assert.False(StructuredFinal.SupportsFinalResult(new JsonObject { ["adapter"] = "grok", ["provider"] = "claude" }));
        Assert.True(StructuredFinal.SupportsFinalResult(new JsonObject { ["adapter"] = 0, ["provider"] = "claude" }));
        Assert.False(StructuredFinal.SupportsFinalResult(new JsonObject { ["adapter"] = "claude-code" }));
        Assert.Equal(8 * 1024 * 1024, StructuredFinal.MaxOutputChars);
    }

    [Fact]
    public void Schema_keeps_unknown_identity_values_and_optional_research_fields()
    {
        var schema = StructuredFinal.ResultSchema(new JsonObject { ["resultId"] = "R", ["attemptId"] = JsonValue.Create((string?)null) });
        Assert.Null(schema["required"]!.AsArray().FirstOrDefault(x => x!.GetValue<string>() == "createdAt"));
        Assert.Null(schema["properties"]!["attemptId"]!["enum"]![0]);
        Assert.Equal("string", schema["properties"]!["reportPath"]!["type"]!.GetValue<string>());
        Assert.Equal("array", schema["properties"]!["sources"]!["type"]!.GetValue<string>());
        Assert.Equal("string", schema["properties"]!["tests"]!["items"]!["properties"]!["evidence"]!["type"]!.GetValue<string>());
        var clone = schema["required"]!.DeepClone();
        schema["required"]!.AsArray().Add((JsonNode?)JsonValue.Create("extra"));
        Assert.Equal(10, clone.AsArray().Count);
    }
}
