using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;

namespace Planwharf.Native.Tests;

public class PullPanelWindowTests
{
    static readonly string Payload = """
    { "ok": true, "state": "open", "author": "all", "more": true,
      "summary": { "merged": 4, "medianMergeMs": 172800000, "neededFix": 1, "failedChecks": [ { "name": "build", "count": 3 } ] },
      "items": [
        { "number": 12, "title": "Add the chart", "branch": "feat/chart", "url": "https://github.com/acme/app/pull/12", "state": "open",
          "openedAt": "2026-11-20T09:00:00Z", "updatedAt": "2026-11-21T08:00:00Z", "conflicts": true, "byPlanwharf": true, "canFix": true,
          "checks": [ { "name": "build", "state": "red" }, { "name": "lint", "state": "green" } ],
          "work": { "goal": "Add a 7-day chart", "lead": "Grok 4.7", "steps": [ { "title": "Draw the chart", "model": "Opus 5.5" } ] } },
        { "number": 8, "title": "Fix the legend", "branch": "feat/legend", "url": "https://github.com/acme/app/pull/8", "state": "merged",
          "openedAt": "2026-11-18T09:00:00Z", "mergedAt": "2026-11-19T09:00:00Z", "durationMs": 86400000, "byPlanwharf": true,
          "fixStatus": "fixed", "fixText": "Conflicts resolved.", "checks": [ { "name": "build", "state": "green" } ] },
        { "number": 3, "title": "Old notes", "branch": "feat/notes", "state": "closed", "openedAt": "2026-11-01T09:00:00Z",
          "closedAt": "2026-11-02T09:00:00Z", "byPlanwharf": false } ] }
    """;

    static bool Has(MainWindow window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == text || (block.Text ?? "").Contains(text, StringComparison.Ordinal));

    static void Click(MainWindow window, string label)
    {
        var button = window.GetVisualDescendants().OfType<Button>().First(item =>
            item.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == label));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void The_panel_draws_open_merged_and_closed_a_running_fix_and_the_filters()
    {
        var window = new MainWindow("test") { Height = 1400, Width = 1200 };
        window.Show();
        try
        {
            window.Show(SampleState.Working());
            window.OpenSide("pulls");
            window.ShowPullRequests(JsonNode.Parse(Payload)!);
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Pull requests");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button =>
                button.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "Check PRs"));
            Assert.True(Has(window, "#12 Add the chart"));
            Assert.True(Has(window, "feat/chart"));
            Assert.True(Has(window, "build · Red"));
            Assert.True(Has(window, "lint · Green"));
            Assert.True(Has(window, "Conflicts"));
            Assert.True(Has(window, "Fix in the background"));
            Assert.True(Has(window, "Open on GitHub"));
            Assert.True(Has(window, "The work behind this pull request"));
            Assert.True(Has(window, "Add a 7-day chart"));
            Assert.True(Has(window, "Show more"));
            Assert.True(Has(window, "Last 30 days"));
            Assert.True(Has(window, "build (3)"));
            Assert.False(Has(window, "Fix the legend"));
            Assert.False(Has(window, "Old notes"));

            Click(window, "Merged");
            Assert.True(Has(window, "#8 Fix the legend"));
            Assert.True(Has(window, "Planwharf fixed this in the background."));
            Assert.False(Has(window, "Add the chart"));
            Assert.False(Has(window, "Fix in the background"));

            Click(window, "Closed");
            Assert.True(Has(window, "#3 Old notes"));
            Assert.False(Has(window, "Fix the legend"));

            Click(window, "Opened by Planwharf");
            Assert.True(Has(window, "No closed pull requests."));
            Click(window, "Opened by others");
            Assert.True(Has(window, "#3 Old notes"));
            Click(window, "Open");
            Click(window, "All");
            Assert.True(Has(window, "#12 Add the chart"));

            var running = JsonNode.Parse(Payload)!;
            var open = running["items"]![0]!.AsObject();
            open["fixStatus"] = "running";
            open["fixText"] = "Reading the failing check";
            open["canFix"] = false;
            window.ShowPullRequests(running);
            window.UpdateLayout();
            Assert.True(Has(window, "Fixing in the background"));
            Assert.True(Has(window, "Reading the failing check"));
            Assert.False(Has(window, "Fix in the background"));

            // While it runs the card says who, for how long, at which stage and what it did last.
            open["fixProgress"] = JsonNode.Parse("{ \"model\": \"Opus 5.5\", \"stage\": \"Working on the fix.\", \"activity\": \"Running npm test\", \"startedAt\": \"" + DateTimeOffset.UtcNow.AddMinutes(-4).ToString("o") + "\" }");
            window.ShowPullRequests(running);
            window.UpdateLayout();
            Assert.True(Has(window, "Fixing in the background · Opus 5.5 · 4 min"));
            Assert.True(Has(window, "Working on the fix."));
            Assert.True(Has(window, "Now: Running npm test"));

            window.ShowPullRequests(JsonNode.Parse("""{ "ok": false, "missing": true }""")!);
            window.UpdateLayout();
            Assert.True(Has(window, "GitHub CLI (gh) is not installed."));
            Assert.False(Has(window, "Add the chart"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void A_failed_fix_is_shown_and_a_bad_read_replaces_the_list()
    {
        var window = new MainWindow("test") { Height = 1000, Width = 1200 };
        window.Show();
        try
        {
            window.Show(SampleState.Working());
            window.OpenSide("pulls");
            window.ShowPullRequests(JsonNode.Parse("""
            { "ok": true, "state": "open", "author": "all", "more": false,
              "summary": { "merged": 0, "neededFix": 0, "failedChecks": [] },
              "items": [ { "number": 4, "title": "Broken chart", "branch": "feat/broken", "state": "open", "conflicts": true, "canFix": true, "fixStatus": "failed", "fixText": "" } ] }
            """)!);
            window.UpdateLayout();
            Assert.True(Has(window, "#4 Broken chart"));
            Assert.True(Has(window, "The fix failed."));
            Assert.True(Has(window, "Fix in the background"));
            Assert.True(Has(window, "No check failed often in the last 30 days."));

            window.ShowPullRequests(JsonNode.Parse("""{ "ok": true, "items": [], "summary": { "merged": 0, "failedChecks": [] } }""")!);
            window.UpdateLayout();
            Assert.True(Has(window, "No open pull requests."));
            Assert.False(Has(window, "Broken chart"));

            window.ShowPullRequests(JsonNode.Parse("""{ "ok": false, "reason": "API rate limit exceeded" }""")!);
            window.UpdateLayout();
            Assert.True(Has(window, "API rate limit exceeded"));
            Assert.False(Has(window, "Fix in the background"));
        }
        finally { window.Close(); }
    }
}
