using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class AntigravityTests
{
    [Fact]
    public void Fixture_keeps_Gemini_readable_when_Claude_and_GPT_are_exhausted()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "fixtures", "antigravity-usage.json")))
            root = root.Parent;
        Assert.NotNull(root);
        var body = JsonNode.Parse(File.ReadAllText(Path.Combine(root.FullName, "fixtures", "antigravity-usage.json")))!;
        var original = Antigravity.ParseUsage(body);
        Assert.Equal(98, original["leftPct"]!.GetValue<int>());
        Assert.Equal(2, original["windows"]!.AsArray().Count);
        Assert.Equal(100, original["extraPlans"]![0]!["leftPct"]!.GetValue<int>());
        foreach (var bucket in body["command"]!["data"]!["groups"]![1]!["buckets"]!.AsArray())
            bucket!["remaining_fraction"] = 0;
        var result = Antigravity.ParseUsage(body);
        Assert.False(result["exhausted"]!.GetValue<bool>());
        Assert.True(result["extraPlans"]![0]!["exhausted"]!.GetValue<bool>());
        Assert.Equal(Antigravity.ClaudeGptPool, result["extraPlans"]![0]!["id"]!.GetValue<string>());
        Assert.False(original["extraPlans"]![0]!["exhausted"]!.GetValue<bool>());
    }

    [Fact]
    public void Usage_envelope_requires_string_status_and_command_name()
    {
        var body = Usage(new JsonObject { ["remaining_fraction"] = 1 });
        body["status"] = new JsonArray("SUCCESS");
        Assert.False(Antigravity.ParseUsage(body)["readable"]!.GetValue<bool>());
        body["status"] = "SUCCESS";
        body["command"]!["name"] = new JsonArray("usage");
        Assert.False(Antigravity.ParseUsage(body)["readable"]!.GetValue<bool>());
        body["status"] = new JsonArray("ERROR");
        body["error"] = "subscription expired";
        Assert.Equal("unknown", Antigravity.ParseUsage(body)["planStatus"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\u2028")]
    public void Group_names_with_trailing_line_terminators_do_not_match(string suffix)
    {
        var body = Usage(new JsonObject { ["remaining_fraction"] = 0 });
        body["command"]!["data"]!["groups"]![0]!["name"] = "Gemini Models" + suffix;
        Assert.False(Antigravity.ParseUsage(body)["readable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Successful_listing_preserves_unicode_executable_bounds_and_exact_labels()
    {
        var calls = 0;
        var result = await Antigravity.ListAntigravityAsync("C:/isolated spazio/工具/agy.exe", (command, _) =>
        {
            calls++;
            Assert.Equal("C:/isolated spazio/工具/agy.exe", command.Executable);
            Assert.Equal(new[] { "models" }, command.Arguments);
            Assert.Equal(20000, command.TimeoutMilliseconds);
            Assert.Equal(1024 * 1024, command.MaxBufferBytes);
            Assert.True(command.WindowsHide);
            return Task.FromResult(new Antigravity.Reply("\u001b[32mgemini-3.8-flash-high\u001b[0m  Gemini 3.8 Flash (High)\r\nclaude-opus-4-6-thinking\u00a0\u00a0Claude Opus 4.6 (Thinking)\ngpt-oss-120b-medium"));
        });
        Assert.Equal(1, calls);
        Assert.Equal(3, result["ids"]!.AsArray().Count);
        Assert.Equal("Gemini 3.8 Flash (High)", result["models"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("Claude Opus 4.6 (Thinking)", result["models"]![1]!["label"]!.GetValue<string>());
        Assert.False(result["models"]![2]!.AsObject().ContainsKey("label"));
    }

    [Fact]
    public async Task Usage_passes_host_cancellation_without_claiming_unread_quota()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Antigravity.ReadAntigravityAsync("fake", (_, token) =>
        {
            Assert.Equal(source.Token, token);
            return Task.FromCanceled<Antigravity.Reply>(token);
        }, source.Token));
    }

    [Theory]
    [InlineData("claude-opus-4-6-thinking", Antigravity.ClaudeGptPool)]
    [InlineData("gpt-oss-120b-medium", Antigravity.ClaudeGptPool)]
    [InlineData("gemini-3-flash", "Gemini")]
    [InlineData("gpt-6", "Gemini")]
    public void Claude_and_GPT_models_have_their_own_quota(string cli, string expected)
        => Assert.Equal(expected, Antigravity.PoolOf(JsonValue.Create(cli), JsonValue.Create("Gemini"))!.GetValue<string>());

    [Fact]
    public void Absent_numeric_quota_stays_unread()
    {
        var result = Antigravity.ParseUsage(null);
        Assert.False(result["readable"]!.GetValue<bool>());
        Assert.Null(result["pct"]);
        Assert.Single(result["extraPlans"]!.AsArray());
    }

    [Fact]
    public void First_readable_label_wins_without_guessing_aliases()
    {
        var result = Antigravity.ParseModels("\u001b[32mclaude-opus-4-6\u001b[0m\nclaude-opus-4-6  Opus\nclaude-opus-4-6  Changed\ngemini-flash");
        Assert.Equal(2, result["ids"]!.AsArray().Count);
        Assert.Equal("Opus", result["models"]![0]!["label"]!.GetValue<string>());
        Assert.Null(result["models"]![1]!["label"]);
        Assert.False(Antigravity.ParseModels("Opus latest")["ok"]!.GetValue<bool>());
    }

    private static JsonObject Usage(params JsonNode?[] buckets) => new()
    {
        ["status"] = "SUCCESS", ["command"] = new JsonObject
        {
            ["name"] = "usage", ["data"] = new JsonObject
            {
                ["groups"] = new JsonArray(new JsonObject
                {
                    ["name"] = "Gemini Models", ["buckets"] = new JsonArray(buckets)
                })
            }
        }
    };

    [Theory]
    [InlineData(0, true, 0)]
    [InlineData(0.0001, false, 0)]
    [InlineData(0.125, false, 13)]
    [InlineData(1, false, 100)]
    public void Only_exact_zero_is_exhausted(double fraction, bool exhausted, int left)
    {
        var result = Antigravity.ParseUsage(Usage(new JsonObject { ["remaining_fraction"] = fraction }));
        Assert.Equal(exhausted, result["exhausted"]!.GetValue<bool>());
        Assert.Equal(left, result["leftPct"]!.GetValue<int>());
        Assert.False(result["extraPlans"]![0]!["readable"]!.GetValue<bool>());
    }

    [Fact]
    public void Invalid_fractions_are_ignored_and_tightest_raw_fraction_wins()
    {
        var result = Antigravity.ParseUsage(Usage(
            new JsonObject { ["remaining_fraction"] = "0" },
            new JsonObject { ["remaining_fraction"] = -1 },
            new JsonObject { ["remaining_fraction"] = 1.01 },
            new JsonObject { ["remaining_fraction"] = 0.004, ["window"] = "tight" },
            new JsonObject { ["remaining_fraction"] = 0.0041, ["name"] = "same rounded quota" }));
        Assert.Equal(2, result["windows"]!.AsArray().Count);
        Assert.Equal(0, result["leftPct"]!.GetValue<int>());
        Assert.False(result["exhausted"]!.GetValue<bool>());
        Assert.Equal("tight", result["windows"]![0]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("subscription expired", "expired")]
    [InlineData("sign in to agy", "not signed in")]
    public void Provider_errors_keep_status_and_do_not_invent_extra_plans(string error, string status)
    {
        var result = Antigravity.ParseUsage(new JsonObject { ["status"] = "ERROR", ["result"] = new JsonObject { ["error"] = error } });
        Assert.Equal(status, result["planStatus"]!.GetValue<string>());
        Assert.False(result.ContainsKey("extraPlans"));
    }

    [Fact]
    public async Task Usage_invocation_preserves_arguments_bounds_and_BOM()
    {
        var result = await Antigravity.ReadAntigravityAsync("C:/fake path/agy.exe", (command, token) =>
        {
            Assert.Equal("C:/fake path/agy.exe", command.Executable);
            Assert.Equal(new[] { "-p", "/usage", "--output-format", "json", "--print-timeout", "15s" }, command.Arguments);
            Assert.Equal(20000, command.TimeoutMilliseconds);
            Assert.Equal(1024 * 1024, command.MaxBufferBytes);
            Assert.True(command.WindowsHide);
            return Task.FromResult(new Antigravity.Reply("\uFEFF" + Usage(new JsonObject { ["remaining_fraction"] = 0.5 }).ToJsonString()));
        });
        Assert.Equal(50, result["leftPct"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(true, "Antigravity quota timed out.")]
    [InlineData(false, "Antigravity /usage failed. Check CLI login.")]
    public async Task Failed_usage_never_grants_quota(bool killed, string reason)
    {
        var result = await Antigravity.ReadAntigravityAsync("fake", (_, _) => Task.FromResult(new Antigravity.Reply(Failed: true, Killed: killed)));
        Assert.Equal(reason, result["reason"]!.GetValue<string>());
        Assert.Null(result["leftPct"]);
    }

    [Fact]
    public async Task Missing_executable_does_not_invoke_host_and_bad_JSON_is_unread()
    {
        static Task<Antigravity.Reply> Forbidden(Antigravity.Command _, CancellationToken token) => throw new InvalidOperationException("Must not run");
        Assert.False((await Antigravity.ReadAntigravityAsync(null, Forbidden))["readable"]!.GetValue<bool>());
        Assert.False((await Antigravity.ListAntigravityAsync("", Forbidden))["ok"]!.GetValue<bool>());
        var bad = await Antigravity.ReadAntigravityAsync("fake", (_, _) => Task.FromResult(new Antigravity.Reply("broken")));
        Assert.Equal("Antigravity /usage JSON format not recognized.", bad["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Listing_distinguishes_login_failure_and_passes_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var result = await Antigravity.ListAntigravityAsync("fake", (command, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            Assert.Equal(new[] { "models" }, command.Arguments);
            Assert.Equal(20000, command.TimeoutMilliseconds);
            Assert.Equal(1024 * 1024, command.MaxBufferBytes);
            return Task.FromResult(new Antigravity.Reply(Stderr: "NOT AUTHENTICATED", Failed: true));
        }, cancellation.Token);
        Assert.Equal("Antigravity CLI cannot list models for this login. Sign in to agy, then refresh models.", result["reason"]!.GetValue<string>());
        result = await Antigravity.ListAntigravityAsync("fake", (_, _) => Task.FromResult(new Antigravity.Reply(Failed: true)));
        Assert.Equal("Antigravity CLI model listing failed. Check CLI access and permissions, then refresh models.", result["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Malformed_bucket_is_a_format_error_when_read_through_the_CLI()
    {
        var invalid = Usage((JsonNode?)null).ToJsonString();
        var result = await Antigravity.ReadAntigravityAsync("fake", (_, _) => Task.FromResult(new Antigravity.Reply(invalid)));
        Assert.Equal("Antigravity /usage JSON format not recognized.", result["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Concurrent_readers_keep_their_own_results()
    {
        var first = new TaskCompletionSource<Antigravity.Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Antigravity.ReadAntigravityAsync("one", (_, _) => first.Task);
        var second = await Antigravity.ReadAntigravityAsync("two", (_, _) => Task.FromResult(new Antigravity.Reply(" \uFEFF" + Usage(new JsonObject { ["remaining_fraction"] = 0.8 }).ToJsonString() + "\uFEFF ")));
        Assert.False(pending.IsCompleted);
        first.SetResult(new Antigravity.Reply(Usage(new JsonObject { ["remaining_fraction"] = 0.2 }).ToJsonString()));
        var prior = await pending;
        Assert.Equal(20, prior["leftPct"]!.GetValue<int>());
        Assert.Equal(80, second["leftPct"]!.GetValue<int>());
        prior["windows"]!.AsArray().Clear();
        Assert.Single(second["windows"]!.AsArray());
    }

    [Fact]
    public async Task Host_cancellation_is_not_disguised_as_a_login_failure()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Antigravity.ListAntigravityAsync("fake",
            (_, token) => Task.FromCanceled<Antigravity.Reply>(token), source.Token));
    }

    [Fact]
    public void Programmatic_integer_quota_is_numeric_but_strings_and_booleans_are_not()
    {
        var result = Antigravity.ParseUsage(Usage(new JsonObject { ["remaining_fraction"] = 0 },
            new JsonObject { ["remaining_fraction"] = 1 },
            new JsonObject { ["remaining_fraction"] = "0" }, new JsonObject { ["remaining_fraction"] = false }));
        Assert.Equal(2, result["windows"]!.AsArray().Count);
        Assert.True(result["exhausted"]!.GetValue<bool>());
        Assert.Equal(0, result["leftPct"]!.GetValue<int>());
    }
}
