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

public class PullPanelLoadTests
{
    [AvaloniaFact]
    public async Task A_filter_clicked_while_the_list_is_loading_is_not_dropped()
    {
        using var server = new PullServer();
        var window = new MainWindow("test") { Height = 1000, Width = 1200 };
        window.Show();
        try
        {
            window.Connect(new PlanwharfApi(server.Address));
            var until = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < until && !Has(window, "Weather")) await Task.Delay(30);
            Assert.True(Has(window, "Weather"));
            window.OpenSide("pulls");
            until = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < until && server.Pulls.Count == 0) await Task.Delay(30);
            Assert.Contains("state=open", Assert.Single(server.Pulls));

            Click(window, "Merged");
            until = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < until && server.Pulls.Count < 2) await Task.Delay(30);
            Assert.Contains(server.Pulls, query => query.Contains("state=merged", StringComparison.Ordinal));
            server.Release();
            until = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < until && !Has(window, "Merged only")) await Task.Delay(30);
            await Task.Delay(150);
            Assert.True(Has(window, "#2 Merged only"));
            Assert.False(Has(window, "Open only"));
            Assert.False(Has(window, "No merged pull requests."));
        }
        finally { window.Close(); }
    }

    static bool Has(MainWindow window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == text || (block.Text ?? "").Contains(text, StringComparison.Ordinal));

    static void Click(MainWindow window, string label)
    {
        var button = window.GetVisualDescendants().OfType<Button>().First(item =>
            item.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == label));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
    }

    sealed class PullServer : IDisposable
    {
        readonly HttpListener listener = new();
        readonly CancellationTokenSource stop = new();
        readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly List<string> pulls = [];

        public PullServer()
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
        public IReadOnlyList<string> Pulls { get { lock (pulls) return pulls.ToList(); } }
        public void Release() => ready.TrySetResult();

        async Task Serve()
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch { break; }
                _ = Answer(context);
            }
        }

        async Task Answer(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url?.AbsolutePath ?? "";
                var query = context.Request.Url?.Query ?? "";
                string json;
                if (path == "/api/prs")
                {
                    lock (pulls) pulls.Add(query);
                    await ready.Task;
                    json = query.Contains("state=merged", StringComparison.Ordinal)
                        ? """{"ok":true,"state":"merged","author":"all","items":[{"number":2,"title":"Merged only","state":"merged","byPlanwharf":false}],"summary":{"merged":1,"failedChecks":[]}}"""
                        : """{"ok":true,"state":"open","author":"all","items":[{"number":1,"title":"Open only","state":"open","byPlanwharf":false}],"summary":{"merged":0,"failedChecks":[]}}""";
                }
                else json = """{"projects":[{"id":"weather","name":"Weather","status":"idle","goal":""}],"asks":[],"models":[],"autonomy":{}}""";
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch { try { context.Response.Abort(); } catch { /* the test already moved on */ } }
        }

        public void Dispose()
        {
            ready.TrySetResult();
            stop.Cancel();
            listener.Stop();
            listener.Close();
        }
    }

    [AvaloniaFact]
    public async Task Show_more_keeps_the_list_when_the_next_page_fails()
    {
        var language = Strings.CurrentLanguage;
        Strings.CurrentLanguage = "en";
        using var server = new PageServer();
        var window = new MainWindow("test") { Height = 1000, Width = 1200 };
        window.Show();
        try
        {
            window.Connect(new PlanwharfApi(server.Address));
            var until = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < until && !Has(window, "Weather")) await Task.Delay(30);
            Assert.True(Has(window, "Weather"));
            window.OpenSide("pulls");
            until = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < until && !Has(window, "#1 Open only")) await Task.Delay(30);
            window.UpdateLayout();
            Assert.True(Has(window, "#1 Open only"));
            Assert.True(Has(window, "Show more"));

            Click(window, "Show more");
            until = DateTime.UtcNow.AddSeconds(4);
            while (DateTime.UtcNow < until && !Has(window, "The next page did not load.")) { window.UpdateLayout(); await Task.Delay(30); }
            Assert.True(Has(window, "The next page did not load."));
            Assert.True(Has(window, "#1 Open only"));
            Assert.False(Has(window, "No open pull requests."));
        }
        finally { window.Close(); Strings.CurrentLanguage = language; }
    }

    sealed class PageServer : IDisposable
    {
        readonly HttpListener listener = new();
        readonly CancellationTokenSource stop = new();
        int pulls;

        public PageServer()
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

        async Task Serve()
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch { break; }
                _ = Answer(context);
            }
        }

        async Task Answer(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url?.AbsolutePath ?? "";
                string json;
                if (path == "/api/prs")
                {
                    var page = Interlocked.Increment(ref pulls);
                    json = page == 1
                        ? """{"ok":true,"state":"open","author":"all","more":true,"items":[{"number":1,"title":"Open only","state":"open","byPlanwharf":false}],"summary":{"merged":0,"failedChecks":[]}}"""
                        : """{"ok":false,"reason":"The next page did not load.","state":"open","author":"all"}""";
                }
                else json = """{"projects":[{"id":"weather","name":"Weather","status":"idle","goal":""}],"asks":[],"models":[],"autonomy":{}}""";
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch { try { context.Response.Abort(); } catch { /* the test already moved on */ } }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            listener.Close();
        }
    }
}
