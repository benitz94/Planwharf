// Ports the checks of engine/autonomy-crew.test.cjs that use provider-refusal.cjs alone
// ("refusals are read from the end of an output, and expire"), plus edge cases checked against the
// same JavaScript: \b next to letters, \s on non-breaking spaces, ASCII-only case folding, the
// typographic apostrophe, the 4000-character tail and the 240-character reason.
//
// Deferred: none. The other cases of autonomy-crew.test.cjs need the orchestrator, the server,
// atomic-rename or project-checks, and are not part of this module.
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ProviderRefusalTests
{
    static double Ms(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    [Fact]
    public void Refusals_are_read_from_the_end_of_an_output_and_expire()
    {
        Assert.Matches("requires usage credits", ProviderRefusal.RefusalReason("work...\nFable 5.1 requires usage credits. Switch to another model."));
        Assert.Equal("Fable 5.1 requires usage credits. Switch to another model.", ProviderRefusal.RefusalReason("work...\nFable 5.1 requires usage credits. Switch to another model."));
        Assert.Matches("5-hour limit", ProviderRefusal.RefusalReason("{\"result\":\"You've hit your 5-hour limit \u00b7 resets 3am (Europe/Rome)\"}"));
        Assert.Null(ProviderRefusal.RefusalReason("All 42 tests passed."));
        Assert.Null(ProviderRefusal.RefusalReason("requires usage credits" + string.Concat(Enumerable.Repeat(" x", 5000))));
        var store = new JsonObject();
        ProviderRefusal.RecordRefusal(store, "m", "credits", "2026-11-04T10:00:00.000Z", 1000);
        Assert.NotNull(ProviderRefusal.ActiveRefusal(store, "m", Ms("2026-11-04T10:00:00.500Z")));
        Assert.Null(ProviderRefusal.ActiveRefusal(store, "m", Ms("2026-11-04T10:00:02.000Z")));
        Assert.Equal("{}", store["refusals"]!.ToJsonString());
    }

    [Fact]
    public void The_cooldown_is_one_hour()
    {
        Assert.Equal(3600000, ProviderRefusal.COOLDOWN_MS);
        var store = new JsonObject { ["other"] = 1 };
        var row = ProviderRefusal.RecordRefusal(store, "m", new string('r', 300), "2026-11-04T10:00:00.000Z");
        Assert.Equal("{\"other\":1,\"refusals\":{\"m\":{\"reason\":\"" + new string('r', 240) + "\",\"at\":\"2026-11-04T10:00:00.000Z\",\"until\":\"2026-11-04T11:00:00.000Z\"}}}", store.ToJsonString());
        Assert.Same(row, store["refusals"]!["m"]);
    }

    [Fact]
    public void A_refusal_is_active_until_its_end_and_removed_after()
    {
        var store = new JsonObject();
        ProviderRefusal.RecordRefusal(store, "a", "quota", "2026-11-04T10:00:00Z", 1000);
        ProviderRefusal.RecordRefusal(store, "b", "quota", "2026-11-04T10:00:00Z", 5000);
        // until is exclusive: at exactly the end the refusal is over.
        Assert.Null(ProviderRefusal.ActiveRefusal(store, "a", Ms("2026-11-04T10:00:01Z")));
        Assert.NotNull(ProviderRefusal.ActiveRefusal(store, "b", Ms("2026-11-04T10:00:01Z")));
        Assert.Equal(["b"], store["refusals"]!.AsObject().Select(pair => pair.Key));
        // A future refusal stays, an unknown model or an empty store is not refused.
        Assert.Null(ProviderRefusal.ActiveRefusal(store, "missing", 0));
        Assert.Null(ProviderRefusal.ActiveRefusal(new JsonObject(), "b", 0));
        Assert.Null(ProviderRefusal.ActiveRefusal(null, "b", 0));
    }

    [Fact]
    public void An_unreadable_end_date_is_expired_and_removed()
    {
        var store = new JsonObject { ["refusals"] = new JsonObject { ["m"] = new JsonObject { ["reason"] = "x", ["until"] = "never" }, ["n"] = new JsonObject { ["reason"] = "x" } } };
        Assert.Null(ProviderRefusal.ActiveRefusal(store, "m", 0));
        Assert.Null(ProviderRefusal.ActiveRefusal(store, "n", 0));
        Assert.Equal("{}", store["refusals"]!.ToJsonString());
    }

    [Fact]
    public void An_invalid_time_fails_as_in_JavaScript()
    {
        var store = new JsonObject();
        var error = Assert.Throws<InvalidOperationException>(() => ProviderRefusal.RecordRefusal(store, "m", "x", "not a date"));
        Assert.Equal("Invalid time value", error.Message);
        // As in JavaScript, the empty map is created before the date is read.
        Assert.Equal("{\"refusals\":{}}", store.ToJsonString());
    }

    [Theory]
    [InlineData("x429 too many requests", null)]
    [InlineData("\u00e9429 rate limited", "\u00e9429 rate limited")]
    [InlineData("HTTP 429: Too Many Requests", "HTTP 429: Too Many Requests")]
    [InlineData("429x rate", null)]
    [InlineData("YOU\u2019VE HIT YOUR weekly LIMIT", "YOU\u2019VE HIT YOUR weekly LIMIT")]
    [InlineData("you\u2019ve hit your limit.", "you\u2019ve hit your limit.")]
    [InlineData("  line one\n\tRate  Limit\u00a0Exceeded  now \n", null)]
    [InlineData("\tRate limit exceeded\u00a0\u2003now\u3000", "Rate limit exceeded now")]
    [InlineData("requires usage credits\n\n\n", "requires usage credits")]
    [InlineData("require\u017f usage credits", null)]
    [InlineData("\u212Aout of credits", "\u212Aout of credits")]
    [InlineData("resource_exhausted", "resource_exhausted")]
    [InlineData("RESOURCE EXHAUSTED", "RESOURCE EXHAUSTED")]
    [InlineData("insufficient balance", "insufficient balance")]
    [InlineData("credit depleted", "credit depleted")]
    [InlineData("usage limit reached", "usage limit reached")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Patterns_match_as_in_JavaScript(string? text, string? expected) => Assert.Equal(expected, ProviderRefusal.RefusalReason(text));

    [Fact]
    public void Only_the_last_4000_characters_are_read_and_the_reason_is_clipped()
    {
        Assert.Equal("out of credits", ProviderRefusal.RefusalReason(new string('x', 5000) + "\nout of credits"));
        Assert.Null(ProviderRefusal.RefusalReason("out of credits" + new string('y', 3990)));
        Assert.Equal(new string('z', 240), ProviderRefusal.RefusalReason(new string('z', 250) + " quota exceeded"));
    }
}
