using System.Text.Json.Nodes;
using Planwharf.Engine;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public sealed class PipelineViewTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("pw view é ").FullName;
    const string ProjectAlpha = "proj-alpha";
    const string ProjectBeta = "proj-beta";
    const string Job1 = "job-1";
    const string Job2 = "job-2";

    public void Dispose()
    {
        Strings.CurrentLanguage = "en";
        for (var attempt = 0; Directory.Exists(root); attempt++)
        {
            try { Directory.Delete(root, true); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(100); }
        }
    }

    string Attempts() => Path.Combine(root, "attempts");
    string JobDir(string proj, string job) => Path.Combine(Attempts(), "pipeline", proj, "jobs", job);

    void WriteJob(
        string proj,
        string job,
        string status,
        string risk = "low",
        long timeMs = 0,
        JsonObject? tokens = null,
        JsonObject? quota = null,
        string? awaitingOwnerReason = null)
    {
        var dir = JobDir(proj, job);
        Directory.CreateDirectory(dir);

        var obj = new JsonObject
        {
            ["summary"] = $"Job summary for {job}",
            ["id"] = job,
            ["projectId"] = proj,
            ["status"] = status,
            ["risk"] = risk,
            ["reportedTimeMs"] = timeMs
        };

        if (tokens is not null) obj["tokens"] = tokens;
        if (quota is not null) obj["quota"] = quota;
        if (awaitingOwnerReason is not null) obj["awaitingOwnerReason"] = awaitingOwnerReason;

        File.WriteAllText(Path.Combine(dir, "job.json"), obj.ToJsonString());
    }

    [Fact]
    public void View_ShowsSelectedProjectOnly_IgnoresOtherProjects()
    {
        WriteJob(ProjectAlpha, Job1, "building");
        WriteJob(ProjectBeta, Job2, "routing");

        var viewAlpha = PipelineViews.ReadProjectView(Attempts(), ProjectAlpha);
        Assert.Single(viewAlpha.Jobs);
        Assert.Equal(Job1, viewAlpha.Jobs[0].JobId);
        Assert.Equal("building", viewAlpha.Jobs[0].Stage);

        var viewBeta = PipelineViews.ReadProjectView(Attempts(), ProjectBeta);
        Assert.Single(viewBeta.Jobs);
        Assert.Equal(Job2, viewBeta.Jobs[0].JobId);
        Assert.Equal("routing", viewBeta.Jobs[0].Stage);
    }

    [Fact]
    public void View_ApprovedJob_ShowsScheduleReason()
    {
        WriteJob(ProjectAlpha, Job1, "approved");
        var schedule = new PipelineRun.ScheduleRecord
        {
            Summary = "Schedule",
            ProjectId = ProjectAlpha,
            UpdatedAt = "2026-11-22T10:00:00Z",
            Running = [],
            Waiting =
            [
                new PipelineRun.WaitingSlot(Job1, PipelineRun.ReasonCap, "Cap reached")
            ]
        };
        PipelineRun.WriteScheduleAtomic(Attempts(), ProjectAlpha, schedule);

        var view = PipelineViews.ReadProjectView(Attempts(), ProjectAlpha);
        Assert.Single(view.Jobs);
        Assert.Equal("approved", view.Jobs[0].Stage);
        Assert.Equal("approved (cap)", view.Jobs[0].StageDisplay);
        Assert.Equal(PipelineRun.ReasonCap, view.Jobs[0].ScheduleReason);
    }

    [Fact]
    public void View_NeedsYourDecision_ForAwaitingOwner_ReadyLocal_AndPullRequestWhenOwnerMerges()
    {
        WriteJob(ProjectAlpha, "job-await", "awaiting-owner");
        WriteJob(ProjectAlpha, "job-local", "ready-local");
        WriteJob(ProjectAlpha, "job-pr-med", "pull-request", risk: "medium");
        WriteJob(ProjectAlpha, "job-pr-low", "pull-request", risk: "low");

        var settings = new PipelineRun.PipelineRunSettings { MergeLowRisk = true };
        var view = PipelineViews.ReadProjectView(Attempts(), ProjectAlpha, settings);

        var map = view.Jobs.ToDictionary(j => j.JobId);
        Assert.True(map["job-await"].NeedsYourDecision);
        Assert.Equal("Needs your decision", map["job-await"].DecisionLabel);

        Assert.True(map["job-local"].NeedsYourDecision);
        Assert.Equal("Needs your decision", map["job-local"].DecisionLabel);

        Assert.True(map["job-pr-med"].NeedsYourDecision);
        Assert.Equal("Needs your decision", map["job-pr-med"].DecisionLabel);

        // Low risk when mergeLowRisk is on auto-merges, so not marked Needs your decision
        Assert.False(map["job-pr-low"].NeedsYourDecision);
        Assert.Null(map["job-pr-low"].DecisionLabel);
    }

    [Fact]
    public void View_QuotaDisplay_Unread_Reported_AndMissing()
    {
        // Unread quota
        var unreadQuota = new JsonObject
        {
            ["providerId"] = "anthropic",
            ["modelId"] = "claude-sonnet",
            ["unread"] = true
        };
        WriteJob(ProjectAlpha, "job-unread", "building", quota: unreadQuota);

        // Known quota
        var knownQuota = new JsonObject
        {
            ["providerId"] = "openai",
            ["modelId"] = "gpt-4",
            ["remainingPercent"] = 65
        };
        WriteJob(ProjectAlpha, "job-known", "building", quota: knownQuota);

        // Missing quota
        WriteJob(ProjectAlpha, "job-missing", "building", quota: null);

        var view = PipelineViews.ReadProjectView(Attempts(), ProjectAlpha);
        var map = view.Jobs.ToDictionary(j => j.JobId);

        Assert.Equal("anthropic · claude-sonnet · unread", map["job-unread"].QuotaDisplay);
        Assert.Equal("openai · gpt-4 · 65%", map["job-known"].QuotaDisplay);
        Assert.Equal("not reported", map["job-missing"].QuotaDisplay);
    }

    [Fact]
    public void View_ElapsedTime_AndTokens()
    {
        var tokens = new JsonObject
        {
            ["input"] = 1200,
            ["output"] = 350
        };
        WriteJob(ProjectAlpha, Job1, "building", timeMs: 125000, tokens: tokens);

        var view = PipelineViews.ReadProjectView(Attempts(), ProjectAlpha);
        Assert.Single(view.Jobs);
        Assert.Equal("2m 5s", view.Jobs[0].ElapsedTimeDisplay);
        Assert.Equal("1,550 tokens", view.Jobs[0].TokensDisplay);
    }

    [Fact]
    public void View_ButtonStates_CanMerge_CanRetryMerge_CanDrop()
    {
        WriteJob(ProjectAlpha, "job-pr", "pull-request");
        WriteJob(ProjectAlpha, "job-fail", "awaiting-owner", awaitingOwnerReason: PipelineRun.ReasonMergeFailed);
        WriteJob(ProjectAlpha, "job-merged", "merged");

        var view = PipelineViews.ReadProjectView(Attempts(), ProjectAlpha);
        var map = view.Jobs.ToDictionary(j => j.JobId);

        Assert.True(map["job-pr"].CanMerge);
        Assert.False(map["job-pr"].CanRetryMerge);
        Assert.True(map["job-pr"].CanDrop);

        Assert.False(map["job-fail"].CanMerge);
        Assert.True(map["job-fail"].CanRetryMerge);
        Assert.True(map["job-fail"].CanDrop);

        Assert.False(map["job-merged"].CanMerge);
        Assert.False(map["job-merged"].CanRetryMerge);
        Assert.False(map["job-merged"].CanDrop);
    }

    [Fact]
    public void View_StageDetails_ShowsSummaryFirst_WithoutCredentials()
    {
        WriteJob(ProjectAlpha, Job1, "building");
        var dir = JobDir(ProjectAlpha, Job1);

        var intakeObj = new JsonObject
        {
            ["summary"] = "Evaluated intake request",
            ["risk"] = "low",
            ["secretApiKey"] = "SHOULD_NOT_LEAK"
        };
        File.WriteAllText(Path.Combine(dir, "intake.json"), intakeObj.ToJsonString());

        var prObj = new JsonObject
        {
            ["summary"] = "Created pull request #55",
            ["number"] = 55
        };
        File.WriteAllText(Path.Combine(dir, "pull-request.json"), prObj.ToJsonString());

        var view = PipelineViews.ReadProjectView(Attempts(), ProjectAlpha);
        Assert.Single(view.Jobs);
        var details = view.Jobs[0].StageDetails;

        Assert.Equal(2, details.Count);
        Assert.Equal("Intake", details[0].StageName);
        Assert.Equal("Evaluated intake request", details[0].Summary);
        Assert.Equal("Pull Request", details[1].StageName);
        Assert.Equal("Created pull request #55", details[1].Summary);
        Assert.DoesNotContain("SHOULD_NOT_LEAK", details.Select(d => d.Summary));
    }

    [Fact]
    public void View_Localization_ItalianCatalog()
    {
        Strings.CurrentLanguage = "it";

        var unreadQuota = new JsonObject
        {
            ["providerId"] = "anthropic",
            ["modelId"] = "claude-sonnet",
            ["unread"] = true
        };
        WriteJob(ProjectAlpha, Job1, "ready-local", quota: unreadQuota);

        var view = PipelineViews.ReadProjectView(Attempts(), ProjectAlpha);
        Assert.Single(view.Jobs);

        Assert.Equal("Serve una tua decisione", view.Jobs[0].DecisionLabel);
        Assert.Contains("non letto", view.Jobs[0].QuotaDisplay);
    }

    [Fact]
    public void View_PartialStageFiles_DoesNotCrashOrAdvanceStatus()
    {
        WriteJob(ProjectAlpha, Job1, "building");
        var dir = JobDir(ProjectAlpha, Job1);

        // Valid intake
        File.WriteAllText(Path.Combine(dir, "intake.json"), new JsonObject { ["summary"] = "Valid intake" }.ToJsonString());

        // Corrupted/partial pull-request.json
        File.WriteAllText(Path.Combine(dir, "pull-request.json"), "{\"summary\": \"trunca");

        // Another job with totally unreadable/corrupted job.json
        var corruptedJobDir = JobDir(ProjectAlpha, "job-corrupted");
        Directory.CreateDirectory(corruptedJobDir);
        File.WriteAllText(Path.Combine(corruptedJobDir, "job.json"), "{ invalid-json ");

        var view = PipelineViews.ReadProjectView(Attempts(), ProjectAlpha);

        // Only the valid job row appears; the unreadable job.json is skipped and does not crash
        Assert.Single(view.Jobs);
        Assert.Equal(Job1, view.Jobs[0].JobId);
        Assert.Equal("building", view.Jobs[0].Stage);

        // The partial pull-request.json did not advance stage details
        Assert.Single(view.Jobs[0].StageDetails);
        Assert.Equal("Intake", view.Jobs[0].StageDetails[0].StageName);
        Assert.Equal("Valid intake", view.Jobs[0].StageDetails[0].Summary);
    }
}
