// Deferred cases from native/porting/jobs/23-phase2-rest.md. These stay unimplemented:
// - attempts.js, createAttemptEngine, and every sentinel test that needs the real store
// - createServer, createOrchestrator, spawnWorker, runner.cjs, and the HTTP loops in
//   task-completion.test.cjs, grill-chat.test.cjs, project-export.test.cjs, project-teams.test.cjs,
//   start-over.test.cjs (archiveProject), and worker-containment.test.cjs (launch artifacts and provider cwd)
// - Live npm test, live node --test, and live taskkill
// - Byte-identical randomUUID values across JavaScript and C#
// - gitEnvironment and within as parity JSON
// - The browser root / window / AMD wrappers, models.json, and provider-catalog
// - A second process appending to invocations.jsonl at the same time
// - Reading the checkout's plan.json or .git
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class PlanTests
{
    const string Stamp = "2026-11-20T00:00:00.000Z";

    [Fact]
    public void Example_is_the_neighborhood_library_fixture()
    {
        var example = Plan.Example();
        Assert.Equal(["schema", "project", "revisions", "tasks"], Keys(example));
        Assert.Equal(1, example["schema"]!.GetValue<int>());
        Assert.Equal(["id", "name"], Keys(example["project"]!.AsObject()));
        Assert.Equal("biblioteca", Text(example["project"]!["id"]));
        Assert.Equal("Neighborhood library", Text(example["project"]!["name"]));
        Assert.Equal(2, example["revisions"]!.AsArray().Count);
        var first = example["revisions"]![0]!.AsObject();
        Assert.Equal(["id", "mode", "title", "createdAt", "direction", "state", "closed", "open"], Keys(first));
        Assert.Equal("Starting point", Text(first["title"]));
        Assert.Equal("Which location should we choose?", Text(first["open"]![0]));
        var task = example["tasks"]![0]!.AsObject();
        Assert.Equal(["id", "projectId", "revision_id", "title", "detail", "status"], Keys(task));
        Assert.Equal("calendario", Text(task["id"]));
        Assert.Equal("running", Text(task["status"]));
        Assert.True(Plan.Validate(example));
        Assert.True(Plan.ValidateProject(example));
    }

    [Fact]
    public void Validate_accepts_schema_1_and_schema_2_and_rejects_a_broken_notebook()
    {
        var example = Plan.Example();
        Assert.True(Plan.Validate(example));
        Assert.False(Plan.Validate(null));
        Assert.False(Plan.Validate(new JsonObject { ["schema"] = 1 }));
        Assert.False(Plan.Validate(new JsonObject { ["schema"] = 2, ["projects"] = new JsonArray() }));
        Assert.False(Plan.Validate(new JsonObject { ["schema"] = 3, ["projects"] = new JsonArray { example.DeepClone() } }));

        var unknown = (JsonObject)example.DeepClone();
        unknown["project"]!.AsObject()["notes"] = "nope";
        Assert.False(Plan.Validate(unknown));

        var duplicateRevision = (JsonObject)example.DeepClone();
        duplicateRevision["revisions"]![1]!["id"] = 1;
        Assert.False(Plan.Validate(duplicateRevision));

        var notebook = Plan.CreateProject(example, Named("Second", "Ship the second project"), Stamp);
        Assert.True(Plan.Validate(notebook));
        Assert.Equal(2, notebook["schema"]!.GetValue<int>());
        var second = notebook["projects"]![1]!.AsObject();
        second["tasks"]!.AsArray().Add(Task("task-1", Text(second["project"]!["id"]), 1, "Work", "", "running"));
        Assert.True(Plan.Validate(notebook));

        var twoRunning = (JsonObject)example.DeepClone();
        twoRunning["tasks"]!.AsArray().Add(Task("other", "biblioteca", 2, "Another", "detail", "running"));
        Assert.False(Plan.Validate(twoRunning));
        Assert.False(Plan.ValidateProject(twoRunning));
    }

    [Fact]
    public void Folder_and_github_normalization_rejects_secrets_nul_and_a_501_character_path()
    {
        Assert.True(Plan.IsFolderPath(@"C:\Library"));
        Assert.True(Plan.IsFolderPath("C:/Library"));
        Assert.True(Plan.IsFolderPath(@"\\server\share"));
        Assert.True(Plan.IsFolderPath("/home/library"));
        Assert.False(Plan.IsFolderPath("/"));
        Assert.False(Plan.IsFolderPath(""));
        Assert.False(Plan.IsFolderPath(null));
        Assert.False(Plan.IsFolderPath("relative/path"));
        Assert.False(Plan.IsFolderPath("C:/Library\0hidden"));
        Assert.False(Plan.IsFolderPath("C:/" + new string('a', 498)));
        Assert.True(Plan.IsFolderPath("C:/" + new string('a', 497)));

        Assert.False(Plan.IsGithubUrl("https://user:secret@forge.example/owner/repo"));
        Assert.False(Plan.IsGithubUrl("https://github.com/../repo"));
        Assert.False(Plan.IsGithubUrl("https://github.com/owner/.."));
        Assert.False(Plan.IsGithubUrl("https://github.com/./repo"));
        Assert.False(Plan.IsGithubUrl("https:///owner/repo"));
        Assert.False(Plan.IsGithubUrl("git@github.com:owner/repo.git"));
        Assert.True(Plan.IsGithubUrl("https://forge.example:3000/org/owner/repo"));
        Assert.True(Plan.IsGithubUrl("https://forge.example/git/owner/repo.git"));
        Assert.True(Plan.IsGithubUrl("https://github.com/owner/repo"));

        var example = Plan.Example();
        var placed = Plan.CreateProject(example, new JsonObject
        {
            ["name"] = "Harbor",
            ["direction"] = "Open the stacks",
            ["folder"] = " \"file:///C:/Library\" ",
            ["github"] = "git@github.com:owner/repo.git"
        }, Stamp);
        var project = placed["projects"]![1]!["project"]!.AsObject();
        Assert.Equal(["id", "name", "folder", "github"], Keys(project));
        Assert.Equal("C:/Library", Text(project["folder"]));
        Assert.Equal("https://github.com/owner/repo", Text(project["github"]));

        var drive = Plan.CreateProject(example, new JsonObject
        {
            ["name"] = "Drive",
            ["direction"] = "Use the drive",
            ["folder"] = "/C:/Library"
        }, Stamp);
        Assert.Equal("C:/Library", Text(drive["projects"]![1]!["project"]!["folder"]));

        Assert.Equal("Folder must be an absolute path on this PC.", Throws(() => Plan.CreateProject(example, new JsonObject
        {
            ["name"] = "Bad",
            ["direction"] = "Goal",
            ["folder"] = "relative/path"
        }, Stamp)));
        Assert.Equal("Folder must be an absolute path on this PC.", Throws(() => Plan.CreateProject(example, new JsonObject
        {
            ["name"] = "Nul",
            ["direction"] = "Goal",
            ["folder"] = "C:/Library\0hidden"
        }, Stamp)));
        Assert.Equal("Folder must be an absolute path on this PC.", Throws(() => Plan.CreateProject(example, new JsonObject
        {
            ["name"] = "Long",
            ["direction"] = "Goal",
            ["folder"] = "C:/" + new string('a', 498)
        }, Stamp)));
        Assert.Equal("Repository must be an HTTPS GitHub or Gitea owner/repository URL.", Throws(() => Plan.CreateProject(example, new JsonObject
        {
            ["name"] = "Secret",
            ["direction"] = "Goal",
            ["github"] = "https://user:secret@forge.example/owner/repo"
        }, Stamp)));
        Assert.Equal("Repository must be an HTTPS GitHub or Gitea owner/repository URL.", Throws(() => Plan.CreateProject(example, new JsonObject
        {
            ["name"] = "Short",
            ["direction"] = "Goal",
            ["github"] = "https://github.com/onlyone"
        }, Stamp)));
    }

    [Fact]
    public void Create_project_wraps_schema_1_and_suffixes_colliding_ids_without_cutting_the_suffix()
    {
        var example = Plan.Example();
        var created = Plan.CreateProject(example, Named("Harbor", "Open the stacks"), Stamp);
        Assert.Equal(1, example["schema"]!.GetValue<int>());
        Same(example, created["projects"]![0]);
        var added = created["projects"]![1]!.AsObject();
        Assert.Equal(["schema", "project", "revisions", "tasks"], Keys(added));
        Assert.Equal("harbor", Text(added["project"]!["id"]));
        Assert.Equal("Harbor", Text(added["project"]!["name"]));
        var revision = added["revisions"]![0]!.AsObject();
        Assert.Equal("", Text(revision["title"]));
        Assert.Equal(Stamp, Text(revision["createdAt"]));
        Assert.Equal("Open the stacks", Text(revision["direction"]));
        Assert.Empty(added["tasks"]!.AsArray());

        var again = Plan.CreateProject(created, Named(" harbor ", "Again"), Stamp);
        Assert.Equal("harbor-2", Text(again["projects"]![2]!["project"]!["id"]));
        Same(created["projects"]![0], again["projects"]![0]);
        Same(created["projects"]![1], again["projects"]![1]);

        var mark = Plan.CreateProject(example, Named("Å", "Marked"), Stamp);
        Assert.Equal("a", Text(mark["projects"]![1]!["project"]!["id"]));
        Assert.Equal("Å", Text(mark["projects"]![1]!["project"]!["name"]));

        var punctuation = Plan.CreateProject(example, Named("...", "Punctuation"), Stamp);
        Assert.Equal("project", Text(punctuation["projects"]![1]!["project"]!["id"]));

        var longName = new string('a', 49);
        var first = Plan.CreateProject(example, Named(longName, "Long"), Stamp);
        var second = Plan.CreateProject(first, Named(longName, "Longer"), Stamp);
        var baseId = Text(first["projects"]![1]!["project"]!["id"]);
        var suffixed = Text(second["projects"]![2]!["project"]!["id"]);
        Assert.Equal(48, baseId.Length);
        Assert.Equal(new string('a', 48), baseId);
        Assert.Equal(baseId + "-2", suffixed);
        Assert.True(suffixed.Length > 48);

        Assert.Equal("Fill in name and goal.", Throws(() => Plan.CreateProject(example, Named("  ", "Goal"), Stamp)));
        Assert.Equal("The saved notebook is invalid.", Throws(() => Plan.CreateProject(new JsonObject { ["schema"] = 2, ["projects"] = new JsonArray() }, Named("A", "B"), Stamp)));
    }

    [Fact]
    public void Restore_pauses_a_running_task_set_place_drops_home_and_reset_leaves_the_other_project()
    {
        var notebook = Plan.CreateProject(Plan.Example(), Named("Keep", "Stay"), Stamp);
        var restored = Plan.RestoreProject(notebook, new JsonObject
        {
            ["name"] = "Imported",
            ["owner"] = "not copied",
            ["revisions"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 4,
                    ["mode"] = "manual",
                    ["title"] = "Brought in",
                    ["createdAt"] = Stamp,
                    ["direction"] = "Keep the shelves",
                    ["state"] = "Labeled",
                    ["closed"] = new JsonArray { "Free borrowing" },
                    ["open"] = new JsonArray(),
                    ["extra"] = true
                }
            },
            ["tasks"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "shift",
                    ["revision_id"] = 4,
                    ["title"] = "Staff it",
                    ["detail"] = "Find two people",
                    ["status"] = "running",
                    ["secret"] = "nope"
                }
            },
            ["notes"] = new JsonArray
            {
                new JsonObject { ["id"] = "n1", ["text"] = "A note", ["createdAt"] = Stamp, ["pinned"] = false }
            }
        }, Stamp);
        var item = restored["projects"]![2]!.AsObject();
        Assert.Equal("imported", Text(item["project"]!["id"]));
        Assert.False(item["project"]!.AsObject().ContainsKey("owner"));
        Assert.False(item["revisions"]![0]!.AsObject().ContainsKey("extra"));
        var task = item["tasks"]![0]!.AsObject();
        Assert.Equal(["id", "projectId", "revision_id", "title", "detail", "status"], Keys(task));
        Assert.Equal("paused", Text(task["status"]));
        Assert.Equal("imported", Text(task["projectId"]));
        Assert.False(task.ContainsKey("secret"));
        Assert.True(item.ContainsKey("home"));
        Assert.False(item["home"]!["notes"]![0]!.AsObject().ContainsKey("pinned"));
        Same(notebook["projects"]![0], restored["projects"]![0]);
        Same(notebook["projects"]![1], restored["projects"]![1]);

        var moved = Plan.SetPlace(restored, new JsonObject { ["id"] = "imported", ["folder"] = @"D:\Shelves" });
        var placed = moved["projects"]![2]!.AsObject();
        Assert.Equal(["schema", "project", "revisions", "tasks"], Keys(placed));
        Assert.False(placed.ContainsKey("home"));
        Assert.Equal(@"D:\Shelves", Text(placed["project"]!["folder"]));
        Assert.Equal("Imported", Text(placed["project"]!["name"]));
        Same(item["revisions"], placed["revisions"]);
        Same(item["tasks"], placed["tasks"]);
        Same(restored["projects"]![1], moved["projects"]![1]);

        Assert.Equal("keep", Text(moved["projects"]![1]!["project"]!["id"]));
        var resetKeep = Plan.ResetProject(moved, new JsonObject { ["id"] = "keep", ["direction"] = " Not set yet. " }, Stamp);
        var kept = resetKeep["projects"]![1]!.AsObject();
        Assert.Equal("keep", Text(kept["project"]!["id"]));
        Assert.Equal("Keep", Text(kept["project"]!["name"]));
        Assert.Empty(kept["tasks"]!.AsArray());
        Assert.Single(kept["revisions"]!.AsArray());
        Assert.Equal("Not set yet.", Text(kept["revisions"]![0]!["direction"]));
        Same(moved["projects"]![0], resetKeep["projects"]![0]);
        Same(moved["projects"]![2], resetKeep["projects"]![2]);

        Assert.Equal("The notebook must keep at least one project.", Throws(() => Plan.RemoveProject(Plan.Example(), new JsonObject { ["id"] = "biblioteca" })));
        var only = Plan.CreateProject(Plan.Example(), Named("Only", "One"), Stamp);
        only["projects"]!.AsArray().RemoveAt(0);
        Assert.Equal("The notebook must keep at least one project.", Throws(() => Plan.RemoveProject(only, new JsonObject { ["id"] = "only" })));
        var removed = Plan.RemoveProject(restored, new JsonObject { ["id"] = "biblioteca" });
        Assert.Equal(2, removed["projects"]!.AsArray().Count);
        Same(restored["projects"]![1], removed["projects"]![0]);
        Same(restored["projects"]![2], removed["projects"]![1]);
    }

    [Fact]
    public void Grill_keeps_replaces_or_follows_a_generic_frontier_and_answers_one_direction()
    {
        var example = Plan.Example();
        Assert.Same(example, Plan.StartGrill(example, Stamp));
        Assert.Equal(2, example["revisions"]!.AsArray().Count);

        var replaced = Plan.StartGrill(example, Stamp, new JsonObject { ["replace"] = true });
        Assert.Equal(2, example["revisions"]!.AsArray().Count);
        var grill = replaced["revisions"]![2]!.AsObject();
        Assert.Equal("grill", Text(grill["mode"]));
        Assert.Equal("Grill me", Text(grill["title"]));
        Assert.Equal(Stamp, Text(grill["createdAt"]));
        Assert.Equal("Is this run the next slice of Neighborhood library: Open the library on Harbor Street for a one-month trial.? Recommended: yes.", Text(grill["open"]![0]));
        Assert.Equal("Keep this local: existing files, no paid APIs. Recommended: yes.", Text(grill["open"]![1]));
        Same(example["tasks"], replaced["tasks"]);

        var generic = (JsonObject)example.DeepClone();
        var open = generic["revisions"]![1]!["open"]!.AsArray();
        open.Clear();
        foreach (var question in Plan.GenericFrontier) open.Add(question);
        Assert.True(Plan.IsGenericFrontier(open));
        Assert.False(Plan.IsGenericFrontier(new JsonArray { " " + Plan.GenericFrontier[0] }));
        var advanced = Plan.StartGrill(generic, Stamp);
        Assert.Equal(3, advanced["revisions"]!.AsArray().Count);
        Assert.Equal(2, generic["revisions"]!.AsArray().Count);

        Assert.Equal("No docs in the folder to grill with.", Throws(() => Plan.StartGrillDocs(example, new JsonArray(), Stamp)));
        Assert.Equal("No docs in the folder to grill with.", Throws(() => Plan.StartGrillDocs(example, null, Stamp)));
        Assert.Equal("No docs in the folder to grill with.", Throws(() => Plan.StartGrillDocs(example, new JsonArray { "  " }, Stamp)));

        var starter = Plan.StarterFrontier();
        Assert.Equal("Is this run the next slice of this project? Recommended: yes, stay on the notebook goal.", Text(starter[0]));
        Assert.Equal("Keep this local: existing files, no paid APIs. Recommended: yes.", Text(starter[1]));
        Assert.Equal("Is the slice done when the work is on disk with tests, and nothing extra ships? Recommended: yes.", Text(starter[2]));
        Assert.Equal("Is everything else out of scope until this slice is done? Recommended: yes.", Text(starter[3]));
        var docs = Plan.DocsFrontier(new JsonArray { " README.md ", "PLAN.md" }, new JsonObject { ["name"] = "Harbor", ["goal"] = "Open the stacks", ["folder"] = "C:/Library" });
        Assert.Equal("Is this run the next slice of Harbor: Open the stacks? Recommended: yes.", Text(docs[0]));
        Assert.Equal("Stay inside C:/Library and the files already there. No paid APIs. Recommended: yes.", Text(docs[1]));
        Assert.Equal("Should README.md, PLAN.md bound this slice? Recommended: yes, those docs are the authority.", Text(docs[4]));
        Assert.Equal(
        [
            "What is the smallest next slice that still serves the current goal?",
            "What is explicitly out of scope for this slice?",
            "How will we know this slice is done?",
            "Which assumption, if wrong, would waste the most work?",
            "What must stay true (local, existing files, no paid APIs)?"
        ], Plan.GenericFrontier);

        var appended = Plan.AppendRevision(example, new JsonObject
        {
            ["title"] = " Next ",
            ["direction"] = " East ",
            ["state"] = " Standing ",
            ["closed"] = new JsonArray { "  ", " kept " },
            ["open"] = new JsonArray { " Who? " }
        }, Stamp);
        var manual = appended["revisions"]![2]!.AsObject();
        Assert.Equal(["id", "mode", "title", "createdAt", "direction", "state", "closed", "open"], Keys(manual));
        Assert.Equal("3", manual["id"]!.ToJsonString());
        Assert.Equal("manual", Text(manual["mode"]));
        Assert.Equal("Next", Text(manual["title"]));
        Assert.Equal("East", Text(manual["direction"]));
        Assert.Equal("Standing", Text(manual["state"]));
        Assert.Equal("kept", Text(manual["closed"]![0]));
        Assert.Equal("Who?", Text(manual["open"]![0]));
        Assert.Single(manual["closed"]!.AsArray());

        var round = Plan.Grill(example, new JsonObject
        {
            ["title"] = " After the visit ",
            ["answers"] = new JsonArray
            {
                new JsonObject { ["question"] = "Who covers the first opening shift?", ["action"] = "keep", ["answer"] = " " },
                new JsonObject { ["question"] = "Should we confirm Saturday morning opening?", ["action"] = "close", ["answer"] = " Yes " }
            }
        }, Stamp);
        var answered = round["revisions"]![2]!.AsObject();
        Assert.Equal(["id", "mode", "title", "createdAt", "direction", "state", "closed", "open", "responses"], Keys(answered));
        Assert.Equal("After the visit", Text(answered["title"]));
        Assert.Equal("grill", Text(answered["mode"]));
        Assert.Equal("Who covers the first opening shift?", Text(answered["open"]![0]));
        Assert.Contains("Should we confirm Saturday morning opening? → Yes", Text(answered["closed"]![^1]));
        Assert.Equal("keep", Text(answered["responses"]![0]!["action"]));
        Assert.Equal("", Text(answered["responses"]![0]!["answer"]));

        var twoWays = (JsonObject)example.DeepClone();
        Assert.Equal("Choose only one new direction per round.", Throws(() => Plan.Grill(twoWays, new JsonObject
        {
            ["title"] = "Turn",
            ["answers"] = new JsonArray
            {
                new JsonObject { ["question"] = "Who covers the first opening shift?", ["action"] = "direction", ["answer"] = "North" },
                new JsonObject { ["question"] = "Should we confirm Saturday morning opening?", ["action"] = "direction", ["answer"] = "South" }
            }
        }, Stamp)));
        Assert.Equal(2, twoWays["revisions"]!.AsArray().Count);

        Assert.Equal("Invalid import JSON.", Throws(() => Plan.ImportRevision(example, "{", Stamp)));
        Assert.Equal("Import an object with title, goal, where it stands, decided decisions, and open questions.", Throws(() => Plan.ImportRevision(example, "{\"title\":\"T\",\"direction\":\"D\",\"state\":\"S\",\"closed\":[],\"open\":[],\"extra\":1}", Stamp)));
        var imported = Plan.ImportRevision(example, "{\"title\":\" Brought \",\"direction\":\" North \",\"state\":\" Standing \",\"closed\":[\" done \"],\"open\":[]}", Stamp);
        var importedRevision = imported["revisions"]![2]!.AsObject();
        Assert.Equal("import", Text(importedRevision["mode"]));
        Assert.Equal("Brought", Text(importedRevision["title"]));
        Assert.Equal("North", Text(importedRevision["direction"]));
        Assert.Equal(Stamp, Text(importedRevision["createdAt"]));
        Assert.Equal("done", Text(importedRevision["closed"]![0]));
    }

    [Fact]
    public void Tasks_timing_and_italian_reopened_questions()
    {
        var example = Plan.Example();
        Assert.Equal("This project already has a running task. No second task was created.", Throws(() => Plan.CreateTask(example, new JsonObject
        {
            ["revision_id"] = 2,
            ["title"] = "Another",
            ["detail"] = ""
        })));

        var quiet = (JsonObject)example.DeepClone();
        quiet["tasks"]![0]!["status"] = "paused";
        quiet["tasks"]!.AsArray().Add(Task("task-1", "biblioteca", 2, "Existing", "Kept", "paused"));
        var created = Plan.CreateTask(quiet, new JsonObject { ["revision_id"] = 2, ["title"] = " Shelve ", ["detail"] = "  " });
        var added = created["tasks"]![2]!.AsObject();
        Assert.Equal(["id", "projectId", "revision_id", "title", "detail", "status"], Keys(added));
        Assert.Equal("task-2", Text(added["id"]));
        Assert.Equal("Shelve", Text(added["title"]));
        Assert.Equal("", Text(added["detail"]));
        Assert.Equal("running", Text(added["status"]));
        Assert.False(added.ContainsKey("timing"));

        Assert.Equal("Set the task deadline.", Throws(() => Plan.CreateTask(quiet, new JsonObject
        {
            ["revision_id"] = 2,
            ["title"] = "Due",
            ["detail"] = "When",
            ["timing"] = "deadline",
            ["deadlineAt"] = "nope"
        })));
        Assert.Equal("Choose a deadline or leave timing to the worker.", Throws(() => Plan.TaskTiming(new JsonObject { ["timing"] = "soon" })));
        Assert.Equal("{}", Plan.TaskTiming(new JsonObject()).ToJsonString());
        Assert.Equal("{}", Plan.TaskTiming(new JsonObject { ["timing"] = "" }).ToJsonString());
        Assert.Equal("{}", Plan.TaskTiming(null).ToJsonString());
        Assert.Equal("{\"timing\":\"discretion\"}", Plan.TaskTiming(new JsonObject { ["timing"] = "discretion" }).ToJsonString());
        var deadline = Plan.TaskTiming(new JsonObject { ["timing"] = "deadline", ["deadlineAt"] = "2026-05-01T02:00:00+02:00" });
        Assert.Equal(["timing", "deadlineAt"], Keys(deadline));
        Assert.Equal("deadline", Text(deadline["timing"]));
        Assert.Equal("2026-05-01T00:00:00.000Z", Text(deadline["deadlineAt"]));

        var removed = Plan.RemoveTask(example, new JsonObject { ["id"] = "calendario" });
        Assert.Empty(removed["tasks"]!.AsArray());
        Same(example["revisions"], removed["revisions"]);
        Same(example["project"], removed["project"]);
        Assert.Equal("Job not found.", Throws(() => Plan.RemoveTask(example, new JsonObject { ["id"] = "missing" })));

        // I/i matches under it-IT. tr-TR would lowercase I to dotless ı and miss it.
        var previous = new JsonObject
        {
            ["direction"] = "same",
            ["state"] = "same",
            ["closed"] = new JsonArray { "I", "À", "STRADA" },
            ["open"] = new JsonArray()
        };
        var current = new JsonObject
        {
            ["direction"] = "same",
            ["state"] = "same",
            ["closed"] = new JsonArray { "I", "À", "STRADA" },
            ["open"] = new JsonArray { "i", "à", "strada" }
        };
        var changes = Plan.Diff(previous, current);
        Assert.Equal("open", Text(changes[0]!["key"]));
        var reopened = changes[1]!.AsObject();
        Assert.Equal(["key", "before", "after"], Keys(reopened));
        Assert.Equal("reopened", Text(reopened["key"]));
        Assert.Empty(reopened["before"]!.AsArray());
        Assert.Equal("I", Text(reopened["after"]![0]));
        Assert.Equal("À", Text(reopened["after"]![1]));
        Assert.Equal("STRADA", Text(reopened["after"]![2]));
        Assert.NotEqual("I", "i");
        Assert.NotEqual("À", "à");
        Assert.NotEqual("STRADA", "strada");
    }

    static JsonObject Named(string name, string direction) => new() { ["name"] = name, ["direction"] = direction };

    static JsonObject Task(string id, string projectId, int revisionId, string title, string detail, string status) => new()
    {
        ["id"] = id,
        ["projectId"] = projectId,
        ["revision_id"] = revisionId,
        ["title"] = title,
        ["detail"] = detail,
        ["status"] = status
    };

    static string Text(JsonNode? node) => node!.GetValue<string>();

    static string[] Keys(JsonObject obj)
    {
        var keys = new string[obj.Count];
        var index = 0;
        foreach (var pair in obj) keys[index++] = pair.Key;
        return keys;
    }

    static void Same(JsonNode? left, JsonNode? right) => Assert.Equal(left?.ToJsonString(), right?.ToJsonString());

    static string Throws(Action action) => Assert.Throws<InvalidOperationException>(action).Message;
}
