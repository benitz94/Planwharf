// Port of engine/provider-refusal.cjs.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

// Recognises a provider refusing work for credits, quota or a usage limit, so Planwharf
// can route around that model instead of stopping. Only the end of an output is read:
// a refusal is the last thing a provider prints, and earlier text may quote these words.
public static class ProviderRefusal
{
    public const long COOLDOWN_MS = 60 * 60 * 1000;

    // The JavaScript patterns carry the `i` flag. Without the `u` flag, JavaScript folds case only between
    // letters that are not mapped to ASCII from outside it: the Kelvin sign is not `k`, the long s is not
    // `s`. The patterns hold ASCII letters only, so they are written in lower case and matched against
    // the text with its ASCII letters lowered (same length, same indexes), never with RegexOptions.IgnoreCase.
    // ECMAScript mode keeps `\b` on ASCII word characters, as in JavaScript.
    const RegexOptions Js = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
    static readonly Regex[] Patterns =
    [
        new("requires usage credits", Js),
        new("you(?:'|\u2019)ve hit your [^.\\n]*limit", Js),
        new("usage limit (?:reached|exceeded)", Js),
        new("(?:quota|credits?) (?:exceeded|exhausted|depleted)", Js),
        new("insufficient (?:credits|quota|balance)", Js),
        new("out of (?:credits|quota)", Js),
        new("rate limit (?:reached|exceeded)", Js),
        new("resource[_ ]exhausted", Js),
        new("\\b429\\b[^\\n]{0,80}(?:too many requests|rate|quota)", Js),
    ];

    // JavaScript's \s: ECMAScript mode in .NET only knows ASCII whitespace, so the set is spelled out.
    static readonly Regex Spaces = new("[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]+", Js);

    public static string? RefusalReason(string? text)
    {
        var all = text ?? "";
        // JS slice(-4000): UTF-16 code units, may cut a surrogate pair.
        var tail = all.Length > 4000 ? all.Substring(all.Length - 4000) : all;
        var lowered = AsciiLower(tail);
        foreach (var pattern in Patterns)
        {
            var match = pattern.Match(lowered);
            if (!match.Success) continue;
            var start = tail.LastIndexOf('\n', match.Index) + 1;
            var end = tail.IndexOf('\n', match.Index);
            var line = end < 0 ? tail.Substring(start) : tail.Substring(start, end - start);
            var reason = Spaces.Replace(line, " ").Trim(' ');
            if (reason.Length > 240) reason = reason.Substring(0, 240);
            return reason.Length > 0 ? reason : tail.Substring(match.Index, match.Length);
        }
        return null;
    }

    // store.refusals: { [modelId]: { reason, at, until } }. Expired entries are removed.
    public static JsonNode? ActiveRefusal(JsonObject? store, string modelId, double nowMs)
    {
        if (store?["refusals"] is not JsonObject refusals || !refusals.TryGetPropertyValue(modelId, out var row) || !Truthy(row)) return null;
        var until = row is JsonObject obj && obj["until"] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? DateParse(value.GetValue<string>()) : double.NaN;
        if (!(until > nowMs)) { refusals.Remove(modelId); return null; }
        return row;
    }

    public static JsonObject RecordRefusal(JsonObject store, string modelId, string reason, string nowIso, double cooldownMs = COOLDOWN_MS)
    {
        if (!Truthy(store["refusals"])) store["refusals"] = new JsonObject();
        var refusals = store["refusals"] as JsonObject ?? throw new InvalidOperationException("store.refusals is not an object");
        var until = ToIsoString(DateParse(nowIso) + cooldownMs);
        var row = new JsonObject { ["reason"] = reason.Length > 240 ? reason.Substring(0, 240) : reason, ["at"] = nowIso, ["until"] = until };
        refusals[modelId] = row;
        return row;
    }

    static string AsciiLower(string text) => string.Create(text.Length, text, (span, source) =>
    {
        for (var i = 0; i < source.Length; i++) span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
    });

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

    // new Date(ms).toISOString(): throws "Invalid time value" outside the range of a JavaScript date.
    static string ToIsoString(double ms)
    {
        if (double.IsNaN(ms) || Math.Abs(ms) > 8.64e15) throw new InvalidOperationException("Invalid time value");
        var date = DateTime.UnixEpoch.AddMilliseconds(Math.Truncate(ms));
        if (date.Year is < 0 or > 9999) throw new InvalidOperationException("Invalid time value");
        return date.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    static readonly Regex IsoDate = new(@"^([+-]\d{6}|\d{4})(?:-(\d{2})(?:-(\d{2}))?)?(?:T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,9}))?)?(Z|[+-]\d{2}:\d{2})?)?$", RegexOptions.ECMAScript);

    // Date.parse for the ISO format JavaScript defines: a date alone is UTC, a time without an offset is
    // local time. Other text falls back to the .NET parser, as JavaScript falls back to its own.
    static double DateParse(string text)
    {
        var m = IsoDate.Match(text);
        if (m.Success)
        {
            var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int Part(int group, int fallback) => m.Groups[group].Success ? int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) : fallback;
            int month = Part(2, 1), day = Part(3, 1), hour = Part(4, 0), minute = Part(5, 0), second = Part(6, 0);
            var fraction = m.Groups[7].Success ? m.Groups[7].Value.PadRight(3, '0').Substring(0, 3) : "000";
            if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 24 || minute > 59 || second > 59) return double.NaN;
            if (hour == 24 && (minute != 0 || second != 0 || fraction != "000")) return double.NaN;
            var local = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified).AddHours(hour).AddMinutes(minute).AddSeconds(second).AddMilliseconds(int.Parse(fraction, CultureInfo.InvariantCulture));
            TimeSpan offset;
            if (m.Groups[8].Success)
            {
                var zone = m.Groups[8].Value;
                offset = zone == "Z" ? TimeSpan.Zero : (zone[0] == '-' ? -1 : 1) * new TimeSpan(int.Parse(zone.Substring(1, 2), CultureInfo.InvariantCulture), int.Parse(zone.Substring(4, 2), CultureInfo.InvariantCulture), 0);
            }
            else offset = m.Groups[4].Success ? TimeZoneInfo.Local.GetUtcOffset(local) : TimeSpan.Zero;
            return (local - offset - DateTime.UnixEpoch).TotalMilliseconds;
        }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.ToUnixTimeMilliseconds() : double.NaN;
    }
}
