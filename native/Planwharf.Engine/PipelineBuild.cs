// Job 33 implementation of native/porting/jobs/21-pipeline-build.md.
// Builds pipeline stages after an approved spec: routing, builder and reviewer pairs,
// acceptance gate integration, worktree and branch management, and pull request generation.
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class PipelineBuild
{
    public const int Schema = 1;
    public const int DefaultMaxReviewRounds = 2;
    public const int DefaultMaxGateReturns = 2;
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    static readonly ConcurrentDictionary<string, object> BuildLocks = new(StringComparer.Ordinal);
    static readonly Regex JobIdRegex = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$", RegexOptions.Compiled);

    public static readonly string[] AllowedJobStatuses =
    [
        "approved",
        "routing",
        "building",
        "gating",
        "reviewing",
        "awaiting-owner",
        "syncing",
        "opening-pr",
        "pull-request",
        "ready-local",
        "stopped"
    ];

    public static readonly string[] DisallowedPreviousStatuses =
    [
        "queued",
        "intake",
        "waiting",
        "specifying",
        "awaiting-approval"
    ];

    public static bool CanTransitionTo(string? currentStatus, string? nextStatus)
    {
        if (string.IsNullOrWhiteSpace(nextStatus)) return false;
        if (DisallowedPreviousStatuses.Contains(nextStatus, StringComparer.OrdinalIgnoreCase)) return false;
        return AllowedJobStatuses.Contains(nextStatus, StringComparer.OrdinalIgnoreCase);
    }

    public static bool SafeJobId(string? jobId) =>
        !string.IsNullOrWhiteSpace(jobId) && JobIdRegex.IsMatch(jobId);

    public static string JobDir(string attemptsRoot, string projectId, string jobId) =>
        Path.GetFullPath(Path.Combine(attemptsRoot, "pipeline", projectId, "jobs", jobId));

    public static object LockFor(string path) =>
        BuildLocks.GetOrAdd(Path.GetFullPath(path), static _ => new object());

    public static void WriteAtomic(string destination, string content) =>
        WriteAtomic(destination, Utf8.GetBytes(content));

    public static void WriteAtomic(string destination, byte[] bytes)
    {
        var full = Path.GetFullPath(destination);
        var dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".tmp-{Guid.NewGuid():N}");
        File.WriteAllBytes(temp, bytes);
        AtomicRename.RenameWithRetry(temp, full);
    }

    public static string Sha256(byte[] bytes) => AcceptanceGate.Sha256(bytes);

    public static JsonObject? LoadJson(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static bool VerifyStartGate(
        string attemptsRoot,
        string projectId,
        string jobId,
        out JsonObject? jobObj,
        out string failureReason)
    {
        jobObj = null;
        failureReason = "";

        if (!AcceptanceGate.SafeId(projectId) || !SafeJobId(jobId))
        {
            failureReason = "Invalid projectId or jobId format.";
            return false;
        }

        var dir = JobDir(attemptsRoot, projectId, jobId);
        var jobFile = Path.Combine(dir, "job.json");
        jobObj = LoadJson(jobFile);
        if (jobObj is null)
        {
            failureReason = "job.json missing or unreadable.";
            return false;
        }

        var status = jobObj["status"]?.ToString();
        if (status != "approved")
        {
            failureReason = $"job.json status must be 'approved', but found '{status}'.";
            return false;
        }

        var snapshotId = jobObj["snapshotId"]?.ToString();
        var manifestSha256 = jobObj["manifestSha256"]?.ToString();
        var manifestPath = jobObj["manifestPath"]?.ToString();
        if (string.IsNullOrWhiteSpace(snapshotId) ||
            string.IsNullOrWhiteSpace(manifestSha256) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            failureReason = "Missing snapshot pointer fields in job.json.";
            return false;
        }

        var fullManifestPath = Path.GetFullPath(Path.Combine(dir, manifestPath));
        if (!fullManifestPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "Manifest path must be under the job attempts directory.";
            return false;
        }

        if (!File.Exists(fullManifestPath))
        {
            failureReason = $"Frozen manifest file missing at '{manifestPath}'.";
            return false;
        }

        byte[] manifestBytes;
        try
        {
            manifestBytes = File.ReadAllBytes(fullManifestPath);
        }
        catch (Exception ex)
        {
            failureReason = $"Failed reading frozen manifest: {ex.Message}";
            return false;
        }

        var actualHash = Sha256(manifestBytes);
        if (!string.Equals(actualHash, manifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = $"Manifest SHA-256 mismatch: expected '{manifestSha256}', actual '{actualHash}'.";
            return false;
        }

        var revDir = Path.GetDirectoryName(fullManifestPath)!;
        var approvalFile = Path.Combine(revDir, "approval.json");
        if (!File.Exists(approvalFile))
        {
            approvalFile = Path.Combine(dir, "approval.json");
        }

        var approvalObj = LoadJson(approvalFile);
        if (approvalObj is null)
        {
            failureReason = "approval.json missing or unreadable.";
            return false;
        }

        var approvedBy = approvalObj["approvedBy"]?.ToString();
        if (approvedBy != "owner")
        {
            failureReason = $"approval.json must say approvedBy 'owner', but found '{approvedBy}'.";
            return false;
        }

        var approvalHash = approvalObj["manifestSha256"]?.ToString();
        if (!string.Equals(approvalHash, manifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = $"approval.json manifestSha256 mismatch: expected '{manifestSha256}', found '{approvalHash}'.";
            return false;
        }

        return true;
    }

    public static bool StartRouting(
        string attemptsRoot,
        string projectId,
        string jobId,
        out string failureReason)
    {
        var dir = JobDir(attemptsRoot, projectId, jobId);
        lock (LockFor(dir))
        {
            if (!VerifyStartGate(attemptsRoot, projectId, jobId, out var jobObj, out failureReason))
            {
                return false;
            }

            jobObj!["status"] = "routing";
            var jobFile = Path.Combine(dir, "job.json");
            WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
    }

    public sealed record RoutingCandidate(string ProviderId, string ModelId, int Weight);

    public sealed record RoutingRow(string Risk, string Lane, IReadOnlyList<RoutingCandidate> Candidates);

    public sealed class RoutingTable
    {
        public int MaxReviewRounds { get; set; } = DefaultMaxReviewRounds;
        public int MaxGateReturns { get; set; } = DefaultMaxGateReturns;
        public List<RoutingRow> Rows { get; set; } = [];
    }

    public sealed record ChosenLaneModel(string ProviderId, string ModelId, int Weight);

    public sealed record DroppedCandidate(string ProviderId, string ModelId, string Reason);

    public sealed class RoutingSnapshot
    {
        public required string Summary { get; init; }
        public required string JobId { get; init; }
        public required string Risk { get; init; }
        public required bool TouchesInterface { get; init; }
        public required bool TouchesSecurity { get; init; }
        public required int MaxReviewRounds { get; init; }
        public required int MaxGateReturns { get; init; }
        public required IReadOnlyList<RoutingRow> RowsUsed { get; init; }
        public required IReadOnlyDictionary<string, ChosenLaneModel> Chosen { get; init; }
        public required IReadOnlyDictionary<string, IReadOnlyList<DroppedCandidate>> Dropped { get; init; }
        public required IReadOnlyList<string> SkippedLanes { get; init; }
    }

    public static readonly string[] ValidRisks = ["low", "medium", "high"];
    public static readonly string[] AllLanes = ["builder", "reviewer-code", "reviewer-interface", "reviewer-security"];

    public static bool ValidateRiskAndParts(
        JsonObject jobObj,
        out string risk,
        out bool touchesInterface,
        out bool touchesSecurity,
        out string? validationError)
    {
        risk = "";
        touchesInterface = false;
        touchesSecurity = false;
        validationError = null;

        var riskVal = jobObj["risk"]?.ToString()?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(riskVal) || !ValidRisks.Contains(riskVal))
        {
            validationError = "invalid risk";
            return false;
        }
        risk = riskVal;

        if (jobObj["touchesInterface"] is not JsonValue tiNode || tiNode.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            validationError = "invalid risk"; // invalid or non-boolean part flag stops with reason invalid risk
            return false;
        }
        touchesInterface = tiNode.GetValue<bool>();

        if (jobObj["touchesSecurity"] is not JsonValue tsNode || tsNode.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
        {
            validationError = "invalid risk";
            return false;
        }
        touchesSecurity = tsNode.GetValue<bool>();

        return true;
    }

    public static List<string> RequiredLanes(bool touchesInterface, bool touchesSecurity)
    {
        var list = new List<string> { "builder", "reviewer-code" };
        if (touchesInterface) list.Add("reviewer-interface");
        if (touchesSecurity) list.Add("reviewer-security");
        return list;
    }

    public static RoutingTable BuildDefaultRoutingTable(JsonArray team)
    {
        var table = new RoutingTable();
        foreach (var risk in ValidRisks)
        {
            var builderCandidates = new List<RoutingCandidate>();
            foreach (var node in team)
            {
                if (node is not JsonObject m) continue;
                var modelWithRoles = ModelRoles.WithModelRoles(m);
                var isWorker = modelWithRoles["roles"]?["worker"]?.GetValue<bool>() ?? false;
                if (!isWorker) continue;

                var id = m["id"]?.ToString() ?? "";
                var prov = m["providerId"]?.ToString() ?? m["provider"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(prov)) continue;

                var weight = GetModelWeight(m);
                builderCandidates.Add(new RoutingCandidate(prov, id, weight));
            }
            table.Rows.Add(new RoutingRow(risk, "builder", builderCandidates));

            foreach (var rLane in new[] { "reviewer-code", "reviewer-interface", "reviewer-security" })
            {
                var reviewerCandidates = new List<RoutingCandidate>();
                foreach (var node in team)
                {
                    if (node is not JsonObject m) continue;
                    var modelWithRoles = ModelRoles.WithModelRoles(m);
                    var isVerifier = modelWithRoles["roles"]?["verifier"]?.GetValue<bool>() ?? false;
                    if (!isVerifier) continue;

                    var id = m["id"]?.ToString() ?? "";
                    var prov = m["providerId"]?.ToString() ?? m["provider"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(prov)) continue;

                    var weight = GetModelWeight(m);
                    reviewerCandidates.Add(new RoutingCandidate(prov, id, weight));
                }
                table.Rows.Add(new RoutingRow(risk, rLane, reviewerCandidates));
            }
        }
        return table;
    }

    static int GetModelWeight(JsonObject model)
    {
        if (model["remainingPercent"] is JsonValue remNode && remNode.TryGetValue<int>(out var rem) && rem > 0)
        {
            return rem;
        }
        return 1;
    }

    public static bool RouteJob(
        string attemptsRoot,
        string projectId,
        string jobId,
        JsonArray team,
        RoutingTable? savedTable,
        out RoutingSnapshot? snapshot,
        out string? awaitReason,
        out string? awaitLane)
    {
        snapshot = null;
        awaitReason = null;
        awaitLane = null;

        var dir = JobDir(attemptsRoot, projectId, jobId);
        lock (LockFor(dir))
        {
            var jobFile = Path.Combine(dir, "job.json");
            var jobObj = LoadJson(jobFile);
            if (jobObj is null)
            {
                awaitReason = "Job not found.";
                return false;
            }

            var currentStatus = jobObj["status"]?.ToString();
            if (currentStatus != "routing" && currentStatus != "awaiting-owner")
            {
                // Can only route from routing or awaiting-owner (before builder starts)
                awaitReason = $"Cannot route job in status '{currentStatus}'.";
                return false;
            }

            // Check if routing.json already exists and first builder started
            var routingFile = Path.Combine(dir, "routing.json");
            if (File.Exists(routingFile) && jobObj["firstBuilderStarted"]?.GetValue<bool>() == true)
            {
                awaitReason = "Routing snapshot is frozen after first builder starts.";
                return false;
            }

            if (!ValidateRiskAndParts(jobObj, out var risk, out var touchesInterface, out var touchesSecurity, out var partError))
            {
                jobObj["status"] = "awaiting-owner";
                jobObj["awaitingOwnerReason"] = partError;
                jobObj.Remove("awaitingOwnerLane");
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                awaitReason = partError;
                return false;
            }

            var table = savedTable;
            var isSaved = savedTable is not null && savedTable.Rows.Count > 0;
            if (!isSaved)
            {
                table = BuildDefaultRoutingTable(team);
            }

            var maxReviewRounds = table!.MaxReviewRounds > 0 ? table.MaxReviewRounds : DefaultMaxReviewRounds;
            var maxGateReturns = table.MaxGateReturns > 0 ? table.MaxGateReturns : DefaultMaxGateReturns;

            var reqLanes = RequiredLanes(touchesInterface, touchesSecurity);
            var skippedLanes = AllLanes.Where(l => !reqLanes.Contains(l)).ToList();

            var rowsUsed = new List<RoutingRow>();
            var chosen = new Dictionary<string, ChosenLaneModel>(StringComparer.Ordinal);
            var dropped = new Dictionary<string, IReadOnlyList<DroppedCandidate>>(StringComparer.Ordinal);

            // Filter builder first
            RoutingRow? builderRow = table.Rows.FirstOrDefault(r =>
                string.Equals(r.Risk, risk, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Lane, "builder", StringComparison.OrdinalIgnoreCase));

            if (builderRow is null)
            {
                jobObj["status"] = "awaiting-owner";
                jobObj["awaitingOwnerReason"] = "no-row";
                jobObj["awaitingOwnerLane"] = "builder";
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                awaitReason = "no-row";
                awaitLane = "builder";
                return false;
            }
            rowsUsed.Add(builderRow);

            var builderChosen = SelectCandidate(builderRow, "builder", team, builderProviderId: null, out var builderDropped);
            dropped["builder"] = builderDropped;

            if (builderChosen is null)
            {
                jobObj["status"] = "awaiting-owner";
                jobObj["awaitingOwnerReason"] = "no eligible model";
                jobObj["awaitingOwnerLane"] = "builder";
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                awaitReason = "no eligible model";
                awaitLane = "builder";
                return false;
            }
            chosen["builder"] = builderChosen;

            // Now filter reviewers
            foreach (var lane in reqLanes.Where(l => l != "builder"))
            {
                RoutingRow? rRow = table.Rows.FirstOrDefault(r =>
                    string.Equals(r.Risk, risk, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Lane, lane, StringComparison.OrdinalIgnoreCase));

                if (rRow is null)
                {
                    jobObj["status"] = "awaiting-owner";
                    jobObj["awaitingOwnerReason"] = "no-row";
                    jobObj["awaitingOwnerLane"] = lane;
                    WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    awaitReason = "no-row";
                    awaitLane = lane;
                    return false;
                }
                rowsUsed.Add(rRow);

                var revChosen = SelectCandidate(rRow, lane, team, builderProviderId: builderChosen.ProviderId, out var revDropped);
                dropped[lane] = revDropped;

                if (revChosen is null)
                {
                    jobObj["status"] = "awaiting-owner";
                    jobObj["awaitingOwnerReason"] = "no eligible model";
                    jobObj["awaitingOwnerLane"] = lane;
                    WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    awaitReason = "no eligible model";
                    awaitLane = lane;
                    return false;
                }
                chosen[lane] = revChosen;
            }

            snapshot = new RoutingSnapshot
            {
                Summary = "Pipeline routing snapshot",
                JobId = jobId,
                Risk = risk,
                TouchesInterface = touchesInterface,
                TouchesSecurity = touchesSecurity,
                MaxReviewRounds = maxReviewRounds,
                MaxGateReturns = maxGateReturns,
                RowsUsed = rowsUsed,
                Chosen = chosen,
                Dropped = dropped,
                SkippedLanes = skippedLanes
            };

            // Write routing.json with summary first
            var rObj = new JsonObject
            {
                ["summary"] = snapshot.Summary,
                ["jobId"] = snapshot.JobId,
                ["risk"] = snapshot.Risk,
                ["touchesInterface"] = snapshot.TouchesInterface,
                ["touchesSecurity"] = snapshot.TouchesSecurity,
                ["maxReviewRounds"] = snapshot.MaxReviewRounds,
                ["maxGateReturns"] = snapshot.MaxGateReturns
            };

            var chosenNode = new JsonObject();
            foreach (var (k, v) in snapshot.Chosen)
            {
                chosenNode[k] = new JsonObject
                {
                    ["providerId"] = v.ProviderId,
                    ["modelId"] = v.ModelId,
                    ["weight"] = v.Weight
                };
            }
            rObj["chosen"] = chosenNode;

            var skippedArr = new JsonArray();
            foreach (var s in snapshot.SkippedLanes) skippedArr.Add((JsonNode)JsonValue.Create(s)!);
            rObj["skippedLanes"] = skippedArr;

            var droppedNode = new JsonObject();
            foreach (var (k, list) in snapshot.Dropped)
            {
                var arr = new JsonArray();
                foreach (var d in list)
                {
                    arr.Add((JsonNode)new JsonObject
                    {
                        ["providerId"] = d.ProviderId,
                        ["modelId"] = d.ModelId,
                        ["reason"] = d.Reason
                    });
                }
                droppedNode[k] = arr;
            }
            rObj["dropped"] = droppedNode;

            var rowsArr = new JsonArray();
            foreach (var row in snapshot.RowsUsed)
            {
                var candArr = new JsonArray();
                foreach (var c in row.Candidates)
                {
                    candArr.Add((JsonNode)new JsonObject
                    {
                        ["providerId"] = c.ProviderId,
                        ["modelId"] = c.ModelId,
                        ["weight"] = c.Weight
                    });
                }
                rowsArr.Add((JsonNode)new JsonObject
                {
                    ["risk"] = row.Risk,
                    ["lane"] = row.Lane,
                    ["candidates"] = candArr
                });
            }
            rObj["rowsUsed"] = rowsArr;

            WriteAtomic(routingFile, rObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // Update job.json
            jobObj.Remove("awaitingOwnerReason");
            jobObj.Remove("awaitingOwnerLane");
            WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            return true;
        }
    }

    static ChosenLaneModel? SelectCandidate(
        RoutingRow row,
        string lane,
        JsonArray team,
        string? builderProviderId,
        out List<DroppedCandidate> dropped)
    {
        dropped = [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var eligible = new List<RoutingCandidate>();

        foreach (var c in row.Candidates)
        {
            if (string.IsNullOrWhiteSpace(c.ProviderId) || string.IsNullOrWhiteSpace(c.ModelId))
            {
                dropped.Add(new DroppedCandidate(c.ProviderId ?? "", c.ModelId ?? "", "empty provider or model id"));
                continue;
            }

            if (c.Weight <= 0)
            {
                dropped.Add(new DroppedCandidate(c.ProviderId, c.ModelId, "weight must be a positive integer"));
                continue;
            }

            var key = $"{c.ProviderId}:{c.ModelId}";
            if (!seen.Add(key))
            {
                dropped.Add(new DroppedCandidate(c.ProviderId, c.ModelId, "duplicate candidate in row"));
                continue;
            }

            var teamModel = team.OfType<JsonObject>().FirstOrDefault(m =>
                string.Equals(m["id"]?.ToString(), c.ModelId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(m["providerId"]?.ToString() ?? m["provider"]?.ToString(), c.ProviderId, StringComparison.OrdinalIgnoreCase));

            if (teamModel is null)
            {
                dropped.Add(new DroppedCandidate(c.ProviderId, c.ModelId, "not on project team"));
                continue;
            }

            var modelWithRoles = ModelRoles.WithModelRoles(teamModel);
            var expectedRole = lane == "builder" ? "worker" : "verifier";
            var permitted = modelWithRoles["roles"]?[expectedRole]?.GetValue<bool>() ?? false;
            if (!permitted)
            {
                dropped.Add(new DroppedCandidate(c.ProviderId, c.ModelId, $"role does not permit {expectedRole}"));
                continue;
            }

            var statusStr = teamModel["status"]?.ToString()?.ToLowerInvariant();
            if (teamModel["disabled"]?.GetValue<bool>() == true ||
                teamModel["unavailable"]?.GetValue<bool>() == true ||
                teamModel["expired"]?.GetValue<bool>() == true ||
                teamModel["signedOut"]?.GetValue<bool>() == true ||
                statusStr is "expired" or "not signed in" or "signed-out" or "disabled" or "unavailable")
            {
                dropped.Add(new DroppedCandidate(c.ProviderId, c.ModelId, "disabled, unavailable, expired, or signed out"));
                continue;
            }

            // Quota policy check: if remaining quota is known and blocked
            if (teamModel["remainingPercent"] is JsonValue remVal && remVal.TryGetValue<int>(out var remPct))
            {
                var minPercent = teamModel["minRemainingPercent"]?.GetValue<int>() ?? 0;
                if (remPct < minPercent)
                {
                    dropped.Add(new DroppedCandidate(c.ProviderId, c.ModelId, "quota blocked by policy"));
                    continue;
                }
            }
            else if (teamModel["quotaKnown"]?.GetValue<bool>() != true && teamModel["quotaUnread"]?.GetValue<bool>() == true)
            {
                dropped.Add(new DroppedCandidate(c.ProviderId, c.ModelId, "unread quota cannot count as remaining"));
                continue;
            }

            eligible.Add(c);
        }

        if (eligible.Count == 0) return null;

        // A reviewer from another company than the builder is preferred, never required: with none free, any
        // available model reviews. Then the highest weight; a tie keeps the earlier candidate in the row.
        bool SameCompany(string providerId) => lane != "builder" && builderProviderId is not null && string.Equals(providerId, builderProviderId, StringComparison.OrdinalIgnoreCase);
        var best = eligible.OrderBy(c => SameCompany(c.ProviderId) ? 1 : 0).ThenByDescending(c => c.Weight).First();
        return new ChosenLaneModel(best.ProviderId, best.ModelId, best.Weight);
    }

    public sealed record SpecTask(string Id, string Title, IReadOnlyList<string> Dependencies);

    public sealed class ParsedSpec
    {
        public string Summary { get; set; } = "";
        public string WhatToBuild { get; set; } = "";
        public string WhatNotToBuild { get; set; } = "";
        public string SharedInterfaces { get; set; } = "";
        public List<SpecTask> Tasks { get; init; } = [];
        public List<string> ChecksQuoted { get; init; } = [];
    }

    public static bool TryParseSpec(string specMarkdown, out ParsedSpec spec, out string? error)
    {
        spec = new ParsedSpec();
        error = null;

        if (string.IsNullOrWhiteSpace(specMarkdown))
        {
            error = "bad-tree";
            return false;
        }

        var lines = specMarkdown.Replace("\r\n", "\n").Split('\n');
        string currentSection = "";
        var sbInterfaces = new StringBuilder();
        var sbWhat = new StringBuilder();

        var taskRegex = new Regex(@"^\s*-\s*\[([^\]]+)\]\s*(.*?)(?:\s*\((?:depends\s+on|deps?):\s*([^\)]+)\))?\s*$", RegexOptions.IgnoreCase);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# Spec:", StringComparison.OrdinalIgnoreCase))
            {
                spec = new ParsedSpec
                {
                    Summary = trimmed["# Spec:".Length..].Trim()
                };
                continue;
            }

            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                currentSection = trimmed[3..].Trim().ToLowerInvariant();
                continue;
            }

            if (currentSection == "shared interfaces")
            {
                sbInterfaces.AppendLine(line);
            }
            else if (currentSection == "what to build")
            {
                sbWhat.AppendLine(line);
            }
            else if (currentSection == "task tree")
            {
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                var match = taskRegex.Match(trimmed);
                if (match.Success)
                {
                    var id = match.Groups[1].Value.Trim();
                    var title = match.Groups[2].Value.Trim();
                    var depsStr = match.Groups[3].Value;
                    var deps = new List<string>();
                    if (!string.IsNullOrWhiteSpace(depsStr))
                    {
                        foreach (var d in depsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!string.IsNullOrWhiteSpace(d)) deps.Add(d);
                        }
                    }
                    spec.Tasks.Add(new SpecTask(id, title, deps));
                }
            }
            else if (currentSection == "acceptance tests")
            {
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    spec.ChecksQuoted.Add(line);
                }
            }
        }

        // Validate task tree
        if (spec.Tasks.Count == 0)
        {
            error = "bad-tree";
            return false;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in spec.Tasks)
        {
            if (string.IsNullOrWhiteSpace(t.Id) || !ids.Add(t.Id))
            {
                error = "bad-tree"; // duplicate or empty id
                return false;
            }
        }

        foreach (var t in spec.Tasks)
        {
            foreach (var d in t.Dependencies)
            {
                if (!ids.Contains(d))
                {
                    error = "bad-tree"; // dependency not in tree
                    return false;
                }
            }
        }

        // Cycle check (topological sort / DFS)
        if (HasCycle(spec.Tasks))
        {
            error = "bad-tree";
            return false;
        }

        spec.SharedInterfaces = sbInterfaces.ToString().Trim();
        spec.WhatToBuild = sbWhat.ToString().Trim();

        return true;
    }

    static bool HasCycle(IReadOnlyList<SpecTask> tasks)
    {
        var graph = tasks.ToDictionary(t => t.Id, t => t.Dependencies, StringComparer.OrdinalIgnoreCase);
        var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=unvisited, 1=visiting, 2=visited

        bool Dfs(string node)
        {
            visited[node] = 1;
            if (graph.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    if (!visited.TryGetValue(dep, out var state) || state == 0)
                    {
                        if (Dfs(dep)) return true;
                    }
                    else if (state == 1)
                    {
                        return true; // cycle detected
                    }
                }
            }
            visited[node] = 2;
            return false;
        }

        foreach (var t in tasks)
        {
            if (!visited.TryGetValue(t.Id, out var state) || state == 0)
            {
                if (Dfs(t.Id)) return true;
            }
        }

        return false;
    }

    public static string FormatBriefMarkdown(
        string taskId,
        string taskTitle,
        string sharedInterfaces,
        IReadOnlyList<string> dependencyResults,
        IReadOnlyList<string> quotedChecks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Brief for task {taskId}: {taskTitle}");
        sb.AppendLine();
        sb.AppendLine("> [!NOTE] Builder Brief");
        sb.AppendLine("> This brief specifies the isolated task implementation.");
        sb.AppendLine("> The builder must not edit the manifest, approval.json, or routing.json.");
        sb.AppendLine();
        sb.AppendLine("## Task");
        sb.AppendLine($"- ID: `{taskId}`");
        sb.AppendLine($"- Title: {taskTitle}");
        sb.AppendLine();
        sb.AppendLine("## Shared Interfaces");
        sb.AppendLine(string.IsNullOrWhiteSpace(sharedInterfaces) ? "(None)" : sharedInterfaces.Trim());
        sb.AppendLine();
        sb.AppendLine("## Dependency Results");
        if (dependencyResults.Count == 0)
        {
            sb.AppendLine("(No prior task dependencies)");
        }
        else
        {
            foreach (var r in dependencyResults)
            {
                sb.AppendLine($"- {r}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Check List (Quoted Data)");
        sb.AppendLine("```text");
        foreach (var c in quotedChecks)
        {
            sb.AppendLine(c);
        }
        sb.AppendLine("```");
        return sb.ToString();
    }

    public static string WriteTaskBrief(
        string attemptsRoot,
        string projectId,
        string jobId,
        ParsedSpec spec,
        SpecTask task,
        IReadOnlyList<string>? dependencyResults = null)
    {
        var dir = JobDir(attemptsRoot, projectId, jobId);
        var taskDir = Path.Combine(dir, "tasks", task.Id);
        Directory.CreateDirectory(taskDir);
        var briefFile = Path.Combine(taskDir, "brief.md");
        var briefContent = FormatBriefMarkdown(
            task.Id,
            task.Title,
            spec.SharedInterfaces,
            dependencyResults ?? [],
            spec.ChecksQuoted);
        WriteAtomic(briefFile, briefContent);
        return briefContent;
    }

    public interface IGitOps
    {
        bool FetchOriginTip(string repoPath, out string tipSha, out string? error);
        bool CreateWorktree(string repoPath, string worktreePath, string branchName, string startCommit, out string? error);
        bool MergeDefaultBranch(string worktreePath, string defaultBranchTip, out bool conflict, out string[] conflictedPaths, out string? error);
        bool PushJobBranch(string worktreePath, string branchName, out string? error);
    }

    public static string WorktreeDir(string dataRoot, string projectId, string jobId) =>
        Path.GetFullPath(Path.Combine(dataRoot, "worktrees", projectId, "jobs", jobId));

    public static string BranchNameForJob(string jobId) => $"pipeline/{jobId}";

    public static bool EnsureWorktree(
        string dataRoot,
        string repoPath,
        string attemptsRoot,
        string projectId,
        string jobId,
        IGitOps gitOps,
        out string worktreePath,
        out string? awaitReason)
    {
        worktreePath = "";
        awaitReason = null;

        if (!SafeJobId(jobId))
        {
            // Set awaiting-owner with reason branch-name
            var d = JobDir(attemptsRoot, projectId, jobId);
            lock (LockFor(d))
            {
                var jFile = Path.Combine(d, "job.json");
                var jObj = LoadJson(jFile);
                if (jObj is not null)
                {
                    jObj["status"] = "awaiting-owner";
                    jObj["awaitingOwnerReason"] = "branch-name";
                    WriteAtomic(jFile, jObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            awaitReason = "branch-name";
            return false;
        }

        var dir = JobDir(attemptsRoot, projectId, jobId);
        lock (LockFor(dir))
        {
            var jobFile = Path.Combine(dir, "job.json");
            var jobObj = LoadJson(jobFile);
            if (jobObj is null)
            {
                awaitReason = "Job not found.";
                return false;
            }

            var branchName = BranchNameForJob(jobId);
            var expectedWorktreePath = WorktreeDir(dataRoot, projectId, jobId);

            // Restart check: if worktree already recorded and still exists
            var existingWorktreePath = jobObj["worktreePath"]?.ToString();
            var existingBranch = jobObj["worktreeBranch"]?.ToString();
            var existingStartCommit = jobObj["startCommit"]?.ToString();

            if (!string.IsNullOrWhiteSpace(existingWorktreePath) &&
                string.Equals(existingWorktreePath, expectedWorktreePath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existingBranch, branchName, StringComparison.Ordinal) &&
                Directory.Exists(existingWorktreePath))
            {
                worktreePath = existingWorktreePath;
                return true;
            }

            // Fetch origin tip
            if (!gitOps.FetchOriginTip(repoPath, out var originTip, out var fetchErr))
            {
                jobObj["worktreeError"] = fetchErr ?? "Failed fetching origin";
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                awaitReason = fetchErr ?? "Fetch origin failed";
                return false;
            }

            var baseCommit = jobObj["baseCommit"]?.ToString() ?? originTip;
            string startCommit = string.Equals(baseCommit, originTip, StringComparison.OrdinalIgnoreCase)
                ? baseCommit
                : originTip;

            if (!gitOps.CreateWorktree(repoPath, expectedWorktreePath, branchName, startCommit, out var createErr))
            {
                // Creation failure leaves status routing, records the error, does not mark branch ready
                jobObj["worktreeError"] = createErr ?? "Worktree creation failed";
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                awaitReason = createErr ?? "Worktree creation failed";
                return false;
            }

            jobObj["worktreePath"] = expectedWorktreePath;
            jobObj["worktreeBranch"] = branchName;
            jobObj["startCommit"] = startCommit;
            jobObj.Remove("worktreeError");
            WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            worktreePath = expectedWorktreePath;
            return true;
        }
    }

    public sealed record BuilderExecutionResult(
        string Summary,
        string Status,
        IReadOnlyList<string> FilesChanged,
        IReadOnlyList<JsonObject> Tests,
        string BuilderMarkdown);

    public sealed record ReviewerExecutionResult(
        string Summary,
        string Verdict,
        string Notes);

    public interface IModelExecutionOps
    {
        bool RunBuilder(
            string brief,
            string worktreePath,
            string taskId,
            int round,
            ChosenLaneModel model,
            string? handoffNotes,
            out BuilderExecutionResult? result,
            out string? error);

        bool RunReviewer(
            string lane,
            string brief,
            string builderMarkdown,
            string worktreePath,
            string taskId,
            int round,
            ChosenLaneModel model,
            out ReviewerExecutionResult? result,
            out string? error);

        bool CheckModelQuotaAvailable(string providerId, string modelId);
    }

    public interface IGateRunner
    {
        AcceptanceGate.Outcome RunGate(AcceptanceGate.Request request);
    }

    public sealed class RealGateRunner : IGateRunner
    {
        public AcceptanceGate.Outcome RunGate(AcceptanceGate.Request request) =>
            AcceptanceGate.Run(request).GetAwaiter().GetResult();
    }

    public static bool ExecuteTask(
        string dataRoot,
        string attemptsRoot,
        string projectId,
        string jobId,
        string taskId,
        IModelExecutionOps modelOps,
        IGateRunner gateRunner,
        out string taskStatus,
        out string? awaitReason)
    {
        taskStatus = "building";
        awaitReason = null;

        var dir = JobDir(attemptsRoot, projectId, jobId);
        lock (LockFor(dir))
        {
            var jobFile = Path.Combine(dir, "job.json");
            var jobObj = LoadJson(jobFile);
            if (jobObj is null)
            {
                awaitReason = "Job not found.";
                return false;
            }

            var routingFile = Path.Combine(dir, "routing.json");
            var routingObj = LoadJson(routingFile);
            if (routingObj is null)
            {
                awaitReason = "routing.json not found.";
                return false;
            }

            var worktreePath = jobObj["worktreePath"]?.ToString();
            if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            {
                awaitReason = "Worktree not found.";
                return false;
            }

            // Mark first builder started permanently
            jobObj["firstBuilderStarted"] = true;

            var snapshotId = jobObj["snapshotId"]?.ToString() ?? "";
            var manifestSha256 = jobObj["manifestSha256"]?.ToString() ?? "";
            var baseCommit = jobObj["baseCommit"]?.ToString() ?? "";
            var startCommit = jobObj["startCommit"]?.ToString() ?? baseCommit;

            var maxReviewRounds = routingObj["maxReviewRounds"]?.GetValue<int>() ?? DefaultMaxReviewRounds;
            var maxGateReturns = routingObj["maxGateReturns"]?.GetValue<int>() ?? DefaultMaxGateReturns;

            var taskDir = Path.Combine(dir, "tasks", taskId);
            Directory.CreateDirectory(taskDir);

            var briefFile = Path.Combine(taskDir, "brief.md");
            var briefContent = File.Exists(briefFile) ? File.ReadAllText(briefFile) : $"# Task {taskId}";

            var gateReturns = jobObj[$"gateReturns_{taskId}"]?.GetValue<int>() ?? 0;
            var reviewReturns = jobObj[$"reviewReturns_{taskId}"]?.GetValue<int>() ?? 0;
            var currentRound = jobObj[$"currentRound_{taskId}"]?.GetValue<int>() ?? 1;

            var chosenBuilder = GetChosenModel(routingObj, "builder");
            if (chosenBuilder is null)
            {
                awaitReason = "No chosen builder model.";
                return false;
            }

            // Loop rounds for this task
            while (true)
            {
                var roundDir = Path.Combine(taskDir, "rounds", currentRound.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(roundDir);

                // Check builder quota
                if (!modelOps.CheckModelQuotaAvailable(chosenBuilder.ProviderId, chosenBuilder.ModelId))
                {
                    // Attempt switch inside snapshot builder row
                    var switched = TrySwitchModel(routingObj, "builder", modelOps, out chosenBuilder);
                    if (!switched)
                    {
                        jobObj["status"] = "awaiting-owner";
                        jobObj["awaitingOwnerReason"] = "builder-quota";
                        jobObj["awaitingOwnerLane"] = "builder";
                        WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        awaitReason = "builder-quota";
                        taskStatus = "awaiting-owner";
                        return false;
                    }
                    WriteAtomic(routingFile, routingObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    jobObj[$"handoff_{taskId}"] = $"Switched builder to {chosenBuilder.ProviderId}:{chosenBuilder.ModelId} due to quota block.";
                    WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }

                jobObj["status"] = "building";
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                var builderFile = Path.Combine(roundDir, "builder.md");
                var resultFile = Path.Combine(roundDir, "result.json");
                string builderMd;
                if (!File.Exists(builderFile))
                {
                    var handoffNotes = jobObj[$"handoff_{taskId}"]?.ToString();
                    if (!modelOps.RunBuilder(briefContent, worktreePath, taskId, currentRound, chosenBuilder, handoffNotes, out var builderRes, out var buildErr))
                    {
                        awaitReason = buildErr ?? "Builder failed";
                        return false;
                    }

                    if (builderRes is null ||
                        string.IsNullOrWhiteSpace(builderRes.Status) ||
                        string.IsNullOrWhiteSpace(builderRes.Summary) ||
                        builderRes.FilesChanged is null)
                    {
                        awaitReason = "Missing required schema fields in builder result.";
                        return false;
                    }

                    builderMd = builderRes.BuilderMarkdown;
                    WriteAtomic(builderFile, builderMd);

                    var resObj = new JsonObject
                    {
                        ["summary"] = builderRes.Summary,
                        ["status"] = builderRes.Status,
                        ["taskId"] = taskId,
                        ["round"] = currentRound,
                        ["modelId"] = chosenBuilder.ModelId,
                        ["providerId"] = chosenBuilder.ProviderId,
                        ["filesChanged"] = new JsonArray(builderRes.FilesChanged.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray())
                    };
                    WriteAtomic(resultFile, resObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    builderMd = File.ReadAllText(builderFile);
                }

                // 2. Pre-review gate
                jobObj["status"] = "gating";
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                var gateReq = new AcceptanceGate.Request
                {
                    AttemptsRoot = attemptsRoot,
                    ProjectId = projectId,
                    JobId = jobId,
                    SnapshotId = snapshotId,
                    ManifestSha256 = manifestSha256,
                    BaseCommit = baseCommit,
                    Stage = "pre-review",
                    AttemptId = $"{taskId}-r{currentRound}",
                    WorktreePath = worktreePath,
                    WorktreeId = jobId,
                    CommitSha = startCommit,
                    TrackedRelativePaths = [] // Non-null list
                };

                AcceptanceGate.Outcome gateOutcome;
                try
                {
                    gateOutcome = gateRunner.RunGate(gateReq);
                }
                catch (Exception)
                {
                    jobObj["status"] = "awaiting-owner";
                    jobObj["awaitingOwnerReason"] = "gate-not-ported";
                    WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    awaitReason = "gate-not-ported";
                    taskStatus = "awaiting-owner";
                    return false;
                }

                if (gateOutcome is null ||
                    gateOutcome.Status != "pass" ||
                    gateOutcome.ManifestSha256 != manifestSha256)
                {
                    if (gateReturns >= maxGateReturns)
                    {
                        jobObj["status"] = "awaiting-owner";
                        jobObj["awaitingOwnerReason"] = "gate-returns";
                        WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        awaitReason = "gate-returns";
                        taskStatus = "awaiting-owner";
                        return false;
                    }

                    // Red pre-review gate returns log to builder
                    gateReturns++;
                    jobObj[$"gateReturns_{taskId}"] = gateReturns;

                    // Hand log back to builder for next iteration
                    var failingCheckIds = gateOutcome?.FailingChecks != null ? string.Join(", ", gateOutcome.FailingChecks) : "(none)";
                    var boundedLog = gateOutcome?.Log ?? "";
                    jobObj[$"handoff_{taskId}"] = $"Pre-review gate failed. Failing checks: {failingCheckIds}\nLog:\n{boundedLog}";
                    currentRound++;
                    jobObj[$"currentRound_{taskId}"] = currentRound;
                    WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    continue;
                }

                // 3. Pre-review gate PASSED! Reviewer lanes run.
                jobObj["status"] = "reviewing";
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                var chosenNode = routingObj["chosen"] as JsonObject;
                var reviewerLanes = chosenNode?.Select(kvp => kvp.Key).Where(k => k.StartsWith("reviewer-")).ToList() ?? [];

                bool anyChanges = false;
                string? reviewerNotesForNextRound = null;

                foreach (var rLane in reviewerLanes)
                {
                    var revModel = GetChosenModel(routingObj, rLane);
                    if (revModel is null) continue;

                    // Quota check
                    if (!modelOps.CheckModelQuotaAvailable(revModel.ProviderId, revModel.ModelId))
                    {
                        var switched = TrySwitchModel(routingObj, rLane, modelOps, out revModel, chosenBuilder.ProviderId);
                        if (!switched)
                        {
                            jobObj["status"] = "awaiting-owner";
                            jobObj["awaitingOwnerReason"] = "reviewer-quota";
                            jobObj["awaitingOwnerLane"] = rLane;
                            WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                            awaitReason = "reviewer-quota";
                            taskStatus = "awaiting-owner";
                            return false;
                        }
                        WriteAtomic(routingFile, routingObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        jobObj[$"reviewerSwitch_{rLane}"] = $"{revModel.ProviderId}:{revModel.ModelId}";
                        WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }

                    if (!modelOps.RunReviewer(rLane, briefContent, builderMd, worktreePath, taskId, currentRound, revModel, out var revRes, out var revErr))
                    {
                        awaitReason = revErr ?? $"Reviewer {rLane} failed";
                        return false;
                    }

                    if (revRes is null || (revRes.Verdict != "accept" && revRes.Verdict != "changes"))
                    {
                        awaitReason = $"Invalid reviewer verdict from {rLane}.";
                        return false;
                    }

                    var revFile = Path.Combine(roundDir, $"review-{rLane}.json");
                    var revObj = new JsonObject
                    {
                        ["summary"] = revRes.Summary,
                        ["lane"] = rLane,
                        ["verdict"] = revRes.Verdict,
                        ["notes"] = revRes.Notes,
                        ["modelId"] = revModel.ModelId,
                        ["providerId"] = revModel.ProviderId
                    };
                    WriteAtomic(revFile, revObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    if (rLane.StartsWith("reviewer-", StringComparison.OrdinalIgnoreCase))
                    {
                        var shortLane = rLane["reviewer-".Length..];
                        var shortRevFile = Path.Combine(roundDir, $"review-{shortLane}.json");
                        WriteAtomic(shortRevFile, revObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }

                    if (revRes.Verdict == "changes")
                    {
                        anyChanges = true;
                        reviewerNotesForNextRound = revRes.Notes;
                        break; // Other reviewer lanes cancelled!
                    }
                }

                if (anyChanges)
                {
                    if (reviewReturns >= maxReviewRounds)
                    {
                        jobObj["status"] = "awaiting-owner";
                        jobObj["awaitingOwnerReason"] = "review-rounds";
                        WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                        awaitReason = "review-rounds";
                        taskStatus = "awaiting-owner";
                        return false;
                    }

                    reviewReturns++;
                    jobObj[$"reviewReturns_{taskId}"] = reviewReturns;

                    jobObj[$"handoff_{taskId}"] = $"Review requested changes:\n{reviewerNotesForNextRound}";
                    currentRound++;
                    jobObj[$"currentRound_{taskId}"] = currentRound;
                    WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    continue;
                }

                // All required lanes accepted!
                taskStatus = "accepted";
                jobObj[$"task_{taskId}_status"] = "accepted";
                jobObj.Remove($"handoff_{taskId}");
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
        }
    }

    public static bool AllowOneMoreReviewRound(string attemptsRoot, string projectId, string jobId)
    {
        var dir = JobDir(attemptsRoot, projectId, jobId);
        lock (LockFor(dir))
        {
            var jobFile = Path.Combine(dir, "job.json");
            var routingFile = Path.Combine(dir, "routing.json");
            var jobObj = LoadJson(jobFile);
            var routingObj = LoadJson(routingFile);
            if (jobObj is null || routingObj is null) return false;

            var cur = routingObj["maxReviewRounds"]?.GetValue<int>() ?? DefaultMaxReviewRounds;
            routingObj["maxReviewRounds"] = cur + 1;
            WriteAtomic(routingFile, routingObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            jobObj["status"] = "building";
            jobObj.Remove("awaitingOwnerReason");
            WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
    }

    static ChosenLaneModel? GetChosenModel(JsonObject routingObj, string lane)
    {
        if (routingObj["chosen"] is JsonObject chosenNode &&
            chosenNode[lane] is JsonObject m)
        {
            var p = m["providerId"]?.ToString() ?? "";
            var id = m["modelId"]?.ToString() ?? "";
            var w = m["weight"]?.GetValue<int>() ?? 1;
            return new ChosenLaneModel(p, id, w);
        }
        return null;
    }

    static bool TrySwitchModel(
        JsonObject routingObj,
        string lane,
        IModelExecutionOps modelOps,
        out ChosenLaneModel newModel,
        string? builderProviderId = null)
    {
        newModel = null!;
        var rowsArr = routingObj["rowsUsed"] as JsonArray;
        if (rowsArr is null) return false;

        foreach (var rNode in rowsArr)
        {
            if (rNode is not JsonObject rObj) continue;
            var rLane = rObj["lane"]?.ToString();
            if (!string.Equals(rLane, lane, StringComparison.OrdinalIgnoreCase)) continue;

            var candArr = rObj["candidates"] as JsonArray;
            if (candArr is null) continue;

            // Another company than the builder first, then any other available model: a preference, not a rule.
            bool SameCompany(string providerId) => lane != "builder" && builderProviderId is not null && string.Equals(providerId, builderProviderId, StringComparison.OrdinalIgnoreCase);
            foreach (var cNode in candArr.OfType<JsonObject>().OrderBy(item => SameCompany(item["providerId"]?.ToString() ?? "") ? 1 : 0).ToList())
            {
                if (cNode is not JsonObject cObj) continue;
                var p = cObj["providerId"]?.ToString() ?? "";
                var id = cObj["modelId"]?.ToString() ?? "";
                var w = cObj["weight"]?.GetValue<int>() ?? 1;

                if (modelOps.CheckModelQuotaAvailable(p, id))
                {
                    newModel = new ChosenLaneModel(p, id, w);
                    if (routingObj["chosen"] is JsonObject chosenObj)
                    {
                        chosenObj[lane] = new JsonObject
                        {
                            ["providerId"] = p,
                            ["modelId"] = id,
                            ["weight"] = w
                        };
                    }
                    return true;
                }
            }
        }

        return false;
    }

    public static SpecTask? GetNextRunnableTask(ParsedSpec spec, JsonObject jobObj)
    {
        foreach (var task in spec.Tasks)
        {
            var status = jobObj[$"task_{task.Id}_status"]?.ToString();
            if (status == "accepted") continue;

            bool allDepsAccepted = true;
            foreach (var dep in task.Dependencies)
            {
                if (jobObj[$"task_{dep}_status"]?.ToString() != "accepted")
                {
                    allDepsAccepted = false;
                    break;
                }
            }

            if (allDepsAccepted)
            {
                return task;
            }
        }
        return null;
    }

    public static bool AreAllTasksAccepted(ParsedSpec spec, JsonObject jobObj)
    {
        return spec.Tasks.Count > 0 && spec.Tasks.All(t => jobObj[$"task_{t.Id}_status"]?.ToString() == "accepted");
    }

    public interface IForgeOps
    {
        bool FindExistingPullRequest(string repoUrlOrName, string headBranch, out int? prNumber, out string? prUrl);
        bool OpenPullRequest(string repoUrlOrName, string headBranch, string title, string body, out int prNumber, out string prUrl, out string? error);
    }

    public static string FormatPullRequestBody(string jobId, int specRevision, string manifestSha256, int? closesIssueNumber = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Pipeline Job: {jobId}");
        sb.AppendLine();
        sb.AppendLine($"- Spec Revision: {specRevision}");
        sb.AppendLine($"- Manifest SHA-256: `{manifestSha256}`");
        if (closesIssueNumber.HasValue && closesIssueNumber.Value > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Closes #{closesIssueNumber.Value}");
        }
        return sb.ToString();
    }

    public static bool SyncAndOpenPullRequest(
        string attemptsRoot,
        string projectId,
        string jobId,
        string? linkedGitHubRepository,
        IGitOps gitOps,
        IGateRunner gateRunner,
        IForgeOps forgeOps,
        out string finalStatus,
        out string? awaitReason)
    {
        finalStatus = "syncing";
        awaitReason = null;

        var dir = JobDir(attemptsRoot, projectId, jobId);
        lock (LockFor(dir))
        {
            var jobFile = Path.Combine(dir, "job.json");
            var jobObj = LoadJson(jobFile);
            if (jobObj is null)
            {
                awaitReason = "Job not found.";
                return false;
            }

            var prFile = Path.Combine(dir, "pull-request.json");

            // Restart check: if pull-request.json already has a number, do not open a second PR
            if (File.Exists(prFile))
            {
                var existingPr = LoadJson(prFile);
                if (existingPr?["number"] is JsonValue numVal && numVal.TryGetValue<int>(out var num) && num > 0)
                {
                    finalStatus = "pull-request";
                    return true;
                }
            }

            // If already ready-local, do not open a pull request retroactively even if a repo is linked later
            if (string.Equals(jobObj["status"]?.ToString(), "ready-local", StringComparison.OrdinalIgnoreCase))
            {
                finalStatus = "ready-local";
                awaitReason = "no linked GitHub repository";
                return true;
            }

            var worktreePath = jobObj["worktreePath"]?.ToString();
            var branchName = jobObj["worktreeBranch"]?.ToString() ?? BranchNameForJob(jobId);
            if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
            {
                awaitReason = "Worktree not found.";
                return false;
            }

            var snapshotId = jobObj["snapshotId"]?.ToString() ?? "";
            var manifestSha256 = jobObj["manifestSha256"]?.ToString() ?? "";
            var baseCommit = jobObj["baseCommit"]?.ToString() ?? "";
            var specRevision = jobObj["specRevision"]?.GetValue<int>() ?? 1;

            // 1. Set status syncing
            jobObj["status"] = "syncing";
            WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // 2. Fetch default branch tip and merge into job branch
            if (!gitOps.FetchOriginTip(worktreePath, out var defaultTip, out var fetchErr))
            {
                awaitReason = fetchErr ?? "Fetch origin failed";
                return false;
            }

            if (!gitOps.MergeDefaultBranch(worktreePath, defaultTip, out var conflict, out var conflictedPaths, out var mergeErr))
            {
                awaitReason = mergeErr ?? "Merge default branch failed";
                return false;
            }

            if (conflict)
            {
                jobObj["status"] = "awaiting-owner";
                jobObj["awaitingOwnerReason"] = "merge conflict";
                var arr = new JsonArray();
                foreach (var p in conflictedPaths) arr.Add((JsonNode)JsonValue.Create(p)!);
                jobObj["conflictedPaths"] = arr;
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                finalStatus = "awaiting-owner";
                awaitReason = "merge conflict";
                return false;
            }

            // 3. Post-sync gate on merge commit
            var postSyncReq = new AcceptanceGate.Request
            {
                AttemptsRoot = attemptsRoot,
                ProjectId = projectId,
                JobId = jobId,
                SnapshotId = snapshotId,
                ManifestSha256 = manifestSha256,
                BaseCommit = baseCommit,
                Stage = "post-sync",
                AttemptId = $"{jobId}-post-sync",
                WorktreePath = worktreePath,
                WorktreeId = jobId,
                CommitSha = defaultTip,
                TrackedRelativePaths = []
            };

            AcceptanceGate.Outcome postSyncOutcome;
            try
            {
                postSyncOutcome = gateRunner.RunGate(postSyncReq);
            }
            catch (Exception)
            {
                jobObj["status"] = "awaiting-owner";
                jobObj["awaitingOwnerReason"] = "gate-not-ported";
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                finalStatus = "awaiting-owner";
                awaitReason = "gate-not-ported";
                return false;
            }

            if (postSyncOutcome is null ||
                postSyncOutcome.Status != "pass" ||
                postSyncOutcome.ManifestSha256 != manifestSha256 ||
                !postSyncOutcome.PullRequestReady ||
                postSyncOutcome.BlocksPullRequest)
            {
                jobObj["status"] = "awaiting-owner";
                jobObj["awaitingOwnerReason"] = "post-sync gate failed";
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                finalStatus = "awaiting-owner";
                awaitReason = "post-sync gate failed";
                return false;
            }

            // 4. Linked repository check
            if (string.IsNullOrWhiteSpace(linkedGitHubRepository))
            {
                // No linked repository -> ready-local
                jobObj["status"] = "ready-local";
                jobObj["awaitingOwnerReason"] = "no linked GitHub repository";
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                WriteAtomic(prFile, "null"); // records null
                finalStatus = "ready-local";
                awaitReason = "no linked GitHub repository";
                return true;
            }

            // Has linked repo: check for unexpected PR for this head
            if (forgeOps.FindExistingPullRequest(linkedGitHubRepository, branchName, out var foundPrNum, out _) && foundPrNum.HasValue)
            {
                jobObj["status"] = "awaiting-owner";
                jobObj["awaitingOwnerReason"] = "unexpected-pr";
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                finalStatus = "awaiting-owner";
                awaitReason = "unexpected-pr";
                return false;
            }

            // Set opening-pr and push job branch
            jobObj["status"] = "opening-pr";
            WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            if (!gitOps.PushJobBranch(worktreePath, branchName, out var pushErr))
            {
                jobObj["pushError"] = pushErr ?? "push failed";
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                finalStatus = "opening-pr";
                awaitReason = "push failed";
                return false;
            }

            // Open PR
            int? closesIssue = null;
            if (jobObj["source"] is JsonObject srcObj &&
                string.Equals(srcObj["type"]?.ToString(), "github", StringComparison.OrdinalIgnoreCase))
            {
                if (srcObj["number"] is JsonValue numV && numV.TryGetValue<int>(out var issueNum))
                {
                    closesIssue = issueNum;
                }
            }

            var prTitle = $"Pipeline: {jobObj["summary"]?.ToString() ?? jobId}";
            var prBody = FormatPullRequestBody(jobId, specRevision, manifestSha256, closesIssue);

            if (!forgeOps.OpenPullRequest(linkedGitHubRepository, branchName, prTitle, prBody, out var prNumber, out var prUrl, out var openPrErr))
            {
                jobObj["status"] = "awaiting-owner";
                jobObj["awaitingOwnerReason"] = openPrErr ?? "Failed to open PR";
                WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                finalStatus = "awaiting-owner";
                awaitReason = openPrErr;
                return false;
            }

            // Write pull-request.json with summary first
            var prRecord = new JsonObject
            {
                ["summary"] = $"Pull request #{prNumber}",
                ["url"] = prUrl,
                ["number"] = prNumber,
                ["headBranch"] = branchName,
                ["headSha"] = defaultTip
            };
            WriteAtomic(prFile, prRecord.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            jobObj["status"] = "pull-request";
            jobObj.Remove("pushError");
            jobObj.Remove("awaitingOwnerReason");
            WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            finalStatus = "pull-request";
            return true;
        }
    }

    public static bool StopJob(string attemptsRoot, string projectId, string jobId)
    {
        var dir = JobDir(attemptsRoot, projectId, jobId);
        lock (LockFor(dir))
        {
            var jobFile = Path.Combine(dir, "job.json");
            var jobObj = LoadJson(jobFile);
            if (jobObj is null) return false;

            jobObj["status"] = "stopped";
            WriteAtomic(jobFile, jobObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
    }
}
