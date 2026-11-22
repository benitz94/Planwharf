// Job 34 implementation of native/porting/jobs/22-pipeline-run.md.
// Pipeline project panel view for the native application.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Planwharf.Engine;

namespace Planwharf.Native.Core;

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
    IReadOnlyList<PipelineJobStageDetail> StageDetails);

public sealed record PipelineProjectView(
    string ProjectId,
    IReadOnlyList<PipelineJobRow> Jobs);

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
            return new PipelineProjectView(projectId, []);
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
            long totalMs = 0;
            if (jobObj["reportedTimeMs"] is JsonValue timeVal && timeVal.TryGetValue<long>(out var tMs))
            {
                totalMs += tMs;
            }

            var elapsedTimeDisplay = totalMs > 0 ? FormatDuration(totalMs) : Strings.T("time not reported");

            // Tokens calculation
            string tokensDisplay = Strings.T("tokens not reported");
            if (jobObj["tokens"] is JsonObject tokensObj)
            {
                var totalTokens = 0;
                if (tokensObj["total"] is JsonValue totVal && totVal.TryGetValue<int>(out var tTot))
                {
                    totalTokens = tTot;
                }
                else
                {
                    var inTok = tokensObj["input"]?.GetValue<int>() ?? 0;
                    var outTok = tokensObj["output"]?.GetValue<int>() ?? 0;
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
                var prov = quotaObj["providerId"]?.ToString() ?? quotaObj["provider"]?.ToString();
                var mod = quotaObj["modelId"]?.ToString();
                var unread = quotaObj["unread"]?.GetValue<bool>() == true || quotaObj["quotaUnread"]?.GetValue<bool>() == true;

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
                StageDetails: stageDetails));
        }

        return new PipelineProjectView(projectId, rows);
    }
}
