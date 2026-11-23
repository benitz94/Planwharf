using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Planwharf.Engine;
using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public sealed class PipelineStripTests : IDisposable
{
    public void Dispose()
    {
        Strings.CurrentLanguage = "en";
    }

    [Fact]
    public void Explainer_ReturnsSevenStagesWithSentencesAndOwnerInterventions()
    {
        var explainer = PipelineViews.GetWorkflowExplainer("test-proj");
        Assert.Equal(7, explainer.Stages.Count);
        Assert.Contains("test-proj", explainer.EmptyQueueMessage);

        var names = explainer.Stages.Select(s => s.StageId).ToList();
        Assert.Equal(PipelineStageNames.All, names);

        // Plain sentence per stage exists and is not empty
        foreach (var stage in explainer.Stages)
        {
            Assert.False(string.IsNullOrWhiteSpace(stage.PlainSentence));
            Assert.False(string.IsNullOrWhiteSpace(stage.Label));
        }

        // Three owner intervention points: answers questions, approves spec, merges PR
        var qStage = explainer.Stages.Single(s => s.StageId == PipelineStageNames.Questions);
        Assert.Equal("Answers the questions", qStage.OwnerIntervention);

        var specStage = explainer.Stages.Single(s => s.StageId == PipelineStageNames.SpecAndTests);
        Assert.Equal("Approves the spec", specStage.OwnerIntervention);

        var prStage = explainer.Stages.Single(s => s.StageId == PipelineStageNames.PullRequest);
        Assert.Equal("Merges the pull request", prStage.OwnerIntervention);

        // Other stages have no owner intervention
        Assert.Null(explainer.Stages.Single(s => s.StageId == PipelineStageNames.Request).OwnerIntervention);
        Assert.Null(explainer.Stages.Single(s => s.StageId == PipelineStageNames.BuildAndReview).OwnerIntervention);
        Assert.Null(explainer.Stages.Single(s => s.StageId == PipelineStageNames.Checks).OwnerIntervention);
        Assert.Null(explainer.Stages.Single(s => s.StageId == PipelineStageNames.Merged).OwnerIntervention);
    }

    [Fact]
    public void StateTransitions_FromRequestToMerged()
    {
        // 1. Queued / Intake -> Request is active, none completed
        var stripQueued = PipelineViews.BuildStageStrip("queued");
        Assert.Equal(PipelineStageStatus.Active, stripQueued.Stages[0].Status);
        Assert.Equal("Request", stripQueued.Stages[0].Id);
        Assert.All(stripQueued.Stages.Skip(1), s => Assert.Equal(PipelineStageStatus.Future, s.Status));
        Assert.Equal("Queued for intake", stripQueued.ActiveStagePlainWords);
        Assert.False(stripQueued.NeedsYou);

        var stripIntake = PipelineViews.BuildStageStrip("intake");
        Assert.Equal(PipelineStageStatus.Active, stripIntake.Stages[0].Status);
        Assert.Equal("Evaluating request", stripIntake.ActiveStagePlainWords);

        // 2. Waiting questions -> Questions active, Request completed, Needs you
        var stripQuestions = PipelineViews.BuildStageStrip("waiting-questions");
        Assert.Equal(PipelineStageStatus.Completed, stripQuestions.Stages[0].Status);
        Assert.Equal(PipelineStageStatus.Active, stripQuestions.Stages[1].Status);
        Assert.Equal("Questions", stripQuestions.Stages[1].Id);
        Assert.True(stripQuestions.NeedsYou);
        Assert.True(stripQuestions.Stages[1].NeedsYou);
        Assert.All(stripQuestions.Stages.Skip(2), s => Assert.Equal(PipelineStageStatus.Future, s.Status));

        // 3. Spec and tests: specifying and awaiting-approval
        var stripSpec = PipelineViews.BuildStageStrip("specifying");
        Assert.Equal(PipelineStageStatus.Completed, stripSpec.Stages[0].Status);
        Assert.Equal(PipelineStageStatus.Completed, stripSpec.Stages[1].Status);
        Assert.Equal(PipelineStageStatus.Active, stripSpec.Stages[2].Status);
        Assert.Equal("Spec and tests", stripSpec.Stages[2].Id);
        Assert.Equal("Specifying tasks and acceptance tests", stripSpec.ActiveStagePlainWords);
        Assert.False(stripSpec.NeedsYou);

        var stripApproval = PipelineViews.BuildStageStrip("awaiting-approval");
        Assert.Equal(PipelineStageStatus.Active, stripApproval.Stages[2].Status);
        Assert.True(stripApproval.NeedsYou);
        Assert.Equal("Spec and tests ready for approval", stripApproval.ActiveStagePlainWords);

        // 4. Build ⇄ Review: building, gating, reviewing
        var stripBuilding = PipelineViews.BuildStageStrip("building");
        Assert.Equal(PipelineStageStatus.Completed, stripBuilding.Stages[0].Status);
        Assert.Equal(PipelineStageStatus.Completed, stripBuilding.Stages[1].Status);
        Assert.Equal(PipelineStageStatus.Completed, stripBuilding.Stages[2].Status);
        Assert.Equal(PipelineStageStatus.Active, stripBuilding.Stages[3].Status);
        Assert.Equal("Build ⇄ Review", stripBuilding.Stages[3].Id);
        Assert.All(stripBuilding.Stages.Skip(4), s => Assert.Equal(PipelineStageStatus.Future, s.Status));

        // 5. Checks: syncing, ready-local, opening-pr
        var stripSyncing = PipelineViews.BuildStageStrip("syncing");
        Assert.Equal(PipelineStageStatus.Active, stripSyncing.Stages[4].Status);
        Assert.Equal("Checks", stripSyncing.Stages[4].Id);
        Assert.Equal(PipelineStageStatus.Future, stripSyncing.Stages[5].Status);
        Assert.Equal(PipelineStageStatus.Future, stripSyncing.Stages[6].Status);

        var stripReadyLocal = PipelineViews.BuildStageStrip("ready-local");
        Assert.Equal(PipelineStageStatus.Active, stripReadyLocal.Stages[4].Status);
        Assert.True(stripReadyLocal.NeedsYou);

        // 6. Pull request
        var stripPR = PipelineViews.BuildStageStrip("pull-request");
        Assert.Equal(PipelineStageStatus.Active, stripPR.Stages[5].Status);
        Assert.Equal("Pull request", stripPR.Stages[5].Id);
        Assert.Equal(PipelineStageStatus.Future, stripPR.Stages[6].Status);

        // 7. Merged -> ALL 7 stages are Completed (ticked)
        var stripMerged = PipelineViews.BuildStageStrip("merged");
        Assert.All(stripMerged.Stages, s => Assert.Equal(PipelineStageStatus.Completed, s.Status));
        Assert.Equal("Merged into main", stripMerged.ActiveStagePlainWords);
    }

    [Fact]
    public void ActiveStageFormatting_PlainWordsAndModels()
    {
        var specObj = new JsonObject
        {
            ["tasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "T1", ["title"] = "First task" },
                new JsonObject { ["id"] = "T2", ["title"] = "Second task" },
                new JsonObject { ["id"] = "T3", ["title"] = "Third task" },
            }
        };

        var routingObj = new JsonObject
        {
            ["maxReviewRounds"] = 3,
            ["chosen"] = new JsonObject
            {
                ["builder"] = new JsonObject { ["providerId"] = "anthropic", ["modelId"] = "claude-sonnet-4-6" },
                ["reviewer-1"] = new JsonObject { ["providerId"] = "google", ["modelId"] = "gemini-3.8-flash-high" }
            }
        };

        var jobObj = new JsonObject
        {
            ["task_T1_status"] = "accepted",
            ["currentRound_T2"] = 2
        };

        // Reviewing task 2 of 3: exactly requirement 'Gemini is reviewing task 2 of 3'
        var stripReviewing = PipelineViews.BuildStageStrip(
            "reviewing",
            jobObj: jobObj,
            routingObj: routingObj,
            specObj: specObj);

        Assert.Equal("Gemini is reviewing task 2 of 3", stripReviewing.ActiveStagePlainWords);
        Assert.NotNull(stripReviewing.ReviewLoop);
        Assert.Equal("round 2 of 3", stripReviewing.ReviewLoop.DisplayText);
        Assert.True(stripReviewing.ReviewLoop.IsActive);

        // Building task 2 of 3: 'Claude is building task 2 of 3'
        var stripBuilding = PipelineViews.BuildStageStrip(
            "building",
            jobObj: jobObj,
            routingObj: routingObj,
            specObj: specObj);

        Assert.Equal("Claude is building task 2 of 3", stripBuilding.ActiveStagePlainWords);

        // Gating task 2 of 3: 'Acceptance gate is checking task 2 of 3'
        var stripGating = PipelineViews.BuildStageStrip(
            "gating",
            jobObj: jobObj,
            routingObj: routingObj,
            specObj: specObj);

        Assert.Equal("Acceptance gate is checking task 2 of 3", stripGating.ActiveStagePlainWords);

        // Approved with schedule reason 'cap': 'Approved, waiting to build (cap)'
        var stripApproved = PipelineViews.BuildStageStrip(
            "approved",
            scheduleReason: "cap");
        Assert.Equal("Approved, waiting to build (cap)", stripApproved.ActiveStagePlainWords);

        // Pull request with PR number 42
        var jobPR = new JsonObject { ["pullRequestNumber"] = 42 };
        var stripPRNum = PipelineViews.BuildStageStrip("pull-request", jobObj: jobPR);
        Assert.Equal("Pull request #42 open", stripPRNum.ActiveStagePlainWords);
    }

    [Fact]
    public void OwnerInterventionPoints_FlaggedWithNeedsYou()
    {
        // 1. Author questions waiting for owner
        var qWait = PipelineViews.BuildStageStrip("awaiting-owner", awaitingOwnerReason: "questions");
        Assert.True(qWait.NeedsYou);
        Assert.Equal(PipelineStageNames.Questions, qWait.Stages[1].Id);
        Assert.True(qWait.Stages[1].NeedsYou);
        Assert.Equal("Needs you: answer author questions", qWait.ActiveStagePlainWords);

        // 2. Spec approval waiting for owner
        var sWait = PipelineViews.BuildStageStrip("awaiting-owner", awaitingOwnerReason: "spec-approval");
        Assert.True(sWait.NeedsYou);
        Assert.Equal(PipelineStageNames.SpecAndTests, sWait.Stages[2].Id);
        Assert.True(sWait.Stages[2].NeedsYou);
        Assert.Equal("Needs you: approve spec and tests", sWait.ActiveStagePlainWords);

        // 3. Review rounds limit reached
        var rWait = PipelineViews.BuildStageStrip(
            "awaiting-owner",
            awaitingOwnerReason: "review-rounds",
            jobObj: new JsonObject { ["currentRound"] = 3 },
            routingObj: new JsonObject { ["maxReviewRounds"] = 3 });
        Assert.True(rWait.NeedsYou);
        Assert.Equal(PipelineStageNames.BuildAndReview, rWait.Stages[3].Id);
        Assert.True(rWait.Stages[3].NeedsYou);
        Assert.Equal("Needs you: review rounds limit reached", rWait.ActiveStagePlainWords);
        Assert.NotNull(rWait.ReviewLoop);
        Assert.Equal("round 3 of 3", rWait.ReviewLoop.DisplayText);

        // 4. Quota exhausted
        var quotaWait = PipelineViews.BuildStageStrip("awaiting-owner", awaitingOwnerReason: "reviewer-quota");
        Assert.True(quotaWait.NeedsYou);
        Assert.Equal("Needs you: reviewer quota exhausted", quotaWait.ActiveStagePlainWords);

        // 5. Gate returns
        var gateWait = PipelineViews.BuildStageStrip("awaiting-owner", awaitingOwnerReason: "gate-returns");
        Assert.True(gateWait.NeedsYou);
        Assert.Equal("Needs you: acceptance gate failed repeatedly", gateWait.ActiveStagePlainWords);

        // 6. Merge failed
        var mergeWait = PipelineViews.BuildStageStrip("awaiting-owner", awaitingOwnerReason: "merge-failed");
        Assert.True(mergeWait.NeedsYou);
        Assert.Equal(PipelineStageNames.PullRequest, mergeWait.Stages[5].Id);
        Assert.Contains("merge failed", mergeWait.ActiveStagePlainWords);
    }

    [Fact]
    public void CorruptedAndFallbackJobData_SafeHandling()
    {
        // Null jobObj, routingObj, specObj
        var stripNull = PipelineViews.BuildStageStrip(null!);
        Assert.NotNull(stripNull);
        Assert.Equal(7, stripNull.Stages.Count);
        Assert.Equal(PipelineStageStatus.Active, stripNull.Stages[0].Status);

        // Unrecognized status
        var stripUnknown = PipelineViews.BuildStageStrip("some_random_future_status");
        Assert.NotNull(stripUnknown);
        Assert.Equal("some_random_future_status", stripUnknown.ActiveStagePlainWords);
        Assert.Equal(7, stripUnknown.Stages.Count);

        // Corrupted task array (non-objects or empty)
        var badSpec = new JsonObject { ["tasks"] = new JsonArray { "not-an-object", 123 } };
        var stripBadSpec = PipelineViews.BuildStageStrip("building", specObj: badSpec);
        Assert.NotNull(stripBadSpec);
        Assert.Contains("building task", stripBadSpec.ActiveStagePlainWords.ToLowerInvariant());

        // Awaiting owner without reason
        var stripAwaitingNoReason = PipelineViews.BuildStageStrip("awaiting-owner");
        Assert.True(stripAwaitingNoReason.NeedsYou);
        Assert.Equal("Needs you", stripAwaitingNoReason.ActiveStagePlainWords);
    }

    [Fact]
    public void Localization_ItalianResources()
    {
        Strings.CurrentLanguage = "it";

        var explainer = PipelineViews.GetWorkflowExplainer("progetto-alfa");
        Assert.Equal("Come funziona la pipeline", explainer.Title);
        Assert.Contains("progetto-alfa", explainer.EmptyQueueMessage);

        var labels = explainer.Stages.Select(s => s.Label).ToList();
        Assert.Contains("Richiesta", labels);
        Assert.Contains("Domande", labels);
        Assert.Contains("Specifica e test", labels);
        Assert.Contains("Costruzione ⇄ Revisione", labels);
        Assert.Contains("Controlli", labels);
        Assert.Contains("Pull request", labels);
        Assert.Contains("Unito", labels);

        var qStage = explainer.Stages.Single(s => s.StageId == PipelineStageNames.Questions);
        Assert.Equal("Risponde alle domande", qStage.OwnerIntervention);

        var sStage = explainer.Stages.Single(s => s.StageId == PipelineStageNames.SpecAndTests);
        Assert.Equal("Approva la specifica", sStage.OwnerIntervention);

        var prStage = explainer.Stages.Single(s => s.StageId == PipelineStageNames.PullRequest);
        Assert.Equal("Unisce la pull request", prStage.OwnerIntervention);

        // Active stage formatting in Italian
        var specObj = new JsonObject
        {
            ["tasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "T1" },
                new JsonObject { ["id"] = "T2" },
                new JsonObject { ["id"] = "T3" }
            }
        };
        var routingObj = new JsonObject
        {
            ["chosen"] = new JsonObject
            {
                ["reviewer-1"] = new JsonObject { ["modelId"] = "gemini-3.8-flash-high" }
            }
        };
        var jobObj = new JsonObject
        {
            ["task_T1_status"] = "accepted",
            ["currentRound_T2"] = 2
        };

        var strip = PipelineViews.BuildStageStrip("reviewing", jobObj: jobObj, routingObj: routingObj, specObj: specObj);
        Assert.Equal("Gemini sta revisionando l'attività 2 di 3", strip.ActiveStagePlainWords);
        Assert.Equal("ciclo 2 di 3", strip.ReviewLoop?.DisplayText);

        // Intervention prompts in Italian
        var qWait = PipelineViews.BuildStageStrip("awaiting-owner", awaitingOwnerReason: "questions");
        Assert.Equal("Serve il tuo intervento: rispondi alle domande", qWait.ActiveStagePlainWords);
    }

    [AvaloniaFact]
    public void AvaloniaControls_RenderCleanly()
    {
        var explainer = PipelineViews.GetWorkflowExplainer("proj-demo");
        var explainerControl = PipelineViews.RenderWorkflowExplainer(explainer);
        Assert.NotNull(explainerControl);

        var strip = PipelineViews.BuildStageStrip("building");
        var stripControl = PipelineViews.RenderStageStrip(strip);
        Assert.NotNull(stripControl);

        var jobRow = new PipelineJobRow(
            JobId: "job-demo",
            Title: "Demo pipeline job",
            Stage: "building",
            StageDisplay: "building",
            ElapsedTimeDisplay: "3m 15s",
            TokensDisplay: "1,200 out",
            QuotaDisplay: "google · gemini · 80%",
            NeedsYourDecision: false,
            DecisionLabel: null,
            ScheduleReason: null,
            CanMerge: false,
            CanRetryMerge: false,
            CanDrop: true,
            StageDetails: [],
            Strip: strip);

        var jobCard = PipelineViews.RenderJobCard(jobRow);
        Assert.NotNull(jobCard);

        // Empty view renders explainer
        var emptyView = new PipelineProjectView("proj-demo", [], explainer);
        var emptyPanel = PipelineViews.RenderPipelinePanel(emptyView);
        Assert.NotNull(emptyPanel);

        // View with jobs renders cards
        var populatedView = new PipelineProjectView("proj-demo", [jobRow], explainer);
        var populatedPanel = PipelineViews.RenderPipelinePanel(populatedView);
        Assert.NotNull(populatedPanel);
    }
}
