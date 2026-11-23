using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Native.Tests;

public class DecisionsInboxViewTests
{
    static readonly string DecisionsPayload = """
    {
      "openCount": 2,
      "open": [
        {
          "id": "dec-1",
          "projectId": "weather",
          "projectName": "Weather dashboard",
          "workId": "MT-1",
          "workTitle": "Add forecast page",
          "isPipeline": false,
          "text": "Cache weather data for 15 minutes?",
          "suggestion": "15 minutes",
          "breaks": true,
          "at": "2026-11-23T10:00:00Z",
          "answer": ""
        },
        {
          "id": "dec-2",
          "projectId": "notes",
          "projectName": "Recipe notes app",
          "workId": "pipe-99",
          "workTitle": "Sync pipeline",
          "isPipeline": true,
          "text": "Overwrite existing recipes on conflict?",
          "suggestion": "ask per file",
          "breaks": false,
          "at": "2026-11-23T10:05:00Z",
          "answer": ""
        }
      ],
      "answered": [
        {
          "id": "dec-old",
          "projectId": "weather",
          "projectName": "Weather dashboard",
          "workId": "MT-1",
          "workTitle": "Add forecast page",
          "isPipeline": false,
          "text": "Use Celsius or Fahrenheit by default?",
          "suggestion": "Celsius",
          "breaks": false,
          "at": "2026-11-23T09:00:00Z",
          "answer": "Celsius",
          "answeredAt": "2026-11-23T09:01:00Z",
          "answeredBy": "Decided by Grok 4.7",
          "autoReason": "Followed recommended suggestion: Celsius",
          "stepStarted": false
        }
      ]
    }
    """;

    static bool Has(MainWindow window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == text || (block.Text ?? "").Contains(text, StringComparison.Ordinal));

    [AvaloniaFact]
    public void The_top_bar_shows_decisions_tab_with_pending_count()
    {
        var window = new MainWindow("test") { Height = 1000, Width = 1200 };
        window.Show();
        try
        {
            window.Show(SampleState.Working());
            window.ShowDecisions(JsonNode.Parse(DecisionsPayload)!);
            window.UpdateLayout();

            // Top bar tab must display Decisions (2)
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Decisions (2)");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_decisions_inbox_panel_draws_open_decisions_buttons_and_auto_switch()
    {
        var window = new MainWindow("test") { Height = 1200, Width = 1200 };
        window.Show();
        try
        {
            window.Show(SampleState.Working());
            window.ShowDecisions(JsonNode.Parse(DecisionsPayload)!);
            window.OpenSide("decisions");
            window.UpdateLayout();

            // Side panel title and description
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Decisions");
            Assert.True(Has(window, "Every open decision of every project and pipeline job in one place."));

            // Auto mode switch
            var toggles = window.GetVisualDescendants().OfType<ToggleSwitch>().ToList();
            Assert.Contains(toggles, t => (t.OnContent?.ToString() ?? "").Contains("Let the lead decide new decisions"));

            // Open decisions section
            Assert.True(Has(window, "Cache weather data for 15 minutes?"));
            Assert.True(Has(window, "Could break something"));
            Assert.True(Has(window, "Suggestion: 15 minutes"));
            Assert.True(Has(window, "Overwrite existing recipes on conflict?"));
            Assert.True(Has(window, "Suggestion: ask per file"));

            // Buttons: Yes, No, Let the lead decide, Send
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Contains(buttons, b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Yes"));
            Assert.Contains(buttons, b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "No"));
            Assert.Contains(buttons, b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Let the lead decide"));
            Assert.Contains(buttons, b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Send"));

            // Answered decisions section
            Assert.True(Has(window, "Use Celsius or Fahrenheit by default?"));
            Assert.True(Has(window, "Decided by Grok 4.7"));
            Assert.True(Has(window, "Followed recommended suggestion: Celsius"));
            Assert.True(Has(window, "Change answer"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Decisions_inbox_localizes_to_Italian()
    {
        var prev = Strings.CurrentLanguage;
        Strings.CurrentLanguage = "it";
        var window = new MainWindow("test") { Height = 1200, Width = 1200 };
        window.Show();
        try
        {
            window.Show(SampleState.Working());
            window.ShowDecisions(JsonNode.Parse(DecisionsPayload)!);
            window.OpenSide("decisions");
            window.UpdateLayout();

            // Top bar tab in Italian
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Decisioni (2)");

            // Auto switch in Italian
            var toggles = window.GetVisualDescendants().OfType<ToggleSwitch>().ToList();
            Assert.Contains(toggles, t => (t.OnContent?.ToString() ?? "").Contains("Lascia decidere al lead le nuove decisioni"));

            // Break highlighting in Italian
            Assert.True(Has(window, "Potrebbe rompere qualcosa"));

            // Let the lead decide button in Italian
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Contains(buttons, b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Lascia decidere al lead"));
        }
        finally
        {
            Strings.CurrentLanguage = prev;
            window.Close();
        }
    }
}
