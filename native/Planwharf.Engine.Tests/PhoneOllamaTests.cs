// Port of engine/phone-ollama.test.cjs.
using System.Net;
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class PhoneOllamaTests
{
    private static HttpResponseMessage Reply(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json) };

    [Fact]
    public void Ollama_defaults_to_local_qwen_and_refuses_other_origins()
    {
        var fresh = PhoneOllama.NormalizeSettings(null);
        Assert.Equal(new OllamaSettings(true, "http://127.0.0.1:11434", "qwen2.5:3b"), fresh);
        Assert.Equal(new OllamaSettings(false, "http://localhost:11434", "qwen3:4b"),
            PhoneOllama.NormalizeSettings(new JsonObject { ["enabled"] = false, ["baseUrl"] = "http://localhost:11434/api", ["model"] = " qwen3:4b " }));
        foreach (var url in new[] { "https://127.0.0.1:11434", "http://example.com:11434", "http://user:pass@127.0.0.1:11434", "http://localhost:11434?x=1", "http://localhost:11434/#x", "not a url" })
        {
            var normalized = PhoneOllama.NormalizeSettings(new JsonObject { ["baseUrl"] = url, ["model"] = "bad model name" });
            Assert.Equal(PhoneOllama.DefaultUrl, normalized.BaseUrl);
            Assert.Equal(PhoneOllama.DefaultModel, normalized.Model);
        }
        Assert.Equal("http://[::1]:11434", PhoneOllama.LoopbackUrl("http://[::1]:11434/path"));
        Assert.Equal(PhoneOllama.DefaultModel, PhoneOllama.NormalizeSettings(new JsonObject { ["model"] = new string('a', 121) }).Model);
    }

    [Fact]
    public void Project_facts_and_prompts_are_bounded_and_keep_exact_voice()
    {
        var projects = new JsonArray
        {
            new JsonObject { ["name"] = "Harbor", ["goal"] = "Paint the dock\nprivate line", ["status"] = "waiting_on_you", ["step"] = new JsonObject { ["step"] = 2, ["total"] = 5 }, ["timeLeft"] = new JsonObject { ["minutes"] = 9 } }
        };
        var facts = PhoneOllama.ProjectFacts(projects);
        Assert.Equal("Paint the dock", facts[0]?["goal"]?.GetValue<string>());
        Assert.Equal("2/5", facts[0]?["step"]?.GetValue<string>());
        Assert.True(facts[0]?["waiting"]?.GetValue<bool>());
        var messages = PhoneOllama.BuildReplyMessages("a che punto siamo?", projects, model: "qwen3:4b", language: "it");
        var prompt = messages[0]?["content"]?.GetValue<string>();
        Assert.Contains("Reply in the user's language", prompt);
        Assert.Contains("The local model answering this message is qwen3:4b.", prompt);
        Assert.Contains("waiting on the captain", prompt);
        Assert.DoesNotContain("private line", prompt);
        Assert.Equal("a che punto siamo?", messages[^1]?["content"]?.GetValue<string>());
        Assert.True(prompt!.Length <= 2400);
        Assert.Null(PhoneOllama.ProjectFacts(new JsonArray { new JsonObject { ["name"] = "No estimate" } })[0]?["minutes"]);
    }

    [Fact]
    public async Task Chat_memory_is_bounded_isolated_clearable_and_recovers_from_broken_file()
    {
        var memory = new ChatMemory(3);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => { memory.Remember("chat-a", "user", $"alpha {i}"); memory.Remember("chat-b", "assistant", $"beta {i}"); })));
        Assert.Equal(3, memory.History("chat-a").Count);
        Assert.All(memory.History("chat-b"), item => Assert.StartsWith("beta", item.Text));
        memory.Clear("chat-a");
        Assert.Empty(memory.History("chat-a"));
        Assert.Equal(3, memory.History("chat-b").Count);
        memory.Remember("chat-b", "system", "ignored");
        memory.Remember("chat-b", "user", new string('x', 600));
        Assert.Equal(500, memory.History("chat-b")[^1].Text.Length);
        var dir = Directory.CreateTempSubdirectory("pw-ollama-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, PhoneOllama.MemoryFile), "{");
            Assert.Empty(PhoneOllama.LoadChatMemory(dir.FullName).History("chat-b"));
            PhoneOllama.SaveChatMemory(dir.FullName, memory);
            Assert.Equal(memory.History("chat-b"), PhoneOllama.LoadChatMemory(dir.FullName).History("chat-b"));
            Assert.Empty(Directory.GetFiles(dir.FullName, "*.tmp"));
            var unicode = new ChatMemory();
            unicode.Remember("città", "user", "À <dock> & sea");
            PhoneOllama.SaveChatMemory(dir.FullName, unicode);
            Assert.Equal("[\n  {\n    \"chatId\": \"città\",\n    \"items\": [\n      {\n        \"role\": \"user\",\n        \"text\": \"À <dock> & sea\"\n      }\n    ]\n  }\n]\n", File.ReadAllText(Path.Combine(dir.FullName, PhoneOllama.MemoryFile)));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task Chat_posts_exact_payload_retries_once_and_escapes_html()
    {
        var calls = 0;
        var messages = new JsonArray { (JsonNode)new JsonObject { ["role"] = "user", ["content"] = "status?" } };
        async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            calls++;
            Assert.Equal("http://127.0.0.1:11434/api/chat", request.RequestUri?.ToString());
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            var payload = JsonNode.Parse((await request.Content!.ReadAsStringAsync(token)))!;
            Assert.Equal("qwen2.5:3b", payload["model"]?.GetValue<string>());
            Assert.False(payload["stream"]?.GetValue<bool>());
            Assert.Equal("5m", payload["keep_alive"]?.GetValue<string>());
            Assert.Equal(180, payload["options"]?["num_predict"]?.GetValue<int>());
            Assert.Equal(0.2, payload["options"]?["temperature"]?.GetValue<double>());
            return calls == 1 ? Reply(HttpStatusCode.ServiceUnavailable, "{\"message\":{\"content\":\"<leak>\"}}") : Reply(HttpStatusCode.OK, "{\"message\":{\"content\":\"Tide <high> & rising\"}}");
        }
        var result = await PhoneOllama.AskOllamaAsync(null, null, messages, Send);
        Assert.Equal(2, calls);
        Assert.True(result.Ok);
        Assert.Equal("🏴‍☠️ Ahoy, Captain!\n\nTide &lt;high&gt; &amp; rising", result.Text);
        Assert.DoesNotContain("<leak>", result.Text);
    }

    [Fact]
    public async Task Errors_empty_json_and_cancellation_fail_closed()
    {
        var calls = 0;
        var failed = await PhoneOllama.AskOllamaAsync(null, null, null, (_, _) => { calls++; return Task.FromResult(Reply(HttpStatusCode.InternalServerError, "{\"message\":{\"content\":\"D:/secret <no>\"}}")); });
        Assert.Equal(2, calls);
        Assert.False(failed.Ok);
        Assert.Equal("HTTP 500", failed.Reason);
        Assert.Equal(PhoneOllama.UnavailableReply("en", "HTTP 500"), failed.Text);
        Assert.DoesNotContain("secret", failed.Text);
        var empty = await PhoneOllama.AskOllamaAsync(null, null, null, (_, _) => Task.FromResult(Reply(HttpStatusCode.OK, "bad json")));
        Assert.Equal("empty", empty.Reason);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        calls = 0;
        var cancelled = await PhoneOllama.AskOllamaAsync(null, null, null, (_, _) => { calls++; return new TaskCompletionSource<HttpResponseMessage>().Task; }, cancellationToken: cancellation.Token);
        Assert.Equal("timeout", cancelled.Reason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Deadlines_apply_to_each_attempt_and_model_listing()
    {
        var calls = 0;
        Task<HttpResponseMessage> Never(HttpRequestMessage _, CancellationToken token)
        {
            calls++;
            return new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }
        var reply = await PhoneOllama.AskOllamaAsync(null, null, null, Never, timeoutMs: 5);
        Assert.Equal(2, calls);
        Assert.Equal("timeout", reply.Reason);
        var models = await PhoneOllama.ListInstalledModelsAsync(null, Never, timeoutMs: 5);
        Assert.False(models.Ok);
        Assert.Empty(models.Models);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Model_listing_and_concurrent_replies_are_isolated()
    {
        var list = await PhoneOllama.ListInstalledModelsAsync("http://localhost:11434", (request, _) =>
        {
            Assert.Equal("http://localhost:11434/api/tags", request.RequestUri?.ToString());
            return Task.FromResult(Reply(HttpStatusCode.OK, "{\"models\":[{\"name\":\"qwen3:4b\"},{\"name\":\"\"},{\"name\":\"qwen2.5:3b\"}]}"));
        });
        Assert.True(list.Ok);
        Assert.Equal(new[] { "qwen3:4b", "qwen2.5:3b" }, list.Models);
        Assert.False((await PhoneOllama.ListInstalledModelsAsync(null, (_, _) => Task.FromResult(Reply(HttpStatusCode.OK, "{")))).Ok);
        var replies = await Task.WhenAll(
            PhoneOllama.AskOllamaAsync(null, "qwen2.5:3b", null, (_, _) => Task.FromResult(Reply(HttpStatusCode.InternalServerError, "{}"))),
            PhoneOllama.AskOllamaAsync(null, "qwen3:4b", null, (_, _) => Task.FromResult(Reply(HttpStatusCode.OK, "{\"message\":{\"content\":\"Clear water <ok>\"}}"))));
        Assert.False(replies[0].Ok);
        Assert.True(replies[1].Ok);
        Assert.Equal("qwen3:4b", replies[1].Model);
        Assert.Contains("Clear water &lt;ok&gt;", replies[1].Text);
    }

    [Theory]
    [InlineData("/clear", true)]
    [InlineData("/dimentica@harbor_bot", true)]
    [InlineData("cancella memoria", true)]
    [InlineData("/clear now", false)]
    public void Clear_commands_are_exact(string text, bool expected) => Assert.Equal(expected, PhoneOllama.IsClearMemory(text));

    [Fact]
    public void Bare_confirmation_and_greetings_do_not_execute_actions()
    {
        Assert.True(PhoneOllama.IsBareConfirmation("SÌ!"));
        Assert.False(PhoneOllama.IsBareConfirmation("what is the status?"));
        Assert.Equal("🏴‍☠️ Ahoy, Capitano!\n\nkept", PhoneOllama.WithCaptain("Ahoy, Capitano! kept", "it"));
        Assert.Equal("🏴‍☠️ Ahoy, Captain!", PhoneOllama.WithCaptain(""));
    }
}
