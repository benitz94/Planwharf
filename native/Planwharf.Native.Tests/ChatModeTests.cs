using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Planwharf.Engine;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public class ChatModeTests
{
    [Fact]
    public void Talk_hides_both_start_controls_and_Plan_keeps_them()
    {
        var talk = WithMode(SampleState.Finished(), "weather", "talk");
        var view = ProjectViews.Project(talk, "weather")!;
        Assert.Equal("talk", view.ChatMode);
        Assert.Equal(CrewAction.None, view.Action);
        Assert.Null(view.NextGoal);
        Assert.Equal("A conversation with the lead. Nothing starts.", view.Hint);

        var plan = ProjectViews.Project(SampleState.Finished(), "weather")!;
        Assert.Equal("plan", plan.ChatMode);
        Assert.Equal(CrewAction.LetTheCrewDoIt, plan.Action);
        Assert.Null(plan.NextGoal);

        var idle = WithMode(SampleState.Finished(), "notes", "plan");
        Assert.Equal(CrewAction.LetTheCrewDoIt, ProjectViews.Project(idle, "notes")!.Action);

        // Auto was removed: a project saved in Auto reads as Plan and offers Let the crew do it.
        var auto = WithMode(SampleState.Finished(enabled: false), "notes", "auto");
        var autoView = ProjectViews.Project(auto, "notes")!;
        Assert.Equal("plan", autoView.ChatMode);
        Assert.Equal(CrewAction.LetTheCrewDoIt, autoView.Action);
        Assert.Equal(CrewAction.StopTheCrew, ProjectViews.Project(SampleState.Working(), "weather")!.Action);
    }

    [Fact]
    public void Pipeline_is_not_a_mode_the_chat_accepts()
    {
        var hidden = WithMode(SampleState.Finished(), "weather", "pipeline");
        var view = ProjectViews.Project(hidden, "weather")!;
        Assert.Equal("plan", view.ChatMode);
        Assert.Equal(CrewAction.LetTheCrewDoIt, view.Action);
    }

    [Fact]
    public void Running_work_in_Talk_mode_preserves_StopTheCrew_action()
    {
        var workingTalk = WithMode(SampleState.Working(), "weather", "talk");
        var view = ProjectViews.Project(workingTalk, "weather")!;
        Assert.Equal("talk", view.ChatMode);
        Assert.Equal(CrewAction.StopTheCrew, view.Action);
        Assert.Equal("Stop the crew", view.ActionText);
        Assert.Null(view.NextGoal);
    }

    [Fact]
    public void ProjectView_normalizes_casing_whitespace_and_invalid_modes()
    {
        var talkUpper = WithMode(SampleState.Finished(), "weather", "  TALK  ");
        Assert.Equal("talk", ProjectViews.Project(talkUpper, "weather")!.ChatMode);

        var autoSpaced = WithMode(SampleState.Finished(), "weather", " auto ");
        Assert.Equal("plan", ProjectViews.Project(autoSpaced, "weather")!.ChatMode);

        var unknown = WithMode(SampleState.Finished(), "weather", "random_mode");
        Assert.Equal("plan", ProjectViews.Project(unknown, "weather")!.ChatMode);
    }

    [AvaloniaFact]
    public void ChatModeBar_normalizes_casing_whitespace_and_invalid_values()
    {
        var bar = new ChatModeBar();
        bar.Show("TALK");
        Assert.Equal(ChatModeBar.Talk, bar.Mode);
        Assert.True(Checked(bar, ChatModeBar.Talk));

        bar.Show("  auto  ");
        Assert.Equal(ChatModeBar.Plan, bar.Mode);
        Assert.True(Checked(bar, ChatModeBar.Plan));

        bar.Show(null);
        Assert.Equal(ChatModeBar.Plan, bar.Mode);
        Assert.True(Checked(bar, ChatModeBar.Plan));

        bar.Show("pipeline");
        Assert.Equal(ChatModeBar.Plan, bar.Mode);
        Assert.True(Checked(bar, ChatModeBar.Plan));

        bar.Show("random");
        Assert.Equal(ChatModeBar.Plan, bar.Mode);
        Assert.True(Checked(bar, ChatModeBar.Plan));
    }

    [AvaloniaFact]
    public async Task Switch_shows_the_saved_mode_and_each_click_calls_the_engine_once()
    {
        using var server = new ModeServer();
        var window = new MainWindow("test");
        window.Show();
        try
        {
            window.Connect(new PlanwharfApi(server.Address));
            var state = SampleState.Finished();
            state["projects"]![0]!["chatMode"] = "talk";
            window.Show(state);
            window.UpdateLayout();
            var bar = window.GetVisualDescendants().OfType<ChatModeBar>().Single();
            Assert.True(bar.IsVisible);
            Assert.Equal(ChatModeBar.Talk, bar.Mode);
            Assert.Equal("A conversation with the lead. Nothing starts.", bar.HintLine.Text);
            Assert.True(Checked(bar, ChatModeBar.Talk));
            Assert.False(Checked(bar, ChatModeBar.Plan));
            Assert.Equal(2, bar.Buttons.Count);
            Assert.DoesNotContain(bar.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Auto");
            Assert.DoesNotContain(bar.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Pipeline");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button.IsVisible && button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Let the crew do it"));

            await Click(bar, ChatModeBar.Plan, server);
            await Click(bar, ChatModeBar.Talk, server);
            Assert.Equal(
            [
                "/api/orchestrator/chat-mode {\"projectId\":\"weather\",\"mode\":\"plan\"}",
                "/api/orchestrator/chat-mode {\"projectId\":\"weather\",\"mode\":\"talk\"}"
            ], server.Posts);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void The_switch_labels_and_hints_are_Italian()
    {
        var previous = Strings.CurrentLanguage;
        try
        {
            Strings.CurrentLanguage = "it";
            Assert.Equal("Chat", ChatModeBar.LabelOf(ChatModeBar.Talk));
            Assert.Equal("Piano", ChatModeBar.LabelOf(ChatModeBar.Plan));
            Assert.Equal("Una conversazione con il coordinatore. Non parte nulla.", ChatModeBar.HintOf(ChatModeBar.Talk));
            Assert.Contains("Affida il lavoro alla squadra", ChatModeBar.HintOf(ChatModeBar.Plan));
        }
        finally { Strings.CurrentLanguage = previous; }
    }

    [Fact]
    public void LabelOf_and_HintOf_fall_back_cleanly_for_null_or_unknown_modes()
    {
        Assert.Equal("Plan", ChatModeBar.LabelOf(null!));
        Assert.Equal("Plan", ChatModeBar.LabelOf("UNKNOWN"));
        Assert.Equal("Plan", ChatModeBar.LabelOf("pipeline"));

        Assert.Contains("Let the crew do it", ChatModeBar.HintOf(null!));
        Assert.Contains("Let the crew do it", ChatModeBar.HintOf("UNKNOWN"));

        var previous = Strings.CurrentLanguage;
        try
        {
            Strings.CurrentLanguage = "it";
            Assert.Equal("Piano", ChatModeBar.LabelOf(null!));
            Assert.Equal("Piano", ChatModeBar.LabelOf("invalid"));
            Assert.Contains("Affida il lavoro alla squadra", ChatModeBar.HintOf(null!));
        }
        finally { Strings.CurrentLanguage = previous; }
    }


    static bool Checked(ChatModeBar bar, string mode) => bar.Buttons.Single(button => (string?)button.Tag == mode).IsChecked == true;

    static async Task Click(ChatModeBar bar, string mode, ModeServer server)
    {
        var before = server.Posts.Count;
        bar.Buttons.Single(button => (string?)button.Tag == mode).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var until = DateTime.UtcNow.AddSeconds(8);
        while (server.Posts.Count == before && DateTime.UtcNow < until) await Task.Delay(30);
        Assert.Equal(before + 1, server.Posts.Count);
    }

    static JsonNode WithMode(JsonNode state, string projectId, string mode)
    {
        var copy = JsonNode.Parse(state.ToJsonString())!;
        foreach (var row in copy["projects"]!.AsArray().OfType<JsonObject>())
            if (row["id"]?.GetValue<string>() == projectId) row["chatMode"] = mode;
        return copy;
    }

    sealed class ModeServer : IDisposable
    {
        readonly HttpListener listener = new();
        readonly List<string> posts = [];
        readonly CancellationTokenSource stop = new();

        public ModeServer()
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            Address = "http://127.0.0.1:" + port;
            listener.Prefixes.Add(Address + "/");
            listener.Start();
            _ = Serve();
        }

        public string Address { get; }
        public IReadOnlyList<string> Posts { get { lock (posts) return posts.ToList(); } }

        async Task Serve()
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch { break; }
                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
                if (context.Request.HttpMethod == "POST")
                {
                    lock (posts) posts.Add(context.Request.Url!.AbsolutePath + " " + body);
                }
                var mode = "plan";
                if (body.Contains("\"mode\":\"talk\"", StringComparison.Ordinal)) mode = "talk";
                else if (body.Contains("\"mode\":\"auto\"", StringComparison.Ordinal)) mode = "auto";
                var json = "{\"mode\":\"manual\",\"projects\":[{\"id\":\"weather\",\"name\":\"Weather\",\"status\":\"idle\",\"chatMode\":\"" + mode + "\"}],\"asks\":[],\"models\":[],\"autonomy\":{}}";
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            listener.Close();
        }
    }
}
