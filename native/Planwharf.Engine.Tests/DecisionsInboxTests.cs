using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class DecisionsInboxTests
{
    [Fact]
    public void FromJson_parses_open_and_answered_decisions_correctly()
    {
        var json = JsonNode.Parse("""
        {
          "openCount": 1,
          "open": [
            {
              "id": "dec-1",
              "projectId": "proj-a",
              "projectName": "Project A",
              "workId": "work-1",
              "workTitle": "Add feature",
              "isPipeline": false,
              "text": "Enable caching?",
              "suggestion": "yes",
              "breaks": true,
              "at": "2026-11-23T12:00:00Z",
              "answer": ""
            }
          ],
          "answered": [
            {
              "id": "dec-2",
              "projectId": "proj-b",
              "projectName": "Project B",
              "workId": "pipe-10",
              "workTitle": "Pipeline 10",
              "isPipeline": true,
              "text": "Overwrite existing files?",
              "suggestion": "no",
              "breaks": false,
              "at": "2026-11-23T11:00:00Z",
              "answer": "no",
              "answeredAt": "2026-11-23T11:05:00Z",
              "answeredBy": "Decided by Lead",
              "autoReason": "Followed recommended suggestion: no",
              "stepStarted": true
            }
          ]
        }
        """);

        var view = DecisionsInboxView.FromJson(json);

        Assert.Equal(1, view.OpenCount);
        Assert.Single(view.Open);
        Assert.Single(view.Answered);

        var open = view.Open[0];
        Assert.Equal("dec-1", open.Id);
        Assert.Equal("proj-a", open.ProjectId);
        Assert.Equal("Project A", open.ProjectName);
        Assert.Equal("work-1", open.WorkId);
        Assert.Equal("Add feature", open.WorkTitle);
        Assert.False(open.IsPipeline);
        Assert.Equal("Enable caching?", open.Text);
        Assert.Equal("yes", open.Suggestion);
        Assert.True(open.Breaks);
        Assert.Equal("", open.Answer);
        Assert.False(open.StepStarted);

        var answered = view.Answered[0];
        Assert.Equal("dec-2", answered.Id);
        Assert.Equal("proj-b", answered.ProjectId);
        Assert.Equal("Project B", answered.ProjectName);
        Assert.True(answered.IsPipeline);
        Assert.False(answered.Breaks);
        Assert.Equal("no", answered.Answer);
        Assert.Equal("Decided by Lead", answered.AnsweredBy);
        Assert.Equal("Followed recommended suggestion: no", answered.AutoReason);
        Assert.True(answered.StepStarted);
    }

    [Fact]
    public void FromJson_handles_null_or_empty_gracefully()
    {
        var emptyView = DecisionsInboxView.FromJson(null);
        Assert.Equal(0, emptyView.OpenCount);
        Assert.Empty(emptyView.Open);
        Assert.Empty(emptyView.Answered);

        var objEmpty = DecisionsInboxView.FromJson(new JsonObject());
        Assert.Equal(0, objEmpty.OpenCount);
        Assert.Empty(objEmpty.Open);
        Assert.Empty(objEmpty.Answered);

        var nonObj = DecisionsInboxView.FromJson(JsonValue.Create("not an object"));
        Assert.Equal(0, nonObj.OpenCount);
        Assert.Empty(nonObj.Open);
        Assert.Empty(nonObj.Answered);
    }

    [Fact]
    public void FromJson_handles_string_and_numeric_booleans_and_malformed_items()
    {
        var json = JsonNode.Parse("""
        {
          "openCount": "7",
          "open": [
            null,
            "corrupt_string_item",
            {
              "id": "dec-str",
              "projectId": "p1",
              "breaks": "true",
              "isPipeline": "true",
              "stepStarted": "false"
            },
            {
              "id": "dec-num",
              "projectId": "p2",
              "breaks": 1,
              "isPipeline": 0,
              "stepStarted": 1
            }
          ],
          "answered": [
            null
          ]
        }
        """);

        var view = DecisionsInboxView.FromJson(json);
        Assert.Equal(7, view.OpenCount);
        Assert.Equal(2, view.Open.Count);
        Assert.Empty(view.Answered);

        var first = view.Open[0];
        Assert.Equal("dec-str", first.Id);
        Assert.True(first.Breaks);
        Assert.True(first.IsPipeline);
        Assert.False(first.StepStarted);

        var second = view.Open[1];
        Assert.Equal("dec-num", second.Id);
        Assert.True(second.Breaks);
        Assert.False(second.IsPipeline);
        Assert.True(second.StepStarted);
    }
}
