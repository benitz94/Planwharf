using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class PlainLines
{
    public const int LineMax = 90;
    public const int SummaryMax = 280;
    public const string VerifyPrefix = "Verify the work independently";

    public const string SplitLineAsk = "For each part also give \"line\": one short plain sentence for the owner, at most 12 words, that says who does what (for example \"Opus builds the weather page\"). No file paths, no code.";
    public const string SummaryAsk = "Also give \"summary\": one or two plain sentences for the owner that say what the work did, from the results above, at most 280 characters. No file paths, no code, no test names.";

    public static string Flat(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var plain = Regex.Replace(text, @"```[\s\S]*?```", " ");
        plain = Regex.Replace(plain, @"`([^`]*)`", "$1");
        plain = Regex.Replace(plain, @"\*\*|__", "");
        plain = Regex.Replace(plain, @"^\s*#+\s*", "", RegexOptions.Multiline);
        plain = Regex.Replace(plain, @"^\s*[-*•]\s+", "", RegexOptions.Multiline);
        return Regex.Replace(plain, @"\s+", " ").Trim();
    }

    public static string Clip(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text[..(max - 1)];
        var space = cut.LastIndexOf(' ');
        var res = space > max / 2 ? cut[..space] : cut;
        return Regex.Replace(res, @"[\s,;:.\-–]+$", "") + "…";
    }

    public static string FirstSentence(string? text)
    {
        var plain = Flat(text);
        if (string.IsNullOrEmpty(plain)) return "";
        var match = Regex.Match(plain, @"[.!?](\s|$)|:\s");
        var end = match.Success ? (plain[match.Index] == ':' ? match.Index : match.Index + 1) : plain.Length;
        return plain[..end].Trim();
    }

    public static string Sentences(string? text, int count)
    {
        var plain = Flat(text);
        if (string.IsNullOrEmpty(plain)) return "";
        var matches = Regex.Matches(plain, @"[^.!?]+[.!?]+(\s|$)|[^.!?]+$");
        var list = new List<string>();
        for (var i = 0; i < Math.Min(count, matches.Count); i++) list.Add(matches[i].Value);
        return string.Concat(list).Trim();
    }

    public static string ShortLine(string? text, int max = LineMax) => Clip(Flat(text), max);

    // A string field from the store. A missing value or the wrong type is empty, so a corrupt snapshot still reads.
    static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    public static (string Line, string From) StepLine(JsonObject? sub)
    {
        if (sub is null) return ("", "none");
        var lead = ShortLine(Text(sub["leadLine"]));
        if (lead.Length == 0) lead = ShortLine(Text(sub["line"]));
        if (lead.Length > 0) return (lead, "lead");
        var title = Text(sub["title"]);
        if (title.StartsWith(VerifyPrefix, StringComparison.Ordinal)) return ("Checks the whole work and runs the tests.", "fallback");
        var fallback = Clip(FirstSentence(title), LineMax);
        return (fallback, fallback.Length > 0 ? "fallback" : "none");
    }

    public static (string Summary, string From) WorkSummary(JsonObject? mega, JsonArray? results, JsonArray? subTasks)
    {
        var lead = Clip(Sentences(Text(mega?["leadSummary"]), 2), SummaryMax);
        if (lead.Length > 0) return (lead, "lead");

        var verify = new HashSet<string>((subTasks ?? [])
            .OfType<JsonObject>()
            .Where(sub => Text(sub["title"]).StartsWith(VerifyPrefix, StringComparison.Ordinal))
            .Select(sub => Text(sub["id"]))
            .Where(id => id.Length > 0));

        var built = (results ?? [])
            .OfType<JsonObject>()
            .Where(r => Text(r["status"]) == "COMPLETE"
                && !verify.Contains(Text(r["subTaskId"]))
                && Flat(Text(r["summary"])).Length > 0)
            .ToList();

        var said = built.Count > 0 ? FirstSentence(Text(built[^1]["summary"])) : "";
        if (said.Length > 0) return (Clip(said, SummaryMax), "fallback");

        var title = Clip(FirstSentence(Text(mega?["title"]).Length > 0 ? Text(mega?["title"]) : Text(mega?["goal"])), SummaryMax);
        return (title, title.Length > 0 ? "fallback" : "none");
    }
}
