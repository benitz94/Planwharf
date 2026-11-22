using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

// Ports engine/job31-sheet.test.cjs and engine/job31-edges.test.cjs for the C# helpers.
public class PlainLinesTests
{
    static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();
    static JsonArray Arr(string json) => JsonNode.Parse(json)!.AsArray();

    [Fact]
    public void Flat_and_clip_recover_from_empty_unclosed_and_non_string_input()
    {
        Assert.Equal("", PlainLines.Flat(null));
        Assert.Equal("", PlainLines.Flat(""));
        Assert.Equal("Step 1: Build the weather page item 1 item 2",
            PlainLines.Flat("  ## Step 1: **Build** the `weather` page\n* item 1\n* item 2  "));
        Assert.Equal("before `code", PlainLines.Flat("before ```code"));
        Assert.Equal("", PlainLines.Clip("", 10));
        Assert.Equal("abc", PlainLines.Clip("abc", 3));
        Assert.Equal("…", PlainLines.Clip("hello world", 1));
        var longNoSpace = PlainLines.Clip(new string('A', 100), PlainLines.LineMax);
        Assert.Equal(PlainLines.LineMax, longNoSpace.Length);
        Assert.EndsWith("…", longNoSpace);
        Assert.Equal("A quick brown fox jumps…", PlainLines.Clip("A quick brown fox jumps over the lazy dog", 25));
    }

    [Fact]
    public void FirstSentence_keeps_decimals_and_splits_on_bang_question_and_colon()
    {
        Assert.Equal("Opus is building the weather page.", PlainLines.FirstSentence("Opus is building the weather page. It also adds tests."));
        Assert.Equal("Build the forecast", PlainLines.FirstSentence("Build the forecast: create API and UI components"));
        Assert.Equal("Stop!", PlainLines.FirstSentence("Stop! Then go."));
        Assert.Equal("Ready?", PlainLines.FirstSentence("Ready? Then go."));
        Assert.Equal("Version 1.2 is ready.", PlainLines.FirstSentence("Version 1.2 is ready. Next."));
        Assert.Equal("Build:", PlainLines.FirstSentence("Build:"));
        Assert.Equal("", PlainLines.FirstSentence(null));
        Assert.Equal("First sentence. Second sentence.", PlainLines.Sentences("First sentence. Second sentence. Third sentence is dropped.", 2));
    }

    [Fact]
    public void StepLine_uses_the_lead_then_falls_back_and_ignores_blank_or_non_string_lines()
    {
        Assert.Equal(("Opus is building the weather page.", "lead"),
            PlainLines.StepLine(Obj("""{ "id": "s1", "title": "Implement feature A", "leadLine": "Opus is building the weather page." }""")));
        Assert.Equal(("Opus builds feature A.", "lead"),
            PlainLines.StepLine(Obj("""{ "id": "s1", "title": "Implement feature A", "line": "Opus builds feature A." }""")));
        Assert.Equal(("from lead", "lead"),
            PlainLines.StepLine(Obj("""{ "leadLine": "from lead", "line": "from line", "title": "T" }""")));
        Assert.Equal(("T", "fallback"),
            PlainLines.StepLine(Obj("""{ "title": "T", "leadLine": "   " }""")));
        Assert.Equal(("T", "fallback"),
            PlainLines.StepLine(Obj("""{ "title": "T", "leadLine": 12 }""")));
        Assert.Equal(("Add 7-day forecast page.", "fallback"),
            PlainLines.StepLine(Obj("""{ "id": "s1", "title": "Add 7-day forecast page. It connects to the weather API." }""")));
        Assert.Equal(("Checks the whole work and runs the tests.", "fallback"),
            PlainLines.StepLine(Obj("""{ "id": "s2", "title": "Verify the work independently against the job: check everything." }""")));
        Assert.Equal(("", "none"), PlainLines.StepLine(Obj("""{ "id": "s3", "title": "" }""")));
        Assert.Equal(("", "none"), PlainLines.StepLine(null));
        var longLine = new string('A', 50) + " " + new string('B', 50);
        var clipped = PlainLines.StepLine(Obj($$"""{ "id": "s1", "leadLine": "{{longLine}}" }"""));
        Assert.True(clipped.Line.Length <= PlainLines.LineMax);
        Assert.EndsWith("…", clipped.Line);
    }

    [Fact]
    public void WorkSummary_skips_failed_blocked_and_verify_and_falls_back_when_the_lead_writes_none()
    {
        Assert.Equal(("Built the forecast page and verified weather data. Tests pass.", "lead"),
            PlainLines.WorkSummary(Obj("""{ "id": "m1", "leadSummary": "Built the forecast page and verified weather data. Tests pass." }"""), [], []));

        var mega = Obj("""{ "id": "m1", "title": "Add weather forecasting" }""");
        var subs = Arr("""[{ "id": "s1", "title": "Build weather page" }, { "id": "s2", "title": "Verify the work independently against the job" }]""");
        var results = Arr("""[{ "subTaskId": "s1", "status": "COMPLETE", "summary": "Added 7-day forecast component and unit tests. All pass." }, { "subTaskId": "s2", "status": "COMPLETE", "summary": "Verified everything independently." }]""");
        Assert.Equal(("Added 7-day forecast component and unit tests.", "fallback"), PlainLines.WorkSummary(mega, results, subs));
        Assert.Equal(("Add weather forecasting", "fallback"), PlainLines.WorkSummary(mega, [], subs));
        Assert.Equal(("", "none"), PlainLines.WorkSummary(new JsonObject(), [], []));
        Assert.Equal(("", "none"), PlainLines.WorkSummary(null, null, null));

        Assert.Equal(("Weather page", "fallback"),
            PlainLines.WorkSummary(Obj("""{ "title": "Weather page" }"""),
                Arr("""[{ "subTaskId": "s1", "status": "FAILED", "summary": "Boom. Bad." }]"""),
                Arr("""[{ "id": "s1", "title": "Build" }]""")));
        Assert.Equal(("Weather page", "fallback"),
            PlainLines.WorkSummary(Obj("""{ "title": "Weather page" }"""),
                Arr("""[{ "subTaskId": "s1", "status": "BLOCKED", "summary": "Stuck." }]"""),
                Arr("""[{ "id": "s1", "title": "Build" }]""")));
        Assert.Equal(("Built page.", "fallback"),
            PlainLines.WorkSummary(Obj("""{ "title": "Weather page" }"""),
                Arr("""[{ "subTaskId": "s1", "status": "COMPLETE", "summary": "Built page." }, { "subTaskId": "s2", "status": "COMPLETE", "summary": "Verified independently. Extra." }]"""),
                Arr("""[{ "id": "s1", "title": "Build" }, { "id": "s2", "title": "Verify the work independently against the job" }]""")));
        Assert.Equal(("Ship the weather page.", "fallback"),
            PlainLines.WorkSummary(Obj("""{ "goal": "Ship the weather page. Extra detail." }"""), [], []));
        Assert.Equal(("First sentence. Second sentence.", "lead"),
            PlainLines.WorkSummary(Obj("""{ "leadSummary": "First sentence. Second sentence. Third sentence is dropped." }"""), [], []));
        Assert.Equal(("", "none"),
            PlainLines.WorkSummary(Obj("""{ "leadSummary": "   " }"""), [], []));
        Assert.Equal(("Weather page", "fallback"),
            PlainLines.WorkSummary(Obj("""{ "title": "Weather page", "leadSummary": 12 }"""), [], []));
    }

    [Fact]
    public void StepLine_and_WorkSummary_stay_the_same_when_many_threads_read_at_once()
    {
        var sub = Obj("""{ "id": "s1", "title": "Build weather page", "leadLine": "Opus builds the forecast" }""");
        var mega = Obj("""{ "leadSummary": "Built the forecast page." }""");
        var results = Arr("""[{ "subTaskId": "s1", "status": "COMPLETE", "summary": "Added charts." }]""");
        var subs = Arr("""[{ "id": "s1", "title": "Build weather page", "leadLine": "Opus builds the forecast" }, { "id": "s2", "title": "Verify the work independently against the job" }]""");
        var expectedStep = PlainLines.StepLine(sub);
        var expectedSummary = PlainLines.WorkSummary(mega, results, subs);
        var steps = new (string Line, string From)[32];
        var summaries = new (string Summary, string From)[32];
        Parallel.For(0, 32, i =>
        {
            steps[i] = PlainLines.StepLine(sub);
            summaries[i] = PlainLines.WorkSummary(mega, results, subs);
        });
        Assert.All(steps, got => Assert.Equal(expectedStep, got));
        Assert.All(summaries, got => Assert.Equal(expectedSummary, got));
    }
}
