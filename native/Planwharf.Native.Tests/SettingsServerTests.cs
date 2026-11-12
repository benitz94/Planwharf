using System.Text.Json.Nodes;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

// Settings against the real server: GitHub link, tools, model switch, skills, updates, move, start over, remove.
public class SettingsServerTests
{
    [Fact]
    public async Task Changes_settings_and_moves_projects()
    {
        var data = Directory.CreateTempSubdirectory("pw-native-settings-");
        var host = new ServerHost(Path.Combine(data.FullName, "data"));
        try
        {
            var api = new PlanwharfApi(await host.StartAsync("v0.0.1", TimeSpan.FromSeconds(60)));
            var folder = Directory.CreateDirectory(Path.Combine(data.FullName, "site")).FullName;
            var id = (await api.CreateProjectAsync("Settings sample", folder))!;
            JsonObject Row(JsonNode state, string projectId) => (state["projects"] as JsonArray)!.OfType<JsonObject>().Single(row => row["id"]?.GetValue<string>() == projectId);

            await api.SetPlaceAsync(id, folder, "https://github.com/sample/site");
            Assert.Equal("https://github.com/sample/site", Row(await api.SnapshotAsync(false, id), id)["github"]?.GetValue<string>());
            await api.SetPlaceAsync(id, folder, null);
            Assert.True(string.IsNullOrEmpty(Row(await api.SnapshotAsync(false, id), id)["github"]?.GetValue<string>()));

            var tools = await api.SetToolsAsync(id, new Dictionary<string, bool> { ["browser"] = true });
            Assert.True(tools?["tools"]?["browser"]?.GetValue<bool>());
            Assert.True((await api.SnapshotAsync(false, id))["projectTools"]?[id]?["browser"]?.GetValue<bool>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => api.SetToolsAsync(id, new Dictionary<string, bool> { ["teleport"] = true }));

            var auto = await api.SetAutoSwitchAsync(true, 30);
            Assert.Equal(30, auto?["thresholdPercent"]?.GetValue<int>());
            await api.SetAutoSwitchAsync(false);
            Assert.False((await api.AutoSwitchAsync())["enabled"]?.GetValue<bool>());

            Assert.NotNull((await api.SkillsAsync())["programs"] as JsonArray);
            Assert.NotNull((await api.UpdatesAsync(check: false))["state"]);

            var file = await api.ExportProjectAsync(id);
            await api.RemoveProjectAsync(id);
            Assert.DoesNotContain(ProjectViews.Projects(await api.SnapshotAsync(false)), project => project.Id == id);
            Assert.True(Directory.Exists(folder));
            var (imported, _) = await api.ImportProjectAsync(file);
            Assert.NotNull(imported);
            Assert.Contains(ProjectViews.Projects(await api.SnapshotAsync(false)), project => project.Id == imported && project.Name == "Settings sample");
            await Assert.ThrowsAsync<InvalidOperationException>(() => api.ImportProjectAsync("not json"));

            await api.StartOverAsync(imported!);
            Assert.Contains(ProjectViews.Projects(await api.SnapshotAsync(false)), project => project.Id == imported);
        }
        finally
        {
            await host.DisposeAsync();
            try { data.Delete(true); } catch { }
        }
    }
}
