// Port of engine/planUsage.js.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public static class PlanUsage
{
    public static readonly IReadOnlyList<string> PlanIds = ["ChatGPT", "Grok", "Gemini"];
    public static IReadOnlyList<string> PLAN_IDS => PlanIds;
    public const string GrokBilling = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    public const string ChatGptUsage = "https://chatgpt.com/backend-api/wham/usage";
    public const string ChatGptRefresh = "https://auth.openai.com/oauth/token";
    public const string GrokRefresh = "https://auth.x.ai/oauth/token";
    public const string CodexClientId = "app_EMoamEEZ73f0CfXlXpSwebNA";
    public sealed record Request(string Url, string Method, IReadOnlyDictionary<string, string> Headers, string? Body, int TimeoutMilliseconds = 8000);
    public sealed record Response(int Status, string? Text);
    public delegate Task<Response> Send(Request request, CancellationToken cancellationToken);
    public sealed record Dependencies(string Home, string ActualHome, Func<string, JsonNode?> ReadJson,
        Send Http, IReadOnlyDictionary<string, string>? Environment = null,
        Func<Task<JsonObject>>? ReadAntigravity = null, long? NowMilliseconds = null);

    private static JsonNode? Field(JsonNode? node, string name) => node is JsonObject obj ? obj[name] : null;
    private static bool String(JsonNode? node, out string value)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var text)) { value = text; return true; }
        value = "";
        return false;
    }
    private static bool Number(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number &&
            double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
    private static bool Bool(JsonNode? node, out bool value)
    {
        if (node is JsonValue v && v.TryGetValue<bool>(out value)) return true;
        value = false;
        return false;
    }
    private static bool Truthy(JsonNode? node) => node is not null && node.ToJsonString() is not "false" and not "0" and not "\"\"";
    private static string JsString(JsonNode? node) => node switch
    {
        JsonObject => "[object Object]",
        JsonArray arr => string.Join(",", arr.Select(JsString)),
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        _ => node?.ToJsonString() ?? "null"
    };
    public static int ClipPct(double value)
    {
        if (!double.IsFinite(value)) return 0;
        var rounded = Math.Floor(value + 0.5);
        if (rounded <= 0) return 0;
        if (rounded >= 100) return 100;
        return (int)rounded;
    }
    private static JsonValue? NumericJsonValue(double value)
    {
        if (value == Math.Floor(value) && value >= long.MinValue && value <= long.MaxValue)
            return JsonValue.Create((long)value);
        return JsonValue.Create(value);
    }
    private static double JsNumber(JsonNode? node)
    {
        if (node is null) return 0;
        if (Number(node, out var number)) return number;
        if (node.ToJsonString() == "true") return 1;
        if (node.ToJsonString() == "false") return 0;
        if (String(node, out var text))
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : double.NaN;
        }
        return double.NaN;
    }

    public static JsonObject UnreadPlan(string id, string? reason) => PlanStatus.WithStatus(
        id == "ChatGPT" ? "codex" : id == "Grok" ? "grok" : "antigravity", new JsonObject
        {
            ["id"] = id, ["label"] = id, ["readable"] = false, ["exhausted"] = false,
            ["pct"] = null, ["leftPct"] = null, ["source"] = null,
            ["reason"] = string.IsNullOrEmpty(reason) ? "Not readable" : reason, ["windows"] = new JsonArray()
        });

    public static JsonObject? WindowOf(JsonNode? raw, string name)
    {
        if (raw is not JsonObject && raw is not JsonArray) return null;
        var used = Field(raw, "used_percent") ?? Field(raw, "usagePercent");
        if (!Number(used, out var numeric) || numeric < 0 || numeric > 100) return null;
        var pct = ClipPct(numeric);
        string? reset = null;
        if (Number(Field(raw, "reset_at"), out var seconds))
        {
            try
            {
                var ms = (long)Math.Round(seconds * 1000.0);
                reset = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            }
            catch { reset = null; }
        }
        else if (String(Field(raw, "end"), out var end)) reset = end;
        else if (String(Field(raw, "resetAt"), out var resetAt)) reset = resetAt;

        JsonNode? secondsNode = null;
        if (Number(Field(raw, "limit_window_seconds"), out var limit))
            secondsNode = NumericJsonValue(limit);

        return new JsonObject
        {
            ["name"] = name, ["pct"] = pct, ["leftPct"] = 100 - pct,
            ["resetAt"] = reset, ["seconds"] = secondsNode
        };
    }

    public static JsonObject ParseGrokCredits(JsonNode? body)
    {
        var config = Field(body, "config") is JsonObject or JsonArray ? Field(body, "config") : body;
        if (!Number(Field(config, "creditUsagePercent"), out var used) || used < 0 || used > 100)
            return UnreadPlan("Grok", "No numeric usage in Grok billing response.");
        var pct = ClipPct(used);
        var period = Field(config, "currentPeriod") is JsonObject or JsonArray ? Field(config, "currentPeriod") : null;
        var end = String(Field(period, "end"), out var periodEnd) ? periodEnd : String(Field(config, "billingPeriodEnd"), out var configEnd) ? configEnd : null;
        var start = String(Field(period, "start"), out var periodStart) ? periodStart : String(Field(config, "billingPeriodStart"), out var configStart) ? configStart : null;
        var products = new JsonArray();
        if (Field(config, "productUsage") is JsonArray rows)
            foreach (var row in rows)
            {
                var product = Field(row, "product");
                var usage = Field(row, "usagePercent");
                products.Add((JsonNode)new JsonObject
                {
                    ["name"] = Truthy(product) ? JsString(product) : "product",
                    ["pct"] = ClipPct(JsNumber(usage))
                });
            }
        return new JsonObject
        {
            ["id"] = "Grok", ["label"] = "Grok", ["readable"] = true, ["exhausted"] = pct >= 100,
            ["pct"] = pct, ["leftPct"] = 100 - pct, ["source"] = "grok", ["planStatus"] = "active",
            ["reason"] = null, ["windows"] = new JsonArray(new JsonObject
            { ["name"] = "weekly", ["pct"] = pct, ["leftPct"] = 100 - pct, ["resetAt"] = end, ["start"] = start }),
            ["products"] = products
        };
    }

    public static JsonObject ParseChatGptUsage(JsonNode? body)
    {
        var rl = Field(body, "rate_limit");
        var windows = new JsonArray();
        var five = WindowOf(Field(rl, "primary_window"), "5-hour");
        var week = WindowOf(Field(rl, "secondary_window"), "weekly");
        if (five is not null) windows.Add((JsonNode)five);
        if (week is not null) windows.Add((JsonNode)week);
        if (windows.Count == 0) return UnreadPlan("ChatGPT", "No numeric usage windows in ChatGPT response.");
        var tight = week is not null && (five is null || week["pct"]!.GetValue<int>() > five["pct"]!.GetValue<int>()) ? week : five!;
        var pct = tight["pct"]!.GetValue<int>();
        var exhausted = (Bool(Field(rl, "limit_reached"), out var reached) && reached) ||
            (Bool(Field(rl, "allowed"), out var allowed) && !allowed) ||
            windows.OfType<JsonObject>().Any(w => w["pct"]!.GetValue<int>() >= 100);
        var planType = Field(body, "plan_type");
        return new JsonObject
        {
            ["id"] = "ChatGPT", ["label"] = "ChatGPT", ["readable"] = true, ["exhausted"] = exhausted,
            ["pct"] = pct, ["leftPct"] = 100 - pct, ["source"] = "chatgpt", ["planStatus"] = "active",
            ["reason"] = null, ["planType"] = String(planType, out var pt) ? pt : Truthy(planType) ? JsString(planType) : null, ["windows"] = windows
        };
    }

    public static bool PlanExhausted(JsonNode? plans, string name)
    {
        var row = (plans as JsonArray)?.OfType<JsonObject>().FirstOrDefault(p => String(p["id"], out var id) && id == name);
        if (row is null) return false;
        if (PlanStatus.BlockedStatus(row)) return true;
        if (Bool(row["readable"], out var readable) && readable)
        {
            if (Bool(row["exhausted"], out var exhausted) && exhausted) return true;
            if (Number(row["leftPct"], out var left) && left == 0) return true;
            if (Number(row["pct"], out var pct) && pct == 100) return true;
        }
        return false;
    }

    public static JsonArray EmptyPlans() => new(PlanIds.Select(id => (JsonNode?)UnreadPlan(id, "Usage not loaded yet.")).ToArray());

    private static string AuthFolder(Dependencies dep, string? explicitHome, string envName, string folder)
    {
        if (!string.IsNullOrEmpty(explicitHome)) return explicitHome;
        if (dep.Home == dep.ActualHome && dep.Environment?.TryGetValue(envName, out var selected) == true && !string.IsNullOrEmpty(selected)) return selected;
        return Path.Combine(dep.Home, folder);
    }
    private static JsonNode? Read(Dependencies dep, string folder, string filename)
    {
        try { return dep.ReadJson(Path.Combine(folder, filename)); }
        catch { return null; }
    }
    private static JsonNode? GrokEntry(JsonNode? raw)
    {
        if (Truthy(Field(raw, "key")) || Truthy(Field(raw, "refresh_token"))) return raw;
        var preferred = Field(raw, "https://auth.x.ai") ?? Field(raw, "https://accounts.x.ai/sign-in");
        if (Truthy(preferred)) return preferred;
        IEnumerable<JsonNode?> values = raw switch
        {
            JsonObject obj => obj.Select(pair => pair.Value),
            JsonArray arr => arr,
            _ => []
        };
        var entries = values.Where(value => value is JsonObject or JsonArray &&
            (Truthy(Field(value, "key")) || Truthy(Field(value, "refresh_token")))).Take(2).ToArray();
        return entries.Length == 1 ? entries[0] : null;
    }
    private static string Text(JsonNode? node) => String(node, out var text) ? text : "";
    private static JsonNode? Parse(string? text)
    {
        try { return JsonNode.Parse(text ?? ""); }
        catch (JsonException) { return null; }
    }
    private static async Task<(int Status, JsonNode? Json)> Call(Dependencies dep, Request request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.TimeoutMilliseconds);
        try
        {
            var response = await dep.Http(request, deadline.Token).WaitAsync(deadline.Token);
            return (response.Status, Parse(response.Text));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }
    private static string Form(params (string Key, string Value)[] pairs) =>
        string.Join("&", pairs.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    private static async Task<string> Refresh(Dependencies dep, string url, string refresh, string clientId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await Call(dep, new Request(url, "POST", new Dictionary<string, string>
            { ["Content-Type"] = "application/x-www-form-urlencoded" }, Form(("grant_type", "refresh_token"),
                ("refresh_token", refresh), ("client_id", clientId))), cancellationToken);
            if (response.Status != 200) return "";
            var token = url == GrokRefresh ? Field(response.Json, "access_token") ?? Field(response.Json, "key") : Field(response.Json, "access_token");
            return Text(token);
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return ""; }
    }
    private static JsonObject WithProviderError(string provider, string label, JsonNode? body, string fallback)
    {
        var status = PlanStatus.ProviderError(provider, body?.ToJsonString() ?? "{}", label);
        if (status is null) return UnreadPlan(label, fallback);
        var plan = UnreadPlan(label, Text(status["reason"]));
        plan["planStatus"] = status["planStatus"]?.DeepClone();
        return plan;
    }

    public static async Task<JsonObject> ReadGrokAsync(Dependencies dep, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = AuthFolder(dep, null, "GROK_HOME", ".grok");
        var entry = GrokEntry(Read(dep, folder, "auth.json"));
        var key = Text(Field(entry, "key"));
        var refresh = Text(Field(entry, "refresh_token"));
        var clientId = Text(Field(entry, "oidc_client_id"));
        if (key.Length == 0 && refresh.Length == 0) return UnreadPlan("Grok", "Sign in with grok CLI to read weekly usage.");
        var expires = Text(Field(entry, "expires_at"));
        var now = dep.NowMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long exp = 0;
        if (DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiry))
            exp = expiry.ToUnixTimeMilliseconds();
        var soon = exp - now < 300000;
        var version = Text(Field(Read(dep, folder, "version.json"), "version"));
        if (version.Length == 0) version = "1.0.50";
        try
        {
            var token = key;
            if ((key.Length == 0 || soon) && refresh.Length > 0 && clientId.Length > 0)
            {
                var refreshed = await Refresh(dep, GrokRefresh, refresh, clientId, cancellationToken);
                if (refreshed.Length > 0) token = refreshed;
            }
            if (token.Length == 0) return UnreadPlan("Grok", "Grok login expired.");
            var response = await Call(dep, new Request(GrokBilling, "GET", new Dictionary<string, string>
            { ["Authorization"] = "Bearer " + token, ["x-grok-client-surface"] = "grok-build", ["x-grok-client-version"] = version }, null), cancellationToken);
            if (response.Status == 200 && response.Json is not null) return ParseGrokCredits(response.Json);
            return WithProviderError("grok", "Grok", response.Json, response.Status == 401 ? "Grok login expired." : $"Grok usage HTTP {response.Status}.");
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return UnreadPlan("Grok", error is TimeoutException ?
                "Grok quota check timed out. CLI model access is checked separately." :
                "Grok quota service could not be reached. This does not mean the Grok CLI is unavailable.");
        }
    }

    public static async Task<JsonObject> ReadChatGptAsync(Dependencies dep, string? authHome = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folder = AuthFolder(dep, authHome, "CODEX_HOME", ".codex");
        var tokens = Field(Read(dep, folder, "auth.json"), "tokens");
        var access = Text(Field(tokens, "access_token"));
        var refresh = Text(Field(tokens, "refresh_token"));
        var accountId = Text(Field(tokens, "account_id"));
        if (access.Length == 0 && refresh.Length == 0)
            return UnreadPlan("ChatGPT", "Sign in with Codex / ChatGPT to read 5-hour and weekly usage.");
        try
        {
            var token = access.Length > 0 ? access : await Refresh(dep, ChatGptRefresh, refresh, CodexClientId, cancellationToken);
            if (token.Length == 0) return UnreadPlan("ChatGPT", "ChatGPT login expired.");
            async Task<(int Status, JsonNode? Json)> Usage(string bearer)
            {
                var headers = new Dictionary<string, string>
                { ["Authorization"] = "Bearer " + bearer, ["User-Agent"] = "Planwharf" };
                if (accountId.Length > 0) headers["ChatGPT-Account-ID"] = accountId;
                return await Call(dep, new Request(ChatGptUsage, "GET", headers, null), cancellationToken);
            }
            var response = await Usage(token);
            if (response.Status == 401 && refresh.Length > 0)
            {
                var refreshed = await Refresh(dep, ChatGptRefresh, refresh, CodexClientId, cancellationToken);
                if (refreshed.Length > 0) response = await Usage(refreshed);
            }
            if (response.Status == 200 && response.Json is not null) return ParseChatGptUsage(response.Json);
            return WithProviderError("codex", "ChatGPT", response.Json,
                response.Status == 401 ? "ChatGPT login expired." : $"ChatGPT usage HTTP {response.Status}.");
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return UnreadPlan("ChatGPT", error is TimeoutException ? "ChatGPT usage timed out." : "ChatGPT usage unreachable.");
        }
    }

    public static async Task<JsonArray> FetchPlanUsageAsync(Dependencies dep, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chat = ReadChatGptAsync(dep, cancellationToken: cancellationToken);
        var grok = ReadGrokAsync(dep, cancellationToken);
        var gemini = dep.ReadAntigravity is null
            ? Task.FromResult(Antigravity.UnreadPlan("Gemini", "Antigravity agy executable not found."))
            : dep.ReadAntigravity();
        await Task.WhenAll(chat, grok, gemini);
        var result = new JsonArray((JsonNode)chat.Result.DeepClone(), (JsonNode)grok.Result.DeepClone(), (JsonNode)gemini.Result.DeepClone());
        if (gemini.Result["extraPlans"] is JsonArray extra)
            foreach (var plan in extra) result.Add(plan?.DeepClone());
        return result;
    }

    public static Task<JsonObject> ReadGrok(Dependencies dep, CancellationToken cancellationToken = default) => ReadGrokAsync(dep, cancellationToken);
    public static Task<JsonObject> ReadChatGpt(Dependencies dep, string? authHome = null, CancellationToken cancellationToken = default) => ReadChatGptAsync(dep, authHome, cancellationToken);
    public static Task<JsonArray> FetchPlanUsage(Dependencies dep, CancellationToken cancellationToken = default) => FetchPlanUsageAsync(dep, cancellationToken);
}
