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
        Assert.Equal(Path.Combine("/home/sample/site", ".planwharf", "plan.md"), panel.WorkPlan);
        Assert.Equal("", panel.TimeLeft);
    }

    [Fact]
    public void Progress_shows_the_time_left_only_while_a_step_is_running_or_waiting()
    {
        var snapshot = JsonNode.Parse("""
        { "projects": [ { "id": "p", "subTasks": [ { "status": "RUNNING", "title": "Build the page" }, { "status": "PENDING", "title": "Check it" } ],
            "timeLeft": { "minutes": 25, "endsAt": "2026-11-12T17:40:00Z", "sure": "rough" } } ] }
        """)!;
        Assert.Equal("About 25 minutes left (ends around 17:40) · rough", PanelViews.Progress(snapshot, "p", "Crew working", TimeZoneInfo.Utc).TimeLeft);
        snapshot["projects"]![0]!["subTasks"]![0]!["status"] = "COMPLETE";
        snapshot["projects"]![0]!["subTasks"]![1]!["status"] = "COMPLETE";
        Assert.Equal("", PanelViews.Progress(snapshot, "p", "Done", TimeZoneInfo.Utc).TimeLeft);
    }

    [Fact]
    public void ChatViews_TimeLeft_formats_edge_cases_cleanly()
    {
        // 1 minute remaining
        var oneMinute = JsonNode.Parse("""{ "minutes": 1, "endsAt": "2026-11-12T17:40:00Z", "sure": "rough" }""");
        Assert.Equal("About 1 minute left (ends around 17:40) · rough", ChatViews.TimeLeft(oneMinute, TimeZoneInfo.Utc));

        // Longer than usual with 1 minute remaining
        var longerOne = JsonNode.Parse("""{ "minutes": 1, "endsAt": "2026-11-12T17:40:00Z", "sure": "rough", "longerThanUsual": true }""");
        Assert.Equal("Longer than usual · about 1 minute left (ends around 17:40) · rough", ChatViews.TimeLeft(longerOne, TimeZoneInfo.Utc));

        // Missing endsAt
        var noEndsAt = JsonNode.Parse("""{ "minutes": 25, "sure": "rough" }""");
        Assert.Equal("About 25 minutes left · rough", ChatViews.TimeLeft(noEndsAt, TimeZoneInfo.Utc));

        // Missing sure
        var noSure = JsonNode.Parse("""{ "minutes": 25, "endsAt": "2026-11-12T17:40:00Z" }""");
        Assert.Equal("About 25 minutes left (ends around 17:40)", ChatViews.TimeLeft(noSure, TimeZoneInfo.Utc));

        // String minutes and float minutes
        var stringMinutes = JsonNode.Parse("""{ "minutes": "25", "endsAt": "2026-11-12T17:40:00Z", "sure": "rough" }""");
        Assert.Equal("About 25 minutes left (ends around 17:40) · rough", ChatViews.TimeLeft(stringMinutes, TimeZoneInfo.Utc));

        var floatMinutes = JsonNode.Parse("""{ "minutes": 24.8, "endsAt": "2026-11-12T17:40:00Z", "sure": "rough" }""");
        Assert.Equal("About 25 minutes left (ends around 17:40) · rough", ChatViews.TimeLeft(floatMinutes, TimeZoneInfo.Utc));

        // Negative minutes or null node return empty string
        var negative = JsonNode.Parse("""{ "minutes": -5 }""");
        Assert.Equal("", ChatViews.TimeLeft(negative, TimeZoneInfo.Utc));
        Assert.Equal("", ChatViews.TimeLeft(null, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Progress_handles_quota_wait_and_missing_subtasks()
    {
        // Waiting for quota step keeps progress time-left visible
        var quotaWait = JsonNode.Parse("""
        { "projects": [ { "id": "p", "subTasks": [ { "status": "PENDING", "waitingFor": "Waiting for quota: Claude 0%" } ],
            "timeLeft": { "minutes": 15, "endsAt": "2026-11-12T17:30:00Z", "sure": "rough" } } ] }
        """)!;
        Assert.Equal("About 15 minutes left (ends around 17:30) · rough", PanelViews.Progress(quotaWait, "p", "Waiting", TimeZoneInfo.Utc).TimeLeft);

        // Missing subTasks / megaTask does not throw and returns empty TimeLeft
        var emptyProject = JsonNode.Parse("""{ "projects": [ { "id": "p", "timeLeft": { "minutes": 15 } } ] }""")!;
        Assert.Equal("", PanelViews.Progress(emptyProject, "p", "Idle", TimeZoneInfo.Utc).TimeLeft);
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
    public void Usage_shows_step_outcomes_summary()
    {
        var snapshot = JsonNode.Parse("""
        {
          "plans": [],
          "aiUsage": {
            "orchestrator": [],
            "workers": [],
            "stepOutcomes": [
              { "model": "Sonnet 4.6", "steps": 5, "accepted": 4, "fixed": 1 },
              { "model": "Opus 4.6", "steps": 2, "accepted": 2, "fixed": 0 }
            ]
          }
        }
        """)!;
        var usage = PanelViews.Usage(snapshot);
        Assert.Equal(2, usage.Outcomes.Count);
        Assert.Equal(("Sonnet 4.6", "5 steps · 4 accepted · 1 fixed by Verify"), usage.Outcomes[0]);
        Assert.Equal(("Opus 4.6", "2 steps · 2 accepted · 0 fixed by Verify"), usage.Outcomes[1]);
    }

    [Fact]
    public void Usage_handles_outcomes_edge_cases_and_missing_arrays()
    {
        // 1. Missing stepOutcomes and aiUsage entirely
        var emptySnapshot = JsonNode.Parse("""{ "plans": [] }""")!;
        var emptyUsage = PanelViews.Usage(emptySnapshot);
        Assert.Empty(emptyUsage.Outcomes);

        // 2. Top-level stepOutcomes with partial / missing fields
        var topLevelSnapshot = JsonNode.Parse("""
        {
          "plans": [],
          "stepOutcomes": [
            { "model": "Grok 4.7" },
            { "model": "Sol", "steps": 3 }
          ]
        }
        """)!;
        var topUsage = PanelViews.Usage(topLevelSnapshot);
        Assert.Equal(2, topUsage.Outcomes.Count);
        Assert.Equal(("Grok 4.7", "0 steps · 0 accepted · 0 fixed by Verify"), topUsage.Outcomes[0]);
        Assert.Equal(("Sol", "3 steps · 0 accepted · 0 fixed by Verify"), topUsage.Outcomes[1]);
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

    static JsonNode Pulls() => JsonNode.Parse("""
    { "ok": true, "state": "open", "author": "all", "more": true,
      "summary": { "merged": 4, "medianMergeMs": 172800000, "neededFix": 1, "failedChecks": [ { "name": "build", "count": 3 }, { "name": "test", "count": 1 } ] },
      "items": [
        { "number": 12, "title": "Add the chart", "branch": "feat/chart", "url": "https://github.com/acme/app/pull/12", "state": "open",
          "openedAt": "2026-11-20T09:00:00Z", "updatedAt": "2026-11-21T08:00:00Z", "conflicts": true, "byPlanwharf": true, "canFix": true,
          "checks": [ { "name": "build", "state": "red" }, { "name": "lint", "state": "running" }, { "name": "odd", "state": "purple" } ],
          "work": { "goal": "Add a 7-day chart", "startedAt": "2026-11-20T09:00:00Z", "completedAt": "2026-11-20T11:00:00Z", "lead": "Grok 4.7", "steps": [ { "title": "Draw the chart", "model": "Opus 5.5" } ] } },
        { "number": 8, "title": "Fix the legend", "branch": "feat/legend", "url": "https://github.com/acme/app/pull/8", "state": "merged",
          "openedAt": "2026-11-18T09:00:00Z", "mergedAt": "2026-11-19T09:00:00Z", "updatedAt": "2026-11-19T09:00:00Z", "durationMs": 86400000,
          "byPlanwharf": true, "fixStatus": "fixed", "fixText": "Conflicts resolved.", "checks": [ { "name": "build", "state": "green" } ] },
        { "number": 3, "title": "Old notes", "branch": "feat/notes", "url": "https://github.com/acme/app/pull/3", "state": "closed",
          "openedAt": "2026-11-01T09:00:00Z", "closedAt": "2026-11-02T09:00:00Z", "updatedAt": "2026-11-02T09:00:00Z", "byPlanwharf": false } ] }
    """)!;

    [Fact]
    public void Pull_requests_keep_open_merged_and_closed_and_the_filters_pick_them()
    {
        var panel = PanelViews.PullRequests(Pulls(), TimeZoneInfo.Utc);
        Assert.Null(panel.Message);
        Assert.True(panel.More);
        Assert.Equal(4, panel.Summary.Merged);
        Assert.Equal("2 d", panel.Summary.Median);
        Assert.Equal(1, panel.Summary.NeededFix);
        Assert.Equal([new FailedCheck("build", 3), new FailedCheck("test", 1)], panel.Summary.FailedChecks);
        var open = Assert.Single(PanelViews.Matching(panel.Items, "open", "all"));
        Assert.Equal(("#12", "Add the chart", "feat/chart", true, true), ( "#" + open.Number, open.Title, open.Branch, open.Conflicts, open.CanFix));
        Assert.Equal([new PullCheck("build", "red"), new PullCheck("lint", "running"), new PullCheck("odd", "running")], open.Checks);
        Assert.Equal("Add a 7-day chart", open.Work!.Goal);
        Assert.Equal("Grok 4.7", open.Work.Lead);
        Assert.Equal(new PullStep("Draw the chart", "Opus 5.5"), Assert.Single(open.Work.Steps));
        var merged = Assert.Single(PanelViews.Matching(panel.Items, "merged", "planwharf"));
        Assert.Equal(("fixed", "24 h", "Conflicts resolved."), (merged.FixStatus, merged.Duration, merged.FixText));
        Assert.Equal(3, Assert.Single(PanelViews.Matching(panel.Items, "closed", "others")).Number);
        Assert.Empty(PanelViews.Matching(panel.Items, "open", "others"));
        Assert.Empty(PanelViews.Matching(panel.Items, "closed", "planwharf"));
    }

    [Fact]
    public void Pull_requests_say_what_to_install_when_gh_is_missing_and_keep_a_running_fix()
    {
        Assert.Equal("Checking pull requests…", PanelViews.PullRequests(null).Message);
        var missing = PanelViews.PullRequests(JsonNode.Parse("""{ "ok": false, "missing": true }""")!);
        Assert.True(missing.Missing);
        Assert.StartsWith("GitHub CLI (gh) is not installed.", missing.Message);
        Assert.Empty(missing.Items);
        var denied = PanelViews.PullRequests(JsonNode.Parse("""{ "ok": false, "reason": "gh auth login" }""")!);
        Assert.False(denied.Missing);
        Assert.Equal("gh auth login", denied.Message);
        var running = JsonNode.Parse("""{ "ok": true, "items": [ { "number": 12, "title": "Add the chart", "state": "open", "fixStatus": "running", "fixText": "Reading the failing check", "canFix": false, "conflicts": true } ] }""")!;
        var row = Assert.Single(PanelViews.PullRequests(running, TimeZoneInfo.Utc).Items);
        Assert.Equal(("running", "Reading the failing check", false), (row.FixStatus, row.FixText, row.CanFix));
        Assert.Null(row.Progress);
        var started = DateTimeOffset.UtcNow.AddMinutes(-4).ToString("o");
        var live = JsonNode.Parse("{ \"ok\": true, \"items\": [ { \"number\": 12, \"title\": \"Add the chart\", \"state\": \"open\", \"fixStatus\": \"running\", \"canFix\": false, \"fixProgress\": { \"model\": \"Opus 5.5\", \"stage\": \"Working on the fix.\", \"activity\": \"Running npm test\", \"startedAt\": \"" + started + "\" } } ] }")!;
        var progress = Assert.Single(PanelViews.PullRequests(live, TimeZoneInfo.Utc).Items).Progress!;
        Assert.Equal(("Opus 5.5", "4 min", "Working on the fix.", "Running npm test"), (progress.Model, progress.Elapsed, progress.Stage, progress.Activity));
        Assert.Equal("under a minute", PanelViews.Span(1000));
        Assert.Equal("—", PanelViews.Span(null));
        Assert.Equal("—", PanelViews.Span(-1));
    }

    [Fact]
    public void Pull_filters_keep_the_open_list_when_the_choice_is_not_one_of_the_chips()
    {
        var panel = PanelViews.PullRequests(Pulls(), TimeZoneInfo.Utc);
        Assert.Equal(12, Assert.Single(PanelViews.Matching(panel.Items, "open", "anyone")).Number);
        Assert.Equal(12, Assert.Single(PanelViews.Matching(panel.Items, "nope", "all")).Number);
        var missing = PanelViews.PullRequests(JsonNode.Parse("""{ "ok": false }""")!);
        Assert.Equal("Could not read pull requests.", missing.Message);
        Assert.Empty(missing.Items);
    }
}
