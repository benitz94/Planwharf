using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Planwharf.Engine;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public class ClearerScreensTests
{
    static double RelativeLuminance(Avalonia.Media.Color color)
    {
        double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    static double ContrastRatio(Avalonia.Media.Color foreground, Avalonia.Media.Color background)
    {
        var l1 = RelativeLuminance(foreground);
        var l2 = RelativeLuminance(background);
        var lighter = Math.Max(l1, l2);
        var darker = Math.Min(l1, l2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    [Fact]
    public void Repeated_events_are_joined_in_chat_views()
    {
        var snapshot = JsonNode.Parse("""
        {
          "asks": [],
          "execution": {
            "timeline": [
              { "projectId": "p", "type": "SUBTASK_COMPLETED", "createdAt": "2026-11-22T10:00:00Z", "summary": "Add the chart: Finished Add the chart." },
              { "projectId": "p", "type": "SUBTASK_COMPLETED", "createdAt": "2026-11-22T10:01:00Z", "summary": "Add the chart: Finished Add the chart." },
              { "projectId": "p", "type": "SUBTASK_COMPLETED", "createdAt": "2026-11-22T10:02:00Z", "summary": "Add the chart: Finished Add the chart." }
            ]
          }
        }
        """)!;
        var thread = ChatViews.Thread(snapshot, "p", _ => "");
        Assert.Single(thread);
        var ev = Assert.IsType<EventItem>(thread[0]);
        Assert.Equal(3, ev.Count);
        Assert.Equal("Step done: Add the chart", ev.Text);
    }

    [Fact]
    public void WorkStep_preserves_plain_line_and_worker_instruction()
    {
        var snapshot = JsonNode.Parse("""
        {
          "projects": [
            {
              "id": "p", "name": "Test Project",
              "megaTask": { "id": "m1", "title": "Weather feature", "status": "RUNNING" },
              "subTasks": [
                {
                  "id": "s1", "title": "Build forecast component with API",
                  "detail": "Long instructions for the worker: connect to weather endpoint, add error handling.",
                  "plainLine": "Opus builds the forecast page.",
                  "assignedModelId": "opus",
                  "status": "IN_PROGRESS"
                }
              ]
            }
          ]
        }
        """)!;
        var view = ProjectViews.Project(snapshot, "p")!;
        Assert.NotNull(view.Work);
        Assert.Single(view.Work.Steps);
        var step = view.Work.Steps[0];
        Assert.Equal("Opus builds the forecast page.", step.PlainLine);
        Assert.StartsWith("Long instructions for the worker", step.Instruction);
    }

    [AvaloniaFact]
    public void Work_finished_shows_three_lines_first_and_details_closed()
    {
        var window = new MainWindow("v0.2.24");
        window.Show();
        try
        {
            window.Show(SampleState.Finished());
            window.UpdateLayout();

            // Find the fold for Steps and results:
            var folds = window.GetVisualDescendants()
                .Where(c => c.GetType().Name == "FoldBox")
                .ToList();

            var stepsFold = folds.FirstOrDefault(f =>
            {
                var textBlocks = f.GetVisualDescendants().OfType<TextBlock>().ToList();
                return textBlocks.Any(tb => tb.Text == Strings.T("Steps and results"));
            });

            Assert.NotNull(stepsFold);
            var prop = stepsFold.GetType().GetProperty("IsExpanded");
            Assert.NotNull(prop);
            Assert.False((bool)prop.GetValue(stepsFold)!);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Settings_shows_advanced_section_closed_by_default()
    {
        var window = new MainWindow("v0.2.24");
        window.Show();
        try
        {
            window.Show(SampleState.Working());
            window.OpenSide("settings");
            window.UpdateLayout();

            var folds = window.GetVisualDescendants()
                .Where(c => c.GetType().Name == "FoldBox")
                .ToList();

            var advFold = folds.FirstOrDefault(f =>
            {
                var textBlocks = f.GetVisualDescendants().OfType<TextBlock>().ToList();
                return textBlocks.Any(tb => tb.Text == Strings.T("Advanced settings"));
            });

            Assert.NotNull(advFold);
            var prop = advFold.GetType().GetProperty("IsExpanded");
            Assert.NotNull(prop);
            Assert.False((bool)prop.GetValue(advFold)!);
        }
        finally { window.Close(); }
    }

    [Theory]
    [InlineData(true)]  // Dark theme
    [InlineData(false)] // Light theme
    public void Theme_contrast_meets_accessibility_minimums(bool dark)
    {
        Palette.Apply(dark, null);
        var text = Palette.ColorOf(Palette.Text);
        var muted = Palette.ColorOf(Palette.Muted);
        var card = Palette.ColorOf(Palette.Card);
        var bg = Palette.ColorOf(Palette.Background);

        // Body text against card and background >= 4.5:1
        Assert.True(ContrastRatio(text, card) >= 4.5, $"Text against Card contrast ratio in {(dark ? "dark" : "light")} theme must be >= 4.5");
        Assert.True(ContrastRatio(text, bg) >= 4.5, $"Text against Background contrast ratio in {(dark ? "dark" : "light")} theme must be >= 4.5");

        // Secondary / muted text contrast >= 4.5:1
        Assert.True(ContrastRatio(muted, card) >= 4.5, $"Muted against Card contrast ratio in {(dark ? "dark" : "light")} theme must be >= 4.5");
        Assert.True(ContrastRatio(muted, bg) >= 4.5, $"Muted against Background contrast ratio in {(dark ? "dark" : "light")} theme must be >= 4.5");
    }

    [Fact]
    public void Sample_state_contains_only_sample_paths_no_real_user_paths()
    {
        var finished = SampleState.Finished().ToJsonString();
        var working = SampleState.Working().ToJsonString();

        foreach (var json in new[] { finished, working })
        {
            Assert.DoesNotContain("D:\\benit", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\Users\\benit", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@users.noreply.github.com", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("github.com/benitz94", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Repeated_events_join_only_when_they_are_neighbours_of_the_same_kind()
    {
        var snapshot = JsonNode.Parse("""
        {
          "asks": [],
          "execution": {
            "timeline": [
              { "projectId": "p", "type": "SUBTASK_COMPLETED", "createdAt": "2026-11-22T10:00:00Z", "summary": "Add the chart: Finished Add the chart." },
              { "projectId": "p", "type": "SUBTASK_STARTED", "createdAt": "2026-11-22T10:01:00Z", "summary": "Add the chart" },
              { "projectId": "p", "type": "SUBTASK_COMPLETED", "createdAt": "2026-11-22T10:02:00Z", "summary": "Add the chart: Finished Add the chart." },
              { "projectId": "q", "type": "SUBTASK_COMPLETED", "createdAt": "2026-11-22T10:03:00Z", "summary": "Add the chart: Finished Add the chart." },
              { "projectId": "p", "type": "WORKER_HEARTBEAT", "createdAt": "2026-11-22T10:04:00Z", "summary": "tick" },
              { "projectId": "p", "type": "SUBTASK_FAILED", "createdAt": "2026-11-22T10:05:00Z", "summary": "" },
              { "projectId": "p", "type": "SUBTASK_FAILED", "createdAt": "2026-11-22T10:05:30Z", "summary": "Build stopped." },
              { "projectId": "p", "type": "SUBTASK_COMPLETED", "createdAt": "2026-11-22T10:06:00Z", "summary": "Add the chart: Finished Add the chart." },
              { "projectId": "p", "type": "SUBTASK_COMPLETED", "createdAt": "2026-11-22T10:07:00Z", "summary": "Add the chart: Finished Add the chart." }
            ]
          }
        }
        """)!;
        var thread = ChatViews.Thread(snapshot, "p", _ => "");
        // Heartbeats, other projects and empty text are dropped and do not split a run.
        // A visible different event does.
        Assert.Equal(5, thread.Count);
        Assert.Equal(("Step done: Add the chart", 1), (Assert.IsType<EventItem>(thread[0]).Text, Assert.IsType<EventItem>(thread[0]).Count));
        Assert.Equal("start", Assert.IsType<EventItem>(thread[1]).Kind);
        Assert.Equal(("Step done: Add the chart", 1), (Assert.IsType<EventItem>(thread[2]).Text, Assert.IsType<EventItem>(thread[2]).Count));
        Assert.Equal(("bad", "Build stopped."), (Assert.IsType<EventItem>(thread[3]).Kind, Assert.IsType<EventItem>(thread[3]).Text));
        Assert.Equal(("Step done: Add the chart", 2), (Assert.IsType<EventItem>(thread[4]).Text, Assert.IsType<EventItem>(thread[4]).Count));
    }

    [Fact]
    public void Long_event_text_is_cut_and_a_broken_time_still_shows_the_event()
    {
        var longSummary = "Add the chart: Finished Add the chart. " + new string('x', 300);
        var snapshot = new JsonObject
        {
            ["asks"] = new JsonArray(),
            ["execution"] = new JsonObject
            {
                ["timeline"] = new JsonArray(
                    new JsonObject { ["projectId"] = "p", ["type"] = "SUBTASK_COMPLETED", ["createdAt"] = "not-a-time", ["summary"] = longSummary },
                    new JsonObject { ["projectId"] = "p", ["type"] = "SUBTASK_COMPLETED", ["createdAt"] = "also-bad", ["summary"] = longSummary }
                )
            }
        };
        var thread = ChatViews.Thread(snapshot, "p", _ => "");
        Assert.Single(thread);
        var ev = Assert.IsType<EventItem>(thread[0]);
        Assert.Equal(2, ev.Count);
        Assert.Equal(DateTimeOffset.MinValue, ev.At);
        Assert.True(ev.Text.Length <= 260);
        Assert.EndsWith("…", ev.Text);
    }

    [Fact]
    public void Work_fallback_skips_verify_and_survives_missing_or_wrong_typed_fields()
    {
        var snapshot = JsonNode.Parse("""
        {
          "projects": [
            {
              "id": "p", "name": "Test",
              "megaTask": { "id": "m1", "title": "Weather page", "status": "COMPLETE" },
              "subTasks": [
                { "id": "s1", "title": "Build the forecast. Extra.", "detail": "Long worker instructions.", "status": "COMPLETE", "assignedModelId": "opus" },
                { "id": "s2", "title": "Verify the work independently against the job", "status": "COMPLETE", "assignedModelId": "astra" }
              ],
              "results": [
                { "subTaskId": "s1", "status": "COMPLETE", "summary": "Built the page. Extra." },
                { "subTaskId": "s2", "status": "COMPLETE", "summary": "Verified independently." }
              ]
            }
          ]
        }
        """)!;
        var view = ProjectViews.Project(snapshot, "p")!;
        Assert.Equal("Built the page.", view.Work!.PlainSummary);
        Assert.Equal("fallback", view.Work.PlainSummaryFrom);
        Assert.Equal("Build the forecast.", view.Work.Steps[0].PlainLine);
        Assert.Equal("Long worker instructions.", view.Work.Steps[0].Instruction);
        Assert.Equal("Checks the whole work and runs the tests.", view.Work.Steps[1].PlainLine);

        var corrupt = JsonNode.Parse("""
        {
          "projects": [
            {
              "id": "p", "name": "Test",
              "megaTask": { "id": "m1", "title": "Weather page", "leadSummary": 12, "status": "COMPLETE" },
              "subTasks": [
                { "id": "s1", "title": "Build it", "leadLine": 12, "plainLine": "", "status": "COMPLETE" }
              ],
              "results": [ { "subTaskId": "s1", "status": "FAILED", "summary": "Boom." } ]
            }
          ]
        }
        """)!;
        var recovered = ProjectViews.Project(corrupt, "p")!;
        Assert.Equal("Weather page", recovered.Work!.PlainSummary);
        Assert.Equal("fallback", recovered.Work.PlainSummaryFrom);
        Assert.Equal("Build it", recovered.Work.Steps[0].PlainLine);
    }

    [Fact]
    public void Engine_plain_summary_wins_over_the_local_fallback()
    {
        var snapshot = JsonNode.Parse("""
        {
          "projects": [
            {
              "id": "p", "name": "Test",
              "megaTask": { "id": "m1", "title": "Weather page", "status": "COMPLETE", "leadSummary": "Ignored because plain is set." },
              "subTasks": [ { "id": "s1", "title": "Build it", "status": "COMPLETE" } ],
              "results": [ { "subTaskId": "s1", "status": "COMPLETE", "summary": "Built it." } ],
              "plain": { "summary": "Lead summary from the engine.", "summaryFrom": "lead" }
            }
          ]
        }
        """)!;
        var view = ProjectViews.Project(snapshot, "p")!;
        Assert.Equal("Lead summary from the engine.", view.Work!.PlainSummary);
        Assert.Equal("lead", view.Work.PlainSummaryFrom);
    }

    static IEnumerable<string> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? "");

    static Button? ButtonNamed(Control root, string label) =>
        root.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == label));

    [AvaloniaFact]
    public void Work_finished_shows_the_three_lines_the_action_and_body_text_large_enough()
    {
        var window = new MainWindow("v0.2.24");
        window.Show();
        try
        {
            window.Show(SampleState.Finished());
            window.UpdateLayout();
            var texts = Texts(window).ToList();
            Assert.Contains("Built the 7-day forecast page with temperature charts. All unit and integration tests pass.", texts);
            Assert.Contains("Tests passed and verification confirmed the goal.", texts);
            Assert.Contains("Try the result in the project folder.", texts);
            Assert.NotNull(ButtonNamed(window, "Open project folder"));
            var body = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.Text is "Built the 7-day forecast page with temperature charts. All unit and integration tests pass."
                    or "Tests passed and verification confirmed the goal."
                    or "Try the result in the project folder.");
            Assert.All(body, block => Assert.True(block.FontSize >= 14, block.Text + " is " + block.FontSize));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Failed_check_and_an_open_decision_stand_out_on_work_finished()
    {
        var window = new MainWindow("v0.2.24");
        window.Show();
        try
        {
            var failed = SampleState.Finished();
            var results = (JsonArray)failed["projects"]![0]!["results"]!;
            results.Add(JsonNode.Parse("""{ "subTaskId": "s2", "status": "FAILED", "summary": "Time zone tests failed." }""")!);
            window.Show(failed);
            window.UpdateLayout();
            Assert.Contains("Verification failed: Time zone tests failed.", Texts(window));
            Assert.NotNull(ButtonNamed(window, "Tell the lead to fix it"));

            var decided = SampleState.Finished();
            ((JsonArray)decided["projects"]![0]!["results"]!).Add(JsonNode.Parse("""{ "subTaskId": "s2", "status": "COMPLETE", "summary": "Covered time zones.\nNeeds your decision: keep the sample charts?" }""")!);
            window.Show(decided);
            window.UpdateLayout();
            Assert.Contains("Answer the open decision to proceed.", Texts(window));
            Assert.Contains(Texts(window), text => text.Contains("keep the sample charts?"));
            Assert.NotNull(ButtonNamed(window, "Answer decision"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Long_events_offer_more_and_step_instructions_start_closed()
    {
        var window = new MainWindow("v0.2.24");
        window.Show();
        try
        {
            var working = SampleState.Working();
            var timeline = (JsonArray)working["execution"]!["timeline"]!;
            timeline.Add(JsonNode.Parse("""{ "projectId": "weather", "type": "SUBTASK_COMPLETED", "createdAt": "2026-11-11T16:00:00Z", "summary": "Implement the whole job.: Finished Implement the whole job. Extra notes that make this event long enough to hide behind more." }""")!);
            window.Show(working);
            window.UpdateLayout();
            Assert.NotNull(ButtonNamed(window, "more"));

            var folds = window.GetVisualDescendants().Where(c => c.GetType().Name == "FoldBox").ToList();
            var instructions = folds.FirstOrDefault(fold =>
                fold.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Strings.T("Instructions")));
            Assert.NotNull(instructions);
            var prop = instructions!.GetType().GetProperty("IsExpanded");
            Assert.False((bool)prop!.GetValue(instructions)!);

            window.OpenSide("team");
            window.UpdateLayout();
            var teamFolds = window.GetVisualDescendants().Where(c => c.GetType().Name == "FoldBox").ToList();
            var limit = teamFolds.FirstOrDefault(fold =>
                fold.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Strings.T("Limit and tools")));
            Assert.NotNull(limit);
            Assert.False((bool)limit!.GetType().GetProperty("IsExpanded")!.GetValue(limit)!);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Thread_keeps_the_last_sixty_events_and_is_safe_to_read_together()
    {
        var timeline = new JsonArray();
        for (var i = 0; i < 80; i++)
            timeline.Add(new JsonObject
            {
                ["projectId"] = "p",
                ["type"] = "SUBTASK_COMPLETED",
                ["createdAt"] = $"2026-11-22T10:{i:00}:00Z",
                ["summary"] = "Add the chart: Finished Add the chart."
            });
        var snapshot = new JsonObject { ["asks"] = new JsonArray(), ["execution"] = new JsonObject { ["timeline"] = timeline } };
        var thread = ChatViews.Thread(snapshot, "p", _ => "");
        Assert.Single(thread);
        Assert.Equal(60, Assert.IsType<EventItem>(thread[0]).Count);

        var copies = new IReadOnlyList<ThreadItem>[16];
        Parallel.For(0, copies.Length, i => copies[i] = ChatViews.Thread(snapshot, "p", _ => ""));
        Assert.All(copies, copy =>
        {
            Assert.Single(copy);
            Assert.Equal(60, Assert.IsType<EventItem>(copy[0]).Count);
        });
    }
}
