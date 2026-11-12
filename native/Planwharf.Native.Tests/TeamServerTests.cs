using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

// New project, folder, lead and roles, against the real server as the app starts it.
public class TeamServerTests
{
    [Fact]
    public async Task Creates_a_project_and_sets_its_folder_lead_and_roles()
    {
        var data = Directory.CreateTempSubdirectory("pw-native-team-");
        var host = new ServerHost(data.FullName);
        try
        {
            var api = new PlanwharfApi(await host.StartAsync("v0.0.1", TimeSpan.FromSeconds(60)));
            var folder = Path.Combine(data.FullName, "my-site");
            Directory.CreateDirectory(folder);
            var id = await api.CreateProjectAsync("Sample site", folder);
            Assert.NotNull(id);
            var state = await api.SnapshotAsync(readOnly: false, id);
            Assert.Contains(ProjectViews.Projects(state), project => project.Id == id && project.Name == "Sample site");

            // Without a folder, Planwharf creates one for the project.
            await Assert.ThrowsAsync<InvalidOperationException>(() => api.CreateProjectAsync("Second sample", ""));
            await api.SetProjectsRootAsync(data.FullName);
            Assert.Equal(Path.GetFullPath(data.FullName), await api.ProjectsRootAsync());
            var second = await api.CreateProjectAsync("Second sample", "");
            Assert.NotNull(second);
            Assert.NotEqual(id, second);

            var moved = Path.Combine(data.FullName, "moved-site");
            Directory.CreateDirectory(moved);
            await api.SetFolderAsync(id!, moved);
            state = await api.SnapshotAsync(readOnly: false, id);
            Assert.DoesNotContain("Set the project folder in Settings.", TeamViews.Missing(state, id));

            var team = TeamViews.Team(state);
            Assert.NotEmpty(team.Models);
            var lead = team.Models.First(model => model.CanLead);
            await api.SetLeadAsync(id!, lead.Id, lead.Lead);
            var builder = team.Models.First();
            await api.SetRolesAsync(id!, builder.Id, new Dictionary<string, bool> { ["worker"] = !builder.Build });
            team = TeamViews.Team(await api.SnapshotAsync(readOnly: false, id));
            Assert.Equal(lead.Id, team.LeadId);
            Assert.Equal(!builder.Build, team.Models.First(model => model.Id == builder.Id).Build);

            // Bench: no role at all in this project.
            await api.BenchAsync(id!, builder.Id);
            Assert.True(TeamViews.Team(await api.SnapshotAsync(readOnly: false, id)).Models.First(model => model.Id == builder.Id).Bench);

            // An AI tool off, then on again.
            var provider = team.Providers.First();
            await api.SetProviderEnabledAsync(provider.Id, false);
            Assert.False(TeamViews.Team(await api.SnapshotAsync(readOnly: false, id)).Providers.First(item => item.Id == provider.Id).Enabled);
            await api.SetProviderEnabledAsync(provider.Id, true);

            // An API connection stores the variable's name, never a key; its models join the list.
            await api.AddConnectionAsync("Sample API", "https://api.example.com/v1", "openai", "SAMPLE_API_KEY", ["sample-chat"], toolsEnabled: false);
            team = TeamViews.Team(await api.SnapshotAsync(readOnly: false, id));
            Assert.Contains(team.Providers, item => item.Label == "Sample API" && item.Models == 1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => api.AddConnectionAsync("Sample API", "https://api.example.com/v1", "openai", null, ["sample-chat"], false));
        }
        finally
        {
            await host.DisposeAsync();
            try { data.Delete(true); } catch { }
        }
    }
}
