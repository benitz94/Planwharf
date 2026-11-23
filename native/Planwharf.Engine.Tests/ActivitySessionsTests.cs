// Tests for ActivitySessions.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ActivitySessionsTests : IDisposable
{
    private readonly string _testDir;

    public ActivitySessionsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "pw_acts_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    [Fact]
    public void ReadRecordedTail_WithinRoot_ReadsLast32KBTruncatedToFirstNewline()
    {
        var file = Path.Combine(_testDir, "test.log");
        File.WriteAllText(file, "line 1\nline 2\nline 3\n");

        var tail = ActivitySessions.ReadRecordedTail(file, _testDir);
        Assert.Equal("line 1\nline 2\nline 3\n", tail);
    }

    [Fact]
    public void ReadRecordedTail_PathTraversalEscape_ReturnsEmptyString()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(outside, "secret");
        try
        {
            var tail = ActivitySessions.ReadRecordedTail(outside, _testDir);
            Assert.Equal("", tail);
        }
        finally
        {
            try { File.Delete(outside); } catch { }
        }
    }

    [Fact]
    public void AssignmentSession_ValidWorkerResult_ValidatesIdentityAndBindsEvents()
    {
        var options = new JsonObject
        {
            ["projectId"] = "p1",
            ["row"] = new JsonObject { ["jobId"] = "job-1" },
            ["part"] = new JsonObject
            {
                ["megaTaskId"] = "MT-1",
                ["subTaskId"] = "ST-1",
                ["attemptId"] = "attempt-1",
                ["providerId"] = "claude",
                ["cli"] = "claude-3-5-sonnet",
                ["role"] = "worker"
            },
            ["execution"] = new JsonObject
            {
                ["megaTasks"] = new JsonArray { new JsonObject { ["id"] = "MT-1", ["projectId"] = "p1" } },
                ["subTasks"] = new JsonArray { new JsonObject { ["id"] = "ST-1", ["megaTaskId"] = "MT-1", ["status"] = "COMPLETE", ["resultId"] = "R-attempt-1" } },
                ["results"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["resultId"] = "R-attempt-1",
                        ["attemptId"] = "attempt-1",
                        ["megaTaskId"] = "MT-1",
                        ["subTaskId"] = "ST-1",
                        ["provider"] = "claude",
                        ["cli"] = "claude-3-5-sonnet",
                        ["status"] = "COMPLETE",
                        ["summary"] = "All good",
                        ["nextAction"] = "",
                        ["filesChanged"] = new JsonArray(),
                        ["blockers"] = new JsonArray(),
                        ["tests"] = new JsonArray(),
                        ["exitCode"] = 0
                    }
                }
            },
            ["tail"] = "{\"item\":{\"type\":\"command_execution\",\"command\":\"cat file.cs\"}}\n"
        };

        var session = ActivitySessions.AssignmentSession(options);
        Assert.NotNull(session);
        Assert.Equal("p1", session["projectId"]?.GetValue<string>());
        Assert.Equal("completed", session["state"]?.GetValue<string>());
        Assert.Equal("COMPLETE", session["validatedResultStatus"]?.GetValue<string>());
    }

    [Fact]
    public void PagedProjectActivitySessions_PaginatesMetadataWithoutReadingLogsPrematurely()
    {
        var options = new JsonObject
        {
            ["projectId"] = "p1",
            ["currentParts"] = new JsonArray
            {
                new JsonObject { ["attemptId"] = "att-1" },
                new JsonObject { ["attemptId"] = "att-2" },
                new JsonObject { ["attemptId"] = "att-3" }
            },
            ["offset"] = 1,
            ["limit"] = 1
        };

        var paged = ActivitySessions.PagedProjectActivitySessions(options);
        Assert.Equal(3, paged["total"]?.GetValue<int>());
        Assert.Equal(1, paged["offset"]?.GetValue<int>());
        Assert.Equal(1, paged["limit"]?.GetValue<int>());
        var sessions = paged["sessions"] as JsonArray;
        Assert.NotNull(sessions);
        Assert.Single(sessions);
    }
}
