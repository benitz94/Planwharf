// Port of engine/provider-accounts.cjs.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

// Only display metadata leaves this module. Never return credentials or email addresses.
// grokEntry stays private: JavaScript exports it and hands the raw login object (key, refresh_token,
// email) to engine/planUsage.js. Quota reads stay on the JavaScript engine.
// CodexAccount and GrokAccount take the provider id (JavaScript reads provider.id) and an optional
// home directory (JavaScript's { home }). A null or empty id still means "codex" or "grok".
// AccountMetadata names are PascalCase for the JSON fields key, type, source and checkedAt.
public static class ProviderAccounts
{
    public sealed record AccountMetadata(string? Key, string Type, string Source, string? CheckedAt);

    static readonly HashSet<string> NormalPlans = new(StringComparer.Ordinal)
    {
        "free", "go", "plus", "pro", "prolite", "personal", "individual", "user"
    };

    const string BusinessPlan = "^(business|team|enterprise|edu|self_serve_business(?:_[a-z_]+)?|enterprise_cbp(?:_[a-z_]+)?)$";
    const RegexOptions JsPattern = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;

    // String(plan || '').toLowerCase(), then the same two buckets. Anything else is unread.
    public static string AccountType(string? plan)
    {
        var value = (plan ?? "").ToLowerInvariant();
        if (Regex.IsMatch(value, BusinessPlan, JsPattern)) return "business";
        if (NormalPlans.Contains(value)) return "normal";
        return "unread";
    }

    public static AccountMetadata UnknownAccount() => new(null, "unread", "Account metadata not exposed", null);

    public static string AccountLabel(string label, AccountMetadata? account) =>
        account?.Type == "business" ? label + " Business" : label;

    public static AccountMetadata CodexAccount(string? providerId, string? home = null)
    {
        var auth = ReadJson(Path.Combine(Home(home, "CODEX_HOME", ".codex"), "auth.json")) as JsonObject;
        var tokens = Prop(auth, "tokens") as JsonObject;
        var idToken = Prop(tokens, "id_token");
        var accessToken = Prop(tokens, "access_token");
        // A truthy non-string id_token blocks the access token, then fails to parse, as in JavaScript.
        var tokenText = Truthy(idToken) ? TokenText(idToken) : Truthy(accessToken) ? TokenText(accessToken) : "";
        var claims = Claims(tokenText);
        var info = Prop(claims, "https://api.openai.com/auth") as JsonObject;
        var accountId = Prop(tokens, "account_id");
        var claimAccount = Prop(info, "chatgpt_account_id");
        var selected = Truthy(accountId) ? accountId : claimAccount;
        var matches = !Truthy(claimAccount) || SameValue(claimAccount, selected);
        var user = FirstText(Prop(info, "chatgpt_user_id"), Prop(claims, "sub"));
        string? identity = Truthy(selected) ? JsString(selected) + ":" + user : null;
        var plan = Prop(info, "chatgpt_plan_type");
        var type = matches ? AccountType(Truthy(plan) ? JsString(plan) : "") : "unread";
        return Metadata(string.IsNullOrEmpty(providerId) ? "codex" : providerId, identity, type, "Codex login metadata");
    }

    public static AccountMetadata GrokAccount(string? providerId, string? home = null)
    {
        var entry = GrokEntry(ReadJson(Path.Combine(Home(home, "GROK_HOME", ".grok"), "auth.json")));
        var userId = Prop(entry, "user_id");
        string? subject = Truthy(userId) ? JsString(userId) : null;
        if (subject is null)
        {
            var keyNode = Prop(entry, "key");
            var sub = Prop(Claims(Truthy(keyNode) ? TokenText(keyNode) : ""), "sub");
            if (Truthy(sub)) subject = JsString(sub);
        }
        var principal = Prop(entry, "principal_type");
        var teamId = Prop(entry, "team_id");
        var team = TextIs(principal, "Team") && Truthy(teamId);
        var type = team ? "business" : TextIs(principal, "User") ? "normal" : "unread";
        string? identity = null;
        if (!string.IsNullOrEmpty(subject))
        {
            var principalId = Prop(entry, "principal_id");
            var scope = team ? JsString(teamId) : Truthy(principalId) ? JsString(principalId) : "personal";
            identity = subject + ":" + scope;
        }
        return Metadata(string.IsNullOrEmpty(providerId) ? "grok" : providerId, identity, type, "Grok login metadata");
    }

    // Match the normal first-party CLI scope; do not guess between multiple logins.
    static JsonObject? GrokEntry(JsonNode? raw)
    {
        if (raw is not JsonObject obj) return SingleCredential(raw as JsonArray);
        if (Truthy(Prop(obj, "key")) || Truthy(Prop(obj, "refresh_token"))) return obj;
        if (Preferred(obj, "https://auth.x.ai", out var preferred)) return preferred;
        if (Preferred(obj, "https://accounts.x.ai/sign-in", out preferred)) return preferred;
        return SingleCredential(obj);
    }

    static bool Preferred(JsonObject obj, string name, out JsonObject? entry)
    {
        entry = null;
        if (!obj.TryGetPropertyValue(name, out var node) || !Truthy(node)) return false;
        entry = node as JsonObject;
        return true;
    }

    static JsonObject? SingleCredential(JsonObject obj)
    {
        JsonObject? found = null;
        foreach (var pair in obj)
        {
            if (pair.Value is JsonObject child && HasCredential(child))
            {
                if (found is not null) return null;
                found = child;
            }
        }
        return found;
    }

    static JsonObject? SingleCredential(JsonArray? values)
    {
        if (values is null) return null;
        JsonObject? found = null;
        foreach (var value in values)
        {
            if (value is JsonObject child && HasCredential(child))
            {
                if (found is not null) return null;
                found = child;
            }
        }
        return found;
    }

    static bool HasCredential(JsonObject obj) => Truthy(Prop(obj, "key")) || Truthy(Prop(obj, "refresh_token"));

    static AccountMetadata Metadata(string providerId, string? identity, string type, string source)
    {
        string? key = string.IsNullOrEmpty(identity)
            ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(providerId + ":" + identity))).ToLowerInvariant();
        // Date.toISOString: UTC with exactly three millisecond digits.
        var checkedAt = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return new AccountMetadata(key, type, source, checkedAt);
    }

    static string Home(string? home, string variable, string folder)
    {
        if (home is not null) return home;
        var fromEnv = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folder);
    }

    static JsonNode? ReadJson(string file)
    {
        try
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
            return JsonNode.Parse(text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // JavaScript claims() swallows every failure and returns {}.
    static JsonObject Claims(string token)
    {
        try
        {
            var first = token.IndexOf('.');
            if (first < 0 || first == token.Length - 1) return new JsonObject();
            var rest = token[(first + 1)..];
            var second = rest.IndexOf('.');
            var payload = second < 0 ? rest : rest[..second];
            if (payload.Length == 0) return new JsonObject();
            return JsonNode.Parse(Encoding.UTF8.GetString(Base64UrlDecode(payload))) as JsonObject ?? new JsonObject();
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException or InvalidOperationException or DecoderFallbackException)
        {
            return new JsonObject();
        }
    }

    static byte[] Base64UrlDecode(string value)
    {
        foreach (var c in value)
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '=';
            if (!ok) throw new FormatException();
        }
        var padded = value.Replace('-', '+').Replace('_', '/');
        var mod = padded.Length % 4;
        if (mod == 2) padded += "==";
        else if (mod == 3) padded += "=";
        else if (mod != 0) throw new FormatException();
        return Convert.FromBase64String(padded);
    }

    static JsonNode? Prop(JsonNode? node, string name) => node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) ? value : null;

    static bool Truthy(JsonNode? node)
    {
        if (node is null) return false;
        if (node is not JsonValue v) return true;
        return v.GetValueKind() switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.Number => Convert.ToDouble(v.ToString(), CultureInfo.InvariantCulture) != 0,
            _ => true
        };
    }

    static bool TextIs(JsonNode? node, string text) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == text;

    static string TokenText(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";

    static bool SameValue(JsonNode? a, JsonNode? b)
    {
        if (ReferenceEquals(a, b)) return true;
        return a is JsonValue && b is JsonValue && JsonNode.DeepEquals(a, b);
    }

    static string FirstText(JsonNode? primary, JsonNode? fallback)
    {
        if (Truthy(primary)) return JsString(primary);
        if (Truthy(fallback)) return JsString(fallback);
        return "";
    }

    // JavaScript String() for the JSON values an account file can hold.
    static string JsString(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonArray array)
        {
            var parts = new string[array.Count];
            for (var i = 0; i < array.Count; i++) parts[i] = JsString(array[i]);
            return string.Join(",", parts);
        }
        if (node is not JsonValue v) return "[object Object]";
        return v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => v.ToJsonString(),
            _ => ""
        };
    }
}
