// Port of engine/provider-diagnostics.cjs.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class ProviderDiagnostics
{
    const RegexOptions Js = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
    // JavaScript's `.` stops at \n, \r, U+2028 and U+2029; .NET's only at \n.
    const string Dot = "[^\\n\\r\u2028\u2029]";
    static Regex Pattern(string source) => new(source.Replace(".", Dot).Replace("\\" + Dot, "\\."), Js);

    // Patterns without the `i` flag are matched against the text as it is. Patterns with it are written in
    // lower case and matched against the text with its ASCII letters lowered: JavaScript's `i` without `u`
    // never folds a non-ASCII letter onto an ASCII one (the Kelvin sign is not `k`), .NET IgnoreCase does.
    static readonly (Regex Pattern, bool IgnoreCase, string Code, string Suggestion)[] Categories =
    [
        (Pattern("PERMISSION_MODE_UNSUPPORTED"), false, "PERMISSION_MODE_UNSUPPORTED", "Choose a supported permission mode or another adapter."),
        (Pattern("PROVIDER_MODEL_IDENTITY_CHANGED"), false, "PROVIDER_MODEL_IDENTITY_CHANGED", "The response identified a different model. Verify the exact model ID and provider aliases before retrying."),
        (Pattern("API_CONFIGURATION_INVALID|CLI_CONFIGURATION_INVALID"), false, "PROVIDER_CONFIGURATION_INVALID", "Review the endpoint, protocol and declared CLI arguments for this connection."),
        (Pattern("unexpected argument|unknown (?:option|flag)|unrecognized (?:argument|option)|flag provided but not defined|unsupported.*format|incompatible.*protocol"), true, "PROVIDER_CLI_INCOMPATIBLE", "The CLI interface may have changed. Check its version and update the Planwharf adapter."),
        (Pattern("provider_protocol_changed|missing or invalid structured|unreadable.*(?:metadata|response)|no recognized response"), true, "PROVIDER_PROTOCOL_CHANGED", "The response contract could not be verified. Review CLI versions and the adapter parser."),
        (Pattern("401|unauthorized|authentication|not logged in|invalid.*(?:key|token)"), true, "PROVIDER_AUTHENTICATION_FAILED", "Sign in to this provider again, then refresh models."),
        (Pattern("429|quota|credit|spending.limit|rate.limit"), true, "PROVIDER_USAGE_LIMIT", "Check provider quota and your configured usage policy."),
        (Pattern("invalid model selection|model.*(?:not found|unsupported|unavailable|does not exist|not recognized|unknown)|access.*model|model.*access"), true, "PROVIDER_MODEL_UNAVAILABLE", "Refresh models and verify access to this exact provider/model ID."),
        (Pattern("executable not found|enoent"), true, "PROVIDER_CLI_MISSING", "Install or configure this adapter executable."),
        (Pattern("timeout|etimedout|enotfound|econn|certificate|network"), true, "PROVIDER_CONNECTION_FAILED", "Check connectivity and certificates before retrying."),
    ];

    // Reports contain only route identity and a bounded category, never raw prompts or credentials.
    public static JsonObject DiagnoseProvider(JsonObject model, string? value, JsonNode? exitCode = null)
    {
        var text = value ?? "";
        var lowered = AsciiLower(text);
        var provider = First(model["providerId"], model["provider"]) ?? "unknown";
        var adapter = First(model["adapter"], model["provider"]) ?? "unknown";
        var id = First(model["cli"], model["id"]) ?? "unknown";
        var code = "PROVIDER_EXECUTION_FAILED";
        var suggestion = "Review the retained local output and retry after checking the provider CLI.";
        foreach (var category in Categories)
        {
            if (!category.Pattern.IsMatch(category.IgnoreCase ? lowered : text)) continue;
            code = category.Code; suggestion = category.Suggestion;
            break;
        }
        var message = $"{code}: {provider} / {id}. {suggestion}";
        var report = $"Planwharf provider integration report\n\nCategory: {code}\nProvider: {provider}\nAdapter: {adapter}\nExact model: {id}\nExit code: {(exitCode is null ? "unavailable" : JsString(exitCode))}\n\nCLI version: [fill in locally]\nExpected behavior: [describe]\nObserved behavior: [describe without credentials or personal project data]\n\nRaw logs and prompts are intentionally excluded.";
        var reportUrl = "https://github.com/benitz94/Planwharf/issues/new?title=" + EncodeURIComponent(code + ": " + adapter) + "&body=" + EncodeURIComponent(report);
        return new JsonObject
        {
            ["code"] = code, ["provider"] = provider, ["adapter"] = adapter, ["model"] = id, ["exitCode"] = exitCode?.DeepClone(),
            ["message"] = message, ["suggestion"] = suggestion, ["report"] = report, ["reportUrl"] = reportUrl
        };
    }

    // encodeURIComponent: UTF-8, upper-case hex, and A-Z a-z 0-9 - _ . ! ~ * ' ( ) kept as they are.
    // Uri.EscapeDataString escapes ! ' ( ) * too, so it is not used. A lone surrogate fails as in JavaScript.
    public static string EncodeURIComponent(string text)
    {
        var builder = new StringBuilder(text.Length);
        Span<byte> bytes = stackalloc byte[4];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || "-_.!~*'()".Contains(c)) { builder.Append(c); continue; }
            int scalar;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) throw new InvalidOperationException("URI malformed");
                scalar = char.ConvertToUtf32(c, text[++i]);
            }
            else if (char.IsLowSurrogate(c)) throw new InvalidOperationException("URI malformed");
            else scalar = c;
            var count = new Rune(scalar).EncodeToUtf8(bytes);
            for (var b = 0; b < count; b++) builder.Append('%').Append(bytes[b].ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    // a || b || 'unknown' as text: the first truthy value, converted as a JavaScript template does.
    static string? First(params JsonNode?[] values)
    {
        foreach (var value in values) if (Truthy(value)) return JsString(value!);
        return null;
    }

    static double Number(JsonValue value) => double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => Number(value) is var number && number != 0 && !double.IsNaN(number),
            _ => true
        },
        _ => true
    };

    static string JsString(JsonNode node) => node switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => JsNumber(Number(value)),
        JsonValue value => value.ToJsonString(),
        JsonArray array => string.Join(",", array.Select(item => item is null ? "" : JsString(item))),
        _ => "[object Object]"
    };

    static string JsNumber(double number) =>
        number == Math.Floor(number) && Math.Abs(number) < 1e21 ? number.ToString("0", CultureInfo.InvariantCulture) : number.ToString("R", CultureInfo.InvariantCulture);

    static string AsciiLower(string text) => string.Create(text.Length, text, (span, source) =>
    {
        for (var i = 0; i < source.Length; i++) span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
    });
}
