using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

// Resume answers 409 "requires confirmation" when the files changed since the work stopped (the crew's own
// commits do). The app used to throw that answer away, so Resume did nothing (job 43, 2026-11-24).
public class ResumeConfirmTests
{
    [Fact]
    public async Task Resume_returns_the_question_and_sends_the_confirmation()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var bodies = new List<string>();
        var serve = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var context = await listener.GetContextAsync();
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                bodies.Add(body);
                var confirmed = JsonNode.Parse(body)?["confirmChanges"]?.GetValue<bool>() == true;
                var reply = confirmed ? "{\"projects\":[]}" : "{\"ok\":false,\"requiresConfirmation\":true,\"reason\":\"Repository or Living Plan changed\",\"diff\":{\"changes\":[\"HEAD changed from a to b\"]}}";
                context.Response.StatusCode = confirmed ? 200 : 409;
                var bytes = Encoding.UTF8.GetBytes(reply);
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        });
        var api = new PlanwharfApi($"http://127.0.0.1:{port}/");
        var asked = await api.ResumeOrAskAsync("p1", false);
        Assert.True(asked?["requiresConfirmation"]?.GetValue<bool>());
        Assert.Equal("HEAD changed from a to b", asked?["diff"]?["changes"]?[0]?.GetValue<string>());
        var resumed = await api.ResumeOrAskAsync("p1", true);
        Assert.NotNull(resumed?["projects"]);
        await serve;
        Assert.Equal("{\"projectId\":\"p1\"}", bodies[0]);
        Assert.Equal("{\"projectId\":\"p1\",\"confirmChanges\":true}", bodies[1]);
    }
}
