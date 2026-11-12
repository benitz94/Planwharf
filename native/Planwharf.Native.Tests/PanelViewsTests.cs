using System.Text.Json.Nodes;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public class PanelViewsTests
{
    [Fact]
    public void Progress_lists_steps_and_the_latest_activity_first()
    {
        var snapshot = JsonNode.Parse("""
        { "models": [ { "id": "opus", "label": "Opus 5.5" } ],
          "projects": [ { "id": "p", "next": "Verify the chart labels.", "folder": "/home/sample/site",
            "subTasks": [ { "status": "COMPLETE", "title": "Build the page", "assignedModelId": "opus" }, { "title": "Check it" } ] } ],
          "execution": { "timeline": [
            { "projectId": "p", "createdAt": "2026-11-12T09:05:00Z", "summary": "Work approved." },
            { "projectId": "q", "createdAt": "2026-11-12T09:06:00Z", "summary": "Other project." },
            { "projectId": "p", "createdAt": "2026-11-12T09:20:00Z", "summary": "Build finished." } ] } }
        """)!;
        var panel = PanelViews.Progress(snapshot, "p", "Crew working", TimeZoneInfo.Utc);
        Assert.Equal("Verify the chart labels.", panel.Next);
        Assert.Equal([new ProgressStep("complete", "Build the page", "Opus 5.5"), new ProgressStep("planned", "Check it", "not assigned")], panel.Steps);
        Assert.Equal([("09:20", "Build finished."), ("09:05", "Work approved.")], panel.Recent);
        Assert.Equal("/home/sample/site", panel.Folder);
    }

    [Fact]
    public void Usage_shows_each_limit_and_what_is_not_reported()
    {
        var snapshot = JsonNode.Parse("""
        { "plans": [
            { "id": "max", "label": "Claude Max", "readable": true, "leftPct": 71, "windows": [ { "name": "5-hour", "leftPct": 71.4, "resetAt": "2026-11-12T13:00:00Z" }, { "name": "weekly", "leftPct": 40, "resetAt": "2026-11-15T08:00:00Z" } ] },
            { "id": "plus", "label": "ChatGPT Plus", "readable": true, "leftPct": 38 },
            { "id": "grok", "label": "SuperGrok", "readable": false, "reason": "Grok does not report quota." },
            { "id": "x", "label": "Other", "readable": true } ],
          "aiUsage": { "orchestrator": [ { "model": "Grok 4.7", "calls": 4, "durationMs": 95000, "outputTokens": 5200 } ],
                       "workers": [ { "model": "Opus 5.5", "calls": 2, "durationMs": 540000 } ] } }
        """)!;
        var usage = PanelViews.Usage(snapshot, DateTimeOffset.Parse("2026-11-12T10:00:00Z"), TimeZoneInfo.Utc);
        Assert.Equal([new QuotaWindow("5 hours", 71, "resets 13:00"), new QuotaWindow("Week", 40, "resets Sun 15 Nov 08:00")], usage.Plans[0].Windows);
        Assert.Equal([new QuotaWindow("", 38, "")], usage.Plans[1].Windows);
        Assert.Equal("Grok does not report quota.", usage.Plans[2].Note);
        Assert.Equal("Not reported by this provider", usage.Plans[3].Note);
        Assert.Equal([("Grok 4.7", "4 · 2 min · 5,200 out")], usage.Lead);
        Assert.Equal([("Opus 5.5", "2 · 9 min · not reported")], usage.Crew);
    }

    [Fact]
    public void Files_name_each_change_and_say_why_when_nothing_can_be_listed()
    {
        var repo = JsonNode.Parse("""{ "checkedAt": "2026-11-12T09:30:00Z", "local": { "exists": true, "git": true, "branch": "main", "uncommitted": 5, "changes": [" M src/app.js", "?? notes.md", "D  old.txt", "R  a.js -> b.js"] } }""");
        var files = PanelViews.Files(repo, "/home/sample/site", null, TimeZoneInfo.Utc);
        Assert.Null(files.Message);
        Assert.Equal("main", files.Branch);
        Assert.Equal([new ChangedFile("changed", "src/app.js"), new ChangedFile("new", "notes.md"), new ChangedFile("deleted", "old.txt"), new ChangedFile("renamed", "a.js -> b.js")], files.Files);
        Assert.Equal(5, files.Total);
        Assert.Equal("09:30", files.CheckedAt);
        Assert.Equal("This project has no folder yet. Set it in Settings.", PanelViews.Files(null, "", null).Message);
        Assert.Equal("This folder is not tracked by Git, so Planwharf cannot list what changed.", PanelViews.Files(JsonNode.Parse("""{ "local": { "exists": true, "git": false } }"""), "/x", null).Message);
        Assert.Equal("The folder /x does not exist.", PanelViews.Files(JsonNode.Parse("""{ "local": { "exists": false } }"""), "/x", null).Message);
    }
}
