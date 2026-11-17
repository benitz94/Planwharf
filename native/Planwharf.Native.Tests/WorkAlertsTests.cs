using System.Text.Json.Nodes;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public class WorkAlertsTests
{
    [Theory]
    [InlineData("COMPLETE", true)]
    [InlineData("FAILED", false)]
    [InlineData("BLOCKED", false)]
    [InlineData("PAUSED", false)]
    [InlineData("READY_FOR_APPROVAL", false)]
    public void Whole_work_transition_alerts_once(string status, bool done)
    {
        var before = SampleState.Working();
        var after = before.DeepClone();
        after["projects"]![0]!["megaTask"]!["status"] = status;
        var alert = Assert.Single(WorkAlerts.Decide(before, after, new()));
        Assert.Equal(done, alert.Done);
        Assert.EndsWith(done ? "is done" : "needs you", alert.Message);
        Assert.Empty(WorkAlerts.Decide(after, after.DeepClone(), new()));
    }

    [Fact]
    public void Startup_and_step_completion_do_not_alert()
    {
        Assert.Empty(WorkAlerts.Decide(null, SampleState.Finished(), new()));
        var before = SampleState.Working();
        var after = before.DeepClone();
        after["projects"]![0]!["subTasks"]![0]!["status"] = "COMPLETE";
        Assert.Empty(WorkAlerts.Decide(before, after, new()));
    }

    [Theory]
    [InlineData("GOAL_REACHED", true)]
    [InlineData("NEEDS_APPROVAL", false)]
    [InlineData("LEAD_FAILED: unavailable", false)]
    public void Autonomy_stopping_alerts(string reason, bool done)
    {
        var before = SampleState.Working();
        var after = before.DeepClone();
        after["autonomy"]!["weather"]!["stopReason"] = reason;
        Assert.Equal(done, Assert.Single(WorkAlerts.Decide(before, after, new())).Done);
    }

    [Fact]
    public void New_work_and_resumed_work_can_alert_again()
    {
        var finished = SampleState.Finished();
        var next = finished.DeepClone();
        next["projects"]![0]!["megaTask"]!["id"] = "MT-2";
        Assert.Single(WorkAlerts.Decide(finished, next, new()));
        Assert.Single(WorkAlerts.Decide(SampleState.Working(), finished, new()));
    }

    [Fact]
    public void Switches_default_on_and_keep_independent_choices()
    {
        Assert.Equal(new AlertOptions(), AlertOptions.Read(null));
        var options = AlertOptions.Read(JsonNode.Parse("""{"alertNotification":false,"alertFlash":true,"alertSound":false}"""));
        Assert.Equal(new AlertOptions(false, true, false), options);
        Assert.Equal(options, Assert.Single(WorkAlerts.Decide(SampleState.Working(), SampleState.Finished(), options)).Options);
    }

    [Fact]
    public void Lead_failure_after_completion_needs_owner_even_when_last_work_is_complete()
    {
        var before = SampleState.Finished(null);
        var after = SampleState.Finished("LEAD_FAILED");
        Assert.False(Assert.Single(WorkAlerts.Decide(before, after, new())).Done);
    }

    [Fact]
    public void Autonomy_can_need_owner_before_a_work_is_created()
    {
        var before = SampleState.Working();
        before["projects"]![0]!.AsObject().Remove("megaTask");
        var after = before.DeepClone();
        after["autonomy"]!["weather"]!["stopReason"] = "START_FAILED";
        Assert.False(Assert.Single(WorkAlerts.Decide(before, after, new())).Done);
    }

    [Theory]
    [InlineData("quote\" and \\ slash", "\"quote\\\" and \\\\ slash\"")]
    [InlineData("line\r\nbreak", "\"line  break\"")]
    public void Notification_text_stays_an_AppleScript_literal(string value, string expected) =>
        Assert.Equal(expected, DesktopAlerts.AppleString(value));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Each_switch_controls_only_its_own_effect(bool notification, bool flash, bool sound)
    {
        var called = new List<string>();
        DesktopAlerts.Deliver(new(notification, flash, sound), false,
            () => called.Add("notification"), () => called.Add("flash"), () => called.Add("sound"));
        Assert.Equal(notification, called.Contains("notification"));
        Assert.Equal(flash, called.Contains("flash"));
        Assert.Equal(sound, called.Contains("sound"));
    }

    [Fact]
    public void Unavailable_notification_does_not_prevent_other_effects()
    {
        var called = new List<string>();
        DesktopAlerts.Deliver(new(), false, () => throw new InvalidOperationException("No desktop service"),
            () => called.Add("flash"), () => called.Add("sound"));
        Assert.Equal(new[] { "flash", "sound" }, called);
    }

    [Fact]
    public void Open_window_does_not_flash_but_still_notifies_and_plays_sound()
    {
        var called = new List<string>();
        DesktopAlerts.Deliver(new(), true, () => called.Add("notification"),
            () => called.Add("flash"), () => called.Add("sound"));
        Assert.Equal(new[] { "notification", "sound" }, called);
    }
}
