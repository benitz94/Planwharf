// Port of engine/api-agent.cjs tool and transport cases.
using System.Net;
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;
namespace Planwharf.Engine.Tests;
public sealed class ApiAgentTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "api-agent-" + Guid.NewGuid());
    public ApiAgentTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    ApiAgent.Dependencies Dependencies() => new() { Folder = root, Http = (_, _) => throw new InvalidOperationException() };
    [Theory]
    [InlineData("../escape", false)]
    [InlineData(".GiT/config", true)]
    [InlineData(".agents/skills/file", true)]
    [InlineData(".codex/config", true)]
    public void Rejects_escape_and_control_writes(string path, bool writing) => Assert.ThrowsAny<Exception>(() => ApiAgent.WorkspacePath(root, JsonValue.Create(path), writing));
    [Fact]
    public async Task Writes_and_reads_utf8_and_denies_lead()
    {
        var call = JsonNode.Parse("""{"name":"write_file","input":{"path":"nested/é.txt","content":"hello é"}}""")!.AsObject();
        await ApiAgent.ExecuteToolAsync(call, new(), Dependencies());
        Assert.Equal("hello é", File.ReadAllText(Path.Combine(root, "nested/é.txt")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApiAgent.ExecuteToolAsync(call, new() { ["lead"] = true }, Dependencies()));
        call["name"] = "read_file";
        Assert.Equal("hello é", (await ApiAgent.ExecuteToolAsync(call, new(), Dependencies()))!.GetValue<string>());
    }
    [Fact]
    public async Task Http_failures_do_not_echo_body_and_malformed_json_is_protocol_failure()
    {
        using var denied = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("sentinel-secret") };
        Assert.Equal("API HTTP 401", (await Assert.ThrowsAsync<InvalidOperationException>(() => ApiAgent.ResponseJsonAsync(denied))).Message);
        using var malformed = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") };
        Assert.Contains("invalid JSON", (await Assert.ThrowsAsync<InvalidOperationException>(() => ApiAgent.ResponseJsonAsync(malformed))).Message);
    }
    static JsonObject Job(bool anthropic = false) => JsonNode.Parse("""{"model":"exact","prompt":"hello","config":{"baseUrl":"https://fixture.invalid","apiProtocol":"openai","maxTurns":3,"maxOutputTokens":100,"requestTimeoutMs":1000}}""")!.AsObject().Also(j => j["config"]!["apiProtocol"] = anthropic ? "anthropic" : "openai");
    static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_protocols_continue_after_permission_denial_and_report_usage(bool anthropic)
    {
        var requests = new List<ApiAgent.Request>(); var events = new List<JsonObject>();
        var job = Job(anthropic);
        var deps = new ApiAgent.Dependencies { Folder = root, Emit = events.Add, Http = (r, _) =>
        {
            requests.Add(r);
            return Task.FromResult(Reply(requests.Count == 1
                ? anthropic ? """{"content":[{"type":"tool_use","id":"one","name":"run_command","input":{"command":"fake","args":[]}}],"usage":{"input_tokens":5,"output_tokens":2}}"""
                    : """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"one","function":{"name":"run_command","arguments":"{\"command\":\"fake\",\"args\":[]}"}}]}}],"usage":{"prompt_tokens":5,"completion_tokens":2}}"""
                : anthropic ? """{"model":"exact","content":[{"type":"text","text":"done"}]}""" : """{"model":"exact","choices":[{"message":{"role":"assistant","content":"done"}}]}"""));
        } };
        var result = await ApiAgent.RunAgentAsync(job, deps);
        Assert.Equal("done", result["answer"]!.GetValue<string>());
        Assert.Equal(5, result["tokens"]!["input"]!.GetValue<double>());
        Assert.Equal(2, requests.Count);
        Assert.Contains("PERMISSION_DENIED", requests[1].Body["messages"]!.ToJsonString());
        Assert.Equal("error", requests[0].Redirect);
        Assert.Contains(anthropic ? "input_schema" : "function", requests[0].Body["tools"]!.ToJsonString());
        Assert.Equal(3, events.Count);
    }
    [Theory]
    [InlineData("{\"model\":\"other\"}", "PROVIDER_MODEL_IDENTITY_CHANGED")]
    [InlineData("{}", "assistant message missing")]
    [InlineData("{\"choices\":[{\"message\":{\"role\":\"user\"}}]}", "assistant message missing")]
    [InlineData("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\" \"}}]}", "no recognized response")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"role\":\"assistant\"}}]}", "API_OUTPUT_LIMIT")]
    [InlineData("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"tool_calls\":null}}]}", "tool_calls must be an array")]
    public async Task Protocol_failures_reject_instead_of_becoming_tool_results(string response, string expected)
    {
        var count = 0;
        var deps = new ApiAgent.Dependencies { Folder = root, Http = (_, _) => { count++; return Task.FromResult(Reply(response)); } };
        Assert.Contains(expected, (await Assert.ThrowsAnyAsync<Exception>(() => ApiAgent.RunAgentAsync(Job(), deps))).Message);
        Assert.Equal(1, count);
    }
    [Fact]
    public async Task Advisory_has_no_tools_and_rejects_returned_calls()
    {
        var job = Job(true); job["advisory"] = true;
        var deps = new ApiAgent.Dependencies { Folder = root, Http = (r, _) =>
        {
            Assert.False(r.Body.ContainsKey("tools"));
            return Task.FromResult(Reply("""{"content":[{"type":"tool_use","id":"a","name":"read_file","input":{"path":"x"}}]}"""));
        } };
        Assert.Contains("Supervisor responses", (await Assert.ThrowsAsync<InvalidOperationException>(() => ApiAgent.RunAgentAsync(job, deps))).Message);
    }
    [Fact]
    public async Task Response_and_file_byte_bounds_are_enforced()
    {
        using var response = Reply(new string(' ', 4194305));
        Assert.Contains("4 MiB", (await Assert.ThrowsAsync<InvalidOperationException>(() => ApiAgent.ResponseJsonAsync(response))).Message);
        File.WriteAllBytes(Path.Combine(root, "large"), new byte[65537]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApiAgent.ExecuteToolAsync(JsonNode.Parse("""{"name":"read_file","input":{"path":"large"}}""")!.AsObject(), new(), Dependencies()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApiAgent.ExecuteToolAsync(new() { ["name"] = "write_file", ["input"] = new JsonObject { ["path"] = "x", ["content"] = new string('é', 524289) } }, new(), Dependencies()));
        Assert.False(File.Exists(Path.Combine(root, "x")));
    }
    [Fact]
    public async Task Commands_have_explicit_bounds_argv_and_cancellation()
    {
        var call = JsonNode.Parse("""{"name":"run_command","input":{"command":"fake","args":["a b","é"]}}""")!.AsObject();
        var deps = new ApiAgent.Dependencies { Folder = root, Http = (_, _) => throw new InvalidOperationException(), RunCommand = (c, _) =>
        {
            Assert.Equal(60000, c.TimeoutMs); Assert.Equal(65536, c.MaxBuffer); Assert.True(c.WindowsHide);
            Assert.Equal(new[] { "a b", "é" }, c.Args);
            return Task.FromResult(new ApiAgent.CommandReply(7, new string('x', 25000), new string('y', 9000)));
        } };
        var result = await ApiAgent.ExecuteToolAsync(call, new() { ["alwaysApprove"] = true }, deps);
        Assert.Equal(24000, result!["stdout"]!.GetValue<string>().Length);
        Assert.Equal(8000, result["stderr"]!.GetValue<string>().Length);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApiAgent.ExecuteToolAsync(call, new(), deps, cancel.Token));
    }
    [Fact]
    public void Junction_ancestors_cannot_escape_even_for_missing_descendants()
    {
        // Windows directory junctions require no symlink privilege.
        var outside = Path.Combine(root, "outside"); var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(outside); Directory.CreateDirectory(workspace);
        var link = Path.Combine(workspace, "link");
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!; process.StandardOutput.ReadToEnd(); process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, outside);
        try { Assert.Contains("resolves outside", Assert.Throws<InvalidOperationException>(() => ApiAgent.WorkspacePath(workspace, JsonValue.Create("link/missing/child"), true)).Message); }
        finally { Directory.Delete(link); }
    }
    [Fact]
    public async Task Assigned_result_can_leave_workspace_only_after_exact_identity_validation()
    {
        var identity = JsonNode.Parse("""{"resultId":"R-a","attemptId":"a","subTaskId":"s","megaTaskId":"m"}""")!.AsObject();
        var result = identity.DeepClone().AsObject();
        result["status"] = "COMPLETE"; result["summary"] = "done"; result["nextAction"] = "stop";
        result["filesChanged"] = new JsonArray(); result["tests"] = new JsonArray(); result["blockers"] = new JsonArray();
        var workspace = Path.Combine(root, "worker"); Directory.CreateDirectory(workspace);
        var destination = Path.Combine(root, "result.json");
        var job = new JsonObject { ["resultOutputPath"] = destination, ["resultIdentity"] = identity };
        var deps = new ApiAgent.Dependencies { Folder = workspace, Http = (_, _) => throw new InvalidOperationException() };
        var call = new JsonObject { ["name"] = "write_result", ["input"] = new JsonObject { ["content"] = result.ToJsonString() } };
        await ApiAgent.ExecuteToolAsync(call, job, deps);
        Assert.True(WorkerResult.ValidateResult(JsonNode.Parse(File.ReadAllText(destination)), identity));
        File.Delete(destination); result["attemptId"] = "wrong"; call["input"]!["content"] = result.ToJsonString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApiAgent.ExecuteToolAsync(call, job, deps));
        Assert.False(File.Exists(destination));
        result["attemptId"] = "a"; call["input"]!["content"] = result.ToJsonString(); job["lead"] = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApiAgent.ExecuteToolAsync(call, job, deps));
    }
    [Fact]
    public async Task Turn_limit_is_not_a_success_and_response_resources_are_disposed()
    {
        var job = Job(); job["config"]!["maxTurns"] = 1;
        HttpResponseMessage? response = null;
        var deps = new ApiAgent.Dependencies { Folder = root, Http = (_, _) => Task.FromResult(response = Reply("""{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"a","function":{"name":"unknown","arguments":"{}"}}]}}]}""")) };
        Assert.Contains("API_TURN_LIMIT", (await Assert.ThrowsAsync<InvalidOperationException>(() => ApiAgent.RunAgentAsync(job, deps))).Message);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => response!.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task Unsafe_usage_is_ignored_and_http_cancellation_propagates()
    {
        var deps = new ApiAgent.Dependencies { Folder = root, Http = (_, _) => Task.FromResult(Reply("""{"usage":{"prompt_tokens":9007199254740992,"completion_tokens":1},"choices":[{"message":{"role":"assistant","content":"ok"}}]}""")) };
        Assert.Null((await ApiAgent.RunAgentAsync(Job(), deps))["tokens"]);
        var cancelling = new ApiAgent.Dependencies { Folder = root, Http = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Reply("{}"); } };
        using var cancellation = new CancellationTokenSource(); cancellation.CancelAfter(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApiAgent.RunAgentAsync(Job(), cancelling, cancellation.Token));
    }
    [Theory]
    [InlineData(33)]
    [InlineData(1)]
    public async Task Invalid_or_excessive_calls_fail_before_tool_execution(int count)
    {
        var calls = new JsonArray();
        for (var i = 0; i < count; i++) calls.Add((JsonNode)new JsonObject { ["id"] = count == 1 ? "" : "id" + i,
            ["function"] = new JsonObject { ["name"] = "write_file", ["arguments"] = "{\"path\":\"unexpected\",\"content\":\"bad\"}" } });
        var message = new JsonObject { ["choices"] = new JsonArray((JsonNode)new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["tool_calls"] = calls } }) };
        var deps = new ApiAgent.Dependencies { Folder = root, Http = (_, _) => Task.FromResult(Reply(message.ToJsonString())) };
        Assert.Contains("invalid or excessive", (await Assert.ThrowsAsync<InvalidOperationException>(() => ApiAgent.RunAgentAsync(Job(), deps))).Message);
        Assert.False(File.Exists(Path.Combine(root, "unexpected")));
    }
}
internal static class ApiAgentTestExtensions
{
    internal static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
