using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Planwharf.Engine;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public sealed class PipelineVerificationTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("pw-verify-test-").FullName;
    const string ProjectId = "proj-verify";

    public void Dispose()
    {
        Strings.CurrentLanguage = "en";
        for (var attempt = 0; Directory.Exists(root); attempt++)
        {
            try { Directory.Delete(root, true); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(50); }
        }
    }

    string Attempts() => Path.Combine(root, "attempts");
    string JobDir(string jobId) => Path.Combine(Attempts(), "pipeline", ProjectId, "jobs", jobId);

    void WriteJob(
        string jobId,
        string status,
        string risk = "low",
        long timeMs = 0,
        JsonObject? tokens = null,
        JsonObject? quota = null,
        string? awaitingOwnerReason = null,
        JsonObject? routing = null,
        JsonObject? spec = null)
    {
        var dir = JobDir(jobId);
        Directory.CreateDirectory(dir);

        var obj = new JsonObject
        {
            ["summary"] = $"Summary for {jobId}",
            ["id"] = jobId,
            ["projectId"] = ProjectId,
            ["status"] = status,
            ["risk"] = risk,
            ["reportedTimeMs"] = timeMs
        };

        if (tokens is not null) obj["tokens"] = tokens;
        if (quota is not null) obj["quota"] = quota;
        if (awaitingOwnerReason is not null) obj["awaitingOwnerReason"] = awaitingOwnerReason;

        File.WriteAllText(Path.Combine(dir, "job.json"), obj.ToJsonString());

        if (routing is not null)
        {
            File.WriteAllText(Path.Combine(dir, "routing.json"), routing.ToJsonString());
        }

        if (spec is not null)
        {
            var revDir = Path.Combine(dir, "revisions", "1");
            Directory.CreateDirectory(revDir);
            File.WriteAllText(Path.Combine(revDir, "spec.json"), spec.ToJsonString());
        }
    }

    [Fact]
    public void StringAndMalformedValues_SafeParsing()
    {
        // 1. Time, tokens, and quota with string/null values
        var tokens = new JsonObject
        {
            ["input"] = "1500",
            ["output"] = "300"
        };
        var quota = new JsonObject
        {
            ["providerId"] = " anthropic ",
            ["modelId"] = " claude-3-5-sonnet ",
            ["remainingPercent"] = "85",
            ["unread"] = "false"
        };
        WriteJob("job-string-vals", "building", timeMs: 45000, tokens: tokens, quota: quota);

        var view = PipelineViews.ReadProjectView(Attempts(), ProjectId);
        var job = view.Jobs.Single(j => j.JobId == "job-string-vals");

        Assert.Equal("45s", job.ElapsedTimeDisplay);
        Assert.Equal("1,800 tokens", job.TokensDisplay);
        Assert.Equal("anthropic · claude-3-5-sonnet · 85%", job.QuotaDisplay);

        // 2. Routing with string maxReviewRounds and flat structure
        var flatRouting = new JsonObject
        {
            ["maxReviewRounds"] = "5",
            ["builder"] = "anthropic:claude-3-5-sonnet",
            ["reviewer-1"] = "google:gemini-3.8-flash"
        };
        var spec = new JsonObject
        {
            ["tasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "T1" },
                new JsonObject { ["id"] = "T2" }
            }
        };
        var jobObj = new JsonObject
        {
            ["currentRound"] = "3",
            ["currentTaskId"] = "T2"
        };

        var strip = PipelineViews.BuildStageStrip("reviewing", jobObj: jobObj, routingObj: flatRouting, specObj: spec);
        Assert.NotNull(strip.ReviewLoop);
        Assert.Equal("round 3 of 5", strip.ReviewLoop.DisplayText);
        Assert.Equal("Gemini is reviewing task 2 of 2", strip.ActiveStagePlainWords);

        // 3. PR number as string
        var prJob = new JsonObject { ["pullRequestNumber"] = "999" };
        var prStrip = PipelineViews.BuildStageStrip("pull-request", jobObj: prJob);
        Assert.Equal("Pull request #999 open", prStrip.ActiveStagePlainWords);
    }

    [Fact]
    public void AllTasksAccepted_PointsToFinalTask()
    {
        var spec = new JsonObject
        {
            ["tasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "T1" },
                new JsonObject { ["id"] = "T2" },
                new JsonObject { ["id"] = "T3" }
            }
        };
        var jobObj = new JsonObject
        {
            ["task_T1_status"] = "accepted",
            ["task_T2_status"] = "accepted",
            ["task_T3_status"] = "accepted"
        };

        var strip = PipelineViews.BuildStageStrip("building", jobObj: jobObj, specObj: spec);
        Assert.Equal("Lead is building task 3 of 3", strip.ActiveStagePlainWords);
    }

    [Fact]
    public void AwaitingOwner_GenericQuotaAndFallbackPaths()
    {
        // Generic quota reason
        var quotaStrip = PipelineViews.BuildStageStrip("awaiting-owner", awaitingOwnerReason: "quota-limit-reached");
        Assert.True(quotaStrip.NeedsYou);
        Assert.Equal(PipelineStageNames.BuildAndReview, quotaStrip.Stages[3].Id);
        Assert.True(quotaStrip.Stages[3].NeedsYou);
        Assert.Equal("Needs you: reviewer quota exhausted", quotaStrip.ActiveStagePlainWords);

        // Custom unknown reason with spec present defaults to Spec stage
        var specStrip = PipelineViews.BuildStageStrip("awaiting-owner", awaitingOwnerReason: "license-check-required", specObj: new JsonObject());
        Assert.True(specStrip.NeedsYou);
        Assert.Equal(PipelineStageNames.SpecAndTests, specStrip.Stages[2].Id);
        Assert.Equal("Needs you: license-check-required", specStrip.ActiveStagePlainWords);

        // Custom unknown reason without spec or routing defaults to Request stage
        var emptyStrip = PipelineViews.BuildStageStrip("awaiting-owner", awaitingOwnerReason: "manual-audit");
        Assert.True(emptyStrip.NeedsYou);
        Assert.Equal(PipelineStageNames.Request, emptyStrip.Stages[0].Id);
        Assert.Equal("Needs you: manual-audit", emptyStrip.ActiveStagePlainWords);
    }

    [Fact]
    public void FormatDuration_BoundaryAndEdgeCases()
    {
        Assert.Equal("0s", PipelineViews.FormatDuration(-100));
        Assert.Equal("0s", PipelineViews.FormatDuration(0));
        Assert.Equal("0s", PipelineViews.FormatDuration(499));
        Assert.Equal("1s", PipelineViews.FormatDuration(1000));
        Assert.Equal("59s", PipelineViews.FormatDuration(59000));
        Assert.Equal("1m 0s", PipelineViews.FormatDuration(60000));
        Assert.Equal("1m 30s", PipelineViews.FormatDuration(90000));
        Assert.Equal("1h 0m", PipelineViews.FormatDuration(3600000));
        Assert.Equal("2h 15m", PipelineViews.FormatDuration(8100000));
    }

    [Fact]
    public void ModelNameFormatting_VariousFormatsAndDelimiters()
    {
        var routing = new JsonObject
        {
            ["chosen"] = new JsonObject
            {
                ["builder"] = new JsonObject { ["modelId"] = "deepseek-ai/deepseek-coder:v2" },
                ["reviewer"] = new JsonObject { ["modelId"] = "ollama:mistral-7b" }
            }
        };
        var strip = PipelineViews.BuildStageStrip("building", routingObj: routing);
        Assert.Equal("Deepseek is building task 1 of 1", strip.ActiveStagePlainWords);

        var stripRev = PipelineViews.BuildStageStrip("reviewing", routingObj: routing);
        Assert.Equal("Mistral is reviewing task 1 of 1", stripRev.ActiveStagePlainWords);
    }

    [Fact]
    public void Recovery_CorruptedRevisionSpec_FallsBackGracefully()
    {
        var dir = JobDir("job-corrupt-spec");
        Directory.CreateDirectory(dir);
        var revDir = Path.Combine(dir, "revisions", "1");
        Directory.CreateDirectory(revDir);

        File.WriteAllText(Path.Combine(dir, "job.json"), new JsonObject
        {
            ["id"] = "job-corrupt-spec",
            ["projectId"] = ProjectId,
            ["status"] = "building"
        }.ToJsonString());

        // Corrupted revision 1 spec
        File.WriteAllText(Path.Combine(revDir, "spec.json"), "{ invalid JSON content ");

        // Valid fallback root spec
        File.WriteAllText(Path.Combine(dir, "spec.json"), new JsonObject
        {
            ["tasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "T1", ["title"] = "Fallback task" }
            }
        }.ToJsonString());

        var view = PipelineViews.ReadProjectView(Attempts(), ProjectId);
        var job = view.Jobs.Single(j => j.JobId == "job-corrupt-spec");
        Assert.NotNull(job.Strip);
        Assert.Equal(PipelineStageStatus.Active, job.Strip.Stages[3].Status);
    }

    [Fact]
    public async Task Concurrency_ParallelReadsAndMutations_NeverThrows()
    {
        for (int i = 0; i < 5; i++)
        {
            WriteJob($"job-conc-{i}", "building", timeMs: 10000 * (i + 1));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var token = cts.Token;

        // Writer task modifying files rapidly
        var writer = Task.Run(() =>
        {
            int round = 0;
            while (!token.IsCancellationRequested)
            {
                round++;
                try
                {
                    var id = $"job-conc-{round % 5}";
                    var dir = JobDir(id);
                    var jobFile = Path.Combine(dir, "job.json");
                    var obj = new JsonObject
                    {
                        ["id"] = id,
                        ["projectId"] = ProjectId,
                        ["status"] = (round % 2 == 0) ? "reviewing" : "building",
                        ["reportedTimeMs"] = round * 1000
                    };
                    File.WriteAllText(jobFile, obj.ToJsonString());
                }
                catch (IOException) { }
                Thread.Sleep(10);
            }
        });

        // Parallel reader tasks reading views
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                var view = PipelineViews.ReadProjectView(Attempts(), ProjectId);
                Assert.NotNull(view);
                foreach (var j in view.Jobs)
                {
                    Assert.NotNull(j.Strip);
                }
                Thread.Sleep(15);
            }
        })).ToArray();

        await Task.WhenAll([writer, .. readers]);
    }

    [AvaloniaFact]
    public void UIControls_InteractionCallbacks_TriggerCleanly()
    {
        bool decisionOpened = false;
        string? actionTriggered = null;
        string? actionJobId = null;

        var strip = PipelineViews.BuildStageStrip("awaiting-owner", awaitingOwnerReason: "questions");
        var stripControl = PipelineViews.RenderStageStrip(strip, () => decisionOpened = true) as Avalonia.Controls.StackPanel;
        Assert.NotNull(stripControl);

        // Find and click the action button
        var promptRow = stripControl.Children.OfType<Avalonia.Controls.StackPanel>().LastOrDefault();
        var actionBtn = promptRow?.Children.OfType<Avalonia.Controls.Button>().FirstOrDefault();
        Assert.NotNull(actionBtn);
        actionBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.True(decisionOpened);

        // Test RenderJobCard actions
        var jobRow = new PipelineJobRow(
            JobId: "job-btn-test",
            Title: "Buttons test",
            Stage: "pull-request",
            StageDisplay: "pull-request",
            ElapsedTimeDisplay: "1m",
            TokensDisplay: "500",
            QuotaDisplay: "google · gemini",
            NeedsYourDecision: true,
            DecisionLabel: "Needs your decision",
            ScheduleReason: null,
            CanMerge: true,
            CanRetryMerge: false,
            CanDrop: true,
            StageDetails: [],
            Strip: strip);

        var cardControl = PipelineViews.RenderJobCard(
            jobRow,
            (act, id) => { actionTriggered = act; actionJobId = id; },
            _ => { }) as Avalonia.Controls.Border;
        Assert.NotNull(cardControl);

        var cardPanel = cardControl.Child as Avalonia.Controls.StackPanel;
        Assert.NotNull(cardPanel);

        var btnRow = cardPanel.Children.OfType<Avalonia.Controls.StackPanel>().LastOrDefault();
        Assert.NotNull(btnRow);

        var mergeBtn = btnRow.Children.OfType<Avalonia.Controls.Button>().First();
        mergeBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Equal("merge", actionTriggered);
        Assert.Equal("job-btn-test", actionJobId);

        // Test RenderPipelinePanel toggle explainer
        bool explainerToggled = false;
        var view = new PipelineProjectView(ProjectId, [jobRow]);
        var panelControl = PipelineViews.RenderPipelinePanel(
            view,
            showExplainer: false,
            onToggleExplainer: () => explainerToggled = true) as Avalonia.Controls.StackPanel;
        Assert.NotNull(panelControl);

        var headSection = panelControl.Children.OfType<Avalonia.Controls.StackPanel>().First();
        Avalonia.Controls.Button? toggleBtn = null;
        void FindButton(Avalonia.Controls.Control parent)
        {
            if (toggleBtn != null) return;
            if (parent is Avalonia.Controls.Button b) { toggleBtn = b; return; }
            if (parent is Avalonia.Controls.Panel p) foreach (var c in p.Children) FindButton(c);
            else if (parent is Avalonia.Controls.ContentControl cc && cc.Content is Avalonia.Controls.Control c) FindButton(c);
            else if (parent is Avalonia.Controls.Border border && border.Child is Avalonia.Controls.Control bChild) FindButton(bChild);
        }
        FindButton(headSection);
        Assert.NotNull(toggleBtn);
        toggleBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.True(explainerToggled);
    }
}
