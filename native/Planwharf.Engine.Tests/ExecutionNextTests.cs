// Tests for ExecutionNext.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ExecutionNextTests
{
    [Fact]
    public void ExecutionNext_ReadyForApproval_ReturnsApprovalPrompt()
    {
        var mega = new JsonObject
        {
            ["id"] = "MT-1",
            ["status"] = "READY_FOR_APPROVAL"
        };
        var next = ExecutionNext.GetExecutionNext(mega);
        Assert.Equal("Review the proposed slices and approve this MegaTask.", next);
    }

    [Fact]
    public void ExecutionNext_Running_ReturnsActiveSubTaskOrPrerequisites()
    {
        var mega = new JsonObject
        {
            ["id"] = "MT-1",
            ["status"] = "RUNNING"
        };
        var durable = new JsonObject
        {
            ["subTasks"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "ST-1",
                    ["megaTaskId"] = "MT-1",
                    ["title"] = "First Slice",
                    ["status"] = "RUNNING"
                }
            }
        };

        var next = ExecutionNext.GetExecutionNext(mega, durable);
        Assert.Equal("Work in progress: First Slice (ST-1).", next);
    }

    [Fact]
    public void ExecutionNext_BlockedOrFailed_ReturnsRecoveryBlockersOrCheckpointAction()
    {
        var mega = new JsonObject
        {
            ["id"] = "MT-1",
            ["status"] = "FAILED",
            ["blockers"] = new JsonArray { "Network down" }
        };
        var durable = new JsonObject
        {
            ["subTasks"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "ST-1",
                    ["megaTaskId"] = "MT-1",
                    ["status"] = "FAILED"
                }
            }
        };

        var next = ExecutionNext.GetExecutionNext(mega, durable);
        Assert.Equal("Resolve blockers: Network down", next);
    }

    [Fact]
    public void HomeNext_UnlinkedFolder_PromptsRelink()
    {
        var item = new JsonObject
        {
            ["project"] = new JsonObject
            {
                ["folder"] = Path.Combine(Path.GetTempPath(), "non_existent_folder_" + Guid.NewGuid().ToString("N"))
            }
        };
        var next = ExecutionNext.GetHomeNext(item);
        Assert.Equal("Relink the project folder on this PC.", next);
    }

    [Fact]
    public void HomeNext_MegaTaskPendingApproval_PromptsApproveMegaTask()
    {
        var temp = Path.GetTempPath();
        var item = new JsonObject
        {
            ["project"] = new JsonObject
            {
                ["id"] = "p1",
                ["folder"] = temp
            }
        };
        var durable = new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "MT-1",
                    ["projectId"] = "p1",
                    ["status"] = "READY_FOR_APPROVAL"
                }
            }
        };

        var next = ExecutionNext.GetHomeNext(item, null, durable);
        Assert.Equal("Approve the prepared MegaTask.", next);
    }

    [Fact]
    public void HomeNext_CanResume_PromptsResumeFromCheckpoint()
    {
        var temp = Path.GetTempPath();
        var item = new JsonObject
        {
            ["project"] = new JsonObject
            {
                ["id"] = "p1",
                ["folder"] = temp
            }
        };
        var row = new JsonObject
        {
            ["canResume"] = true
        };

        var next = ExecutionNext.GetHomeNext(item, row, null);
        Assert.Equal("Resume from checkpoint.", next);
    }
}
