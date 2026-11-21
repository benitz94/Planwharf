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

public class SavingModeTests
{
    [Fact]
    public void The_project_name_stays_and_the_saved_mode_is_shown_beside_it()
    {
        var safe = WithMode(SampleState.Finished(), "weather", "safe");
        var item = ProjectViews.Projects(safe).Single(project => project.Id == "weather");
        Assert.Equal("Weather dashboard", item.Name);
        Assert.Equal(SavingModes.Safe, item.SavingMode);
        Assert.Equal("Weather dashboard · Safe", SavingModes.Beside(item.Name, item.SavingMode));
        var view = ProjectViews.Project(safe, "weather")!;
        Assert.Equal("Weather dashboard", view.Name);
        Assert.Equal(SavingModes.Safe, view.SavingMode);

        var ultra = ProjectViews.Project(WithMode(SampleState.Working(), "weather", "ultra-safe"), "weather")!;
        Assert.Equal(SavingModes.UltraSafe, ultra.SavingMode);
        Assert.Equal("Weather dashboard · Ultra safe", SavingModes.Beside(ultra.Name, ultra.SavingMode));

        var missing = ProjectViews.Project(SampleState.Finished(), "notes")!;
        Assert.Equal(SavingModes.Normal, missing.SavingMode);
        Assert.Equal("Recipe notes app · Normal", SavingModes.Beside(missing.Name, missing.SavingMode));
        Assert.Equal(SavingModes.Normal, SavingModes.Normalize("pipeline"));
        Assert.Equal(SavingModes.UltraSafe, SavingModes.Normalize("  Ultra Sicura "));
    }

    [Fact]
    public void The_labels_and_hints_are_Italian()
    {
        var previous = Strings.CurrentLanguage;
        try
        {
            Strings.CurrentLanguage = "it";
            Assert.Equal("Normale", SavingModes.Label(SavingModes.Normal));
            Assert.Equal("Sicura", SavingModes.Label(SavingModes.Safe));
            Assert.Equal("Ultra sicura", SavingModes.Label(SavingModes.UltraSafe));
            Assert.Contains("modello di punta", SavingModes.Hint(SavingModes.Safe));
            Assert.Contains("leggeri", SavingModes.Hint(SavingModes.UltraSafe));
            Assert.Equal("Meteo · Sicura", SavingModes.Beside("Meteo", "safe"));
        }
        finally { Strings.CurrentLanguage = previous; }
    }

    [AvaloniaFact]
    public void SavingModeBar_shows_the_saved_mode()
    {
        var bar = new SavingModeBar();
        bar.Show("safe");
        Assert.Equal(SavingModes.Safe, bar.Mode);
        Assert.True(Checked(bar, SavingModes.Safe));
        Assert.False(Checked(bar, SavingModes.Normal));
        Assert.Contains("Mid and light only", bar.HintLine.Text);

        bar.Show("ULTRA SAFE");
        Assert.Equal(SavingModes.UltraSafe, bar.Mode);
        Assert.True(Checked(bar, SavingModes.UltraSafe));

        bar.Show(null);
        Assert.Equal(SavingModes.Normal, bar.Mode);
        Assert.Equal(3, bar.Buttons.Count);
    }

    [AvaloniaFact]
    public void The_title_and_the_crew_card_name_the_saved_mode()
    {
        var previous = Strings.CurrentLanguage;
        var window = new MainWindow("test");
        window.Show();
        try
        {
            window.Show(WithMode(SampleState.Working(), "weather", "safe"));
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Weather dashboard · Safe");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => (block.Text ?? "").StartsWith("Crew at work · Safe"));

            Strings.CurrentLanguage = "it";
            window.Show(WithMode(SampleState.Working(), "weather", "ultra-safe"));
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Weather dashboard · Ultra sicura");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => (block.Text ?? "").StartsWith("Squadra al lavoro · Ultra sicura"));
        }
        finally { Strings.CurrentLanguage = previous; window.Close(); }
    }

    [AvaloniaFact]
    public async Task Each_click_in_Team_calls_the_engine_once()
    {
        using var server = new ModeServer();
        var window = new MainWindow("test");
        window.Show();
        try
        {
            window.Connect(new PlanwharfApi(server.Address));
            var state = WithMode(SampleState.Finished(), "weather", "safe");
            window.Show(state);
            window.OpenSide("team");
            window.UpdateLayout();
            var bar = window.GetVisualDescendants().OfType<SavingModeBar>().Single();
            Assert.True(bar.IsVisible);
            Assert.Equal(SavingModes.Safe, bar.Mode);
            Assert.True(Checked(bar, SavingModes.Safe));

            await Click(bar, SavingModes.UltraSafe, server);
            await Click(bar, SavingModes.Normal, server);
            Assert.Equal(
            [
                "/api/orchestrator/saving-mode {\"projectId\":\"weather\",\"mode\":\"ultra-safe\"}",
                "/api/orchestrator/saving-mode {\"projectId\":\"weather\",\"mode\":\"normal\"}"
            ], server.Posts);
        }
        finally { window.Close(); }
    }

    static bool Checked(SavingModeBar bar, string mode) => bar.Buttons.Single(button => (string?)button.Tag == mode).IsChecked == true;

    static async Task Click(SavingModeBar bar, string mode, ModeServer server)
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
            if (row["id"]?.GetValue<string>() == projectId) row["savingMode"] = mode;
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
            var port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
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
                if (context.Request.HttpMethod == "POST") { lock (posts) posts.Add(context.Request.Url!.AbsolutePath + " " + body); }
                var json = "{\"mode\":\"manual\",\"projects\":[{\"id\":\"weather\",\"name\":\"Weather\",\"status\":\"idle\",\"savingMode\":\"safe\"}],\"asks\":[],\"models\":[],\"autonomy\":{}}";
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
