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

public class IsolatedDataFolderTests
{
    [Fact]
    public void Planwharf_data_points_the_server_at_the_isolated_notebook()
    {
        var saved = Environment.GetEnvironmentVariable("PLANWHARF_DATA");
        var isolated = Path.Combine(Path.GetTempPath(), "pw-isolated-" + Guid.NewGuid().ToString("N"));
        var pinned = Path.Combine(Path.GetTempPath(), "pw-pinned-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("PLANWHARF_DATA", "  " + isolated + "  ");
            var host = new ServerHost();
            Assert.Equal(Path.GetFullPath(isolated), host.DataFolder);
            Assert.NotEqual(ServerHost.DefaultDataFolder(), host.DataFolder);
            var explicitHost = new ServerHost(pinned);
            Assert.Equal(Path.GetFullPath(pinned), explicitHost.DataFolder);
            Environment.SetEnvironmentVariable("PLANWHARF_DATA", "   ");
            Assert.Equal(ServerHost.DefaultDataFolder(), new ServerHost().DataFolder);
        }
        finally { Environment.SetEnvironmentVariable("PLANWHARF_DATA", saved); }
    }
}
