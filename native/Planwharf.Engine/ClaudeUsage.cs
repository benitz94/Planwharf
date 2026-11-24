// Port of engine/claude-usage.cjs.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public record ClaudeAuthInfo(string Token, long? ExpiresAt);

public static class ClaudeUsage
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public const int MaxBytes = 65536;

    public static JsonObject Unread(string? reason)
    {
        var resp = new JsonObject
        {
            ["id"] = "Claude",
            ["label"] = "Claude",
            ["readable"] = false,
            ["exhausted"] = false,
            ["pct"] = null,
            ["leftPct"] = null,
            ["source"] = null,
            ["reason"] = reason ?? "Not readable",
            ["windows"] = new JsonArray(),
            // A failed read never says the login is gone: only Anthropic's own answer can.
            ["planStatus"] = "unknown"
        };
        return resp;
    }

    public static JsonObject ParseClaudeUsage(JsonObject? body)
    {
        var windows = new List<(string name, double pct, double leftPct, string? resetAt)>();
        var pairs = new[] { ("five_hour", "5-hour"), ("seven_day", "weekly") };

        foreach (var (key, name) in pairs)
        {
            if (body?[key] is not JsonObject row) continue;
            var utilNode = row["utilization"];
            if (utilNode == null || !double.TryParse(utilNode.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var util) ||
                !double.IsFinite(util) || util < 0 || util > 100)
            {
                return Unread("Claude usage response contains an invalid quota window.");
            }

            var resetNode = row["resets_at"];
            string? resetsAt = null;
            if (resetNode != null)
            {
                resetsAt = resetNode.GetValue<string>();
                if (!DateTimeOffset.TryParse(resetsAt, out _))
                    return Unread("Claude usage response contains an invalid quota window.");
            }

            windows.Add((name, util, 100 - util, resetsAt));
        }

        if (windows.Count == 0)
            return Unread("Claude usage response contains no numeric subscription quota windows.");

        var tight = windows.OrderBy(w => w.leftPct).First();
        var winArr = new JsonArray();
        foreach (var w in windows)
        {
            winArr.Add((JsonNode)new JsonObject
            {
                ["name"] = w.name,
                ["pct"] = w.pct,
                ["leftPct"] = w.leftPct,
                ["resetAt"] = w.resetAt
            });
        }

        return new JsonObject
        {
            ["id"] = "Claude",
            ["label"] = "Claude",
            ["readable"] = true,
            ["exhausted"] = windows.Any(w => w.pct == 100),
            ["pct"] = tight.pct,
            ["leftPct"] = tight.leftPct,
            ["resetAt"] = tight.resetAt,
            ["source"] = "Claude subscription OAuth usage",
            ["planStatus"] = "active",
            ["reason"] = null,
            ["windows"] = winArr
        };
    }

    public static async Task<ClaudeAuthInfo?> ReadClaudeAuthAsync(
        IDictionary<string, string>? env = null,
        string? home = null,
        string? command = null,
        Func<long>? now = null)
    {
        var envMap = env ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var blockedKeys = new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY", "ANTHROPIC_BASE_URL" };
        if (blockedKeys.Any(k => envMap.ContainsKey(k) && !string.IsNullOrEmpty(envMap[k])))
            return null;

        if (envMap.TryGetValue("CLAUDE_CODE_OAUTH_TOKEN", out var oauthToken) && !string.IsNullOrWhiteSpace(oauthToken))
        {
            return new ClaudeAuthInfo(oauthToken.Trim(), null);
        }

        var homeDir = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string configDir = envMap.TryGetValue("CLAUDE_CONFIG_DIR", out var cfg) && !string.IsNullOrEmpty(cfg) ? cfg : Path.Combine(homeDir, ".claude");
        var credFile = Path.Combine(configDir, ".credentials.json");

        if (!File.Exists(credFile)) return null;
        var fi = new FileInfo(credFile);
        if (fi.Length > MaxBytes) return null;

        try
        {
            var text = await File.ReadAllTextAsync(credFile);
            var parsed = JsonNode.Parse(text) as JsonObject;
            var auth = parsed?["claudeAiOauth"] as JsonObject;
            var token = auth?["accessToken"]?.GetValue<string>();
            var expiresAtNode = auth?["expiresAt"];

            if (string.IsNullOrWhiteSpace(token)) return null;

            long? exp = null;
            if (expiresAtNode != null && long.TryParse(expiresAtNode.ToString(), out var parsedExp))
                exp = parsedExp;

            var currentTime = now != null ? now() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (exp != null && exp <= currentTime) return null;

            return new ClaudeAuthInfo(token.Trim(), exp);
        }
        catch
        {
            return null;
        }
    }

    public static Func<Task<JsonObject>> CreateClaudeUsageReader(
        Func<Task<ClaudeAuthInfo?>>? readAuth = null,
        Func<string, HttpRequestMessage, Task<HttpResponseMessage>>? fetchImpl = null,
        Func<long>? now = null,
        int ttlMs = 300000)
    {
        long NowMs() => now != null ? now() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        readAuth ??= () => ReadClaudeAuthAsync(now: NowMs);

        (string key, JsonObject result, long until)? cached = null;
        Task<JsonObject>? pending = null;
        string? pendingKey = null;

        string? Scope(ClaudeAuthInfo? a)
        {
            if (a == null || string.IsNullOrWhiteSpace(a.Token)) return null;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(a.Token));
            return Convert.ToHexStringLower(hash);
        }

        return async () =>
        {
            var auth = await readAuth();
            var key = Scope(auth);
            var currentNow = NowMs();

            if (string.IsNullOrEmpty(key) || (auth?.ExpiresAt != null && auth.ExpiresAt <= currentNow))
            {
                cached = null;
                return Unread("Claude subscription credentials are unavailable or expired. Open Claude Code to refresh the login, then refresh usage. API and gateway logins do not provide this subscription quota.");
            }

            if (cached != null && cached.Value.key == key && currentNow < cached.Value.until)
            {
                return (JsonObject)cached.Value.result.DeepClone();
            }

            if (pending != null && pendingKey == key)
            {
                var pRes = await pending;
                return (JsonObject)pRes.DeepClone();
            }

            async Task<JsonObject> ExecuteRequestAsync()
            {
                int retryMs = ttlMs;
                JsonObject result;
                try
                {
                    if (fetchImpl != null)
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
                        req.Headers.Add("Authorization", "Bearer " + auth!.Token);
                        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");

                        using var resp = await fetchImpl(UsageUrl, req);
                        if (!resp.IsSuccessStatusCode)
                        {
                            if ((int)resp.StatusCode == 429)
                            {
                                if (resp.Headers.TryGetValues("retry-after", out var raVals) && raVals.FirstOrDefault() is string raStr && int.TryParse(raStr, out var sec))
                                {
                                    retryMs = Math.Max(ttlMs, Math.Min(sec * 1000, 3600000));
                                }
                            }
                            result = Unread((int)resp.StatusCode == 401
                                ? "Claude usage could not be read: the usage service refused the saved login. Claude Code renews it when it runs; Claude models are not stopped for this."
                                : (int)resp.StatusCode == 429
                                ? "Claude usage endpoint is rate limited. A later refresh will retry; previous quota is not treated as current."
                                : $"Claude usage HTTP {(int)resp.StatusCode}.");
                        }
                        else
                        {
                            var respText = await resp.Content.ReadAsStringAsync();
                            var bodyObj = JsonNode.Parse(respText) as JsonObject;
                            result = ParseClaudeUsage(bodyObj);
                        }
                    }
                    else
                    {
                        result = Unread("No fetch implementation provided.");
                    }
                }
                catch
                {
                    result = Unread("Claude subscription usage could not be read: connection, timeout or response format error.");
                }

                var afterNow = NowMs();
                var until = afterNow + retryMs;
                result["checkedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                cached = (key, (JsonObject)result.DeepClone(), until);
                return result;
            }

            var reqTask = ExecuteRequestAsync();

            pending = reqTask;
            pendingKey = key;
            try
            {
                var finalRes = await reqTask;
                return (JsonObject)finalRes.DeepClone();
            }
            finally
            {
                if (pending == reqTask)
                {
                    pending = null;
                    pendingKey = null;
                }
            }
        };
    }
}
