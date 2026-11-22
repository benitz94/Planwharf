using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Planwharf.Engine;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public class AddToWorkTests
{
    [Fact]
    public void AdditionText_extracts_addition_line()
    {
        Assert.Equal("Add a logger module in src/logger.js.", ProjectViews.AdditionText("Here is the plan.\nAddition to this work: Add a logger module in src/logger.js."));
        Assert.Equal("Add a logger module in src/logger.js.", ProjectViews.AdditionText("Here is the plan.\n**Addition to this work:** Add a logger module in src/logger.js."));
        Assert.Null(ProjectViews.AdditionText("Here is the plan.\nSuggested next goal: Add a logger module."));
        Assert.Null(ProjectViews.AdditionText("Just discussing without any addition."));
        Assert.Null(ProjectViews.AdditionText(""));
        Assert.Null(ProjectViews.AdditionText(null!));
    }

    [AvaloniaFact]
    public async Task Button_shows_only_under_addition_while_work_runs_and_one_click_calls_engine_once()
    {
        using var server = new AddToWorkServer();
        var window = new MainWindow("test");
        window.Show();
        try
        {
            window.Connect(new PlanwharfApi(server.Address));

            // 1. Working state with addition -> Button is visible
            var workingState = SampleState.Working();
            workingState["asks"]![0]!["answer"] = "I can add the logger.\nAddition to this work: Add logger module.";
            window.Show(workingState);
            window.UpdateLayout();

            var addBtn = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Add to this work"));
            Assert.NotNull(addBtn);
            Assert.True(addBtn.IsVisible);

            // Click the button -> engine is called once
            var before = server.Posts.Count;
            addBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var until = DateTime.UtcNow.AddSeconds(5);
            while (server.Posts.Count == before && DateTime.UtcNow < until) await Task.Delay(30);
            Assert.Equal(before + 1, server.Posts.Count);
            Assert.StartsWith("/api/orchestrator/add-to-work", server.Posts[0]);
            Assert.Contains("\"projectId\":\"weather\"", server.Posts[0]);
            Assert.Contains("Add logger module.", server.Posts[0]);

            // 2. Finished work -> Button is NOT shown
            var finishedState = SampleState.Finished();
            finishedState["asks"]![0]!["answer"] = "I can add the logger.\nAddition to this work: Add logger module.";
            window.Show(finishedState);
            window.UpdateLayout();

            var finishedBtn = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Add to this work"));
            Assert.Null(finishedBtn);

            // 3. Talk mode while working -> Button is NOT shown
            var talkWorkingState = SampleState.Working();
            talkWorkingState["projects"]![0]!["chatMode"] = "talk";
            talkWorkingState["asks"]![0]!["answer"] = "I can add the logger.\nAddition to this work: Add logger module.";
            window.Show(talkWorkingState);
            window.UpdateLayout();

            var talkBtn = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Add to this work"));
            Assert.Null(talkBtn);

            // 4. Working without addition line -> Button is NOT shown
            var noAddWorking = SampleState.Working();
            noAddWorking["asks"]![0]!["answer"] = "Working on the forecast page now.";
            window.Show(noAddWorking);
            window.UpdateLayout();

            var noAddBtn = window.GetVisualDescendants()
                .OfType<Button>()
                .FirstOrDefault(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Add to this work"));
            Assert.Null(noAddBtn);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void Italian_translations_are_present()
    {
        var prev = Strings.CurrentLanguage;
        try
        {
            Strings.CurrentLanguage = "it";
            Assert.Equal("Aggiungi a questo lavoro", Strings.T("Add to this work"));
            Assert.Equal("Aggiunto a questo lavoro: qualcosa", Strings.Format(Strings.T("Added to this work: {0}"), "qualcosa"));
        }
        finally
        {
            Strings.CurrentLanguage = prev;
        }
    }

    sealed class AddToWorkServer : IDisposable
    {
        readonly HttpListener listener = new();
        readonly List<string> posts = [];
        readonly CancellationTokenSource stop = new();

        public AddToWorkServer()
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
                var json = "{\"ok\":true,\"subTask\":{\"id\":\"s3\",\"title\":\"Add logger module.\"},\"snapshot\":{\"mode\":\"manual\",\"projects\":[{\"id\":\"weather\",\"name\":\"Weather\",\"status\":\"watching\",\"chatMode\":\"plan\"}],\"asks\":[],\"models\":[],\"autonomy\":{}}}";
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
