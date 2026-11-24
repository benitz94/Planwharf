using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class PlanUsageTests
{
    static JsonNode? Json(string text) => JsonNode.Parse(text);
    static string Norm(string path) => path.Replace('\\', '/');

    static PlanUsage.Dependencies Dep(Dictionary<string, JsonNode?> files, PlanUsage.Send send,
        string home = "synthetic-home", string? actual = null, IReadOnlyDictionary<string, string>? env = null,
        Func<Task<JsonObject>>? gemini = null, long? now = null) =>
        new(home, actual ?? home, file =>
        {
            if (files.TryGetValue(file, out var direct)) return direct;
            var target = Norm(file);
            foreach (var (k, v) in files)
            {
                if (Norm(k) == target) return v;
            }
            return null;
        }, send, env, gemini, now);

    static Dictionary<string, JsonNode?> Auth(string folder, string body) => new()
    {
        [Path.Combine(folder.Replace('/', Path.DirectorySeparatorChar), "auth.json")] = Json(body)
    };

    [Fact]
    public void ChatGPT_uses_strictest_rounded_window_and_keeps_source_order()
    {
        var plan = PlanUsage.ParseChatGptUsage(Json("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":10.49,"reset_at":1800000000},"secondary_window":{"usagePercent":10.5,"end":"later"}}}"""));
        Assert.Equal(11, plan["pct"]!.GetValue<int>());
        Assert.Equal(89, plan["leftPct"]!.GetValue<int>());
        Assert.Equal("5-hour", plan["windows"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("weekly", plan["windows"]![1]!["name"]!.GetValue<string>());
        Assert.Equal("2027-01-15T08:00:00.000Z", plan["windows"]![0]!["resetAt"]!.GetValue<string>());
        Assert.Equal("later", plan["windows"]![1]!["resetAt"]!.GetValue<string>());
    }

    [Fact]
    public void Invalid_windows_remain_unread_but_explicit_refusal_blocks()
    {
        var unread = PlanUsage.ParseChatGptUsage(Json("""{"rate_limit":{"primary_window":{"used_percent":"100"}}}"""));
        Assert.False(unread["readable"]!.GetValue<bool>());
        Assert.False(PlanUsage.PlanExhausted(new JsonArray(unread), "ChatGPT"));
        var blocked = PlanUsage.UnreadPlan("ChatGPT", "ChatGPT login expired.");
        Assert.Equal("not signed in", blocked["planStatus"]!.GetValue<string>());
        Assert.True(PlanUsage.PlanExhausted(new JsonArray(blocked), "ChatGPT"));
    }

    [Fact]
    public void Grok_billing_products_follow_JavaScript_rounding_and_coercion()
    {
        var plan = PlanUsage.ParseGrokCredits(Json("""{"config":{"creditUsagePercent":11.5,"currentPeriod":{"start":"first","end":"last"},"productUsage":[{"product":7,"usagePercent":2.5},{"usagePercent":"50"}]}}"""));
        Assert.Equal(12, plan["pct"]!.GetValue<int>());
        Assert.Equal("7", plan["products"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(3, plan["products"]![0]!["pct"]!.GetValue<int>());
        Assert.Equal(50, plan["products"]![1]!["pct"]!.GetValue<int>());
        Assert.Equal("last", plan["windows"]![0]!["resetAt"]!.GetValue<string>());
    }

    [Fact]
    public void Empty_plans_have_three_ordered_unread_rows()
    {
        var plans = PlanUsage.EmptyPlans();
        Assert.Equal(new[] { "ChatGPT", "Grok", "Gemini" }, plans.Select(p => p!["id"]!.GetValue<string>()));
        Assert.All(plans, p => Assert.False(p!["readable"]!.GetValue<bool>()));
        Assert.Null(PlanUsage.WindowOf(Json("""{"used_percent":true}"""), "weekly"));
    }

    [Fact]
    public async Task Two_account_folders_are_isolated_and_never_return_secrets()
    {
        var files = Auth("alpha", """{"tokens":{"access_token":"synthetic-alpha","account_id":"acct-alpha"}}""");
        files[Path.Combine("beta", "auth.json")] = Json("""{"tokens":{"access_token":"synthetic-beta","account_id":"acct-beta"}}""");
        var calls = new List<PlanUsage.Request>();
        var dep = Dep(files, (request, _) =>
        {
            calls.Add(request);
            var used = request.Headers["Authorization"].EndsWith("alpha", StringComparison.Ordinal) ? 80 : 15;
            return Task.FromResult(new PlanUsage.Response(200, "{\"rate_limit\":{\"primary_window\":{\"used_percent\":" + used + "}}}"));
        });
        var a = await PlanUsage.ReadChatGptAsync(dep, "alpha");
        var b = await PlanUsage.ReadChatGptAsync(dep, "beta");
        Assert.Equal(20, a["leftPct"]!.GetValue<int>());
        Assert.Equal(85, b["leftPct"]!.GetValue<int>());
        Assert.Equal("acct-alpha", calls[0].Headers["ChatGPT-Account-ID"]);
        Assert.Equal("acct-beta", calls[1].Headers["ChatGPT-Account-ID"]);
        Assert.DoesNotContain("synthetic-", new JsonArray(a, b).ToJsonString());
        Assert.All(calls, c => Assert.Equal(8000, c.TimeoutMilliseconds));
    }

    [Fact]
    public async Task ChatGPT_401_refreshes_once_then_uses_new_token_in_memory()
    {
        var files = Auth(Path.Combine("synthetic-home", ".codex"), """{"tokens":{"access_token":"old","refresh_token":"synthetic-refresh","account_id":"acct"}}""");
        var requests = new List<PlanUsage.Request>();
        var dep = Dep(files, (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(request.Url == PlanUsage.ChatGptRefresh
                ? new PlanUsage.Response(200, """{"access_token":"new"}""")
                : request.Headers["Authorization"] == "Bearer old"
                    ? new PlanUsage.Response(401, "{}")
                    : new PlanUsage.Response(200, """{"rate_limit":{"primary_window":{"used_percent":9}}}"""));
        });
        var plan = await PlanUsage.ReadChatGptAsync(dep);
        Assert.Equal(91, plan["leftPct"]!.GetValue<int>());
        Assert.Equal(new[] { PlanUsage.ChatGptUsage, PlanUsage.ChatGptRefresh, PlanUsage.ChatGptUsage }, requests.Select(r => r.Url));
        Assert.Equal("Bearer new", requests[2].Headers["Authorization"]);
        Assert.Contains("refresh_token=synthetic-refresh", requests[1].Body);
        Assert.Equal("old", files[Path.Combine("synthetic-home", ".codex", "auth.json")]!["tokens"]!["access_token"]!.GetValue<string>());
    }

    [Fact]
    public async Task Grok_refreshes_near_expiry_and_falls_back_to_old_key_on_failure()
    {
        var files = Auth(Path.Combine("synthetic-home", ".grok"), """{"https://auth.x.ai":{"key":"old-key","refresh_token":"synthetic-refresh","oidc_client_id":"client","expires_at":"2026-11-24T12:03:00Z"}}""");
        var requests = new List<PlanUsage.Request>();
        var now = DateTimeOffset.Parse("2026-11-24T12:00:00Z").ToUnixTimeMilliseconds();
        var dep = Dep(files, (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(request.Url == PlanUsage.GrokRefresh
                ? new PlanUsage.Response(500, "{}")
                : new PlanUsage.Response(200, """{"config":{"creditUsagePercent":20}}"""));
        }, now: now);
        var plan = await PlanUsage.ReadGrokAsync(dep);
        Assert.Equal(80, plan["leftPct"]!.GetValue<int>());
        Assert.Equal(new[] { PlanUsage.GrokRefresh, PlanUsage.GrokBilling }, requests.Select(r => r.Url));
        Assert.Equal("Bearer old-key", requests[1].Headers["Authorization"]);
        Assert.Equal("1.0.50", requests[1].Headers["x-grok-client-version"]);
    }

    [Fact]
    public async Task Timeout_unreachable_and_provider_refusal_have_distinct_unread_states()
    {
        var files = Auth(Path.Combine("synthetic-home", ".codex"), """{"tokens":{"access_token":"synthetic-key"}}""");
        var timeout = Dep(files, async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new PlanUsage.Response(200, "{}"); });
        using var shortCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlanUsage.ReadChatGptAsync(timeout, cancellationToken: shortCancel.Token));
        var unreachable = Dep(files, (_, _) => throw new System.Net.Http.HttpRequestException("offline"));
        Assert.Equal("ChatGPT usage unreachable.", (await PlanUsage.ReadChatGptAsync(unreachable))["reason"]!.GetValue<string>());
        var refused = Dep(files, (_, _) => Task.FromResult(new PlanUsage.Response(403, """{"error":"subscription is inactive"}""")));
        var blocked = await PlanUsage.ReadChatGptAsync(refused);
        Assert.Equal("expired", blocked["planStatus"]!.GetValue<string>());
        Assert.True(PlanUsage.PlanExhausted(new JsonArray(blocked), "ChatGPT"));
    }

    [Fact]
    public async Task Aggregate_waits_for_all_and_preserves_order_with_extra_plans()
    {
        var files = new Dictionary<string, JsonNode?>();
        var gate = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dep = Dep(files, (_, _) => throw new Exception("No auth should make HTTP calls"), gemini: () => gate.Task);
        var pending = PlanUsage.FetchPlanUsageAsync(dep);
        Assert.False(pending.IsCompleted);
        gate.SetResult((JsonObject)Json("""{"id":"Gemini","extraPlans":[{"id":"Antigravity Claude/GPT"}]}""")!);
        var plans = await pending;
        Assert.Equal(new[] { "ChatGPT", "Grok", "Gemini", "Antigravity Claude/GPT" }, plans.Select(p => p!["id"]!.GetValue<string>()));
    }

    [Fact]
    public void ClipPct_edge_cases_follow_JavaScript_coercion_and_clamping()
    {
        Assert.Equal(0, PlanUsage.ClipPct(double.NaN));
        Assert.Equal(0, PlanUsage.ClipPct(double.PositiveInfinity));
        Assert.Equal(0, PlanUsage.ClipPct(double.NegativeInfinity));
        Assert.Equal(0, PlanUsage.ClipPct(-50));
        Assert.Equal(0, PlanUsage.ClipPct(-0.4));
        Assert.Equal(0, PlanUsage.ClipPct(0));
        Assert.Equal(2, PlanUsage.ClipPct(2.49));
        Assert.Equal(3, PlanUsage.ClipPct(2.5));
        Assert.Equal(10, PlanUsage.ClipPct(10.49));
        Assert.Equal(11, PlanUsage.ClipPct(10.5));
        Assert.Equal(100, PlanUsage.ClipPct(100));
        Assert.Equal(100, PlanUsage.ClipPct(150));
        Assert.Equal(100, PlanUsage.ClipPct(1e20));
        Assert.Equal(0, PlanUsage.ClipPct(-1e20));
    }

    [Fact]
    public void WindowOf_parses_formats_and_rejects_invalid_values()
    {
        Assert.Null(PlanUsage.WindowOf(null, "5-hour"));
        Assert.Null(PlanUsage.WindowOf(Json("\"string\""), "5-hour"));
        Assert.Null(PlanUsage.WindowOf(Json("123"), "5-hour"));
        Assert.Null(PlanUsage.WindowOf(Json("{}"), "5-hour"));
        Assert.Null(PlanUsage.WindowOf(Json("""{"used_percent": -1}"""), "5-hour"));
        Assert.Null(PlanUsage.WindowOf(Json("""{"used_percent": 101}"""), "5-hour"));
        Assert.Null(PlanUsage.WindowOf(Json("""{"used_percent": "50"}"""), "5-hour"));

        // usagePercent fallback
        var w1 = PlanUsage.WindowOf(Json("""{"usagePercent": 45.2, "limit_window_seconds": 18000}"""), "5-hour");
        Assert.NotNull(w1);
        Assert.Equal("5-hour", w1["name"]!.GetValue<string>());
        Assert.Equal(45, w1["pct"]!.GetValue<int>());
        Assert.Equal(55, w1["leftPct"]!.GetValue<int>());
        Assert.Equal(18000, w1["seconds"]!.GetValue<long>());
        Assert.Null(w1["resetAt"]);

        // reset_at timestamp
        var w2 = PlanUsage.WindowOf(Json("""{"used_percent": 0, "reset_at": 0}"""), "weekly");
        Assert.NotNull(w2);
        Assert.Equal("1970-01-01T00:00:00.000Z", w2["resetAt"]!.GetValue<string>());

        // end string
        var w3 = PlanUsage.WindowOf(Json("""{"used_percent": 100, "end": "2026-12-31"}"""), "weekly");
        Assert.NotNull(w3);
        Assert.Equal("2026-12-31", w3["resetAt"]!.GetValue<string>());

        // resetAt string
        var w4 = PlanUsage.WindowOf(Json("""{"used_percent": 50, "resetAt": "soon"}"""), "weekly");
        Assert.NotNull(w4);
        Assert.Equal("soon", w4["resetAt"]!.GetValue<string>());
    }

    [Fact]
    public void ParseGrokCredits_handles_empty_invalid_and_various_product_types()
    {
        // Malformed / invalid
        var u1 = PlanUsage.ParseGrokCredits(null);
        Assert.False(u1["readable"]!.GetValue<bool>());
        Assert.Equal("No numeric usage in Grok billing response.", u1["reason"]!.GetValue<string>());

        var u2 = PlanUsage.ParseGrokCredits(Json("""{"config": {"creditUsagePercent": "not a number"}}"""));
        Assert.False(u2["readable"]!.GetValue<bool>());

        var u3 = PlanUsage.ParseGrokCredits(Json("""{"creditUsagePercent": -1}"""));
        Assert.False(u3["readable"]!.GetValue<bool>());

        var u4 = PlanUsage.ParseGrokCredits(Json("""{"creditUsagePercent": 105}"""));
        Assert.False(u4["readable"]!.GetValue<bool>());

        // Valid with various product items and billingPeriod fallback
        var body = Json("""
        {
            "config": {
                "creditUsagePercent": 100,
                "billingPeriodStart": "start-val",
                "billingPeriodEnd": "end-val",
                "productUsage": [
                    {"product": "code", "usagePercent": 42.6},
                    {"product": "", "usagePercent": null},
                    {"product": true, "usagePercent": "invalid"},
                    null,
                    {"usagePercent": 10}
                ]
            }
        }
        """);
        var plan = PlanUsage.ParseGrokCredits(body);
        Assert.True(plan["readable"]!.GetValue<bool>());
        Assert.True(plan["exhausted"]!.GetValue<bool>());
        Assert.Equal(100, plan["pct"]!.GetValue<int>());
        Assert.Equal(0, plan["leftPct"]!.GetValue<int>());
        Assert.Equal("end-val", plan["windows"]![0]!["resetAt"]!.GetValue<string>());
        Assert.Equal("start-val", plan["windows"]![0]!["start"]!.GetValue<string>());

        var products = (JsonArray)plan["products"]!;
        Assert.Equal(5, products.Count);
        Assert.Equal("code", products[0]!["name"]!.GetValue<string>());
        Assert.Equal(43, products[0]!["pct"]!.GetValue<int>());
        Assert.Equal("product", products[1]!["name"]!.GetValue<string>());
        Assert.Equal(0, products[1]!["pct"]!.GetValue<int>());
        Assert.Equal("true", products[2]!["name"]!.GetValue<string>());
        Assert.Equal(0, products[2]!["pct"]!.GetValue<int>());
        Assert.Equal("product", products[3]!["name"]!.GetValue<string>());
        Assert.Equal(0, products[3]!["pct"]!.GetValue<int>());
        Assert.Equal("product", products[4]!["name"]!.GetValue<string>());
        Assert.Equal(10, products[4]!["pct"]!.GetValue<int>());
    }

    [Fact]
    public void ParseChatGptUsage_handles_equal_windows_and_refusal_flags()
    {
        // When 5-hour and weekly are equal, 5-hour (first) is kept as tightest
        var equalBody = Json("""
        {
            "plan_type": "pro",
            "rate_limit": {
                "primary_window": {"used_percent": 50, "limit_window_seconds": 18000},
                "secondary_window": {"used_percent": 50, "limit_window_seconds": 604800}
            }
        }
        """);
        var equalPlan = PlanUsage.ParseChatGptUsage(equalBody);
        Assert.Equal(50, equalPlan["pct"]!.GetValue<int>());
        Assert.Equal("pro", equalPlan["planType"]!.GetValue<string>());
        Assert.False(equalPlan["exhausted"]!.GetValue<bool>());

        // limit_reached = true marks exhausted
        var reachedBody = Json("""{"rate_limit": {"primary_window": {"used_percent": 20}, "limit_reached": true}}""");
        Assert.True(PlanUsage.ParseChatGptUsage(reachedBody)["exhausted"]!.GetValue<bool>());

        // allowed = false marks exhausted
        var notAllowedBody = Json("""{"rate_limit": {"primary_window": {"used_percent": 20}, "allowed": false}}""");
        Assert.True(PlanUsage.ParseChatGptUsage(notAllowedBody)["exhausted"]!.GetValue<bool>());

        // window >= 100 marks exhausted
        var saturatedBody = Json("""{"rate_limit": {"primary_window": {"used_percent": 100}}}""");
        Assert.True(PlanUsage.ParseChatGptUsage(saturatedBody)["exhausted"]!.GetValue<bool>());

        // Only secondary window present
        var onlySecondary = Json("""{"rate_limit": {"secondary_window": {"used_percent": 75}}}""");
        var secPlan = PlanUsage.ParseChatGptUsage(onlySecondary);
        Assert.Equal(75, secPlan["pct"]!.GetValue<int>());
        Assert.Equal(25, secPlan["leftPct"]!.GetValue<int>());
        Assert.Single((JsonArray)secPlan["windows"]!);
    }

    [Fact]
    public void PlanExhausted_covers_all_status_and_readability_combinations()
    {
        Assert.False(PlanUsage.PlanExhausted(null, "ChatGPT"));
        Assert.False(PlanUsage.PlanExhausted(new JsonArray(), "ChatGPT"));

        // Readable but not exhausted
        var normal = new JsonObject { ["id"] = "ChatGPT", ["readable"] = true, ["exhausted"] = false, ["leftPct"] = 50, ["pct"] = 50 };
        Assert.False(PlanUsage.PlanExhausted(new JsonArray(normal), "ChatGPT"));

        // Readable and exhausted flag
        var exh1 = new JsonObject { ["id"] = "ChatGPT", ["readable"] = true, ["exhausted"] = true, ["leftPct"] = 50, ["pct"] = 50 };
        Assert.True(PlanUsage.PlanExhausted(new JsonArray(exh1), "ChatGPT"));

        // Readable and leftPct == 0
        var exh2 = new JsonObject { ["id"] = "ChatGPT", ["readable"] = true, ["exhausted"] = false, ["leftPct"] = 0, ["pct"] = 100 };
        Assert.True(PlanUsage.PlanExhausted(new JsonArray(exh2), "ChatGPT"));

        // Readable and pct == 100
        var exh3 = new JsonObject { ["id"] = "ChatGPT", ["readable"] = true, ["exhausted"] = false, ["leftPct"] = 1, ["pct"] = 100 };
        Assert.True(PlanUsage.PlanExhausted(new JsonArray(exh3), "ChatGPT"));

        // Unread and expired planStatus
        var blockedExpired = new JsonObject { ["id"] = "ChatGPT", ["readable"] = false, ["planStatus"] = "expired" };
        Assert.True(PlanUsage.PlanExhausted(new JsonArray(blockedExpired), "ChatGPT"));

        // Unread and not signed in planStatus
        var blockedSignedOut = new JsonObject { ["id"] = "ChatGPT", ["readable"] = false, ["planStatus"] = "not signed in" };
        Assert.True(PlanUsage.PlanExhausted(new JsonArray(blockedSignedOut), "ChatGPT"));

        // Unread but unknown planStatus
        var unreadUnknown = new JsonObject { ["id"] = "ChatGPT", ["readable"] = false, ["planStatus"] = "unknown" };
        Assert.False(PlanUsage.PlanExhausted(new JsonArray(unreadUnknown), "ChatGPT"));
    }

    [Fact]
    public void UnreadPlan_and_constants_match_expected_structure()
    {
        Assert.Equal(new[] { "ChatGPT", "Grok", "Gemini" }, PlanUsage.PLAN_IDS);
        Assert.Equal("https://cli-chat-proxy.grok.com/v1/billing?format=credits", PlanUsage.GrokBilling);
        Assert.Equal("https://chatgpt.com/backend-api/wham/usage", PlanUsage.ChatGptUsage);
        Assert.Equal("https://auth.openai.com/oauth/token", PlanUsage.ChatGptRefresh);
        Assert.Equal("https://auth.x.ai/oauth/token", PlanUsage.GrokRefresh);
        Assert.Equal("app_EMoamEEZ73f0CfXlXpSwebNA", PlanUsage.CodexClientId);

        var c = PlanUsage.UnreadPlan("ChatGPT", null);
        Assert.Equal("Not readable", c["reason"]!.GetValue<string>());
        Assert.Equal("ChatGPT", c["id"]!.GetValue<string>());
        Assert.False(c["readable"]!.GetValue<bool>());

        var g = PlanUsage.UnreadPlan("Grok", "custom reason");
        Assert.Equal("custom reason", g["reason"]!.GetValue<string>());
        Assert.Equal("Grok", g["id"]!.GetValue<string>());

        var a = PlanUsage.UnreadPlan("Gemini", "antigravity missing");
        Assert.Equal("antigravity missing", a["reason"]!.GetValue<string>());
        Assert.Equal("Gemini", a["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadGrok_covers_missing_auth_expired_refresh_failure_and_env_precedence()
    {
        // Missing auth
        var emptyDep = Dep(new Dictionary<string, JsonNode?>(), (_, _) => throw new Exception());
        var missingPlan = await PlanUsage.ReadGrokAsync(emptyDep);
        Assert.False(missingPlan["readable"]!.GetValue<bool>());
        Assert.Equal("Sign in with grok CLI to read weekly usage.", missingPlan["reason"]!.GetValue<string>());

        // Key not near expiry -> uses key directly without calling refresh
        var files = Auth(Path.Combine("synthetic-home", ".grok"), """{"key": "valid-key", "expires_at": "2026-11-24T13:00:00Z"}""");
        var requests = new List<PlanUsage.Request>();
        var dep = Dep(files, (req, _) =>
        {
            requests.Add(req);
            return Task.FromResult(new PlanUsage.Response(200, """{"config":{"creditUsagePercent": 15}}"""));
        }, now: DateTimeOffset.Parse("2026-11-24T12:00:00Z").ToUnixTimeMilliseconds());

        var validPlan = await PlanUsage.ReadGrok(dep);
        Assert.True(validPlan["readable"]!.GetValue<bool>());
        Assert.Equal(15, validPlan["pct"]!.GetValue<int>());
        Assert.Single(requests);
        Assert.Equal(PlanUsage.GrokBilling, requests[0].Url);
        Assert.Equal("Bearer valid-key", requests[0].Headers["Authorization"]);

        // Only refresh token, refresh fails -> Grok login expired.
        var refreshOnlyFiles = Auth(Path.Combine("synthetic-home", ".grok"), """{"refresh_token": "rf-token", "oidc_client_id": "cid"}""");
        var failDep = Dep(refreshOnlyFiles, (req, _) => Task.FromResult(new PlanUsage.Response(400, "{}")));
        var expiredPlan = await PlanUsage.ReadGrok(failDep);
        Assert.False(expiredPlan["readable"]!.GetValue<bool>());
        Assert.Equal("Grok login expired.", expiredPlan["reason"]!.GetValue<string>());

        // HTTP 401 on GrokBilling -> Grok login expired.
        var p401Dep = Dep(files, (req, _) => Task.FromResult(new PlanUsage.Response(401, "{}")), now: DateTimeOffset.Parse("2026-11-24T12:00:00Z").ToUnixTimeMilliseconds());
        var p401 = await PlanUsage.ReadGrok(p401Dep);
        Assert.Equal("Grok login expired.", p401["reason"]!.GetValue<string>());

        // HTTP 500 on GrokBilling -> Grok usage HTTP 500.
        var p500Dep = Dep(files, (req, _) => Task.FromResult(new PlanUsage.Response(500, "{}")), now: DateTimeOffset.Parse("2026-11-24T12:00:00Z").ToUnixTimeMilliseconds());
        var p500 = await PlanUsage.ReadGrok(p500Dep);
        Assert.Equal("Grok usage HTTP 500.", p500["reason"]!.GetValue<string>());

        // Timeout on GrokBilling -> Grok quota check timed out...
        var timeoutDep = Dep(files, (_, _) => throw new TimeoutException(), now: DateTimeOffset.Parse("2026-11-24T12:00:00Z").ToUnixTimeMilliseconds());
        var timeoutPlan = await PlanUsage.ReadGrok(timeoutDep);
        Assert.Equal("Grok quota check timed out. CLI model access is checked separately.", timeoutPlan["reason"]!.GetValue<string>());

        // Network error on GrokBilling -> Grok quota service could not be reached...
        var netDep = Dep(files, (_, _) => throw new System.Net.Http.HttpRequestException("offline"), now: DateTimeOffset.Parse("2026-11-24T12:00:00Z").ToUnixTimeMilliseconds());
        var netPlan = await PlanUsage.ReadGrok(netDep);
        Assert.Equal("Grok quota service could not be reached. This does not mean the Grok CLI is unavailable.", netPlan["reason"]!.GetValue<string>());

        // GROK_HOME environment variable precedence when Home == ActualHome
        var customFolder = Path.Combine("custom", "grok-env");
        var envFiles = Auth(customFolder, """{"key": "env-key", "expires_at": "2026-11-24T13:00:00Z"}""");
        var envDep = Dep(envFiles, (req, _) => Task.FromResult(new PlanUsage.Response(200, """{"config":{"creditUsagePercent": 5}}""")),
            home: "home-dir", actual: "home-dir", env: new Dictionary<string, string> { ["GROK_HOME"] = customFolder },
            now: DateTimeOffset.Parse("2026-11-24T12:00:00Z").ToUnixTimeMilliseconds());
        var envPlan = await PlanUsage.ReadGrok(envDep);
        Assert.True(envPlan["readable"]!.GetValue<bool>());
        Assert.Equal(5, envPlan["pct"]!.GetValue<int>());
    }

    [Fact]
    public async Task ReadChatGpt_covers_missing_auth_refresh_only_retry_failure_and_env_precedence()
    {
        // Missing auth
        var emptyDep = Dep(new Dictionary<string, JsonNode?>(), (_, _) => throw new Exception());
        var missingPlan = await PlanUsage.ReadChatGptAsync(emptyDep);
        Assert.False(missingPlan["readable"]!.GetValue<bool>());
        Assert.Equal("Sign in with Codex / ChatGPT to read 5-hour and weekly usage.", missingPlan["reason"]!.GetValue<string>());

        // Only refresh token present -> refreshes token and calls usage
        var refreshOnlyFiles = Auth(Path.Combine("synthetic-home", ".codex"), """{"tokens":{"refresh_token":"rf-123"}}""");
        var requests = new List<PlanUsage.Request>();
        var dep = Dep(refreshOnlyFiles, (req, _) =>
        {
            requests.Add(req);
            return Task.FromResult(req.Url == PlanUsage.ChatGptRefresh
                ? new PlanUsage.Response(200, """{"access_token":"new-acc"}""")
                : new PlanUsage.Response(200, """{"rate_limit":{"primary_window":{"used_percent":30}}}"""));
        });
        var plan = await PlanUsage.ReadChatGpt(dep);
        Assert.True(plan["readable"]!.GetValue<bool>());
        Assert.Equal(30, plan["pct"]!.GetValue<int>());
        Assert.Equal(2, requests.Count);
        Assert.Equal(PlanUsage.ChatGptRefresh, requests[0].Url);
        Assert.Equal(PlanUsage.ChatGptUsage, requests[1].Url);
        Assert.Equal("Bearer new-acc", requests[1].Headers["Authorization"]);

        // 401 retry: first call 401, refresh fails -> returns 401 unread plan
        var authFiles = Auth(Path.Combine("synthetic-home", ".codex"), """{"tokens":{"access_token":"bad-acc","refresh_token":"bad-rf"}}""");
        var failRefreshDep = Dep(authFiles, (req, _) =>
        {
            return Task.FromResult(req.Url == PlanUsage.ChatGptUsage
                ? new PlanUsage.Response(401, "{}")
                : new PlanUsage.Response(500, "{}"));
        });
        var failPlan = await PlanUsage.ReadChatGpt(failRefreshDep);
        Assert.False(failPlan["readable"]!.GetValue<bool>());
        Assert.Equal("ChatGPT login expired.", failPlan["reason"]!.GetValue<string>());

        // 401 retry: first call 401, refresh succeeds, second call 401 -> exactly 2 usage calls and 1 refresh call
        var reqList = new List<PlanUsage.Request>();
        var failSecondDep = Dep(authFiles, (req, _) =>
        {
            reqList.Add(req);
            return Task.FromResult(req.Url == PlanUsage.ChatGptRefresh
                ? new PlanUsage.Response(200, """{"access_token":"fresh-acc"}""")
                : new PlanUsage.Response(401, "{}"));
        });
        var failSecondPlan = await PlanUsage.ReadChatGpt(failSecondDep);
        Assert.False(failSecondPlan["readable"]!.GetValue<bool>());
        Assert.Equal("ChatGPT login expired.", failSecondPlan["reason"]!.GetValue<string>());
        Assert.Equal(3, reqList.Count);
        Assert.Equal(new[] { PlanUsage.ChatGptUsage, PlanUsage.ChatGptRefresh, PlanUsage.ChatGptUsage }, reqList.Select(r => r.Url));

        // Timeout on ChatGptUsage -> ChatGPT usage timed out.
        var validFiles = Auth(Path.Combine("synthetic-home", ".codex"), """{"tokens":{"access_token":"valid"}}""");
        var timeoutDep = Dep(validFiles, (_, _) => throw new TimeoutException());
        var timeoutPlan = await PlanUsage.ReadChatGpt(timeoutDep);
        Assert.Equal("ChatGPT usage timed out.", timeoutPlan["reason"]!.GetValue<string>());

        // CODEX_HOME environment variable precedence when Home == ActualHome
        var customCodexFolder = Path.Combine("custom", "codex-env");
        var envCodexFiles = Auth(customCodexFolder, """{"tokens":{"access_token":"env-tok"}}""");
        var envDep = Dep(envCodexFiles, (req, _) => Task.FromResult(new PlanUsage.Response(200, """{"rate_limit":{"primary_window":{"used_percent":8}}}""")),
            home: "home-dir", actual: "home-dir", env: new Dictionary<string, string> { ["CODEX_HOME"] = customCodexFolder });
        var envPlan = await PlanUsage.ReadChatGpt(envDep);
        Assert.True(envPlan["readable"]!.GetValue<bool>());
        Assert.Equal(8, envPlan["pct"]!.GetValue<int>());
    }

    [Fact]
    public async Task FetchPlanUsage_defaults_gemini_when_ReadAntigravity_is_null()
    {
        var dep = Dep(new Dictionary<string, JsonNode?>(), (_, _) => Task.FromResult(new PlanUsage.Response(200, "{}")));
        var plans = await PlanUsage.FetchPlanUsage(dep);
        Assert.Equal(3, plans.Count);
        Assert.Equal("ChatGPT", plans[0]!["id"]!.GetValue<string>());
        Assert.Equal("Grok", plans[1]!["id"]!.GetValue<string>());
        Assert.Equal("Gemini", plans[2]!["id"]!.GetValue<string>());
        Assert.Equal("Antigravity agy executable not found.", plans[2]!["reason"]!.GetValue<string>());

        // Cancellation propagates
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlanUsage.FetchPlanUsage(dep, cts.Token));
    }
}
