// Tests for Activity.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ActivityTests
{
    [Fact]
    public void CommandActivity_IdentifiesReadingSearchingTestingBuildingAndWriting()
    {
        Assert.Equal("Reading file.cs.", Activity.CommandActivity("cat file.cs"));
        Assert.Equal("Searching project files near file.cs.", Activity.CommandActivity("grep pattern file.cs"));
        Assert.Equal("Inspecting project folders.", Activity.CommandActivity("ls"));
        Assert.Equal("Running tests for tests.cs.", Activity.CommandActivity("dotnet test tests.cs"));
        Assert.Equal("Building the application using Program.cs.", Activity.CommandActivity("csc Program.cs"));
        Assert.Equal("Writing output.txt.", Activity.CommandActivity("tee output.txt"));
    }

    [Fact]
    public void ToolActivity_ParsesReadWriteShellSearchAndScreenshotTools()
    {
        Assert.Equal("Reading test.txt.", Activity.ToolActivity("read_file", new JsonObject { ["file_path"] = "/path/to/test.txt" }));
        Assert.Equal("Writing project files: test.txt.", Activity.ToolActivity("write_file", new JsonObject { ["file_path"] = "/path/to/test.txt" }));
        Assert.Equal("Reading test.txt.", Activity.ToolActivity("run_command", new JsonObject { ["command"] = "cat test.txt" }));
        Assert.Equal("Checking the application in the browser.", Activity.ToolActivity("screenshot", new JsonObject()));
    }

    [Fact]
    public void PlainActivity_ExtractsErrorAndTestSummariesWhileFilteringJson()
    {
        Assert.Equal("", Activity.PlainActivity("{\"event\": \"test\"}"));
        Assert.Equal("Hit an error: Error: something failed", Activity.PlainActivity("Error: something failed"));
        Assert.Equal("Test output: 5 passed", Activity.PlainActivity("5 passed"));
        Assert.Equal("Writing project files: wrote changes", Activity.PlainActivity("wrote changes"));
    }

    [Fact]
    public void InterpretActivity_AliveVersusStopped_IncludesLastRecordedAction()
    {
        var tail = "{\"item\":{\"type\":\"command_execution\",\"command\":\"cat file.cs\"}}\n";
        var aliveAction = Activity.InterpretActivity(tail, null, true);
        Assert.Equal("Reading file.cs.", aliveAction);

        var stoppedAction = Activity.InterpretActivity(tail, null, false);
        Assert.Equal("Stopped. Last recorded action: Reading file.cs.", stoppedAction);
    }

    [Fact]
    public void WorkerReport_ExtractsFinalResponsePayloadFromStream()
    {
        var stream = "{\"event\":\"result\",\"result\":{\"response\":\"Task complete!\"}}\n";
        var report = Activity.WorkerReport(stream);
        Assert.Equal("Task complete!", report);
    }
}
