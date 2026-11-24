// Port of engine/antigravity.cjs.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Globalization;

namespace Planwharf.Engine;

public static class Antigravity
{
    public sealed record Command(string Executable, IReadOnlyList<string> Arguments,
        int TimeoutMilliseconds = 20000, int MaxBufferBytes = 1024 * 1024, bool WindowsHide = true);
    public sealed record Reply(string Stdout = "", string Stderr = "", bool Failed = false, bool Killed = false);
    public delegate Task<Reply> Run(Command command, CancellationToken cancellationToken);
    // Verified with agy 1.3.3: /usage is a CLI command, num_turns=0, numeric quota buckets.
    // Antigravity keeps the Claude/GPT quota separate from the Gemini quota.
    public const string ClaudeGptPool = "Antigravity Claude/GPT";
    private const RegexOptions Options = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
    private const string Space = @"[\u0009-\u000d\u0020\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]";

    public static JsonNode? PoolOf(JsonNode? cli, JsonNode? pool) =>
        Regex.IsMatch(Text(cli), @"^(?:claude|gpt-oss)-", Options)
            ? JsonValue.Create(ClaudeGptPool) : pool?.DeepClone();

    private static string Text(JsonNode? value) => value switch
    {
        JsonObject => "[object Object]",
        JsonArray array => string.Join(",", array.Select(Text)),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => value?.ToJsonString() ?? ""
    };
    private static JsonNode? Field(JsonNode? value, string key) => value is JsonObject o ? o[key] : null;
    private static bool IsString(JsonNode? value, string expected) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) && text == expected;
    private static bool Truthy(JsonNode? value) => value != null && value.ToJsonString() is not "false" and not "0" and not "\"\"";
    private static bool Number(JsonValue value, out double number)
    {
        if (value.TryGetValue<double>(out number)) return true;
        number = 0;
        return value.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
            double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    internal static JsonObject UnreadPlan(string id, string reason) => PlanStatus.WithStatus("antigravity", new JsonObject
    {
        ["id"] = id, ["label"] = id, ["readable"] = false, ["exhausted"] = false,
        ["pct"] = null, ["leftPct"] = null, ["source"] = null,
        ["reason"] = string.IsNullOrEmpty(reason) ? "Not readable" : reason, ["windows"] = new JsonArray()
    });

    private static JsonObject GroupPlan(JsonNode? groups, string name, string id)
    {
        var windows = new JsonArray();
        JsonObject? tight = null;
        var exhausted = false;
        foreach (var group in groups as JsonArray ?? new JsonArray())
        {
            if (group == null) throw new InvalidOperationException("Cannot read properties of null (reading 'name')");
            if (!string.Equals(Text(Field(group, "name")), name, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var bucket in Field(group, "buckets") as JsonArray ?? new JsonArray())
            {
                if (bucket == null) throw new InvalidOperationException("Cannot read properties of null (reading 'remaining_fraction')");
                if (Field(bucket, "remaining_fraction") is not JsonValue fractionValue ||
                    !Number(fractionValue, out var fraction) || !double.IsFinite(fraction) || fraction < 0 || fraction > 1) continue;
                var rawPercent = fraction * 100;
                var floor = Math.Floor(rawPercent);
                var left = (int)(floor + (rawPercent - floor >= 0.5 ? 1 : 0));
                var window = new JsonObject
                {
                    ["pct"] = 100 - left, ["leftPct"] = left, ["remainingFraction"] = fraction,
                    ["resetAt"] = Truthy(Field(bucket, "reset_time")) ? Field(bucket, "reset_time")!.DeepClone() : null
                };
                var windowName = Truthy(Field(bucket, "window")) ? Field(bucket, "window") : Field(bucket, "name");
                if (bucket is JsonObject b && (Truthy(b["window"]) || b.ContainsKey("name")))
                    window["name"] = windowName?.DeepClone();
                windows.Add((JsonNode)window);
                if (tight == null || fraction <= tight["remainingFraction"]!.GetValue<double>()) tight = window;
                exhausted |= fraction == 0;
            }
        }
        if (tight == null) return UnreadPlan(id, $"Antigravity returned no numeric {id} quota.");
        return new JsonObject
        {
            ["id"] = id, ["label"] = id, ["readable"] = true, ["exhausted"] = exhausted,
            ["pct"] = tight["pct"]!.DeepClone(), ["leftPct"] = tight["leftPct"]!.DeepClone(),
            ["source"] = "agy /usage JSON", ["planStatus"] = "active", ["reason"] = null, ["windows"] = windows
        };
    }

    public static JsonObject ParseUsage(JsonNode? body)
    {
        if (IsString(Field(body, "status"), "ERROR"))
        {
            var error = Field(Field(body, "result"), "error");
            error = Truthy(error) ? error : Field(body, "error");
            // providerError stringifies structured provider errors before classifying the status.
            var errorText = error is JsonValue v && v.TryGetValue<string>(out var message)
                ? message : Truthy(error) ? error!.ToJsonString() : "{}";
            var state = PlanStatus.ProviderError("antigravity", errorText, "Gemini");
            if (state != null)
            {
                var unread = UnreadPlan("Gemini", state["reason"]!.GetValue<string>());
                unread["planStatus"] = state["planStatus"]!.DeepClone();
                return unread;
            }
        }
        var command = Field(body, "command");
        var groups = IsString(Field(body, "status"), "SUCCESS") && IsString(Field(command, "name"), "usage")
            ? Field(Field(command, "data"), "groups") : null;
        var plan = GroupPlan(groups, "Gemini Models", "Gemini");
        plan["extraPlans"] = new JsonArray(GroupPlan(groups, "Claude and GPT models", ClaudeGptPool));
        return plan;
    }

    public static JsonObject ParseModels(string? output)
    {
        var text = Regex.Replace(output ?? "", @"\x1b\[[0-9;]*m", "", Options);
        const string idPattern = @"(?:gemini|claude|gpt-oss)-[a-zA-Z0-9][a-zA-Z0-9._:-]*";
        var ids = Regex.Matches(text, @"\b" + idPattern, Options).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return new JsonObject { ["ok"] = false,
            ["reason"] = "Antigravity returned no exact model IDs. Configured routes remain unverified; no aliases were guessed." };
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Regex.Split(text, @"\r?\n", Options))
        {
            // JavaScript's whitespace includes BOM/NBSP and Unicode space separators, while .NET's
            // ECMAScript option uses ASCII whitespace. Keep its ASCII word-boundary behavior above.
            var named = Regex.Match(line, "^" + Space + "*(" + idPattern + ")" + Space + "{2,}((?!" + Space + @")[^\r\n\u2028\u2029][^\r\n\u2028\u2029]*?)" + Space + @"*\z", Options);
            if (named.Success) labels.TryAdd(named.Groups[1].Value, named.Groups[2].Value);
        }
        var models = new JsonArray();
        foreach (var id in ids)
        {
            var model = new JsonObject { ["cli"] = id };
            if (labels.TryGetValue(id, out var label)) model["label"] = label;
            models.Add((JsonNode)model);
        }
        return new JsonObject { ["ok"] = true, ["ids"] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()), ["models"] = models };
    }

    // The host owns executable discovery and the bounded process implementation. Explicit injection
    // keeps tests and repository inspection from starting the owner's installed coding tools.
    public static async Task<JsonObject> ReadAntigravityAsync(string? command, Run run, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(command)) return UnreadPlan("Gemini", "Antigravity agy executable not found.");
        var reply = await run(new Command(command, ["-p", "/usage", "--output-format", "json", "--print-timeout", "15s"]), cancellationToken);
        if (reply.Failed) return UnreadPlan("Gemini", reply.Killed
            ? "Antigravity quota timed out." : "Antigravity /usage failed. Check CLI login.");
        try
        {
            var output = reply.Stdout.StartsWith('\uFEFF') ? reply.Stdout[1..] : reply.Stdout;
            return ParseUsage(JsonNode.Parse(Regex.Replace(output, "^" + Space + "+|" + Space + @"+\z", "", Options)));
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException)
        {
            return UnreadPlan("Gemini", "Antigravity /usage JSON format not recognized.");
        }
    }

    public static async Task<JsonObject> ListAntigravityAsync(string? command, Run run, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(command)) return new JsonObject { ["ok"] = false, ["reason"] = "Antigravity agy executable not found." };
        var reply = await run(new Command(command, ["models"]), cancellationToken);
        if (!reply.Failed) return ParseModels(reply.Stdout);
        return new JsonObject { ["ok"] = false, ["reason"] = Regex.IsMatch(reply.Stdout + reply.Stderr, "sign in|not logged|not authenticated", Options | RegexOptions.IgnoreCase)
            ? "Antigravity CLI cannot list models for this login. Sign in to agy, then refresh models."
            : "Antigravity CLI model listing failed. Check CLI access and permissions, then refresh models." };
    }
}
