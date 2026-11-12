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
    public void Other_adapters_and_empty_output_yield_nothing(string adapter, string? text) => Assert.Null(StructuredFinal.ReadFinalResult(adapter, text));
}
