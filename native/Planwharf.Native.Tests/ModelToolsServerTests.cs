using System.Text.Json.Nodes;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

[Collection("Real server")]
public class ModelToolsServerTests
{
    [Fact]
    public async Task Saves_a_models_tools_and_returns_to_the_project_default()
    {
        var data = Directory.CreateTempSubdirectory("pw-native-model-tools-");
        var host = new ServerHost(data.FullName);
        try
        {
            var api = new PlanwharfApi(await host.StartAsync("v0.0.1", TimeSpan.FromSeconds(60)));
            var folder = Directory.CreateDirectory(Path.Combine(data.FullName, "site")).FullName;
            var id = (await api.CreateProjectAsync("Tools sample", folder))!;
            var state = await api.SnapshotAsync(false, id);
            var models = (state["models"] as JsonArray)!.OfType<JsonObject>().ToList();
            var modelId = models.First()["id"]!.GetValue<string>();
            await api.SetToolsAsync(id, new Dictionary<string, bool> { ["browser"] = true });
            await api.SetToolsAsync(id, new Dictionary<string, bool> { ["browser"] = false, ["memory"] = true }, modelId);
            state = await api.SnapshotAsync(false, id);
            var view = ModelToolsView.Read(state, id, modelId);
            Assert.False(view.SameAsProject);
            Assert.False(view.Tools["browser"]);
            Assert.True(view.Tools["memory"]);
            Assert.True(state["projectTools"]![id]!["browser"]!.GetValue<bool>());
            var other = models.Skip(1).First()["id"]!.GetValue<string>();
            Assert.True(ModelToolsView.Read(state, id, other).SameAsProject);
            Assert.True(ModelToolsView.Read(state, id, other).Tools["browser"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => api.SetToolsAsync(id, new Dictionary<string, bool>(), "missing-model"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => api.SetToolsAsync(id, null));
            await api.SetToolsAsync(id, null, modelId);
            view = ModelToolsView.Read(await api.SnapshotAsync(false, id), id, modelId);
            Assert.True(view.SameAsProject);
            Assert.True(view.Tools["browser"]);
            Assert.False(view.Tools["memory"]);
        }
        finally
        {
            await host.DisposeAsync();
            try { data.Delete(true); } catch { }
        }
    }
}
