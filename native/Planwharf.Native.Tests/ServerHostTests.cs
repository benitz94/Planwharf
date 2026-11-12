using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

// The real server, started the way the app starts it, answering and then quitting.
[Collection("Real server")]
public class ServerHostTests
{
    [Fact]
    public async Task Starts_the_real_server_reads_its_state_and_stops_it()
    {
        var data = Directory.CreateTempSubdirectory("pw-native-");
        try
        {
            var host = new ServerHost(data.FullName);
            var address = await host.StartAsync("v0.0.1", TimeSpan.FromSeconds(60));
            Assert.Matches(@"^http://127\.0\.0\.1:\d+$", address);
            var api = new PlanwharfApi(address);
            var state = await api.SnapshotAsync(readOnly: true);
            Assert.NotEmpty(ProjectViews.Projects(state));
            Assert.Equal("v0.0.1", (await api.GetAsync("/api/app"))["version"]?.GetValue<string>());
            await host.DisposeAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => new HttpClient { Timeout = TimeSpan.FromSeconds(3) }.GetStringAsync(address + "/api/app"));
            // Nothing stays open: on Windows an open log or a running server would lock the folder.
            data.Delete(true);
            Assert.False(Directory.Exists(data.FullName));
        }
        finally { if (Directory.Exists(data.FullName)) try { data.Delete(true); } catch { } }
    }
}
