using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public class WorkAlertWindowTests
{
    [AvaloniaFact]
    public void Snapshot_refresh_delivers_once_for_each_project_including_background_projects()
    {
        var alerts = new List<WorkAlert>();
        var window = new MainWindow("test") { AlertReceived = alerts.Add };
        window.Show();
        try
        {
            var working = SampleState.Working();
            var background = working["projects"]![0]!.DeepClone();
            background["id"] = "background";
            background["megaTask"]!["id"] = "MT-2";
            background["megaTask"]!["title"] = "Background work";
            working["projects"]!.AsArray().Add(background);
            window.Show(working);
            Assert.Empty(alerts);
            var done = working.DeepClone();
            done["projects"]![0]!["megaTask"]!["status"] = "COMPLETE";
            done["projects"]![2]!["megaTask"]!["status"] = "FAILED";
            window.Show(done);
            window.Show(done.DeepClone());
            Assert.Equal(2, alerts.Count);
            Assert.Contains(alerts, alert => alert.Goal == "Background work" && !alert.Done);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Look_has_three_named_keyboard_focusable_alert_switches()
    {
        var window = new MainWindow("test");
        window.Show();
        try
        {
            window.Show(SampleState.Working());
            window.OpenSide("settings");
            window.UpdateLayout();
            var switches = window.GetVisualDescendants().OfType<ToggleSwitch>()
                .Where(toggle => AutomationProperties.GetName(toggle)?.StartsWith("Alerts: ") == true).ToList();
            Assert.Equal(3, switches.Count);
            Assert.All(switches, toggle => Assert.True(toggle.Focusable));
            Assert.Equal(new[] { "Alerts: flash", "Alerts: notification", "Alerts: sound" },
                switches.Select(AutomationProperties.GetName).Order().ToArray());
        }
        finally { window.Close(); }
    }
}
