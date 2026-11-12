using System.Text.Json.Nodes;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public class ChatViewsTests
{
    static string Label(string id) => id == "opus" ? "Opus 5.5" : id == "grok" ? "Grok 4.7" : "";

    [Theory]
    [InlineData("""{ "autonomy": { "p": { "enabled": true, "stopReason": "GOAL_REACHED" } } }""", "running", "Goal reached", "done")]
    [InlineData("""{ "autonomy": { "p": { "enabled": true, "stopReason": "TIME_LIMIT" } } }""", "idle", "Stopped: time limit reached", "attention")]
    [InlineData("""{ "autonomy": { "p": { "enabled": true, "stopReason": "NEW_THING: x" } } }""", "idle", "Stopped: new thing", "attention")]
    [InlineData("""{ "autonomy": { "p": { "enabled": true } } }""", "idle", "Crew working", "running")]
    [InlineData("""{}""", "watching", "Working", "running")]
    [InlineData("""{}""", "needs_approval", "Needs you", "attention")]
    [InlineData("""{}""", "complete", "Done", "done")]
    [InlineData("""{}""", "idle", "Ready", "")]
    public void Project_status_reads_as_in_the_chat_page(string snapshot, string status, string text, string kind) =>
        Assert.Equal(new ProjectStatusView(text, kind), ChatViews.Status(JsonNode.Parse(snapshot)!, new JsonObject { ["id"] = "p", ["status"] = status }));

    [Fact]
    public void Says_who_is_working_and_for_how_long()
    {
        var row = JsonNode.Parse("""{ "dispatch": [ { "alive": false, "modelId": "grok" }, { "alive": true, "modelId": "opus", "startedAt": "2026-11-12T10:00:00Z" } ] }""")!.AsObject();
        var (who, since) = ChatViews.Working(row, Label)!.Value;
        Assert.Equal("Opus 5.5 is working · 4 min", ChatViews.WorkingText(who, since, DateTimeOffset.Parse("2026-11-12T10:04:00Z")));
        Assert.Null(ChatViews.Working(new JsonObject(), Label));
    }

    [Fact]
    public void Events_read_in_plain_words()
    {
        Assert.Equal(("good", "Step done: Add the chart"), ChatViews.Event("SUBTASK_COMPLETED", "Add the chart: Finished Add the chart."));
        Assert.Equal(("plan", "Add charts: proposed in 2 steps."), ChatViews.Event("MEGATASK_PROPOSED", "Add charts: 2 proposed slice(s)."));
        Assert.Equal(("plan", "Plan updated: Goal from the chat"), ChatViews.Event("PLAN_REVISION", "Living Plan R4: Goal from the chat"));
        Assert.Equal(("plan", "Plan updated."), ChatViews.Event("PLAN_REVISION", "Living Plan R1: "));
        Assert.Equal(("good", "You approved: Add charts"), ChatViews.Event("MEGATASK_APPROVED", "Approved Add charts"));
        Assert.Equal(("start", "Check: the chart"), ChatViews.Event("SUBTASK_STARTED", "Verify the work independently against the job: the chart"));
        Assert.Equal(("bad", "Build stopped."), ChatViews.Event("SUBTASK_BLOCKED", "Build stopped. COMPLETE"));
    }

    [Fact]
    public void The_thread_mixes_messages_and_events_in_time_order()
    {
        var snapshot = JsonNode.Parse("""
        { "asks": [ { "projectId": "p", "at": "2026-11-12T10:00:00Z", "question": "Add charts?", "answer": "Yes.", "orchestratorId": "grok", "ms": 4200, "thinking": "Reading files", "toolRequest": "browser" },
                    { "projectId": "p", "at": "2026-11-12T10:10:00Z", "question": "Again", "answer": "Claude is not signed in.", "diagnostic": { "code": "AUTH" } },
                    { "projectId": "q", "at": "2026-11-12T10:01:00Z", "question": "Other" } ],
          "execution": { "timeline": [ { "projectId": "p", "type": "MEGATASK_APPROVED", "createdAt": "2026-11-12T10:05:00Z", "summary": "Approved Add charts" },
                                       { "projectId": "p", "type": "WORKER_HEARTBEAT", "createdAt": "2026-11-12T10:06:00Z", "summary": "tick" } ] } }
        """)!;
        var thread = ChatViews.Thread(snapshot, "p", Label);
        Assert.Equal(3, thread.Count);
        var first = Assert.IsType<AskItem>(thread[0]);
        Assert.Equal(("Grok 4.7", "4 s", "Reading files", "browser", false), (first.Who, first.Elapsed, first.Thinking, first.ToolRequest, first.Failed));
        Assert.Equal("You approved: Add charts", Assert.IsType<EventItem>(thread[1]).Text);
        var failed = Assert.IsType<AskItem>(thread[2]);
        Assert.True(failed.Failed); Assert.Equal("Lead", failed.Who); Assert.Equal("", failed.Elapsed);
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T10:10:00Z"), ChatViews.LastNews(snapshot, "p"));
    }

    [Theory]
    [InlineData("stop", CommandKind.StopNow, 0, "")]
    [InlineData("Fermati adesso!", CommandKind.StopNow, 0, "")]
    [InlineData("stop in 10 minutes", CommandKind.StopIn, 10, "")]
    [InlineData("fermati tra 5 min", CommandKind.StopIn, 5, "")]
    [InlineData("stop in 0 minutes", CommandKind.StopNow, 0, "")]
    [InlineData("open team", CommandKind.OpenPanel, 0, "team")]
    [InlineData("mostra consumi", CommandKind.OpenPanel, 0, "usage")]
    [InlineData("files", CommandKind.OpenPanel, 0, "files")]
    [InlineData("Impostazioni", CommandKind.OpenPanel, 0, "settings")]
    [InlineData("add a stop button to the page", CommandKind.None, 0, "")]
    [InlineData("please show me the team and then the usage of every model today", CommandKind.None, 0, "")]
    public void Short_orders_are_handled_by_planwharf(string text, CommandKind kind, int minutes, string panel)
    {
        var command = ChatViews.Command(text);
        Assert.Equal((kind, minutes, panel), (command.Kind, command.Minutes, command.Panel));
    }

    [Fact]
    public void Bench_orders_name_the_model()
    {
        Assert.Equal(new LocalCommand(CommandKind.Bench, Model: "grok"), ChatViews.Command("bench grok"));
        Assert.Equal(new LocalCommand(CommandKind.Bench, Model: "opus"), ChatViews.Command("metti opus in panchina"));
        Assert.Equal(CommandKind.None, ChatViews.Command("put the chart on the page").Kind);
    }
}
