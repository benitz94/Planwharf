// Job 34 implementation of native/porting/jobs/22-pipeline-run.md.
// Pipeline project panel view for the native application.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Planwharf.Engine;

namespace Planwharf.Native.Core;

public enum PipelineStageStatus
{
    Completed,
    Active,
    Future
}

public static class PipelineStageNames
{
    public const string Request = "Request";
    public const string Questions = "Questions";
    public const string SpecAndTests = "Spec and tests";
    public const string BuildAndReview = "Build ⇄ Review";
    public const string Checks = "Checks";
    public const string PullRequest = "Pull request";
    public const string Merged = "Merged";

    public static readonly IReadOnlyList<string> All =
    [
        Request,
        Questions,
        SpecAndTests,
        BuildAndReview,
        Checks,
        PullRequest,
        Merged
    ];

    public static string LocalizedLabel(string id) => id switch
    {
        Request => Strings.T("Request"),
        Questions => Strings.T("Questions"),
        SpecAndTests => Strings.T("Spec and tests"),
        BuildAndReview => Strings.T("Build ⇄ Review"),
        Checks => Strings.T("Checks"),
        PullRequest => Strings.T("Pull request"),
        Merged => Strings.CurrentLanguage == "it" ? "Unito" : Strings.T("Merged"),
        _ => id
    };
}

public sealed record PipelineStage(
    string Id,
    string Label,
    PipelineStageStatus Status,
    bool NeedsYou = false,
    string? PlainWords = null);

public sealed record PipelineReviewLoop(
    int CurrentRound,
    int MaxRounds,
    bool IsActive,
    string DisplayText);

public sealed record PipelineStageStrip(
    IReadOnlyList<PipelineStage> Stages,
    string ActiveStagePlainWords,
    PipelineReviewLoop? ReviewLoop = null,
    bool NeedsYou = false,
    string? DecisionPrompt = null);

public sealed record PipelineExplainerStage(
    string StageId,
    string Label,
    string PlainSentence,
    string? OwnerIntervention);

public sealed record PipelineExplainerView(
    IReadOnlyList<PipelineExplainerStage> Stages,
    string Title,
    string Description,
    string EmptyQueueMessage);

public sealed record PipelineJobStageDetail(string StageName, string Summary);

public sealed record PipelineJobRow(
    string JobId,
    string Title,
    string Stage,
    string StageDisplay,
    string ElapsedTimeDisplay,
    string TokensDisplay,
    string QuotaDisplay,
    bool NeedsYourDecision,
    string? DecisionLabel,
    string? ScheduleReason,
    bool CanMerge,
    bool CanRetryMerge,
    bool CanDrop,
    IReadOnlyList<PipelineJobStageDetail> StageDetails,
    PipelineStageStrip? Strip = null);

public sealed record PipelineProjectView(
    string ProjectId,
    IReadOnlyList<PipelineJobRow> Jobs,
    PipelineExplainerView? Explainer = null);

public static class PipelineViews
{
    public static string FormatDuration(long milliseconds)
    {
        if (milliseconds <= 0) return "0s";
        var ts = TimeSpan.FromMilliseconds(milliseconds);
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        }
        if (ts.TotalMinutes >= 1)
        {
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        }
        return $"{ts.Seconds}s";
    }

    public static PipelineProjectView ReadProjectView(
        string attemptsRoot,
        string projectId,
        PipelineRun.PipelineRunSettings? settings = null)
    {
        var projectJobsDir = Path.Combine(attemptsRoot, "pipeline", projectId, "jobs");
        if (!Directory.Exists(projectJobsDir))
        {
            return new PipelineProjectView(projectId, [], GetWorkflowExplainer(projectId));
        }

        var schedule = PipelineRun.LoadSchedule(attemptsRoot, projectId);
        var waitingReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (schedule is not null)
        {
            foreach (var w in schedule.Waiting)
            {
                if (!string.IsNullOrWhiteSpace(w.JobId) && !string.IsNullOrWhiteSpace(w.Reason))
                {
                    waitingReasons[w.JobId] = w.Reason;
                }
            }
        }

        var jobDirs = Directory.GetDirectories(projectJobsDir);
        var rows = new List<PipelineJobRow>();

        foreach (var jobDir in jobDirs)
        {
            var jobId = Path.GetFileName(jobDir);
            var jobFile = Path.Combine(jobDir, "job.json");
            if (!File.Exists(jobFile)) continue;

            JsonObject? jobObj;
            try
            {
                var text = File.ReadAllText(jobFile);
                jobObj = JsonNode.Parse(text) as JsonObject;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Unreadable or partially written JSON does not advance status on screen
                continue;
            }

            if (jobObj is null) continue;

            // Verify project ID matches: another project's rows are not shown
            var jobProjectId = jobObj["projectId"]?.ToString() ?? "";
            if (!string.Equals(jobProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = jobObj["summary"]?.ToString() ?? jobId;
            var status = jobObj["status"]?.ToString() ?? "unknown";
            var awaitingReason = jobObj["awaitingOwnerReason"]?.ToString();

            string? schedReason = null;
            if (string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase))
            {
                waitingReasons.TryGetValue(jobId, out schedReason);
            }

            var stageDisplay = status;
            if (!string.IsNullOrWhiteSpace(schedReason))
            {
                stageDisplay = $"{status} ({schedReason})";
            }

            // Needs your decision logic:
            // awaiting-owner, ready-local, or pull-request (when owner merges by default or not auto-merged)
            bool needsDecision = false;
            if (string.Equals(status, "awaiting-owner", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "ready-local", StringComparison.OrdinalIgnoreCase))
            {
                needsDecision = true;
            }
            else if (string.Equals(status, "pull-request", StringComparison.OrdinalIgnoreCase))
            {
                var mergeLowRisk = settings?.MergeLowRisk ?? false;
                var risk = jobObj["risk"]?.ToString()?.ToLowerInvariant();
                if (!mergeLowRisk || risk != "low")
                {
                    needsDecision = true;
                }
            }

            var decisionLabel = needsDecision ? Strings.T("Needs your decision") : null;

            // Elapsed time calculation
            long totalMs = SafeGetLong(jobObj["reportedTimeMs"], 0);
            var elapsedTimeDisplay = totalMs > 0 ? FormatDuration(totalMs) : Strings.T("time not reported");

            // Tokens calculation
            string tokensDisplay = Strings.T("tokens not reported");
            if (jobObj["tokens"] is JsonObject tokensObj)
            {
                var totalTokens = SafeGetInt(tokensObj["total"], 0);
                if (totalTokens <= 0)
                {
                    var inTok = SafeGetInt(tokensObj["input"], 0);
                    var outTok = SafeGetInt(tokensObj["output"], 0);
                    totalTokens = inTok + outTok;
                }

                if (totalTokens > 0)
                {
                    tokensDisplay = $"{totalTokens.ToString("N0", CultureInfo.InvariantCulture)} tokens";
                }
            }

            // Quota calculation: names provider, modelId, and remaining percent from last read
            string quotaDisplay = Strings.T("not reported");
            if (jobObj["quota"] is JsonObject quotaObj)
            {
                var prov = (quotaObj["providerId"]?.ToString() ?? quotaObj["provider"]?.ToString())?.Trim();
                var mod = quotaObj["modelId"]?.ToString()?.Trim();
                var unread = SafeGetBool(quotaObj["unread"]) || SafeGetBool(quotaObj["quotaUnread"]);

                if (!string.IsNullOrWhiteSpace(prov) && !string.IsNullOrWhiteSpace(mod))
                {
                    if (unread)
                    {
                        quotaDisplay = $"{prov} · {mod} · {Strings.T("unread")}";
                    }
                    else if (quotaObj["remainingPercent"] is JsonValue remV && remV.TryGetValue<int>(out var remPct))
                    {
                        quotaDisplay = $"{prov} · {mod} · {remPct}%";
                    }
                    else if (SafeGetInt(quotaObj["remainingPercent"], -1) is var rPct && rPct >= 0)
                    {
                        quotaDisplay = $"{prov} · {mod} · {rPct}%";
                    }
                    else
                    {
                        quotaDisplay = $"{prov} · {mod}";
                    }
                }
            }

            // Button actions
            var canMerge = string.Equals(status, "pull-request", StringComparison.OrdinalIgnoreCase);
            var canRetryMerge = (string.Equals(status, "awaiting-owner", StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(awaitingReason, PipelineRun.ReasonMergeFailed, StringComparison.OrdinalIgnoreCase)) ||
                                string.Equals(status, "merging", StringComparison.OrdinalIgnoreCase);
            var canDrop = !string.Equals(status, "merged", StringComparison.OrdinalIgnoreCase) &&
                          !string.Equals(status, "dropped", StringComparison.OrdinalIgnoreCase);

            // Stage details: reads each stage file's summary first
            var stageDetails = new List<PipelineJobStageDetail>();

            void TryAddDetail(string fileName, string stageName)
            {
                var p = Path.Combine(jobDir, fileName);
                if (File.Exists(p))
                {
                    try
                    {
                        var node = JsonNode.Parse(File.ReadAllText(p)) as JsonObject;
                        var s = node?["summary"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            stageDetails.Add(new PipelineJobStageDetail(stageName, s));
                        }
                    }
                    catch (Exception) { }
                }
            }

            TryAddDetail("intake.json", "Intake");
            TryAddDetail(Path.Combine("revisions", "1", "acceptance.json"), "Acceptance");
            TryAddDetail("routing.json", "Routing");
            TryAddDetail("pull-request.json", "Pull Request");
            TryAddDetail("merge.json", "Merge");

            var routingObj = TryLoadJson(Path.Combine(jobDir, "routing.json"));
            var specObj = TryLoadJson(Path.Combine(jobDir, "revisions", "1", "spec.json")) ?? TryLoadJson(Path.Combine(jobDir, "spec.json"));
            var strip = BuildStageStrip(status, awaitingReason, schedReason, jobObj, routingObj, specObj);

            rows.Add(new PipelineJobRow(
                JobId: jobId,
                Title: title,
                Stage: status,
                StageDisplay: stageDisplay,
                ElapsedTimeDisplay: elapsedTimeDisplay,
                TokensDisplay: tokensDisplay,
                QuotaDisplay: quotaDisplay,
                NeedsYourDecision: needsDecision,
                DecisionLabel: decisionLabel,
                ScheduleReason: schedReason,
                CanMerge: canMerge,
                CanRetryMerge: canRetryMerge,
                CanDrop: canDrop,
                StageDetails: stageDetails,
                Strip: strip));
        }

        return new PipelineProjectView(projectId, rows, GetWorkflowExplainer(projectId));
    }

    public static JsonObject? TryLoadJson(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static int SafeGetInt(JsonNode? node, int fallback = 0)
    {
        if (node is null) return fallback;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<int>(out var i)) return i;
            if (val.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return fallback;
    }

    public static long SafeGetLong(JsonNode? node, long fallback = 0)
    {
        if (node is null) return fallback;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<long>(out var l)) return l;
            if (val.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return fallback;
    }

    public static bool SafeGetBool(JsonNode? node, bool fallback = false)
    {
        if (node is null) return fallback;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<bool>(out var b)) return b;
            if (val.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed))
                return parsed;
        }
        return fallback;
    }

    public static PipelineExplainerView GetWorkflowExplainer(string? projectName = null)
    {
        var title = Strings.T("How the pipeline works");
        var desc = Strings.T("Pipeline workflow");
        var emptyMsg = string.IsNullOrWhiteSpace(projectName)
            ? Strings.T("No pipeline jobs yet.")
            : Strings.Format(Strings.T("No pipeline jobs yet for {0}."), projectName);

        var stages = new List<PipelineExplainerStage>
        {
            new(PipelineStageNames.Request,
                PipelineStageNames.LocalizedLabel(PipelineStageNames.Request),
                Strings.T("Describe what you want built; your request enters the pipeline queue."),
                OwnerIntervention: null),
            new(PipelineStageNames.Questions,
                PipelineStageNames.LocalizedLabel(PipelineStageNames.Questions),
                Strings.T("The author asks questions if requirements need clarity."),
                OwnerIntervention: Strings.T("Answers the questions")),
            new(PipelineStageNames.SpecAndTests,
                PipelineStageNames.LocalizedLabel(PipelineStageNames.SpecAndTests),
                Strings.T("The author writes the specification and acceptance tests."),
                OwnerIntervention: Strings.T("Approves the spec")),
            new(PipelineStageNames.BuildAndReview,
                PipelineStageNames.LocalizedLabel(PipelineStageNames.BuildAndReview),
                Strings.T("A builder creates code and a reviewer checks it in rounds until accepted."),
                OwnerIntervention: null),
            new(PipelineStageNames.Checks,
                PipelineStageNames.LocalizedLabel(PipelineStageNames.Checks),
                Strings.T("Automated gates run the test suite and verify sync with main."),
                OwnerIntervention: null),
            new(PipelineStageNames.PullRequest,
                PipelineStageNames.LocalizedLabel(PipelineStageNames.PullRequest),
                Strings.T("A pull request is opened on GitHub."),
                OwnerIntervention: Strings.T("Merges the pull request")),
            new(PipelineStageNames.Merged,
                PipelineStageNames.LocalizedLabel(PipelineStageNames.Merged),
                Strings.T("Changes are merged cleanly into main."),
                OwnerIntervention: null),
        };

        return new PipelineExplainerView(stages, title, desc, emptyMsg);
    }

    public static PipelineStageStrip BuildStageStrip(
        string jobDir,
        string status,
        string? awaitingOwnerReason = null,
        string? scheduleReason = null,
        JsonObject? jobObj = null)
    {
        var routingObj = TryLoadJson(Path.Combine(jobDir, "routing.json"));
        var specObj = TryLoadJson(Path.Combine(jobDir, "revisions", "1", "spec.json")) ?? TryLoadJson(Path.Combine(jobDir, "spec.json"));
        return BuildStageStrip(status, awaitingOwnerReason, scheduleReason, jobObj, routingObj, specObj);
    }

    public static PipelineStageStrip BuildStageStrip(
        string status,
        string? awaitingOwnerReason = null,
        string? scheduleReason = null,
        JsonObject? jobObj = null,
        JsonObject? routingObj = null,
        JsonObject? specObj = null)
    {
        var normStatus = (status ?? "").Trim().ToLowerInvariant();
        int activeIndex;
        bool needsYou = false;
        string? decisionPrompt = null;
        string activePlainWords;
        PipelineReviewLoop? reviewLoop = null;

        static string FormatModelName(string? rawModelId, string? rawProvider)
        {
            rawModelId = rawModelId?.Trim();
            rawProvider = rawProvider?.Trim();
            if (string.IsNullOrWhiteSpace(rawModelId))
            {
                if (!string.IsNullOrWhiteSpace(rawProvider))
                {
                    return char.ToUpperInvariant(rawProvider[0]) + rawProvider[1..];
                }
                return Strings.T("Lead");
            }
            var lower = rawModelId.ToLowerInvariant();
            if (lower.Contains("gemini")) return "Gemini";
            if (lower.Contains("claude")) return "Claude";
            if (lower.Contains("codex") || lower.Contains("chatgpt") || lower.Contains("gpt")) return "Codex";
            if (lower.Contains("grok")) return "Grok";
            var parts = rawModelId.Split(['-', '_', '/', ':'], StringSplitOptions.RemoveEmptyEntries);
            int partIdx = 0;
            if (parts.Length > 1 && (parts[0].Equals("ollama", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("local", StringComparison.OrdinalIgnoreCase)))
            {
                partIdx = 1;
            }
            if (parts.Length > partIdx && parts[partIdx].Length > 0)
            {
                return char.ToUpperInvariant(parts[partIdx][0]) + parts[partIdx][1..];
            }
            return rawModelId;
        }

        switch (normStatus)
        {
            case "queued":
            case "intake":
                activeIndex = 0;
                activePlainWords = normStatus == "queued"
                    ? Strings.T("Queued for intake")
                    : Strings.T("Evaluating request");
                break;

            case "waiting-questions":
            case "questions":
                activeIndex = 1;
                activePlainWords = Strings.T("Awaiting answers to questions");
                needsYou = true;
                decisionPrompt = Strings.T("Needs you: answer author questions");
                break;

            case "specifying":
            case "awaiting-approval":
            case "approved":
            case "routing":
                activeIndex = 2;
                if (normStatus == "specifying")
                {
                    activePlainWords = Strings.T("Specifying tasks and acceptance tests");
                }
                else if (normStatus == "awaiting-approval")
                {
                    activePlainWords = Strings.T("Spec and tests ready for approval");
                    needsYou = true;
                    decisionPrompt = Strings.T("Needs you: approve spec and tests");
                }
                else if (normStatus == "approved")
                {
                    activePlainWords = !string.IsNullOrWhiteSpace(scheduleReason)
                        ? Strings.Format(Strings.T("Approved, waiting to build ({0})"), scheduleReason)
                        : Strings.T("Approved, waiting to build");
                }
                else
                {
                    activePlainWords = Strings.T("Routing builder and reviewer");
                }
                break;

            case "building":
            case "gating":
            case "reviewing":
                activeIndex = 3;
                var tasksArray = specObj?["tasks"] as JsonArray;
                int totalTasks = tasksArray?.Count ?? 1;
                if (totalTasks <= 0) totalTasks = 1;
                int currentTaskIndex = 1;
                string? currentTaskId = null;
                if (tasksArray != null)
                {
                    for (int i = 0; i < tasksArray.Count; i++)
                    {
                        var tObj = tasksArray[i] as JsonObject;
                        var tId = tObj?["id"]?.ToString() ?? $"T{i + 1}";
                        var tStatus = jobObj?[$"task_{tId}_status"]?.ToString();
                        if (!string.Equals(tStatus, "accepted", StringComparison.OrdinalIgnoreCase))
                        {
                            currentTaskIndex = i + 1;
                            currentTaskId = tId;
                            break;
                        }
                    }
                }
                if (currentTaskId == null && tasksArray != null && tasksArray.Count > 0)
                {
                    currentTaskIndex = totalTasks;
                    var lastObj = tasksArray[^1] as JsonObject;
                    currentTaskId = lastObj?["id"]?.ToString() ?? $"T{totalTasks}";
                }
                if (jobObj?["currentTaskId"]?.ToString() is string explicitTaskId && !string.IsNullOrWhiteSpace(explicitTaskId))
                {
                    currentTaskId = explicitTaskId;
                    if (tasksArray != null)
                    {
                        for (int i = 0; i < tasksArray.Count; i++)
                        {
                            var tObj = tasksArray[i] as JsonObject;
                            var tId = tObj?["id"]?.ToString() ?? $"T{i + 1}";
                            if (string.Equals(tId, explicitTaskId, StringComparison.OrdinalIgnoreCase))
                            {
                                currentTaskIndex = i + 1;
                                break;
                            }
                        }
                    }
                }

                var chosenObj = routingObj?["chosen"] as JsonObject ?? routingObj;
                var builderNode = chosenObj?["builder"] as JsonObject;
                var builderModel = builderNode is not null
                    ? FormatModelName(
                        builderNode["modelId"]?.ToString() ?? builderNode["model"]?.ToString(),
                        builderNode["providerId"]?.ToString() ?? builderNode["provider"]?.ToString())
                    : FormatModelName(chosenObj?["builder"]?.ToString(), null);

                string? reviewerModel = null;
                if (chosenObj != null)
                {
                    foreach (var kvp in chosenObj)
                    {
                        if (kvp.Key.StartsWith("reviewer", StringComparison.OrdinalIgnoreCase))
                        {
                            if (kvp.Value is JsonObject rObj)
                            {
                                reviewerModel = FormatModelName(
                                    rObj["modelId"]?.ToString() ?? rObj["model"]?.ToString(),
                                    rObj["providerId"]?.ToString() ?? rObj["provider"]?.ToString());
                                break;
                            }
                            if (kvp.Value is JsonValue rVal)
                            {
                                reviewerModel = FormatModelName(rVal.ToString(), null);
                                break;
                            }
                        }
                    }
                }
                reviewerModel ??= "Gemini";

                int currentRound = 1;
                if (!string.IsNullOrWhiteSpace(currentTaskId))
                {
                    if (jobObj?[$"currentRound_{currentTaskId}"] is JsonValue crVal && crVal.TryGetValue<int>(out var cr))
                    {
                        currentRound = cr;
                    }
                    else if (jobObj?[$"reviewReturns_{currentTaskId}"] is JsonValue rrVal && rrVal.TryGetValue<int>(out var rr))
                    {
                        currentRound = 1 + rr;
                    }
                    else
                    {
                        var sCr = SafeGetInt(jobObj?[$"currentRound_{currentTaskId}"], 0);
                        if (sCr > 0) currentRound = sCr;
                    }
                }
                if (currentRound <= 1)
                {
                    var sC = SafeGetInt(jobObj?["currentRound"], 1);
                    if (sC > 0) currentRound = sC;
                }
                if (currentRound < 1) currentRound = 1;

                int maxReviewRounds = SafeGetInt(routingObj?["maxReviewRounds"], 3);
                if (maxReviewRounds < 1) maxReviewRounds = 1;
                if (maxReviewRounds < currentRound) maxReviewRounds = currentRound;

                var loopText = Strings.Format(Strings.T("round {0} of {1}"), currentRound, maxReviewRounds);
                reviewLoop = new PipelineReviewLoop(currentRound, maxReviewRounds, IsActive: currentRound > 1 || normStatus == "reviewing" || normStatus == "building", loopText);

                if (normStatus == "reviewing")
                {
                    activePlainWords = Strings.Format(Strings.T("{0} is reviewing task {1} of {2}"), reviewerModel, currentTaskIndex, totalTasks);
                }
                else if (normStatus == "building")
                {
                    activePlainWords = Strings.Format(Strings.T("{0} is building task {1} of {2}"), builderModel, currentTaskIndex, totalTasks);
                }
                else
                {
                    activePlainWords = Strings.Format(Strings.T("Acceptance gate is checking task {0} of {1}"), currentTaskIndex, totalTasks);
                }
                break;

            case "syncing":
            case "ready-local":
            case "opening-pr":
                activeIndex = 4;
                if (normStatus == "syncing")
                {
                    activePlainWords = Strings.T("Syncing changes with main");
                }
                else if (normStatus == "ready-local")
                {
                    activePlainWords = Strings.T("Checks passed on local branch");
                    needsYou = true;
                    decisionPrompt = Strings.T("Needs you: merge pull request");
                }
                else
                {
                    activePlainWords = Strings.T("Opening pull request");
                }
                break;

            case "pull-request":
            case "merging":
                activeIndex = 5;
                if (normStatus == "merging")
                {
                    activePlainWords = Strings.T("Merging pull request into main");
                }
                else
                {
                    var prNum = SafeGetInt(jobObj?["pullRequestNumber"], 0);
                    activePlainWords = prNum > 0
                        ? Strings.Format(Strings.T("Pull request #{0} open"), prNum)
                        : Strings.T("Pull request open");
                }
                break;

            case "merged":
                activeIndex = 6;
                activePlainWords = Strings.T("Merged into main");
                break;

            case "awaiting-owner":
                needsYou = true;
                var reason = awaitingOwnerReason?.ToLowerInvariant() ?? "";
                if (reason.Contains("question"))
                {
                    activeIndex = 1;
                    activePlainWords = Strings.T("Needs you: answer author questions");
                    decisionPrompt = Strings.T("Needs you: answer author questions");
                }
                else if (reason.Contains("spec") || reason.Contains("approval"))
                {
                    activeIndex = 2;
                    activePlainWords = Strings.T("Needs you: approve spec and tests");
                    decisionPrompt = Strings.T("Needs you: approve spec and tests");
                }
                else if (reason.Contains("review-rounds") || reason.Contains("rounds"))
                {
                    activeIndex = 3;
                    activePlainWords = Strings.T("Needs you: review rounds limit reached");
                    decisionPrompt = Strings.T("Needs you: review rounds limit reached");
                    int cr = SafeGetInt(jobObj?["currentRound"], 3);
                    int mr = SafeGetInt(routingObj?["maxReviewRounds"], 3);
                    if (cr < 1) cr = 1;
                    if (mr < cr) mr = cr;
                    reviewLoop = new PipelineReviewLoop(cr, mr, true, Strings.Format(Strings.T("round {0} of {1}"), cr, mr));
                }
                else if (reason.Contains("reviewer-quota"))
                {
                    activeIndex = 3;
                    activePlainWords = Strings.T("Needs you: reviewer quota exhausted");
                    decisionPrompt = Strings.T("Needs you: reviewer quota exhausted");
                }
                else if (reason.Contains("builder-quota"))
                {
                    activeIndex = 3;
                    activePlainWords = Strings.T("Needs you: builder quota exhausted");
                    decisionPrompt = Strings.T("Needs you: builder quota exhausted");
                }
                else if (reason.Contains("quota"))
                {
                    activeIndex = 3;
                    activePlainWords = Strings.T("Needs you: reviewer quota exhausted");
                    decisionPrompt = Strings.T("Needs you: reviewer quota exhausted");
                }
                else if (reason.Contains("gate-returns"))
                {
                    activeIndex = 3;
                    activePlainWords = Strings.T("Needs you: acceptance gate failed repeatedly");
                    decisionPrompt = Strings.T("Needs you: acceptance gate failed repeatedly");
                }
                else if (reason.Contains("merge-failed") || reason.Contains("merge"))
                {
                    activeIndex = 5;
                    activePlainWords = Strings.Format(Strings.T("Needs you: merge failed ({0})"), awaitingOwnerReason ?? "error");
                    decisionPrompt = Strings.T("Needs you: merge pull request");
                }
                else
                {
                    if (jobObj?["routing"] != null || routingObj != null)
                    {
                        activeIndex = 3;
                    }
                    else if (specObj != null)
                    {
                        activeIndex = 2;
                    }
                    else
                    {
                        activeIndex = 0;
                    }
                    activePlainWords = !string.IsNullOrWhiteSpace(awaitingOwnerReason)
                        ? Strings.Format(Strings.T("Needs you: {0}"), awaitingOwnerReason)
                        : Strings.T("Needs you");
                    decisionPrompt = activePlainWords;
                }
                break;

            case "dropped":
                activeIndex = 0;
                activePlainWords = Strings.T("Job dropped");
                break;

            case "stopped":
                activeIndex = 0;
                activePlainWords = Strings.T("Job stopped");
                break;

            default:
                activeIndex = 0;
                activePlainWords = string.IsNullOrWhiteSpace(status) ? Strings.T("unknown") : status;
                break;
        }

        var stages = new List<PipelineStage>();
        for (int i = 0; i < PipelineStageNames.All.Count; i++)
        {
            var id = PipelineStageNames.All[i];
            var label = PipelineStageNames.LocalizedLabel(id);
            PipelineStageStatus stageStatus;
            bool stageNeedsYou = false;
            string? stagePlainWords = null;

            if (normStatus == "merged")
            {
                stageStatus = PipelineStageStatus.Completed;
                stagePlainWords = Strings.T("Merged into main");
            }
            else if (i < activeIndex)
            {
                stageStatus = PipelineStageStatus.Completed;
            }
            else if (i == activeIndex)
            {
                stageStatus = PipelineStageStatus.Active;
                stageNeedsYou = needsYou;
                stagePlainWords = activePlainWords;
            }
            else
            {
                stageStatus = PipelineStageStatus.Future;
            }

            stages.Add(new PipelineStage(id, label, stageStatus, stageNeedsYou, stagePlainWords));
        }

        return new PipelineStageStrip(stages, activePlainWords, reviewLoop, needsYou, decisionPrompt);
    }

    public static Control RenderStageStrip(PipelineStageStrip strip, Action? onOpenDecision = null)
    {
        var container = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4) };

        var stripRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        for (int i = 0; i < strip.Stages.Count; i++)
        {
            var stage = strip.Stages[i];
            Control stageItem;

            if (stage.Status == PipelineStageStatus.Completed)
            {
                var tickLabel = "✓ " + stage.Label;
                var b = Look.Badge(tickLabel, Palette.Accent, Palette.AccentSoft);
                ToolTip.SetTip(b, Strings.T("Completed"));
                stageItem = b;
            }
            else if (stage.Status == PipelineStageStatus.Active)
            {
                var activePanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var litBadge = Look.Badge(stage.Label, stage.NeedsYou ? Palette.Warn : Palette.Accent, stage.NeedsYou ? Palette.WarnSoft : Palette.AccentSoft);
                activePanel.Children.Add(litBadge);

                if (stage.NeedsYou)
                {
                    var needsBadge = Look.Badge(Strings.T("Needs you"), Palette.Danger, Palette.DangerSoft);
                    activePanel.Children.Add(needsBadge);
                }

                if (stage.Id == PipelineStageNames.BuildAndReview && strip.ReviewLoop is { } loop && loop.IsActive)
                {
                    var loopBadge = Look.Badge("↺ " + loop.DisplayText, Palette.Build, Palette.BuildSoft);
                    ToolTip.SetTip(loopBadge, loop.DisplayText);
                    activePanel.Children.Add(loopBadge);
                }

                stageItem = activePanel;
            }
            else
            {
                var dimBadge = new Border
                {
                    Background = Palette.Surface2,
                    BorderBrush = Palette.Line,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(99),
                    Padding = new Thickness(8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.55,
                    Child = new TextBlock
                    {
                        Text = stage.Label,
                        FontSize = 14,
                        Foreground = Palette.Muted
                    }
                };
                stageItem = dimBadge;
            }

            stripRow.Children.Add(stageItem);

            if (i < strip.Stages.Count - 1)
            {
                var sep = new TextBlock
                {
                    Text = "→",
                    Foreground = Palette.Muted,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0)
                };
                stripRow.Children.Add(sep);
            }
        }

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = stripRow
        };
        container.Children.Add(scroll);

        if (!string.IsNullOrWhiteSpace(strip.ActiveStagePlainWords))
        {
            var plainText = new TextBlock
            {
                Text = strip.ActiveStagePlainWords,
                Foreground = Palette.Text,
                FontSize = 14,
                FontWeight = FontWeight.Medium,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 0, 0)
            };
            container.Children.Add(plainText);
        }

        if (strip.NeedsYou)
        {
            var promptRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 4, 0, 0)
            };
            var actionBtn = Look.Button(strip.DecisionPrompt ?? Strings.T("Open decision"), "alert", Look.Kind.Primary);
            if (onOpenDecision != null)
            {
                actionBtn.Click += (_, _) => onOpenDecision();
            }
            promptRow.Children.Add(actionBtn);
            container.Children.Add(promptRow);
        }

        return container;
    }

    public static Control RenderWorkflowExplainer(PipelineExplainerView explainer)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(Look.Head("pipeline", Palette.Accent, Palette.AccentSoft, explainer.Title));
        panel.Children.Add(new TextBlock
        {
            Text = explainer.Description,
            Foreground = Palette.Muted,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });

        var diagramRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 6)
        };
        for (int i = 0; i < explainer.Stages.Count; i++)
        {
            var st = explainer.Stages[i];
            var stepText = Strings.Format("{0}. {1}", i + 1, st.Label);
            var stepBadge = Look.Badge(stepText, Palette.Accent, Palette.AccentSoft);
            diagramRow.Children.Add(stepBadge);
            if (i < explainer.Stages.Count - 1)
            {
                diagramRow.Children.Add(new TextBlock
                {
                    Text = "→",
                    Foreground = Palette.Muted,
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0)
                });
            }
        }
        var diagramScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = diagramRow
        };
        panel.Children.Add(diagramScroll);

        var stageList = new StackPanel { Spacing = 10, Margin = new Thickness(0, 4) };
        foreach (var s in explainer.Stages)
        {
            var card = new StackPanel { Spacing = 4 };
            var headerRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            headerRow.Children.Add(new TextBlock
            {
                Text = s.Label,
                FontWeight = FontWeight.Bold,
                FontSize = 14,
                Foreground = Palette.Text
            });

            if (!string.IsNullOrWhiteSpace(s.OwnerIntervention))
            {
                var stepInBadge = Look.Badge(
                    Strings.Format(Strings.T("Where you step in: {0}"), s.OwnerIntervention),
                    Palette.Warn,
                    Palette.WarnSoft);
                headerRow.Children.Add(stepInBadge);
            }

            card.Children.Add(headerRow);
            card.Children.Add(new TextBlock
            {
                Text = s.PlainSentence,
                FontSize = 13,
                Foreground = Palette.Muted,
                TextWrapping = TextWrapping.Wrap
            });

            stageList.Children.Add(Look.Card(card, padding: 12));
        }

        panel.Children.Add(stageList);
        return panel;
    }

    public static Control RenderJobCard(
        PipelineJobRow job,
        Action<string, string>? onAction = null,
        Action<string>? onOpenDecision = null)
    {
        var cardPanel = new StackPanel { Spacing = 8 };

        var headRow = new DockPanel();
        var titleBlock = new TextBlock
        {
            Text = job.Title,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.Text,
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(titleBlock, Dock.Left);
        headRow.Children.Add(titleBlock);
        cardPanel.Children.Add(headRow);

        var metaText = $"{job.ElapsedTimeDisplay} · {job.TokensDisplay} · {job.QuotaDisplay}";
        cardPanel.Children.Add(new TextBlock
        {
            Text = metaText,
            FontSize = 12,
            Foreground = Palette.Muted
        });

        if (job.Strip is not null)
        {
            cardPanel.Children.Add(RenderStageStrip(job.Strip, () => onOpenDecision?.Invoke(job.JobId)));
        }

        var buttonsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0)
        };

        if (job.CanMerge)
        {
            var btn = Look.Button(Strings.T("Merge"), "git", Look.Kind.Primary);
            btn.Click += (_, _) => onAction?.Invoke("merge", job.JobId);
            buttonsRow.Children.Add(btn);
        }

        if (job.CanRetryMerge)
        {
            var btn = Look.Button(Strings.T("Retry merge"), "refresh", Look.Kind.Danger);
            btn.Click += (_, _) => onAction?.Invoke("retry-merge", job.JobId);
            buttonsRow.Children.Add(btn);
        }

        if (job.CanDrop)
        {
            var btn = Look.Button(Strings.T("Drop"), "trash", Look.Kind.Quiet);
            btn.Click += (_, _) => onAction?.Invoke("drop", job.JobId);
            buttonsRow.Children.Add(btn);
        }

        if (buttonsRow.Children.Count > 0)
        {
            cardPanel.Children.Add(buttonsRow);
        }

        return Look.Card(cardPanel, padding: 14);
    }

    public static Control RenderPipelinePanel(
        PipelineProjectView view,
        bool showExplainer = false,
        Action? onToggleExplainer = null,
        Action<string, string>? onJobAction = null,
        Action<string>? onOpenDecision = null)
    {
        var panel = new StackPanel { Spacing = 12 };

        var headerSection = new StackPanel { Spacing = 6 };
        headerSection.Children.Add(Look.Head("pipeline", Palette.Accent, Palette.AccentSoft, Strings.T("Pipeline")));

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        var explainerBtn = Look.Button(
            showExplainer ? Strings.T("Hide how it works") : Strings.T("How it works"),
            "box",
            Look.Kind.Small);
        if (onToggleExplainer != null) explainerBtn.Click += (_, _) => onToggleExplainer();
        toolbar.Children.Add(explainerBtn);

        headerSection.Children.Add(toolbar);
        panel.Children.Add(headerSection);

        if (view.Jobs.Count == 0)
        {
            var explainer = view.Explainer ?? GetWorkflowExplainer(view.ProjectId);
            var emptyNotice = new TextBlock
            {
                Text = explainer.EmptyQueueMessage,
                FontSize = 14,
                Foreground = Palette.Muted,
                Margin = new Thickness(0, 0, 0, 8)
            };
            panel.Children.Add(emptyNotice);
            panel.Children.Add(RenderWorkflowExplainer(explainer));
        }
        else
        {
            if (showExplainer)
            {
                panel.Children.Add(RenderWorkflowExplainer(view.Explainer ?? GetWorkflowExplainer(view.ProjectId)));
            }

            foreach (var job in view.Jobs)
            {
                panel.Children.Add(RenderJobCard(job, onJobAction, onOpenDecision));
            }
        }

        return panel;
    }
}
