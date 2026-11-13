// Ports engine/activity-events.test.cjs for LIMITS and sanitize only.
//
// Ported:
//  - Limits: the four names and values of LIMITS (tail, line, events, text).
//  - The sanitize checks: the mixed secrets string (api_key, quoted password, Bearer, sk-, ghp_,
//    URL credentials, <script>, ANSI escape); quoted JSON-style key and token=; PEM private key;
//    the length bound `friendly.length <= LIMITS.text` for a 1000-character input.
//  - Extra checks against the same JavaScript: null, custom limit, negative limit, non-ASCII text,
//    surrogate pair at the cut, Unicode whitespace, unterminated private key, case-insensitive keys,
//    Kelvin sign not matching, control characters, assignment-key variants, token length thresholds,
//    github_pat_/gh[pousr]_ prefixes, http(s) credentials, ANSI, U+0085 kept, BOM trimmed.
//    CR vs LF/tab, PUBLIC KEY kept, assignment terminators, word-boundary token prefixes,
//    sanitize-then-slice, Bearer non-matches, VT stripped, substring token/secret keys.
//
// Deferred (outside the scope of this job, only LIMITS and sanitize are ported):
//  - Every case using activityEvents, activityMessages, activityCapabilities, interpretActivity
//    (activity.cjs / translateEvents / activityCapabilities of activity-events.cjs): provider identity,
//    classification, provenance, state, malformed protocols, bounded history (LIMITS.events, .line, .tail
//    behaviour), capabilities, the "no fabricated timestamp" check, and activityMessages redaction.
//
// Differences from the JavaScript:
//  - Sanitize takes string? (null gives ""); JavaScript's String(value) also accepts numbers, objects, etc.
//  - LIMITS is a static class of int constants (Limits.Tail ...), not a frozen object.
//  - Sanitize's limit is an int; JavaScript's NaN/undefined/fractional limits are not modelled
//    (the default applies only when the argument is omitted). Negative limits follow slice(0, -n).
//  - Regexes use RegexOptions.ECMAScript with \s spelled out as JavaScript's whitespace set, and \z for
//    JavaScript's `$` (Node: /foo$/.test("foo\\n") is false; .NET `$` can still match before a final newline).
//    JS /i is spelled as ASCII [Aa] classes: .NET IgnoreCase folds U+212A to k, JavaScript /i without `u` does not.
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ActivityEventsTests
{
    [Fact]
    public void Limits_have_the_javascript_values()
    {
        Assert.Equal(131072, ActivityEvents.Limits.Tail);
        Assert.Equal(16384, ActivityEvents.Limits.Line);
        Assert.Equal(200, ActivityEvents.Limits.Events);
        Assert.Equal(260, ActivityEvents.Limits.Text);
    }

    [Fact]
    public void Secrets_markup_and_escapes_are_removed()
    {
        var safe = ActivityEvents.Sanitize("api_key=supersecret password=\"two words\" Bearer abc123 sk-123456789 ghp_123456789 https://user:pass@example.invalid <script>alert(1)</script>\u001b[31m");
        Assert.DoesNotMatch("supersecret|two words|abc123|123456789|user:pass|<script>|\u001b", safe);
        Assert.Contains("REDACTED", safe);
        Assert.Equal("api_key=[REDACTED] password=[REDACTED] Bearer [REDACTED] [REDACTED] [REDACTED] https://[REDACTED]@example.invalid &lt;script&gt;alert(1)&lt;/script&gt;", safe);
    }

    [Fact]
    public void Quoted_keys_and_tokens_are_redacted()
    {
        Assert.DoesNotMatch("private-value|other-private", ActivityEvents.Sanitize("\"api_key\": \"private-value\" token=other-private"));
        Assert.Equal("\"api_key\": [REDACTED] token=[REDACTED]", ActivityEvents.Sanitize("\"api_key\": \"private-value\" token=other-private"));
        Assert.Equal("TOKEN: [REDACTED] x", ActivityEvents.Sanitize("TOKEN: \"a b\" x"));
        Assert.Equal("api_key=[REDACTED] b", ActivityEvents.Sanitize("api_key=a\nb"));
        Assert.Equal("Bearer [REDACTED],rest", ActivityEvents.Sanitize("Bearer xyz,rest"));
        Assert.Equal("'api_key'=[REDACTED]", ActivityEvents.Sanitize("'api_key'='secret-value'"));
    }

    [Fact]
    public void Private_keys_are_redacted()
    {
        Assert.DoesNotContain("private material", ActivityEvents.Sanitize("-----BEGIN PRIVATE KEY-----\nprivate material\n-----END PRIVATE KEY-----"));
        Assert.Equal("[REDACTED PRIVATE KEY]", ActivityEvents.Sanitize("-----BEGIN RSA PRIVATE KEY-----\nabc\n"));
        Assert.Equal("before [REDACTED PRIVATE KEY] after", ActivityEvents.Sanitize("before -----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY----- after"));
    }

    [Fact]
    public void Output_is_bounded_by_the_text_limit()
    {
        Assert.True(ActivityEvents.Sanitize(new string('x', 1000)).Length <= ActivityEvents.Limits.Text);
        Assert.Equal(260, ActivityEvents.Sanitize(new string('x', 1000)).Length);
        Assert.Equal("abc", ActivityEvents.Sanitize("abcdef", 3));
        Assert.Equal("", ActivityEvents.Sanitize("x", 0));
        Assert.Equal("abcd", ActivityEvents.Sanitize("abcdef", -2));
    }

    [Fact]
    public void Missing_values_become_empty()
    {
        Assert.Equal("", ActivityEvents.Sanitize(null));
        Assert.Equal("", ActivityEvents.Sanitize("  \t\n "));
    }

    [Fact]
    public void Whitespace_collapses_and_control_characters_go()
    {
        Assert.Equal("a b", ActivityEvents.Sanitize("  a　　b  "));
        Assert.Equal("ab c", ActivityEvents.Sanitize("a\u0000\u0007b\u007f\u001f c"));
    }

    [Fact]
    public void Non_ascii_text_is_kept_and_counted_in_utf16_units()
    {
        Assert.Equal("ééééé&lt;b&gt;", ActivityEvents.Sanitize("ééééé<b>"));
        Assert.Equal("ééé", ActivityEvents.Sanitize("ééééé", 3));
    }

    [Fact]
    public void A_surrogate_pair_cut_in_half_keeps_the_high_surrogate_like_javascript()
    {
        Assert.Equal("ab😀cd", ActivityEvents.Sanitize("ab😀cd"));
        Assert.Equal("ab\ud83d", ActivityEvents.Sanitize("ab😀cd", 3));
        Assert.Equal("ab😀", ActivityEvents.Sanitize("ab😀cd", 4));
    }

    [Fact]
    public void Case_folding_matches_javascript()
    {
        // The Kelvin sign is not a "k" for JavaScript's /i; .NET IgnoreCase would treat it as one.
        Assert.Equal("toKen=abc", ActivityEvents.Sanitize("toKen=abc"));
        Assert.Equal("PASSWORD=[REDACTED]", ActivityEvents.Sanitize("PASSWORD=x"));
        Assert.Equal("bearer [REDACTED]", ActivityEvents.Sanitize("bearer xyz"));
        Assert.Equal("HTTP://[REDACTED]@x.test", ActivityEvents.Sanitize("HTTP://u:p@x.test"));
    }

    [Fact]
    public void Assignment_key_variants_are_redacted()
    {
        Assert.Equal("apikey=[REDACTED]", ActivityEvents.Sanitize("apikey=zzz"));
        Assert.Equal("api-key=[REDACTED]", ActivityEvents.Sanitize("api-key=zzz"));
        Assert.Equal("access_token=[REDACTED]", ActivityEvents.Sanitize("access_token=zzz"));
        Assert.Equal("access-token=[REDACTED]", ActivityEvents.Sanitize("access-token=zzz"));
        Assert.Equal("refresh_token=[REDACTED]", ActivityEvents.Sanitize("refresh_token=zzz"));
        Assert.Equal("cookie=[REDACTED]", ActivityEvents.Sanitize("cookie=zzz"));
        Assert.Equal("secret=[REDACTED]", ActivityEvents.Sanitize("secret=zzz"));
        Assert.Equal("authorization: [REDACTED]", ActivityEvents.Sanitize("authorization: zzz"));
        Assert.Equal("Cookie: [REDACTED]", ActivityEvents.Sanitize("Cookie: abc"));
        Assert.Equal("password=[REDACTED] b\"", ActivityEvents.Sanitize("password=\"a\nb\""));
    }

    [Fact]
    public void Known_token_shapes_follow_the_javascript_thresholds()
    {
        Assert.Equal("sk-1234567 keep", ActivityEvents.Sanitize("sk-1234567 keep"));
        Assert.Equal("[REDACTED]", ActivityEvents.Sanitize("sk-12345678"));
        Assert.Equal("[REDACTED]", ActivityEvents.Sanitize("github_pat_abcdefgh"));
        Assert.Equal("[REDACTED]", ActivityEvents.Sanitize("gho_12345678"));
        Assert.Equal("[REDACTED]", ActivityEvents.Sanitize("ghu_12345678"));
        Assert.Equal("[REDACTED]", ActivityEvents.Sanitize("ghs_12345678"));
        Assert.Equal("[REDACTED]", ActivityEvents.Sanitize("ghr_12345678"));
        Assert.Equal("gha_12345678", ActivityEvents.Sanitize("gha_12345678"));
        Assert.Equal("http://[REDACTED]@x.test", ActivityEvents.Sanitize("http://user:pass@x.test"));
        Assert.Equal("okred", ActivityEvents.Sanitize("ok\u001b[31mred\u001b[0m"));
    }

    [Fact]
    public void Non_javascript_whitespace_and_empty_inputs_are_left_as_javascript_leaves_them()
    {
        Assert.Equal("", ActivityEvents.Sanitize(""));
        Assert.Equal("\u0085keep\u0085", ActivityEvents.Sanitize("\u0085keep\u0085"));
        Assert.Equal("hi", ActivityEvents.Sanitize("\ufeffhi\ufeff"));
        Assert.Equal("", ActivityEvents.Sanitize("ab", -5));
    }

    [Fact]
    public void Line_endings_and_control_whitespace_follow_javascript_order()
    {
        Assert.Equal("ab c", ActivityEvents.Sanitize("a\rb\nc"));
        Assert.Equal("a b", ActivityEvents.Sanitize("a\tb"));
        Assert.Equal("ab", ActivityEvents.Sanitize("a\vb"));
    }

    [Fact]
    public void Public_keys_and_urls_without_passwords_are_kept()
    {
        Assert.Equal("-----BEGIN PUBLIC KEY----- abc -----END PUBLIC KEY-----", ActivityEvents.Sanitize("-----BEGIN PUBLIC KEY-----\nabc\n-----END PUBLIC KEY-----"));
        Assert.Equal("[REDACTED PRIVATE KEY]", ActivityEvents.Sanitize("-----BEGIN EC PRIVATE KEY-----\nxyz\n-----END EC PRIVATE KEY-----"));
        Assert.Equal("https://example.invalid/path", ActivityEvents.Sanitize("https://example.invalid/path"));
        Assert.Equal("https://user@example.invalid", ActivityEvents.Sanitize("https://user@example.invalid"));
    }

    [Fact]
    public void Assignment_stops_at_javascript_terminators_and_matches_key_suffixes()
    {
        Assert.Equal("token=[REDACTED],next", ActivityEvents.Sanitize("token=abc,next"));
        Assert.Equal("token=[REDACTED];next", ActivityEvents.Sanitize("token=abc;next"));
        Assert.Equal("token=[REDACTED]&next", ActivityEvents.Sanitize("token=abc&next"));
        Assert.Equal("mytoken=[REDACTED]", ActivityEvents.Sanitize("mytoken=visible"));
        Assert.Equal("notsecret=[REDACTED]", ActivityEvents.Sanitize("notsecret=visible"));
    }

    [Fact]
    public void Known_token_word_boundaries_match_javascript()
    {
        Assert.Equal("ghp_1234567", ActivityEvents.Sanitize("ghp_1234567"));
        Assert.Equal("[REDACTED]", ActivityEvents.Sanitize("ghp_12345678"));
        Assert.Equal("github_pat_", ActivityEvents.Sanitize("github_pat_"));
        Assert.Equal("[REDACTED]", ActivityEvents.Sanitize("github_pat_a"));
        Assert.Equal("ask-12345678", ActivityEvents.Sanitize("ask-12345678"));
        Assert.Equal("xsk-12345678", ActivityEvents.Sanitize("xsk-12345678"));
        Assert.Equal("[REDACTED]é", ActivityEvents.Sanitize("sk-12345678é"));
        Assert.Equal("sk-1234567é", ActivityEvents.Sanitize("sk-1234567é"));
    }

    [Fact]
    public void Redaction_runs_before_the_length_bound()
    {
        Assert.Equal("api_key=[RED", ActivityEvents.Sanitize("api_key=supersecret", 12));
        Assert.Equal("&lt", ActivityEvents.Sanitize("<>", 3));
        var longThenToken = new string('y', 250) + " sk-12345678 extra";
        var bounded = ActivityEvents.Sanitize(longThenToken);
        Assert.Equal(ActivityEvents.Limits.Text, bounded.Length);
        Assert.Equal(new string('y', 250) + " [REDACTED", bounded);
        Assert.DoesNotMatch("12345678|extra", bounded);
    }

    [Fact]
    public void Bearer_needs_a_token_and_keeps_quoted_values()
    {
        Assert.Equal("Bearer", ActivityEvents.Sanitize("Bearer "));
        Assert.Equal("Bearer \"abc\"", ActivityEvents.Sanitize("Bearer \"abc\""));
        Assert.Equal("Bearer [REDACTED]", ActivityEvents.Sanitize("Bearer   xyz"));
    }
}
