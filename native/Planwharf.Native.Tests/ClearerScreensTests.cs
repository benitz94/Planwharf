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
}
