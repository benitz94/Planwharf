// Port of engine/activity-events.cjs (LIMITS and Sanitize only).
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class ActivityEvents
{
    // JS: Object.freeze({ tail, line, events, text }). Same names and values.
    public static class Limits
    {
        public const int Tail = 131072;
        public const int Line = 16384;
        public const int Events = 200;
        public const int Text = 260;
    }

    // JavaScript's \s: ECMAScript mode in .NET only knows ASCII whitespace, so the set is spelled out.
    const string Ws = "\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff";
    const RegexOptions Js = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
    // JS /i without the unicode flag is ASCII-only. .NET IgnoreCase still folds Kelvin (U+212A) to k.

    // \z, not $: in .NET `$` also matches before a final newline, in JavaScript it does not.
    static readonly Regex PrivateKey = new(@"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?(?:-----END [^-]*PRIVATE KEY-----|\z)", Js);
    static readonly Regex Ansi = new(@"\x1b\[[0-?]*[ -/]*[@-~]", Js);
    static readonly Regex Control = new(@"[\u0000-\u0008\u000b-\u001f\u007f]", Js);
    static readonly Regex Bearer = new(@"\b([Bb][Ee][Aa][Rr][Ee][Rr][" + Ws + @"]+)[^" + Ws + @",;""']+", Js);
    static readonly Regex Assignment = new(
        @"([""']?(?:[Aa][Pp][Ii][_-]?[Kk][Ee][Yy]|[Aa][Cc][Cc][Ee][Ss][Ss][_-]?[Tt][Oo][Kk][Ee][Nn]|[Rr][Ee][Ff][Rr][Ee][Ss][Hh][_-]?[Tt][Oo][Kk][Ee][Nn]|[Tt][Oo][Kk][Ee][Nn]|[Pp][Aa][Ss][Ss][Ww][Oo][Rr][Dd]|[Ss][Ee][Cc][Rr][Ee][Tt]|[Aa][Uu][Tt][Hh][Oo][Rr][Ii][Zz][Aa][Tt][Ii][Oo][Nn]|[Cc][Oo][Oo][Kk][Ii][Ee])[""']?[" + Ws + @"]*[=:][" + Ws + @"]*)(?:""[^""\n]*""|'[^'\n]*'|[^" + Ws + @",;&]+)",
        Js);
    static readonly Regex KnownTokens = new(@"\b(?:sk-[\w-]{8,}|gh[pousr]_[\w]{8,}|github_pat_[\w]+)\b", Js);
    static readonly Regex UrlCredentials = new(@"([Hh][Tt][Tt][Pp][Ss]?://)[^" + Ws + @"/@]+:[^" + Ws + @"/@]+@", Js);
    static readonly Regex Spaces = new("[" + Ws + "]+", Js);

    // Display evidence is deliberately a whitelist. Raw logs remain with their existing owner.
    public static string Sanitize(string? value, int limit = Limits.Text)
    {
        var text = value ?? "";
        text = PrivateKey.Replace(text, "[REDACTED PRIVATE KEY]");
        text = Ansi.Replace(text, "");
        text = Control.Replace(text, "");
        text = Bearer.Replace(text, "$1[REDACTED]");
        text = Assignment.Replace(text, "$1[REDACTED]");
        text = KnownTokens.Replace(text, "[REDACTED]");
        text = UrlCredentials.Replace(text, "$1[REDACTED]@");
        text = text.Replace("<", "&lt;").Replace(">", "&gt;");
        // After collapsing JS \s to ASCII spaces, trim() only needs to drop those spaces.
        text = Spaces.Replace(text, " ").Trim(' ');
        // JS slice(0, limit): UTF-16 code units, negative counts from the end, may cut a surrogate pair.
        var end = limit < 0 ? Math.Max(0, text.Length + limit) : Math.Min(limit, text.Length);
        return text.Substring(0, end);
    }
}
