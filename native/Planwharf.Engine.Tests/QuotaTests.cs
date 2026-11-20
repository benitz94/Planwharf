// Deferred cases from native/porting/jobs/23-phase2-rest.md. Not implemented here:
// - attempts.js, createAttemptEngine, and every sentinel test that needs the real store
// - createServer, createOrchestrator, spawnWorker, runner.cjs, and the HTTP loops in
//   task-completion.test.cjs, grill-chat.test.cjs, project-export.test.cjs, project-teams.test.cjs,
//   start-over.test.cjs (archiveProject), and worker-containment.test.cjs (launch artifacts and provider cwd)
// - Live npm test, live node --test, and live taskkill
// - Byte-identical randomUUID values across JavaScript and C#
// - gitEnvironment and within as parity JSON
// - The browser root / window / AMD wrappers, models.json, and provider-catalog
// - A second process appending to invocations.jsonl at the same time
// - Reading the checkout's plan.json or .git
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class QuotaTests
{
    static JsonObject Plan(string json) => JsonNode.Parse(json)!.AsObject();

    // engine/phase1.test.cjs: quota remaining bands and explicit text including shared provider pools
    [Fact]
    public void Quota_remaining_bands_and_explicit_text_including_shared_provider_pools()
    {
        foreach (var (remaining, band) in new (int Remaining, string Band)[]
        {
            (90, "green"), (60, "orange"), (40, "yellow"), (10, "red"), (0, "red"),
            (75, "green"), (50, "orange"), (25, "yellow"), (1, "red")
        })
        {
            var plan = Plan($$"""{"readable":true,"pct":{{100 - remaining}},"leftPct":{{remaining}}}""");
            Assert.Equal(band, Quota.Describe(plan)["band"]!.GetValue<string>());
            var html = Quota.Html(plan);
            Assert.Contains("REMAINING", html);
            if (remaining == 0) Assert.Contains("EXHAUSTED", html);
        }

        Assert.Equal("""{"remaining":null,"band":"grey","text":"REMAINING UNREAD"}""", Quota.Describe(Plan("""{"readable":false}""")).ToJsonString());
        Assert.Equal(82, Quota.Describe(Plan("""{"readable":true,"pct":18}"""))["remaining"]!.GetValue<int>());

        var provider = Plan("""{"id":"ChatGPT","readable":true,"leftPct":82}""");
        var plans = new[] { provider };
        var models = new[] { ("astra", "ChatGPT"), ("sol", "ChatGPT") };
        var texts = models.Select(model => Quota.Describe(plans.First(item => item["id"]!.GetValue<string>() == model.Item2))["text"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "82% REMAINING", "82% REMAINING" }, texts);
    }

    // engine/claude-usage.test.cjs: the quota.js text check inside
    // "actual reader feeds readable quota through provider views and remaining UI".
    // The reader, catalog, and credential checks in that test are not quota.js.
    [Fact]
    public void Claude_usage_quota_text_is_82_percent_remaining()
    {
        var quota = Plan("""{"readable":true,"leftPct":82}""");
        Assert.Equal("82% REMAINING", Quota.Describe(quota)["text"]!.GetValue<string>());
        Assert.Equal(82, quota["leftPct"]!.GetValue<int>());
    }

    // Comparison cases from the sheet: nine band boundaries, unread, pct without leftPct,
    // a negative number, a number above 100, and html for red exhausted and for grey.
    [Fact]
    public void Quota_comparison_boundaries_unread_pct_clamp_and_html()
    {
        foreach (var (remaining, band) in new (int Remaining, string Band)[]
        {
            (90, "green"), (75, "green"), (60, "orange"), (50, "orange"), (40, "yellow"),
            (25, "yellow"), (10, "red"), (1, "red"), (0, "red")
        })
            AssertSame(remaining, band, remaining == 0 ? "0% REMAINING \u00b7 EXHAUSTED" : remaining + "% REMAINING",
                Quota.Describe(Plan($$"""{"readable":true,"leftPct":{{remaining}}}""")));

        Assert.Equal("""{"remaining":null,"band":"grey","text":"REMAINING UNREAD"}""", Quota.Describe(Plan("""{"readable":false,"leftPct":82}""")).ToJsonString());
        AssertSame(82, "green", "82% REMAINING", Quota.Describe(Plan("""{"readable":true,"pct":18}""")));
        AssertSame(0, "red", "0% REMAINING \u00b7 EXHAUSTED", Quota.Describe(Plan("""{"readable":true,"leftPct":-4}""")));
        AssertSame(100, "green", "100% REMAINING", Quota.Describe(Plan("""{"readable":true,"leftPct":140}""")));
        Assert.Equal("<span class=\"quota-remaining quota-red\">0% REMAINING \u00b7 EXHAUSTED</span>", Quota.Html(Plan("""{"readable":true,"leftPct":0}""")));
        Assert.Equal("<span class=\"quota-remaining quota-grey\">REMAINING UNREAD</span>", Quota.Html(Plan("""{"readable":false}""")));
    }

    static void AssertSame(int remaining, string band, string text, JsonObject described)
    {
        Assert.Equal(new[] { "remaining", "band", "text" }, described.Select(pair => pair.Key).ToArray());
        Assert.Equal(remaining, described["remaining"]!.GetValue<int>());
        Assert.Equal(band, described["band"]!.GetValue<string>());
        Assert.Equal(text, described["text"]!.GetValue<string>());
    }

    [Fact]
    public void Left_percent_wins_and_non_finite_values_fall_through_or_stay_unread()
    {
        Assert.Equal(10, Quota.Describe(Plan("""{"readable":true,"leftPct":10,"pct":0}"""))["remaining"]!.GetValue<int>());
        Assert.Equal(0, Quota.Describe(Plan("""{"readable":true,"leftPct":0,"pct":18}"""))["remaining"]!.GetValue<int>());
        Assert.Equal(82, Quota.Describe(Plan("""{"readable":true,"leftPct":"no","pct":18}"""))["remaining"]!.GetValue<int>());
        Assert.Equal("grey", Quota.Describe(Plan("""{"readable":true}"""))["band"]!.GetValue<string>());
        Assert.Equal("grey", Quota.Describe(null)["band"]!.GetValue<string>());
        Assert.Equal("grey", Quota.Describe(Plan("""{"leftPct":40}"""))["band"]!.GetValue<string>());
        Assert.Equal("grey", Quota.Describe(Plan("""{"readable":0,"leftPct":40}"""))["band"]!.GetValue<string>());
        Assert.Equal("grey", Quota.Describe(Plan("""{"readable":"","leftPct":40}"""))["band"]!.GetValue<string>());
        Assert.Equal(0, Quota.Describe(Plan("""{"readable":true,"pct":180}"""))["remaining"]!.GetValue<int>());
        Assert.Equal(100, Quota.Describe(Plan("""{"readable":true,"pct":-20}"""))["remaining"]!.GetValue<int>());
    }

    [Fact]
    public void Rounding_uses_the_midpoint_toward_positive_infinity_then_clamps()
    {
        Assert.Equal("green", Quota.Describe(Plan("""{"readable":true,"leftPct":74.5}"""))["band"]!.GetValue<string>());
        Assert.Equal(75, Quota.Describe(Plan("""{"readable":true,"leftPct":74.5}"""))["remaining"]!.GetValue<int>());
        Assert.Equal(74, Quota.Describe(Plan("""{"readable":true,"leftPct":74.4}"""))["remaining"]!.GetValue<int>());
        Assert.Equal(50, Quota.Describe(Plan("""{"readable":true,"leftPct":49.5}"""))["remaining"]!.GetValue<int>());
        Assert.Equal(25, Quota.Describe(Plan("""{"readable":true,"leftPct":24.5}"""))["remaining"]!.GetValue<int>());
        Assert.Equal(24, Quota.Describe(Plan("""{"readable":true,"leftPct":24.4}"""))["remaining"]!.GetValue<int>());
        Assert.Equal("red", Quota.Describe(Plan("""{"readable":true,"leftPct":0.4}"""))["band"]!.GetValue<string>());
        Assert.Equal(1, Quota.Describe(Plan("""{"readable":true,"leftPct":0.5}"""))["remaining"]!.GetValue<int>());
    }

    [Fact]
    public void Html_is_the_band_sentence_and_does_not_copy_other_plan_fields()
    {
        var plan = Plan("""{"readable":true,"leftPct":10,"text":"<script>alert(1)</script>","band":"green"}""");
        Assert.Equal("<span class=\"quota-remaining quota-red\">10% REMAINING</span>", Quota.Html(plan));
        Assert.Equal("<script>alert(1)</script>", plan["text"]!.GetValue<string>());
    }
}
