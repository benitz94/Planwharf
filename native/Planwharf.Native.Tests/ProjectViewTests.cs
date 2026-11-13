using System.Text.Json.Nodes;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public class ProjectViewTests
{
    [Fact]
    public void Reads_the_server_address_line() =>
        Assert.Equal("http://127.0.0.1:4173", ServerHost.ReadAddress("Planwharf starting\nhttp://127.0.0.1:4173\n"));

    [Fact]
    public void Lists_projects_with_the_crew_status()
    {
        var projects = ProjectViews.Projects(SampleState.Finished());
        Assert.Equal(["Weather dashboard", "Recipe notes app"], projects.Select(p => p.Name));
        Assert.Equal("Goal reached", projects[0].Status);
        Assert.Equal("Ready", projects[1].Status);
    }

    [Fact]
    public void Shows_the_chat_and_the_finished_work_without_model_changes()
    {
        var view = ProjectViews.Project(SampleState.Finished(), "weather")!;
        Assert.Equal(2, view.Messages.Count);
        Assert.Equal("Grok 4.7", view.Messages[1].Who);
        Assert.Equal(["Build · Opus 5.5", "Verify · GPT-6 Astra"], view.Work!.Steps.Select(s => $"{s.Role} · {s.Model}"));
        Assert.Equal(2, view.Work.Done);
        Assert.DoesNotContain("Model changed.", view.Work.Results);
    }

    [Fact]
    public void Offers_one_clear_next_action()
    {
        Assert.Equal(CrewAction.LetTheCrewDoIt, ProjectViews.Project(SampleState.Finished(), "weather")!.Action);
        Assert.Equal(CrewAction.StopTheCrew, ProjectViews.Project(SampleState.Finished(stopReason: null), "weather")!.Action);
        var idle = ProjectViews.Project(SampleState.Finished(), "notes")!;
        Assert.StartsWith("Talk with the lead about what to build", idle.Hint);
    }

    [Fact]
    public void Says_why_the_crew_stopped_in_plain_English()
    {
        var view = ProjectViews.Project(SampleState.Finished("LEAD_UNCONFIRMED"), "weather")!;
        Assert.Equal("Work finished, but the lead did not confirm the goal. Check the result in the project folder.", view.Hint);
        Assert.Equal("The crew could not start the work: no folder", ProjectViews.StopSentence("START_FAILED: no folder"));
    }

    [Fact]
    public void The_goal_agreed_in_the_chat_is_offered_only_when_it_is_new()
    {
        var state = SampleState.Finished();
        Assert.Null(ProjectViews.Project(state, "weather")!.NextGoal);
        state["projects"]![0]!["goal"] = "Something older";
        Assert.Equal("Add a 7-day forecast page with charts and tests.", ProjectViews.Project(state, "weather")!.NextGoal);
    }

    // Planwharf keeps the chat newest first: the goal offered is the one from the lead's latest reply,
    // never an old one from the start of the project (it once restarted a finished job).
    [Fact]
    public void The_goal_offered_comes_from_the_latest_reply_not_the_first()
    {
        var state = SampleState.Finished();
        state["projects"]![0]!["goal"] = "Fix the build errors of the pilot.";
        var older = state["asks"]![0]!.DeepClone();
        var newer = state["asks"]![0]!.DeepClone();
        newer["at"] = "2026-11-13T12:00:00Z";
        newer["answer"] = "Job 02 is ready.\n\nSuggested next goal: Port the five modules of job 02.";
        state["asks"] = new System.Text.Json.Nodes.JsonArray(newer, older);
        Assert.Equal("Port the five modules of job 02.", ProjectViews.Project(state, "weather")!.NextGoal);
    }
}

public class AppLayoutTests
{
    // A published app carries Planwharf in "app" and Node.js in "node" next to the program.
    [Fact]
    public void Finds_the_bundled_app_files_and_node()
    {
        var root = Directory.CreateTempSubdirectory("pw-native-layout-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "app"));
            File.WriteAllText(Path.Combine(root.FullName, "app", "planwharf.cjs"), "");
            Directory.CreateDirectory(Path.Combine(root.FullName, "node"));
            var node = Path.Combine(root.FullName, "node", OperatingSystem.IsWindows() ? "node.exe" : "node");
            File.WriteAllText(node, "");
            Assert.Equal(Path.Combine(root.FullName, "app"), ServerHost.FindAppFolder(root.FullName));
            Assert.Equal(node, ServerHost.FindNode(root.FullName));
        }
        finally { root.Delete(true); }
    }
}

public class DataFolderTests
{
    // A new Linux user has no ~/.config yet: the data folder must still be a real, absolute folder.
    [Fact]
    public void The_data_folder_is_absolute_even_before_the_config_folder_exists()
    {
        if (!OperatingSystem.IsLinux()) return;
        var saved = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var missing = Path.Combine(Path.GetTempPath(), "pw-native-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", missing);
            var folder = ServerHost.DefaultDataFolder();
            Assert.True(Path.IsPathRooted(folder), folder);
            Assert.Equal(Path.Combine(missing, "Planwharf", "data"), folder);
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", saved); if (Directory.Exists(missing)) Directory.Delete(missing, true); }
    }
}

public class MacDataFolderTests
{
    // On a Mac the native app uses the desktop app's folder, so both see the same projects.
    [Fact]
    public void On_a_Mac_the_data_folder_is_the_desktop_apps()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.EndsWith(Path.Combine("Library", "Application Support", "Planwharf", "data"), ServerHost.DefaultDataFolder());
    }
}

public class GoalFromChatTests
{
    // Only a goal the lead suggested is offered to the crew: a plain question is not work to do.
    [Fact]
    public void A_question_without_a_suggested_goal_is_not_offered_as_the_goal()
    {
        var state = SampleState.Finished();
        state["projects"]![0]!["goal"] = "Something older";
        state["asks"]![0]!["question"] = "Why two workers on this project?";
        state["asks"]![0]!["answer"] = "Because the work has a build step and a check step.";
        var view = ProjectViews.Project(state, "weather")!;
        Assert.Null(view.NextGoal);
    }
}

public class TextRunsTests
{
    [Fact]
    public void Bold_and_code_are_shown_as_such_not_as_asterisks()
    {
        var runs = Planwharf.Native.Core.TextRuns.Parse("Found **24 files** in `tools/check.py`, done.");
        Assert.Equal(["Found ", "24 files", " in ", "tools/check.py", ", done."], runs.Select(run => run.Text));
        Assert.True(runs[1].Bold); Assert.True(runs[3].Code); Assert.False(runs[0].Bold || runs[0].Code);
    }
}
