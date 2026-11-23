// Tests for ProjectExport.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ProjectExportTests
{
    [Fact]
    public void Sanitize_RedactsBearerTokensAndApiKeys()
    {
        var raw = new JsonObject
        {
            ["text"] = "Authorization: Bearer sk-ant-api03-1234567890abcdef and gh_p12345678901234567890"
        };
        var sanitized = ProjectExport.Sanitize(raw) as JsonObject;
        Assert.NotNull(sanitized);
        var text = sanitized["text"]?.GetValue<string>();
        Assert.Contains("Bearer [REDACTED]", text);
    }

    [Fact]
    public void Sanitize_StripsForbiddenKeys_CredentialsAuthSecretsLogs()
    {
        var raw = new JsonObject
        {
            ["name"] = "MyProject",
            ["secret"] = "my_secret_token",
            ["password"] = "pass123",
            ["environment"] = new JsonObject { ["KEY"] = "VAL" }
        };
        var sanitized = ProjectExport.Sanitize(raw) as JsonObject;
        Assert.NotNull(sanitized);
        Assert.True(sanitized.ContainsKey("name"));
        Assert.False(sanitized.ContainsKey("secret"));
        Assert.False(sanitized.ContainsKey("password"));
        Assert.False(sanitized.ContainsKey("environment"));
    }

    [Fact]
    public void ExportProject_FiltersExecutionByProjectId_ProducesSchema1Bundle()
    {
        var project = new JsonObject
        {
            ["project"] = new JsonObject { ["id"] = "p1", ["name"] = "Proj 1" },
            ["revisions"] = new JsonArray { new JsonObject { ["direction"] = "Build app" } }
        };
        var execution = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "MT-1", ["projectId"] = "p1", ["status"] = "PAUSED" },
                new JsonObject { ["id"] = "MT-2", ["projectId"] = "p2", ["status"] = "PAUSED" }
            },
            ["subTasks"] = new JsonArray(),
            ["results"] = new JsonArray(),
            ["checkpoints"] = new JsonArray(),
            ["timeline"] = new JsonArray()
        };

        var bundle = ProjectExport.ExportProject(project, execution);
        Assert.Equal("planwharf-project-export", bundle["format"]?.GetValue<string>());
        Assert.Equal(1, bundle["schema"]?.GetValue<int>());
        var exec = bundle["execution"] as JsonObject;
        Assert.NotNull(exec);
        var megas = exec["megaTasks"] as JsonArray;
        Assert.NotNull(megas);
        Assert.Single(megas);
        Assert.Equal("MT-1", megas[0]?["id"]?.GetValue<string>());
    }

    [Fact]
    public void RemapExecution_RegeneratesUUIDsForMegaSubCheckpointsAndTimeline()
    {
        var execution = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "MT-old", ["status"] = "PAUSED" }
            },
            ["subTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "ST-old", ["megaTaskId"] = "MT-old" }
            },
            ["results"] = new JsonArray(),
            ["checkpoints"] = new JsonArray
            {
                new JsonObject { ["id"] = "CP-old", ["megaTaskId"] = "MT-old" }
            },
            ["timeline"] = new JsonArray()
        };

        var remapped = ProjectExport.RemapExecution(execution, "new-p1");
        var megas = remapped["megaTasks"] as JsonArray;
        Assert.NotNull(megas);
        Assert.Single(megas);
        var newMegaId = megas[0]?["id"]?.GetValue<string>();
        Assert.NotEqual("MT-old", newMegaId);
        Assert.StartsWith("MT-", newMegaId!);

        var subs = remapped["subTasks"] as JsonArray;
        Assert.NotNull(subs);
        Assert.Single(subs);
        Assert.Equal(newMegaId, subs[0]?["megaTaskId"]?.GetValue<string>());
    }

    [Fact]
    public void ContinueFrom_PausedWithCheckpoint_ResolvesResumeAction()
    {
        var execution = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "MT-1", ["status"] = "PAUSED", ["currentCheckpointId"] = "CP-1" }
            }
        };

        var cont = ProjectExport.ContinueFrom(execution);
        Assert.Equal("resume", cont["action"]?.GetValue<string>());
        Assert.Equal("MT-1", cont["megaTaskId"]?.GetValue<string>());
        Assert.Equal("CP-1", cont["checkpointId"]?.GetValue<string>());
    }

    [Fact]
    public void RemapExecution_RebindsDependenciesAndCheckpointsCorrectly()
    {
        var execution = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "MT-orig", ["status"] = "PAUSED", ["currentCheckpointId"] = "CP-orig" }
            },
            ["subTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "ST-1", ["megaTaskId"] = "MT-orig", ["dependencies"] = new JsonArray() },
                new JsonObject { ["id"] = "ST-2", ["megaTaskId"] = "MT-orig", ["dependencies"] = new JsonArray { "ST-1" } }
            },
            ["results"] = new JsonArray(),
            ["checkpoints"] = new JsonArray
            {
                new JsonObject { ["id"] = "CP-orig", ["megaTaskId"] = "MT-orig" }
            },
            ["timeline"] = new JsonArray
            {
                new JsonObject
                {
                    ["reference"] = new JsonObject { ["megaTaskId"] = "MT-orig", ["checkpointId"] = "CP-orig" }
                }
            }
        };

        var remapped = ProjectExport.RemapExecution(execution, "new-proj-id");
        var megas = remapped["megaTasks"] as JsonArray;
        var subs = remapped["subTasks"] as JsonArray;
        var cps = remapped["checkpoints"] as JsonArray;
        var timeline = remapped["timeline"] as JsonArray;

        Assert.NotNull(megas);
        Assert.NotNull(subs);
        Assert.NotNull(cps);
        Assert.NotNull(timeline);

        var newMegaId = megas[0]?["id"]?.GetValue<string>();
        var newCpId = cps[0]?["id"]?.GetValue<string>();
        Assert.Equal(newCpId, megas[0]?["currentCheckpointId"]?.GetValue<string>());

        var newSub1Id = subs[0]?["id"]?.GetValue<string>();
        var sub2Deps = subs[1]?["dependencies"] as JsonArray;
        Assert.NotNull(sub2Deps);
        Assert.Equal(newSub1Id, sub2Deps[0]?.GetValue<string>());

        var timeRef = timeline[0]?["reference"] as JsonObject;
        Assert.NotNull(timeRef);
        Assert.Equal(newMegaId, timeRef["megaTaskId"]?.GetValue<string>());
        Assert.Equal(newCpId, timeRef["checkpointId"]?.GetValue<string>());
    }

    [Fact]
    public void ImportProject_RestoresProjectIntoNotebookAndRemapsExecution()
    {
        var notebook = new JsonObject
        {
            ["schema"] = 2,
            ["projects"] = new JsonArray { Plan.Example() }
        };

        var execution = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "MT-1", ["projectId"] = "biblioteca", ["status"] = "PAUSED", ["currentCheckpointId"] = "CP-1" }
            },
            ["subTasks"] = new JsonArray(),
            ["results"] = new JsonArray(),
            ["checkpoints"] = new JsonArray
            {
                new JsonObject { ["id"] = "CP-1", ["projectId"] = "biblioteca", ["megaTaskId"] = "MT-1" }
            },
            ["timeline"] = new JsonArray()
        };

        var bundle = ProjectExport.ExportProject(Plan.Example(), execution);
        var imported = ProjectExport.ImportProject(notebook, bundle);
        Assert.NotNull(imported["notebook"]);
        Assert.NotNull(imported["project"]);
        Assert.NotNull(imported["execution"]);
        var cont = imported["continue"] as JsonObject;
        Assert.NotNull(cont);
        Assert.Equal("resume", cont["action"]?.GetValue<string>());
    }

    [Fact]
    public void ImportProject_InvalidOrUnsupportedExport_ThrowsInvalidOperationException()
    {
        var notebook = new JsonObject { ["schema"] = 2, ["projects"] = new JsonArray { Plan.Example() } };
        var badBundle = new JsonObject { ["format"] = "other-format", ["schema"] = 2 };

        var ex = Assert.Throws<InvalidOperationException>(() => ProjectExport.ImportProject(notebook, badBundle));
        Assert.Equal("Import a Planwharf project export (schema 1).", ex.Message);
    }
}
