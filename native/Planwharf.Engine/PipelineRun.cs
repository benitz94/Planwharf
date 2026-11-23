// Job 34 implementation of native/porting/jobs/22-pipeline-run.md.
// Concurrent pipeline scheduler and optional low-risk auto-merge engine.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public static class PipelineRun
{
    public const int Schema = 1;

    public const string ReasonQuota = "quota";
    public const string ReasonUnreadQuota = "unread-quota";
    public const string ReasonModelBusy = "model-busy";
    public const string ReasonCap = "cap";
    public const string ReasonInvalidCap = "invalid-cap";
    public const string ReasonWorktreeCollision = "worktree-collision";
    public const string ReasonBranchCollision = "branch-collision";
    public const string ReasonWrongHead = "wrong-head";
    public const string ReasonNotMergeable = "not-mergeable";
    public const string ReasonMergeFailed = "merge-failed";
    public const string ReasonNoLinkedRepo = "no linked GitHub repository";
    public const string ReasonDirtyWorktree = "dirty-worktree";
    public const string ReasonCleanupFailed = "cleanup-failed";
    public const string ReasonBranchName = "branch-name";

    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    static readonly ConcurrentDictionary<string, object> ProjectLocks = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, string> ActiveProjectLocks = new(StringComparer.Ordinal);

    public sealed class PipelineRunSettings
    {
        public int? MaxConcurrentJobs { get; set; }
        public bool MergeLowRisk { get; set; }
        public bool HasInvalidCap { get; set; }

        public static PipelineRunSettings FromJson(JsonObject? obj)
        {
            var settings = new PipelineRunSettings();
            if (obj is null) return settings;

            if (obj["pipeline.run.mergeLowRisk"] is JsonValue mVal && mVal.TryGetValue<bool>(out var mergeLow))
            {
                settings.MergeLowRisk = mergeLow;
            }
            else if (obj["mergeLowRisk"] is JsonValue mVal2 && mVal2.TryGetValue<bool>(out var mergeLow2))
            {
                settings.MergeLowRisk = mergeLow2;
            }

            var capNode = obj["pipeline.run.maxConcurrentJobs"] ?? obj["maxConcurrentJobs"];
            if (capNode is not null)
            {
                if (capNode is JsonValue jv && jv.TryGetValue<int>(out var capInt))
                {
                    settings.MaxConcurrentJobs = capInt;
                    if (capInt <= 0) settings.HasInvalidCap = true;
                }
                else
                {
                    settings.HasInvalidCap = true;
                }
            }

            return settings;
        }
    }

    public sealed record ScheduleSlot(
        string JobId,
        string ModelId,
        string ProviderId,
        string Status,
        string StartedAt,
        string? WorktreePath = null);

    public sealed record WaitingSlot(
        string JobId,
        string Reason,
        string Summary,
        string? ModelId = null,
        string? CreatedAt = null);

    public sealed class ScheduleRecord
    {
        public required string Summary { get; init; }
        public int Schema { get; init; } = PipelineRun.Schema;
        public required string ProjectId { get; init; }
        public required string UpdatedAt { get; init; }
        public List<ScheduleSlot> Running { get; init; } = [];
        public List<WaitingSlot> Waiting { get; init; } = [];
    }

    public sealed class MergeRecord
    {
        public required string Summary { get; init; }
        public required string JobId { get; init; }
        public required int PullRequestNumber { get; init; }
        public required string Sha { get; init; }
        public string Method { get; init; } = "merge";
        public required string MergedAt { get; init; }
    }

    public sealed record PipelineForgePullRequest(
        int Number,
        string HeadSha,
        string HeadBranch,
        bool Mergeable,
        bool Merged,
        string? MergeCommitSha = null);

    public interface IPipelineForgeOps
    {
        bool GetPullRequest(string repoUrlOrName, int prNumber, out PipelineForgePullRequest? pr, out string? error);
        bool MergePullRequest(string repoUrlOrName, int prNumber, string headSha, string commitTitle, out string? mergeCommitSha, out string? refusalReason, out string? error);
    }

    public interface IPipelineGitOps
    {
        bool IsWorktreeDirty(string worktreePath, out bool isDirty, out string? error);
        bool RemoveWorktree(string repoPath, string worktreePath, out string? error);
        bool DeleteLocalBranch(string repoPath, string branchName, bool force, out string? error);
        bool IsBranchMergedIntoDefault(string repoPath, string branchName, string defaultBranch, out bool isMerged, out string? error);
        bool BranchExists(string repoPath, string branchName, out bool exists, out string? error);
        bool WorktreeExists(string repoPath, string worktreePath, out bool exists, out string? error);
    }

    public interface IGateEvidenceVerifier
    {
        bool HasPassingPostSyncGate(string attemptsRoot, string projectId, string jobId, string manifestSha256, string headSha);
    }

    public sealed class DefaultGateEvidenceVerifier : IGateEvidenceVerifier
    {
        public bool HasPassingPostSyncGate(string attemptsRoot, string projectId, string jobId, string manifestSha256, string headSha)
        {
            if (!AcceptanceGate.SafeId(projectId) || !PipelineBuild.SafeJobId(jobId)) return false;
            var jobDir = PipelineBuild.JobDir(attemptsRoot, projectId, jobId);
            var gateRunsDir = Path.Combine(jobDir, "gate-runs");
            if (!Directory.Exists(gateRunsDir)) return false;

            var jsonFiles = Directory.GetFiles(gateRunsDir, "*.json");
            foreach (var file in jsonFiles)
            {
                var seal = file[..^".json".Length] + ".seal";
                if (!File.Exists(seal)) continue;

                try
                {
                    var fileBytes = File.ReadAllBytes(file);
                    var expectedSeal = File.ReadAllText(seal).Trim();
                    if (!string.Equals(expectedSeal, AcceptanceGate.Sha256(fileBytes), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (JsonNode.Parse(fileBytes) is not JsonObject node) continue;
                    var outcome = node["outcome"]?.ToString();
                    var stage = node["stage"]?.ToString();
                    var nodeJobId = node["jobId"]?.ToString();
                    var nodeManifest = node["manifestSha256"]?.ToString();
                    var nodeCommit = node["commitSha"]?.ToString();

                    if (outcome == "pass" &&
                        stage == "post-sync" &&
                        string.Equals(nodeJobId, jobId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(nodeManifest, manifestSha256, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(nodeCommit, headSha, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Ignore unreadable gate run records
                }
            }

            return false;
        }
    }

    public static string ScheduleFile(string attemptsRoot, string projectId) =>
        Path.GetFullPath(Path.Combine(attemptsRoot, "pipeline", projectId, "schedule.json"));

    public static string ScheduleLockFile(string attemptsRoot, string projectId) =>
        Path.GetFullPath(Path.Combine(attemptsRoot, "pipeline", projectId, "schedule.lock"));

    public static string MergeFile(string attemptsRoot, string projectId, string jobId) =>
        Path.GetFullPath(Path.Combine(PipelineBuild.JobDir(attemptsRoot, projectId, jobId), "merge.json"));

    public static object LockFor(string projectId) =>
        ProjectLocks.GetOrAdd(projectId, static _ => new object());

    public static ScheduleRecord? LoadSchedule(string attemptsRoot, string projectId)
    {
        var file = ScheduleFile(attemptsRoot, projectId);
        if (!File.Exists(file)) return null;
        try
        {
            var text = File.ReadAllText(file);
            var obj = JsonNode.Parse(text) as JsonObject;
            if (obj is null) return null;

            var summary = obj["summary"]?.ToString() ?? "";
            var schema = obj["schema"]?.GetValue<int>() ?? 1;
            var proj = obj["projectId"]?.ToString() ?? projectId;
            var updatedAt = obj["updatedAt"]?.ToString() ?? "";

            var running = new List<ScheduleSlot>();
            if (obj["running"] is JsonArray runArr)
            {
                foreach (var item in runArr.OfType<JsonObject>())
                {
                    running.Add(new ScheduleSlot(
                        JobId: item["jobId"]?.ToString() ?? "",
                        ModelId: item["modelId"]?.ToString() ?? "",
                        ProviderId: item["providerId"]?.ToString() ?? "",
                        Status: item["status"]?.ToString() ?? "",
                        StartedAt: item["startedAt"]?.ToString() ?? "",
                        WorktreePath: item["worktreePath"]?.ToString()));
                }
            }

            var waiting = new List<WaitingSlot>();
            if (obj["waiting"] is JsonArray waitArr)
            {
                foreach (var item in waitArr.OfType<JsonObject>())
                {
                    waiting.Add(new WaitingSlot(
                        JobId: item["jobId"]?.ToString() ?? "",
                        Reason: item["reason"]?.ToString() ?? "",
                        Summary: item["summary"]?.ToString() ?? "",
                        ModelId: item["modelId"]?.ToString(),
                        CreatedAt: item["createdAt"]?.ToString()));
                }
            }

            return new ScheduleRecord
            {
                Summary = summary,
                Schema = schema,
                ProjectId = proj,
                UpdatedAt = updatedAt,
                Running = running,
                Waiting = waiting
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void WriteScheduleAtomic(string attemptsRoot, string projectId, ScheduleRecord record)
    {
        var jsonObj = new JsonObject
        {
            ["summary"] = record.Summary,
            ["schema"] = record.Schema,
            ["projectId"] = record.ProjectId,
            ["updatedAt"] = record.UpdatedAt,
            ["running"] = new JsonArray(record.Running.Select(r => new JsonObject
            {
                ["jobId"] = r.JobId,
                ["modelId"] = r.ModelId,
                ["providerId"] = r.ProviderId,
                ["status"] = r.Status,
                ["startedAt"] = r.StartedAt,
                ["worktreePath"] = r.WorktreePath
            }).ToArray()),
            ["waiting"] = new JsonArray(record.Waiting.Select(w => new JsonObject
            {
                ["summary"] = w.Summary,
                ["jobId"] = w.JobId,
                ["reason"] = w.Reason,
                ["modelId"] = w.ModelId,
                ["createdAt"] = w.CreatedAt
            }).ToArray())
        };

        var dest = ScheduleFile(attemptsRoot, projectId);
        PipelineBuild.WriteAtomic(dest, jsonObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static MergeRecord? LoadMerge(string attemptsRoot, string projectId, string jobId)
    {
        var file = MergeFile(attemptsRoot, projectId, jobId);
        if (!File.Exists(file)) return null;
        try
        {
            var text = File.ReadAllText(file);
            var obj = JsonNode.Parse(text) as JsonObject;
            if (obj is null) return null;

            return new MergeRecord
            {
                Summary = obj["summary"]?.ToString() ?? "",
                JobId = obj["jobId"]?.ToString() ?? jobId,
                PullRequestNumber = obj["pullRequestNumber"]?.GetValue<int>() ?? 0,
                Sha = obj["sha"]?.ToString() ?? "",
                Method = obj["method"]?.ToString() ?? "merge",
                MergedAt = obj["mergedAt"]?.ToString() ?? ""
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void WriteMergeAtomic(string attemptsRoot, string projectId, string jobId, MergeRecord record)
    {
        var jsonObj = new JsonObject
        {
            ["summary"] = record.Summary,
            ["jobId"] = record.JobId,
            ["pullRequestNumber"] = record.PullRequestNumber,
            ["sha"] = record.Sha,
            ["method"] = record.Method,
            ["mergedAt"] = record.MergedAt
        };

        var dest = MergeFile(attemptsRoot, projectId, jobId);
        PipelineBuild.WriteAtomic(dest, jsonObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public sealed class ScheduleLockHandle : IDisposable
    {
        readonly string projectId;
        readonly string lockFile;
        readonly object monitor;
        bool disposed;

        public ScheduleLockHandle(string projectId, string lockFile, object monitor)
        {
            this.projectId = projectId;
            this.lockFile = lockFile;
            this.monitor = monitor;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                ActiveProjectLocks.TryRemove(projectId, out _);
                if (File.Exists(lockFile))
                {
                    File.Delete(lockFile);
                }
            }
            catch (Exception)
            {
                // Suppress file delete errors on dispose
            }
            finally
            {
                Monitor.Exit(monitor);
            }
        }
    }

    public static bool TryAcquireScheduleLock(
        string attemptsRoot,
        string projectId,
        out IDisposable? handle,
        out string? failureReason)
    {
        handle = null;
        failureReason = null;

        if (!ActiveProjectLocks.TryAdd(projectId, projectId))
        {
            failureReason = $"Schedule lock for project '{projectId}' is already held in this process.";
            return false;
        }

        var monitor = LockFor(projectId);
        if (!Monitor.TryEnter(monitor, TimeSpan.FromSeconds(5)))
        {
            ActiveProjectLocks.TryRemove(projectId, out _);
            failureReason = "In-memory project lock is held.";
            return false;
        }

        var lockFile = ScheduleLockFile(attemptsRoot, projectId);
        try
        {
            if (File.Exists(lockFile))
            {
                var text = File.ReadAllText(lockFile);
                if (JsonNode.Parse(text) is JsonObject lockObj &&
                    lockObj["pid"] is JsonValue pidVal &&
                    pidVal.TryGetValue<int>(out var pid))
                {
                    bool isAlive = false;
                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        isAlive = !proc.HasExited;
                    }
                    catch (Exception)
                    {
                        isAlive = false;
                    }

                    if (isAlive)
                    {
                        failureReason = $"Project schedule lock held by active process PID {pid}.";
                        ActiveProjectLocks.TryRemove(projectId, out _);
                        Monitor.Exit(monitor);
                        return false;
                    }
                }

                // If PID is gone or unparseable, restart clears the stale lock
                try
                {
                    File.Delete(lockFile);
                }
                catch (Exception ex)
                {
                    failureReason = $"Failed to clear stale lock file: {ex.Message}";
                    ActiveProjectLocks.TryRemove(projectId, out _);
                    Monitor.Exit(monitor);
                    return false;
                }
            }

            var newLockObj = new JsonObject
            {
                ["summary"] = $"Schedule lock for project {projectId}",
                ["pid"] = Environment.ProcessId,
                ["lockedAt"] = DateTime.UtcNow.ToString("O")
            };
            PipelineBuild.WriteAtomic(lockFile, newLockObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            handle = new ScheduleLockHandle(projectId, lockFile, monitor);
            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"Failed to acquire schedule lock: {ex.Message}";
            ActiveProjectLocks.TryRemove(projectId, out _);
            Monitor.Exit(monitor);
            return false;
        }
    }

    public static readonly string[] ActiveRunningStatuses =
    [
        "routing",
        "building",
        "gating",
        "reviewing",
        "awaiting-owner",
        "syncing",
        "opening-pr",
        "merging"
    ];

    public sealed record AdmissibilityResult(
        bool IsAdmissible,
        string? Reason,
        string? ModelId,
        string? ProviderId,
        string? WorktreePath);

    public static (string? modelId, string? providerId) ResolveBuilderModel(
        string attemptsRoot,
        string projectId,
        string jobId,
        JsonArray? team)
    {
        var jobDir = PipelineBuild.JobDir(attemptsRoot, projectId, jobId);

        // Check existing routing snapshot
        var routingFile = Path.Combine(jobDir, "routing.json");
        if (!File.Exists(routingFile))
        {
            // check revisions/1/routing.json
            var revRouting = Path.Combine(jobDir, "revisions", "1", "routing.json");
            if (File.Exists(revRouting)) routingFile = revRouting;
        }

        if (File.Exists(routingFile))
        {
            var rObj = PipelineBuild.LoadJson(routingFile);
            if (rObj?["chosen"] is JsonObject chosenObj &&
                chosenObj["builder"] is JsonObject builderObj)
            {
                var mId = builderObj["modelId"]?.ToString();
                var pId = builderObj["providerId"]?.ToString();
                if (!string.IsNullOrWhiteSpace(mId))
                {
                    return (mId, pId);
                }
            }
        }

        if (team is null) return (null, null);

        // Resolve highest weight worker model
        JsonObject? best = null;
        var bestWeight = -1;
        foreach (var m in team.OfType<JsonObject>())
        {
            if (m["roles"] is JsonObject roles && roles["worker"]?.GetValue<bool>() == true)
            {
                var w = m["weight"]?.GetValue<int>() ?? 10;
                if (w > bestWeight)
                {
                    bestWeight = w;
                    best = m;
                }
            }
        }

        if (best is not null)
        {
            var mId = best["id"]?.ToString() ?? best["modelId"]?.ToString();
            var pId = best["providerId"]?.ToString() ?? best["provider"]?.ToString();
            return (mId, pId);
        }

        return (null, null);
    }

    public static AdmissibilityResult CheckJobAdmissibility(
        string attemptsRoot,
        string dataRoot,
        string repoPath,
        string projectId,
        string jobId,
        PipelineRunSettings settings,
        JsonArray? team,
        ISet<string> busyModelIds,
        int currentRunningJobCount,
        IPipelineGitOps? gitOps = null)
    {
        if (!PipelineBuild.VerifyStartGate(attemptsRoot, projectId, jobId, out var jobObj, out _))
        {
            return new AdmissibilityResult(false, "start-gate-failed", null, null, null);
        }

        if (settings.HasInvalidCap || (settings.MaxConcurrentJobs.HasValue && settings.MaxConcurrentJobs.Value <= 0))
        {
            return new AdmissibilityResult(false, ReasonInvalidCap, null, null, null);
        }

        if (settings.MaxConcurrentJobs.HasValue && currentRunningJobCount >= settings.MaxConcurrentJobs.Value)
        {
            return new AdmissibilityResult(false, ReasonCap, null, null, null);
        }

        var (builderModelId, builderProviderId) = ResolveBuilderModel(attemptsRoot, projectId, jobId, team);
        if (string.IsNullOrWhiteSpace(builderModelId))
        {
            return new AdmissibilityResult(false, ReasonQuota, null, null, null);
        }

        if (team is not null)
        {
            var teamModel = team.OfType<JsonObject>().FirstOrDefault(m =>
                string.Equals(m["id"]?.ToString() ?? m["modelId"]?.ToString(), builderModelId, StringComparison.OrdinalIgnoreCase));

            if (teamModel is not null)
            {
                if (teamModel["quotaKnown"]?.GetValue<bool>() == false ||
                    teamModel["quotaUnread"]?.GetValue<bool>() == true ||
                    teamModel["unread"]?.GetValue<bool>() == true)
                {
                    return new AdmissibilityResult(false, ReasonUnreadQuota, builderModelId, builderProviderId, null);
                }

                if (teamModel["disabled"]?.GetValue<bool>() == true ||
                    teamModel["unavailable"]?.GetValue<bool>() == true ||
                    teamModel["expired"]?.GetValue<bool>() == true ||
                    teamModel["signedOut"]?.GetValue<bool>() == true)
                {
                    return new AdmissibilityResult(false, ReasonQuota, builderModelId, builderProviderId, null);
                }

                if (teamModel["remainingPercent"] is JsonValue remNode && remNode.TryGetValue<int>(out var remPct))
                {
                    var minPct = teamModel["minRemainingPercent"]?.GetValue<int>() ?? 0;
                    if (remPct <= minPct)
                    {
                        return new AdmissibilityResult(false, ReasonQuota, builderModelId, builderProviderId, null);
                    }
                }
            }
        }

        if (busyModelIds.Contains(builderModelId))
        {
            return new AdmissibilityResult(false, ReasonModelBusy, builderModelId, builderProviderId, null);
        }

        var worktreeDir = PipelineBuild.WorktreeDir(dataRoot, projectId, jobId);
        var branchName = PipelineBuild.BranchNameForJob(jobId);

        if (gitOps is not null)
        {
            if (gitOps.WorktreeExists(repoPath, worktreeDir, out var wtExists, out _) && wtExists)
            {
                // Check if directory belongs to this job (recorded on earlier run) or another job
                var recordedWorktree = jobObj?["worktreePath"]?.ToString();
                var isRecordedWorktree = !string.IsNullOrWhiteSpace(recordedWorktree) &&
                    string.Equals(Path.GetFullPath(recordedWorktree), Path.GetFullPath(worktreeDir), StringComparison.OrdinalIgnoreCase);

                var existingJobFile = Path.Combine(worktreeDir, ".planwharf-job");
                var hasMatchingJobFile = File.Exists(existingJobFile) &&
                    string.Equals(File.ReadAllText(existingJobFile).Trim(), jobId, StringComparison.Ordinal);

                if (!isRecordedWorktree && !hasMatchingJobFile)
                {
                    if (File.Exists(existingJobFile) || !Directory.Exists(worktreeDir) || Directory.EnumerateFileSystemEntries(worktreeDir).Any())
                    {
                        return new AdmissibilityResult(false, ReasonWorktreeCollision, builderModelId, builderProviderId, null);
                    }
                }
            }

            if (gitOps.BranchExists(repoPath, branchName, out var bExists, out _) && bExists)
            {
                // If branch exists and is not recorded for this job
                var recordedBranch = jobObj?["worktreeBranch"]?.ToString() ?? jobObj?["headBranch"]?.ToString();
                if (!string.Equals(recordedBranch, branchName, StringComparison.OrdinalIgnoreCase))
                {
                    return new AdmissibilityResult(false, ReasonBranchCollision, builderModelId, builderProviderId, null);
                }
            }
        }

        return new AdmissibilityResult(true, null, builderModelId, builderProviderId, worktreeDir);
    }

    public static bool CheckReviewerLaneAvailable(
        string attemptsRoot,
        string projectId,
        string jobId,
        string reviewerLane,
        ISet<string> busyModelIds,
        JsonArray? team,
        out string? waitReason)
    {
        waitReason = null;
        var dir = PipelineBuild.JobDir(attemptsRoot, projectId, jobId);
        var routingFile = Path.Combine(dir, "routing.json");
        if (!File.Exists(routingFile))
        {
            var revRouting = Path.Combine(dir, "revisions", "1", "routing.json");
            if (File.Exists(revRouting)) routingFile = revRouting;
        }

        if (!File.Exists(routingFile))
        {
            waitReason = "routing-missing";
            return false;
        }

        var routingObj = PipelineBuild.LoadJson(routingFile);
        if (routingObj is null)
        {
            waitReason = "routing-unreadable";
            return false;
        }

        var chosenNode = routingObj["chosen"] as JsonObject;
        var builderModel = chosenNode?["builder"] as JsonObject;
        var builderProvider = builderModel?["providerId"]?.ToString();

        var revObj = chosenNode?[reviewerLane] as JsonObject;
        if (revObj is null)
        {
            waitReason = "reviewer-lane-missing";
            return false;
        }

        var revModelId = revObj["modelId"]?.ToString();
        var revProviderId = revObj["providerId"]?.ToString();

        if (string.IsNullOrWhiteSpace(revModelId))
        {
            waitReason = "reviewer-model-missing";
            return false;
        }

        // A reviewer from the builder's company is allowed: another company is a preference of the routing,
        // not a rule that makes a job wait.

        if (team is not null)
        {
            var tm = team.OfType<JsonObject>().FirstOrDefault(m =>
                string.Equals(m["id"]?.ToString() ?? m["modelId"]?.ToString(), revModelId, StringComparison.OrdinalIgnoreCase));

            if (tm is not null)
            {
                if (tm["quotaKnown"]?.GetValue<bool>() == false ||
                    tm["quotaUnread"]?.GetValue<bool>() == true ||
                    tm["unread"]?.GetValue<bool>() == true)
                {
                    waitReason = ReasonUnreadQuota;
                    return false;
                }

                if (tm["disabled"]?.GetValue<bool>() == true ||
                    tm["unavailable"]?.GetValue<bool>() == true ||
                    tm["expired"]?.GetValue<bool>() == true ||
                    tm["signedOut"]?.GetValue<bool>() == true)
                {
                    waitReason = ReasonQuota;
                    return false;
                }

                if (tm["remainingPercent"] is JsonValue remV && remV.TryGetValue<int>(out var remPct))
                {
                    var minPct = tm["minRemainingPercent"]?.GetValue<int>() ?? 0;
                    if (remPct <= minPct)
                    {
                        waitReason = ReasonQuota;
                        return false;
                    }
                }
            }
        }

        if (busyModelIds.Contains(revModelId))
        {
            waitReason = ReasonModelBusy;
            return false;
        }

        return true;
    }

    public sealed record ScheduleOutcome(
        int StartedCount,
        int WaitingCount,
        IReadOnlyList<string> StartedJobIds,
        IReadOnlyList<(string JobId, string Reason)> WaitingJobs,
        string? Error = null);

    sealed record ApprovedJobCandidate(
        string JobId,
        DateTimeOffset ApprovedAt,
        JsonObject JobObj);

    public static ScheduleOutcome ScheduleJobs(
        string attemptsRoot,
        string dataRoot,
        string repoPath,
        string projectId,
        PipelineRunSettings settings,
        JsonArray? team,
        IPipelineGitOps? gitOps = null,
        PipelineBuild.IGitOps? buildGitOps = null)
    {
        if (!TryAcquireScheduleLock(attemptsRoot, projectId, out var lockHandle, out var lockErr))
        {
            return new ScheduleOutcome(0, 0, [], [], lockErr ?? "Could not acquire schedule lock.");
        }

        using (lockHandle)
        {
            var projectJobsDir = Path.Combine(attemptsRoot, "pipeline", projectId, "jobs");
            if (!Directory.Exists(projectJobsDir))
            {
                var emptyRecord = new ScheduleRecord
                {
                    Summary = $"Pipeline schedule for {projectId}: 0 running, 0 waiting.",
                    ProjectId = projectId,
                    UpdatedAt = DateTime.UtcNow.ToString("O"),
                    Running = [],
                    Waiting = []
                };
                WriteScheduleAtomic(attemptsRoot, projectId, emptyRecord);
                return new ScheduleOutcome(0, 0, [], []);
            }

            var jobDirs = Directory.GetDirectories(projectJobsDir);
            var runningSlots = new List<ScheduleSlot>();
            var busyModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var approvedList = new List<ApprovedJobCandidate>();

            // Collect active running models across every project (shared account quota)
            var pipelineDir = Path.Combine(attemptsRoot, "pipeline");
            if (Directory.Exists(pipelineDir))
            {
                foreach (var projDir in Directory.GetDirectories(pipelineDir))
                {
                    var otherProjJobsDir = Path.Combine(projDir, "jobs");
                    if (!Directory.Exists(otherProjJobsDir)) continue;
                    foreach (var jd in Directory.GetDirectories(otherProjJobsDir))
                    {
                        var jf = Path.Combine(jd, "job.json");
                        var jObj = PipelineBuild.LoadJson(jf);
                        if (jObj is null) continue;
                        var st = jObj["status"]?.ToString() ?? "";
                        if (ActiveRunningStatuses.Contains(st, StringComparer.OrdinalIgnoreCase))
                        {
                            var otherProjId = Path.GetFileName(projDir);
                            var otherJobId = Path.GetFileName(jd);
                            var (otherModelId, _) = ResolveBuilderModel(attemptsRoot, otherProjId, otherJobId, team);
                            if (!string.IsNullOrWhiteSpace(otherModelId))
                            {
                                busyModelIds.Add(otherModelId);
                            }
                        }
                    }
                }
            }

            foreach (var jd in jobDirs)
            {
                var jf = Path.Combine(jd, "job.json");
                var jObj = PipelineBuild.LoadJson(jf);
                if (jObj is null) continue;

                var jId = Path.GetFileName(jd);
                var status = jObj["status"]?.ToString() ?? "";

                if (ActiveRunningStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
                {
                    var (mId, pId) = ResolveBuilderModel(attemptsRoot, projectId, jId, team);
                    if (!string.IsNullOrWhiteSpace(mId))
                    {
                        busyModelIds.Add(mId);
                    }

                    var worktreePath = PipelineBuild.WorktreeDir(dataRoot, projectId, jId);
                    runningSlots.Add(new ScheduleSlot(
                        JobId: jId,
                        ModelId: mId ?? "unknown",
                        ProviderId: pId ?? "unknown",
                        Status: status,
                        StartedAt: jObj["startedAt"]?.ToString() ?? DateTime.UtcNow.ToString("O"),
                        WorktreePath: worktreePath));
                }
                else if (string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase))
                {
                    // Read approvedAt from approval.json
                    var approvedAt = DateTimeOffset.MaxValue;
                    var revDir = Path.Combine(jd, "revisions", "1");
                    var approvalFile = Path.Combine(revDir, "approval.json");
                    if (!File.Exists(approvalFile)) approvalFile = Path.Combine(jd, "approval.json");

                    if (File.Exists(approvalFile))
                    {
                        var appObj = PipelineBuild.LoadJson(approvalFile);
                        if (appObj?["approvedAt"] is JsonValue appAtVal &&
                            DateTimeOffset.TryParse(appAtVal.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                        {
                            approvedAt = dt;
                        }
                    }

                    approvedList.Add(new ApprovedJobCandidate(jId, approvedAt, jObj));
                }
            }

            var sortedApproved = approvedList
                .OrderBy(c => c.ApprovedAt)
                .ThenBy(c => c.JobId, StringComparer.Ordinal)
                .ToList();

            var waitingSlots = new List<WaitingSlot>();
            var startedJobIds = new List<string>();
            var waitingJobDetails = new List<(string JobId, string Reason)>();

            if (settings.HasInvalidCap || (settings.MaxConcurrentJobs.HasValue && settings.MaxConcurrentJobs.Value <= 0))
            {
                // Invalid cap: starts nothing for that project until corrected
                foreach (var candidate in sortedApproved)
                {
                    waitingSlots.Add(new WaitingSlot(
                        JobId: candidate.JobId,
                        Reason: ReasonInvalidCap,
                        Summary: candidate.JobObj["summary"]?.ToString() ?? candidate.JobId,
                        ModelId: null,
                        CreatedAt: DateTime.UtcNow.ToString("O")));
                    waitingJobDetails.Add((candidate.JobId, ReasonInvalidCap));
                }

                var invalidRecord = new ScheduleRecord
                {
                    Summary = $"Pipeline schedule for {projectId}: {runningSlots.Count} running, {waitingSlots.Count} waiting.",
                    ProjectId = projectId,
                    UpdatedAt = DateTime.UtcNow.ToString("O"),
                    Running = runningSlots,
                    Waiting = waitingSlots
                };
                WriteScheduleAtomic(attemptsRoot, projectId, invalidRecord);
                return new ScheduleOutcome(0, waitingSlots.Count, [], waitingJobDetails);
            }

            var currentRunningCount = runningSlots.Count;

            foreach (var candidate in sortedApproved)
            {
                var jId = candidate.JobId;
                var adm = CheckJobAdmissibility(
                    attemptsRoot,
                    dataRoot,
                    repoPath,
                    projectId,
                    jId,
                    settings,
                    team,
                    busyModelIds,
                    currentRunningCount,
                    gitOps);

                if (adm.IsAdmissible)
                {
                    // Start job
                    if (buildGitOps is not null)
                    {
                        PipelineBuild.EnsureWorktree(
                            dataRoot,
                            repoPath,
                            attemptsRoot,
                            projectId,
                            jId,
                            buildGitOps,
                            out _,
                            out _);
                    }

                    PipelineBuild.StartRouting(attemptsRoot, projectId, jId, out _);

                    if (!string.IsNullOrWhiteSpace(adm.ModelId))
                    {
                        busyModelIds.Add(adm.ModelId);
                    }

                    currentRunningCount++;
                    startedJobIds.Add(jId);

                    runningSlots.Add(new ScheduleSlot(
                        JobId: jId,
                        ModelId: adm.ModelId ?? "unknown",
                        ProviderId: adm.ProviderId ?? "unknown",
                        Status: "routing",
                        StartedAt: DateTime.UtcNow.ToString("O"),
                        WorktreePath: adm.WorktreePath));
                }
                else
                {
                    // Job stays approved, record waiting reason
                    var reason = adm.Reason ?? "unknown";
                    waitingSlots.Add(new WaitingSlot(
                        JobId: jId,
                        Reason: reason,
                        Summary: candidate.JobObj["summary"]?.ToString() ?? jId,
                        ModelId: adm.ModelId,
                        CreatedAt: DateTime.UtcNow.ToString("O")));
                    waitingJobDetails.Add((jId, reason));
                }
            }

            var scheduleRecord = new ScheduleRecord
            {
                Summary = $"Pipeline schedule for {projectId}: {runningSlots.Count} running, {waitingSlots.Count} waiting.",
                ProjectId = projectId,
                UpdatedAt = DateTime.UtcNow.ToString("O"),
                Running = runningSlots,
                Waiting = waitingSlots
            };

            WriteScheduleAtomic(attemptsRoot, projectId, scheduleRecord);
            return new ScheduleOutcome(startedJobIds.Count, waitingSlots.Count, startedJobIds, waitingJobDetails);
        }
    }

    public sealed record MergeOutcome(
        bool Merged,
        string Status,
        string? Reason,
        string? MergeCommitSha,
        string? Error = null);

    public static MergeOutcome TryAutoMerge(
        string attemptsRoot,
        string projectId,
        string jobId,
        string? linkedGitHubRepository,
        PipelineRunSettings settings,
        IPipelineForgeOps forgeOps,
        IGateEvidenceVerifier? gateVerifier = null,
        bool isRetry = false)
    {
        var dir = PipelineBuild.JobDir(attemptsRoot, projectId, jobId);
        lock (PipelineBuild.LockFor(dir))
        {
            var jobFile = Path.Combine(dir, "job.json");
            var jobObj = PipelineBuild.LoadJson(jobFile);
            if (jobObj is null)
            {
                return new MergeOutcome(false, "unknown", "missing-job", null, "job.json missing or unreadable.");
            }

            var currentStatus = jobObj["status"]?.ToString() ?? "";

            // Check if already merged
            var existingMerge = LoadMerge(attemptsRoot, projectId, jobId);
            if (existingMerge is not null)
            {
                if (currentStatus != "merged")
                {
                    jobObj["status"] = "merged";
                    PipelineBuild.WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                return new MergeOutcome(true, "merged", null, existingMerge.Sha);
            }

            // ready-local check
            if (string.Equals(currentStatus, "ready-local", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(linkedGitHubRepository))
            {
                return new MergeOutcome(false, "ready-local", ReasonNoLinkedRepo, null);
            }

            // Status must be pull-request, or awaiting-owner/merging on retry
            if (!isRetry && currentStatus != "pull-request")
            {
                return new MergeOutcome(false, currentStatus, "invalid-status", null, $"Job status must be 'pull-request', found '{currentStatus}'.");
            }

            if (isRetry)
            {
                if (currentStatus == "awaiting-owner")
                {
                    var awaitReason = jobObj["awaitingOwnerReason"]?.ToString();
                    if (!string.Equals(awaitReason, ReasonMergeFailed, StringComparison.OrdinalIgnoreCase))
                    {
                        return new MergeOutcome(false, currentStatus, "invalid-await-reason", null, $"Retry merge is only permitted when awaitingOwnerReason is '{ReasonMergeFailed}', found '{awaitReason}'.");
                    }
                }
                else if (currentStatus is not ("pull-request" or "merging"))
                {
                    return new MergeOutcome(false, currentStatus, "invalid-status", null, $"Retry merge requires pull-request, awaiting-owner, or merging; found '{currentStatus}'.");
                }
            }

            // Owner merges by default: setting must be true
            if (!settings.MergeLowRisk && !isRetry)
            {
                return new MergeOutcome(false, "pull-request", "owner-merges-by-default", null);
            }

            // 1. Stored risk must be low. Medium and high never auto-merge.
            var risk = jobObj["risk"]?.ToString()?.Trim().ToLowerInvariant();
            if (risk != "low")
            {
                return new MergeOutcome(false, "pull-request", "risk-not-low", null);
            }

            // Read pull-request.json
            var prFile = Path.Combine(dir, "pull-request.json");
            var prObj = PipelineBuild.LoadJson(prFile);
            if (prObj is null)
            {
                return new MergeOutcome(false, currentStatus, "missing-pr-file", null, "pull-request.json missing or unreadable.");
            }

            var prNumber = prObj["number"]?.GetValue<int>() ?? 0;
            var recordedHeadSha = prObj["headSha"]?.ToString() ?? "";
            var recordedHeadBranch = prObj["headBranch"]?.ToString() ?? PipelineBuild.BranchNameForJob(jobId);

            if (prNumber <= 0 || string.IsNullOrWhiteSpace(recordedHeadSha))
            {
                return new MergeOutcome(false, currentStatus, "invalid-pr-record", null, "pull-request.json contains invalid prNumber or headSha.");
            }

            // 2. Job 19's post-sync evidence passed for this job id, approved manifest hash, and head SHA
            var manifestSha256 = jobObj["manifestSha256"]?.ToString() ?? "";
            gateVerifier ??= new DefaultGateEvidenceVerifier();
            if (!gateVerifier.HasPassingPostSyncGate(attemptsRoot, projectId, jobId, manifestSha256, recordedHeadSha))
            {
                return new MergeOutcome(false, "pull-request", "post-sync-gate-required", null);
            }

            // 3. Forge checks
            if (!forgeOps.GetPullRequest(linkedGitHubRepository, prNumber, out var forgePr, out var getPrErr) || forgePr is null)
            {
                jobObj["status"] = "awaiting-owner";
                jobObj["awaitingOwnerReason"] = ReasonMergeFailed;
                jobObj["mergeError"] = getPrErr ?? "Failed to get pull request from forge.";
                PipelineBuild.WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return new MergeOutcome(false, "awaiting-owner", ReasonMergeFailed, null, getPrErr);
            }

            // If forge shows PR already merged
            if (forgePr.Merged)
            {
                var commitSha = forgePr.MergeCommitSha ?? recordedHeadSha;
                var record = new MergeRecord
                {
                    Summary = $"Merged pull request #{prNumber}",
                    JobId = jobId,
                    PullRequestNumber = prNumber,
                    Sha = commitSha,
                    Method = "merge",
                    MergedAt = DateTime.UtcNow.ToString("O")
                };
                WriteMergeAtomic(attemptsRoot, projectId, jobId, record);

                jobObj["status"] = "merged";
                jobObj.Remove("awaitingOwnerReason");
                jobObj.Remove("mergeError");
                PipelineBuild.WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return new MergeOutcome(true, "merged", null, commitSha);
            }

            // Verify head SHA matches
            if (!string.Equals(forgePr.HeadSha, recordedHeadSha, StringComparison.OrdinalIgnoreCase))
            {
                return new MergeOutcome(false, "pull-request", ReasonWrongHead, null);
            }

            // Verify mergeable
            if (!forgePr.Mergeable)
            {
                return new MergeOutcome(false, "pull-request", ReasonNotMergeable, null);
            }

            // Transition to merging before calling forge
            jobObj["status"] = "merging";
            PipelineBuild.WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // Call forge merge API once, method merge
            var title = $"Merge pull request #{prNumber} from {recordedHeadBranch}";
            if (!forgeOps.MergePullRequest(linkedGitHubRepository, prNumber, recordedHeadSha, title, out var mergeSha, out var refusal, out var mergeErr))
            {
                // Refusal or branch protection failure: reason merge-failed, set awaiting-owner
                jobObj["status"] = "awaiting-owner";
                jobObj["awaitingOwnerReason"] = ReasonMergeFailed;
                jobObj["mergeError"] = refusal ?? mergeErr ?? "Merge failed.";
                PipelineBuild.WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return new MergeOutcome(false, "awaiting-owner", ReasonMergeFailed, null, refusal ?? mergeErr);
            }

            var finalCommitSha = mergeSha ?? recordedHeadSha;
            var mergeRecord = new MergeRecord
            {
                Summary = $"Merged pull request #{prNumber}",
                JobId = jobId,
                PullRequestNumber = prNumber,
                Sha = finalCommitSha,
                Method = "merge",
                MergedAt = DateTime.UtcNow.ToString("O")
            };
            WriteMergeAtomic(attemptsRoot, projectId, jobId, mergeRecord);

            jobObj["status"] = "merged";
            jobObj.Remove("awaitingOwnerReason");
            jobObj.Remove("mergeError");
            PipelineBuild.WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            return new MergeOutcome(true, "merged", null, finalCommitSha);
        }
    }

    public static MergeOutcome RetryMerge(
        string attemptsRoot,
        string projectId,
        string jobId,
        string? linkedGitHubRepository,
        IPipelineForgeOps forgeOps,
        IGateEvidenceVerifier? gateVerifier = null)
    {
        var settings = new PipelineRunSettings { MergeLowRisk = true };
        return TryAutoMerge(attemptsRoot, projectId, jobId, linkedGitHubRepository, settings, forgeOps, gateVerifier, isRetry: true);
    }

    public static void RecoverMergingStatus(
        string attemptsRoot,
        string projectId,
        string jobId,
        string? linkedGitHubRepository,
        IPipelineForgeOps forgeOps)
    {
        var dir = PipelineBuild.JobDir(attemptsRoot, projectId, jobId);
        lock (PipelineBuild.LockFor(dir))
        {
            var jobFile = Path.Combine(dir, "job.json");
            var jobObj = PipelineBuild.LoadJson(jobFile);
            if (jobObj is null) return;

            var status = jobObj["status"]?.ToString() ?? "";
            if (status != "merging") return;

            var prFile = Path.Combine(dir, "pull-request.json");
            var prObj = PipelineBuild.LoadJson(prFile);
            if (prObj is null || string.IsNullOrWhiteSpace(linkedGitHubRepository)) return;

            var prNumber = prObj["number"]?.GetValue<int>() ?? 0;
            var recordedHeadSha = prObj["headSha"]?.ToString() ?? "";

            if (prNumber > 0 && forgeOps.GetPullRequest(linkedGitHubRepository, prNumber, out var forgePr, out _) && forgePr is not null)
            {
                if (forgePr.Merged)
                {
                    var commitSha = forgePr.MergeCommitSha ?? recordedHeadSha;
                    var record = new MergeRecord
                    {
                        Summary = $"Merged pull request #{prNumber}",
                        JobId = jobId,
                        PullRequestNumber = prNumber,
                        Sha = commitSha,
                        Method = "merge",
                        MergedAt = DateTime.UtcNow.ToString("O")
                    };
                    WriteMergeAtomic(attemptsRoot, projectId, jobId, record);

                    jobObj["status"] = "merged";
                    PipelineBuild.WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                // If not merged in forge, leave merging / awaiting-owner as recorded and do not call forge
            }
        }
    }

    public sealed record CleanupOutcome(bool Cleaned, string? Reason, string? Error = null);

    public static IReadOnlyList<string> ListUnknownWorktreeFolders(string dataRoot, string projectId, IEnumerable<string> knownJobIds)
    {
        var knownSet = new HashSet<string>(knownJobIds, StringComparer.OrdinalIgnoreCase);
        var jobsDir = Path.GetFullPath(Path.Combine(dataRoot, "worktrees", projectId, "jobs"));
        if (!Directory.Exists(jobsDir)) return [];

        var unknown = new List<string>();
        foreach (var dir in Directory.GetDirectories(jobsDir))
        {
            var folderName = Path.GetFileName(dir);
            if (!knownSet.Contains(folderName))
            {
                unknown.Add(dir);
            }
        }
        return unknown;
    }

    public static CleanupOutcome PerformCleanup(
        string attemptsRoot,
        string dataRoot,
        string repoPath,
        string projectId,
        string jobId,
        IPipelineGitOps gitOps,
        string defaultBranch = "main")
    {
        if (!PipelineBuild.SafeJobId(jobId) || !AcceptanceGate.SafeId(projectId))
        {
            return new CleanupOutcome(false, ReasonCleanupFailed, "Invalid projectId or jobId format.");
        }

        var dir = PipelineBuild.JobDir(attemptsRoot, projectId, jobId);
        lock (PipelineBuild.LockFor(dir))
        {
            var jobFile = Path.Combine(dir, "job.json");
            var jobObj = PipelineBuild.LoadJson(jobFile);
            if (jobObj is null)
            {
                return new CleanupOutcome(false, "missing-job", "job.json not found.");
            }

            var status = jobObj["status"]?.ToString() ?? "";
            if (status != "merged")
            {
                return new CleanupOutcome(false, "not-merged", $"Cleanup after merge requires 'merged' status, found '{status}'.");
            }

            var recordedWorktree = PipelineBuild.WorktreeDir(dataRoot, projectId, jobId);
            var expectedJobsParent = Path.GetFullPath(Path.Combine(dataRoot, "worktrees", projectId, "jobs"));

            // Safety: Chat worktrees, project folder, and main are never cleanup target
            if (!recordedWorktree.StartsWith(expectedJobsParent, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(recordedWorktree, repoPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(recordedWorktree, attemptsRoot, StringComparison.OrdinalIgnoreCase))
            {
                return new CleanupOutcome(false, ReasonCleanupFailed, "Path does not match recorded pipeline worktree path.");
            }

            var jobWorktreePath = jobObj["worktreePath"]?.ToString();
            if (!string.IsNullOrWhiteSpace(jobWorktreePath) &&
                !string.Equals(Path.GetFullPath(jobWorktreePath), Path.GetFullPath(recordedWorktree), StringComparison.OrdinalIgnoreCase))
            {
                return new CleanupOutcome(false, ReasonCleanupFailed, "Stored worktree path does not match expected pipeline path.");
            }

            // Check if worktree directory exists
            if (Directory.Exists(recordedWorktree) ||
                (gitOps.WorktreeExists(repoPath, recordedWorktree, out var wtExists, out _) && wtExists))
            {
                if (gitOps.IsWorktreeDirty(recordedWorktree, out var isDirty, out var dirtyErr))
                {
                    if (isDirty)
                    {
                        jobObj["status"] = "awaiting-owner";
                        jobObj["awaitingOwnerReason"] = ReasonDirtyWorktree;
                        PipelineBuild.WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        return new CleanupOutcome(false, ReasonDirtyWorktree, "Worktree is dirty.");
                    }
                }

                if (!gitOps.RemoveWorktree(repoPath, recordedWorktree, out var rmErr))
                {
                    return new CleanupOutcome(false, ReasonCleanupFailed, rmErr);
                }

                try
                {
                    if (Directory.Exists(recordedWorktree))
                    {
                        Directory.Delete(recordedWorktree, true);
                    }
                }
                catch (Exception)
                {
                    // Suppress local file deletion errors if git worktree removed it
                }
            }

            // Local branch deletion
            var branchName = PipelineBuild.BranchNameForJob(jobId);
            if (string.Equals(branchName, defaultBranch, StringComparison.OrdinalIgnoreCase) ||
                !branchName.StartsWith("pipeline/", StringComparison.Ordinal))
            {
                return new CleanupOutcome(false, ReasonCleanupFailed, "Cannot delete default or non-pipeline branch.");
            }

            if (gitOps.BranchExists(repoPath, branchName, out var bExists, out _) && bExists)
            {
                // Must be contained in the fetched default branch
                if (!gitOps.IsBranchMergedIntoDefault(repoPath, branchName, defaultBranch, out var isMerged, out var mergeCheckErr) || !isMerged)
                {
                    return new CleanupOutcome(false, ReasonCleanupFailed, mergeCheckErr ?? "Branch is not merged into default branch.");
                }

                if (!gitOps.DeleteLocalBranch(repoPath, branchName, force: false, out var delErr))
                {
                    return new CleanupOutcome(false, ReasonCleanupFailed, delErr);
                }
            }

            return new CleanupOutcome(true, null);
        }
    }

    public static CleanupOutcome DropJob(
        string attemptsRoot,
        string dataRoot,
        string repoPath,
        string projectId,
        string jobId,
        IPipelineGitOps gitOps,
        bool confirmed = true,
        string defaultBranch = "main")
    {
        if (!confirmed)
        {
            return new CleanupOutcome(false, "unconfirmed", "Drop requires owner confirmation.");
        }

        if (!PipelineBuild.SafeJobId(jobId) || !AcceptanceGate.SafeId(projectId))
        {
            return new CleanupOutcome(false, ReasonCleanupFailed, "Invalid projectId or jobId format.");
        }

        var dir = PipelineBuild.JobDir(attemptsRoot, projectId, jobId);
        lock (PipelineBuild.LockFor(dir))
        {
            var jobFile = Path.Combine(dir, "job.json");
            var jobObj = PipelineBuild.LoadJson(jobFile);
            if (jobObj is null)
            {
                return new CleanupOutcome(false, "missing-job", "job.json not found.");
            }

            var status = jobObj["status"]?.ToString() ?? "";
            if (string.Equals(status, "merged", StringComparison.OrdinalIgnoreCase))
            {
                return new CleanupOutcome(false, "already-merged", "Cannot drop a merged job.");
            }

            // Set dropped
            jobObj["status"] = "dropped";
            jobObj.Remove("awaitingOwnerReason");
            jobObj.Remove("mergeError");
            PipelineBuild.WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var recordedWorktree = PipelineBuild.WorktreeDir(dataRoot, projectId, jobId);
            var expectedJobsParent = Path.GetFullPath(Path.Combine(dataRoot, "worktrees", projectId, "jobs"));

            // Safety: Chat worktrees, project folder, and main are never cleanup target
            if (!recordedWorktree.StartsWith(expectedJobsParent, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(recordedWorktree, repoPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(recordedWorktree, attemptsRoot, StringComparison.OrdinalIgnoreCase))
            {
                return new CleanupOutcome(false, ReasonCleanupFailed, "Path does not match recorded pipeline worktree path.");
            }

            var jobWorktreePath = jobObj["worktreePath"]?.ToString();
            if (!string.IsNullOrWhiteSpace(jobWorktreePath) &&
                !string.Equals(Path.GetFullPath(jobWorktreePath), Path.GetFullPath(recordedWorktree), StringComparison.OrdinalIgnoreCase))
            {
                return new CleanupOutcome(false, ReasonCleanupFailed, "Stored worktree path does not match expected pipeline path.");
            }

            // Remove recorded worktree even if dirty
            if (Directory.Exists(recordedWorktree) ||
                (gitOps.WorktreeExists(repoPath, recordedWorktree, out var wtExists, out _) && wtExists))
            {
                gitOps.RemoveWorktree(repoPath, recordedWorktree, out _);
                try
                {
                    if (Directory.Exists(recordedWorktree))
                    {
                        Directory.Delete(recordedWorktree, true);
                    }
                }
                catch (Exception)
                {
                    // Ignore directory deletion errors
                }
            }

            // Delete local branch: only when name is exactly pipeline/<J> and not default branch
            var branchName = PipelineBuild.BranchNameForJob(jobId);
            if (!string.Equals(branchName, defaultBranch, StringComparison.OrdinalIgnoreCase) &&
                branchName.StartsWith("pipeline/", StringComparison.Ordinal))
            {
                if (gitOps.BranchExists(repoPath, branchName, out var bExists, out _) && bExists)
                {
                    gitOps.DeleteLocalBranch(repoPath, branchName, force: true, out _);
                }
            }

            return new CleanupOutcome(true, null);
        }
    }
}
