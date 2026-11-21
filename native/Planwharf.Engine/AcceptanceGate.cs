// Job 26 implementation of native/porting/jobs/19-gate.md.
// Not a port of a JavaScript module. engine/project-checks.cjs is unchanged and is not this gate.
//
// Missing ports, left to their jobs: repository and git (job 30), the process runner (job 32),
// the orchestrator, intake and spec approval (job 20), and pull-request routing (job 21).
// This module does not approve a manifest, open a pull request, or call a model.
//
// Manifest. Job state lives under {attempts}/pipeline/{project}/jobs/{job}/, outside the
// candidate worktree. acceptance.json is hashed as raw bytes (schema 1, the job 20 document).
// approval.json must say approvedBy "owner" and name the same SHA-256, spec revision, and base.
// job.json points at that snapshot. A candidate copy is never read.
//
// Check store. Trusted programs (engine-owned, pinned-external) live in check-store/{checkId}/
// and must match programSha256. candidate-code is untrusted: its exit cannot pass the gate.
// It runs only when engineOwnedAssertionId names an engine-owned check whose bytes match.
//
// Snapshot. The repository port is job 30 and is not in C# yet, so the caller supplies tracked
// relative paths. A null list stops (repository-port-missing); this module does not invent one.
// Working inputs are the untracked build inputs the engine was given. Private notebook paths
// (.planwharf, .git, .env, credentials, keys) are not read. A symlink, an unparsed ignore rule,
// or an undeclared ordinary file is unsafe: the gate stops and does not pass. Declared
// artifact outputs are not inputs. Their digest is stored on the pass so a later edit cannot
// reuse it. Chat work is not submitted here; a pipeline job with no accepted manifest waits.
//
// Isolation. No sandbox is claimed. Children get a short environment (no Planwharf tokens,
// provider credentials, or git push credentials) and a working directory inside the worktree.
// The worktree must not contain, or sit inside, the attempts store. Descendants are killed on
// timeout and cancellation. shell true runs only when that check id is listed in the owner
// approval; otherwise the gate stops for the owner (needs-owner-review) and does not ask the
// builder to weaken the check.
//
// Evidence. gate-runs/{runId}.json is append-only. A pass exists only after a matching .seal.
// A later run writes a new file. It may resume a sealed passing command only when the job,
// stage, attempt, manifest, worktree, and snapshot digests all match.
//
// Outcomes. pass, blocked, waiting (no accepted manifest), error (spawn, path, crash, evidence).
// pre-review pass releases the reviewer only. post-sync pass is the only pull-request ready.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public static class AcceptanceGate
{
    public const int Schema = 1;
    public const int MaxOutputChars = 8_000;
    public const int MaxLogChars = 3_000;

    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public sealed class ProcessRequest
    {
        public required string Executable { get; init; }
        public required string[] Arguments { get; init; }
        public required string WorkingDirectory { get; init; }
        public required IReadOnlyDictionary<string, string> Environment { get; init; }
        public required int TimeoutMs { get; init; }
        public required bool Shell { get; init; }
        public required string CheckId { get; init; }
    }

    public sealed class ProcessEvents
    {
        public Action<string> Output { get; set; } = static _ => { };
        public Action<int?> Exit { get; set; } = static _ => { };
        public Action<string> Error { get; set; } = static _ => { };
        public Action KillTree { get; set; } = static () => { };
    }

    public delegate void Starter(ProcessRequest request, ProcessEvents events);

    public sealed class Request
    {
        public required string AttemptsRoot { get; init; }
        public required string ProjectId { get; init; }
        public required string JobId { get; init; }
        public required string SnapshotId { get; init; }
        public required string ManifestSha256 { get; init; }
        public required string BaseCommit { get; init; }
        public required string Stage { get; init; }
        public required string AttemptId { get; init; }
        public required string WorktreePath { get; init; }
        public required string WorktreeId { get; init; }
        public string? CommitSha { get; init; }
        public string? TreeSha { get; init; }
        public IReadOnlyList<string>? TrackedRelativePaths { get; init; }
        public IReadOnlyList<string>? WorkingInputs { get; init; }
        public string InitiatedBy { get; init; } = "engine";
        public Func<long>? Now { get; init; }
        public Starter? Start { get; init; }
        public Action? BeforeSeal { get; init; }
        public CancellationToken Cancellation { get; init; }
    }

    public sealed record Outcome(
        string Status,
        string Reason,
        string? RunId,
        string[] FailingChecks,
        string Log,
        bool ReturnToBuilder,
        bool PullRequestReady,
        bool BlocksPullRequest,
        string ManifestSha256,
        string[] CheckIds,
        string TreeDigest,
        string WorkingInputDigest,
        string ArtifactDigest);

    public sealed class Check
    {
        public required string Id { get; init; }
        public required string Purpose { get; init; }
        public required string Executable { get; init; }
        public required string[] Arguments { get; init; }
        public required string WorkingDirectory { get; init; }
        public required int TimeoutSeconds { get; init; }
        public required int ExpectedExitCode { get; init; }
        public required string ExpectedResult { get; init; }
        public required bool Shell { get; init; }
        public required string Trust { get; init; }
        public string? EngineOwnedAssertionId { get; init; }
        public string? ProgramSha256 { get; init; }
        public string Digest { get; init; } = "";
    }

    public sealed class Loaded
    {
        public required string Status { get; init; }
        public required string Reason { get; init; }
        public string ManifestSha256 { get; init; } = "";
        public string[] CheckIds { get; init; } = [];
        public Check[] Checks { get; init; } = [];
        public string BaseCommit { get; init; } = "";
        public int SpecRevision { get; init; }
        public string[] ShellChecks { get; init; } = [];
        public string JobDirectory { get; init; } = "";
    }

    // Reads the job pointer, hashes the manifest bytes, and checks the owner record.
    // Does not look inside the candidate worktree.
    public static Loaded Load(string attemptsRoot, string projectId, string jobId, string snapshotId, string manifestSha256, string baseCommit)
    {
        if (!SafeId(projectId) || !SafeId(jobId) || !SafeId(snapshotId))
            return FailLoad("error", "path-escape");
        if (!IsSha256(manifestSha256) || !IsCommit(baseCommit))
            return FailLoad("blocked", "corrupt-manifest");
        var attempts = Path.GetFullPath(attemptsRoot);
        var jobDir = Path.GetFullPath(Path.Combine(attempts, "pipeline", projectId, "jobs", jobId));
        if (!Inside(attempts, jobDir)) return FailLoad("error", "path-escape");
        var jobFile = Path.Combine(jobDir, "job.json");
        if (!File.Exists(jobFile)) return FailLoad("waiting", "missing-manifest");
        JsonObject job;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(jobFile)) is not JsonObject parsed) return FailLoad("blocked", "corrupt-manifest");
            job = parsed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return FailLoad("blocked", "corrupt-manifest");
        }
        if (string.IsNullOrWhiteSpace(Text(job, "summary")))
            return FailLoad("blocked", "corrupt-manifest");
        if (Text(job, "status") != "approved")
            return FailLoad("waiting", "missing-manifest");
        if (Text(job, "snapshotId") is not { } pointed || pointed.Length == 0)
            return FailLoad("waiting", "missing-manifest");
        if (!string.Equals(pointed, snapshotId, StringComparison.Ordinal))
            return FailLoad("blocked", "stale");
        if (!string.Equals(Text(job, "manifestSha256"), manifestSha256, StringComparison.OrdinalIgnoreCase))
            return FailLoad("blocked", "hash-mismatch");
        if (!string.Equals(Text(job, "baseCommit"), baseCommit, StringComparison.OrdinalIgnoreCase))
            return FailLoad("blocked", "stale");
        var manifestPath = Text(job, "manifestPath");
        if (!TryRelative(manifestPath, out var relativeManifest)) return FailLoad("error", "path-escape");
        var manifestFile = ResolveInside(jobDir, relativeManifest);
        if (manifestFile is null || !File.Exists(manifestFile)) return FailLoad("blocked", "missing-manifest");
        byte[] bytes;
        try { bytes = File.ReadAllBytes(manifestFile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return FailLoad("blocked", "corrupt-manifest");
        }
        var hash = Sha256(bytes);
        if (!string.Equals(hash, manifestSha256, StringComparison.OrdinalIgnoreCase))
            return FailLoad("blocked", "hash-mismatch");
        JsonObject manifest;
        try
        {
            if (JsonNode.Parse(Utf8.GetString(bytes)) is not JsonObject parsed) return FailLoad("blocked", "corrupt-manifest");
            manifest = parsed;
        }
        catch (System.Text.Json.JsonException)
        {
            return FailLoad("blocked", "corrupt-manifest");
        }
        if (!TryInt(manifest["schema"], out var schema) || schema != Schema) return FailLoad("blocked", "corrupt-manifest");
        if (string.IsNullOrWhiteSpace(Text(manifest, "summary"))) return FailLoad("blocked", "corrupt-manifest");
        if (!string.Equals(Text(manifest, "jobId"), jobId, StringComparison.Ordinal)) return FailLoad("blocked", "stale");
        if (!TryInt(manifest["specRevision"], out var revision)) return FailLoad("blocked", "corrupt-manifest");
        if (!string.Equals(Text(manifest, "baseCommit"), baseCommit, StringComparison.OrdinalIgnoreCase))
            return FailLoad("blocked", "stale");
        if (!job.ContainsKey("specRevision") || !SameRevision(job["specRevision"], revision))
            return FailLoad("blocked", "stale");
        if (manifest["checks"] is not JsonArray checks || checks.Count == 0) return FailLoad("blocked", "corrupt-manifest");
        var parsedChecks = new List<Check>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in checks)
        {
            if (ParseCheck(node) is not { } check || !seen.Add(check.Id)) return FailLoad("blocked", "corrupt-manifest");
            parsedChecks.Add(check);
        }
        foreach (var check in parsedChecks)
        {
            if (check.Trust == "candidate-code")
            {
                var assertion = parsedChecks.FirstOrDefault(item => item.Id == check.EngineOwnedAssertionId);
                if (assertion is null || assertion.Trust != "engine-owned") return FailLoad("blocked", "insufficient-check");
            }
        }
        var approval = ReadApproval(jobDir);
        if (approval.Status != "ok") return FailLoad(approval.Status, approval.Reason);
        if (!string.Equals(approval.ManifestSha256, hash, StringComparison.OrdinalIgnoreCase))
            return FailLoad("blocked", "unauthorized");
        if (!string.Equals(approval.ApprovedBy, "owner", StringComparison.Ordinal))
            return FailLoad("blocked", "unauthorized");
        if (approval.SpecRevision != revision || !string.Equals(approval.BaseCommit, baseCommit, StringComparison.OrdinalIgnoreCase))
            return FailLoad("blocked", "unauthorized");
        if (!string.Equals(approval.JobId, jobId, StringComparison.Ordinal))
            return FailLoad("blocked", "unauthorized");
        return new Loaded
        {
            Status = "ok",
            Reason = "",
            ManifestSha256 = hash,
            CheckIds = parsedChecks.Select(check => check.Id).ToArray(),
            Checks = parsedChecks.ToArray(),
            BaseCommit = baseCommit,
            SpecRevision = revision,
            ShellChecks = approval.ShellChecks,
            JobDirectory = jobDir
        };
    }

    static Loaded FailLoad(string status, string reason) => new() { Status = status, Reason = reason };

    sealed class Approval
    {
        public string Status { get; init; } = "ok";
        public string Reason { get; init; } = "";
        public string ApprovedBy { get; init; } = "";
        public string ManifestSha256 { get; init; } = "";
        public string BaseCommit { get; init; } = "";
        public string JobId { get; init; } = "";
        public int SpecRevision { get; init; }
        public string[] ShellChecks { get; init; } = [];
    }

    static Approval ReadApproval(string jobDir)
    {
        var file = Path.Combine(jobDir, "approval.json");
        if (!File.Exists(file)) return new Approval { Status = "waiting", Reason = "missing-manifest" };
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject parsed)
                return new Approval { Status = "blocked", Reason = "unauthorized" };
            if (string.IsNullOrWhiteSpace(Text(parsed, "summary")) || string.IsNullOrWhiteSpace(Text(parsed, "approvedAt")))
                return new Approval { Status = "blocked", Reason = "unauthorized" };
            if (Text(parsed, "approvedBy") is not { } who || who.Length == 0)
                return new Approval { Status = "blocked", Reason = "unauthorized" };
            var shells = new List<string>();
            if (parsed["shellChecks"] is JsonArray list)
            {
                foreach (var item in list)
                {
                    if (item is not JsonValue || TextValue(item) is not { } id || !SafeId(id))
                        return new Approval { Status = "blocked", Reason = "corrupt-manifest" };
                    shells.Add(id);
                }
            }
            if (!TryInt(parsed["specRevision"], out var revision) || Text(parsed, "manifestSha256") is not { } sha || !IsSha256(sha))
                return new Approval { Status = "blocked", Reason = "unauthorized" };
            return new Approval
            {
                ApprovedBy = who,
                ManifestSha256 = sha,
                BaseCommit = Text(parsed, "baseCommit") ?? "",
                JobId = Text(parsed, "jobId") ?? "",
                SpecRevision = revision,
                ShellChecks = shells.ToArray()
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new Approval { Status = "blocked", Reason = "corrupt-manifest" };
        }
    }

    static Check? ParseCheck(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var id = Text(obj, "id");
        var purpose = Text(obj, "purpose");
        var executable = Text(obj, "executable");
        var workingDirectory = Text(obj, "workingDirectory");
        var expected = Text(obj, "expectedResult");
        var trust = Text(obj, "trust");
        if (!SafeId(id) || string.IsNullOrEmpty(purpose) || string.IsNullOrEmpty(expected)) return null;
        if (!TryRelative(executable, out var exe) || !TryRelative(workingDirectory, true, out var cwd)) return null;
        if (obj["arguments"] is not JsonArray args) return null;
        var arguments = new string[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            if (TextValue(args[i]) is not { } arg || !SafeArgument(arg)) return null;
            arguments[i] = arg;
        }
        if (!TryInt(obj["timeoutSeconds"], out var timeout) || timeout is < 1 or > 3600) return null;
        if (!TryInt(obj["expectedExitCode"], out var exit)) return null;
        var shell = false;
        if (obj["shell"] is JsonValue shellValue)
        {
            if (!shellValue.TryGetValue<bool>(out shell)) return null;
        }
        if (trust is not ("engine-owned" or "pinned-external" or "candidate-code")) return null;
        var assertion = Text(obj, "engineOwnedAssertionId");
        var program = Text(obj, "programSha256");
        if (trust == "candidate-code")
        {
            if (!SafeId(assertion)) return null;
        }
        else if (!IsSha256(program)) return null;
        if (expected!.StartsWith("artifact-sha256:", StringComparison.Ordinal)
            && (!TryArtifact(expected, out var artifact, out _) || IsPrivate(artifact))) return null;
        var digest = Sha256(Utf8.GetBytes(string.Join('\0', id, exe, string.Join('\0', arguments), cwd, timeout.ToString(CultureInfo.InvariantCulture), exit.ToString(CultureInfo.InvariantCulture), expected, trust, assertion ?? "", program ?? "")));
        return new Check
        {
            Id = id!,
            Purpose = purpose!,
            Executable = exe,
            Arguments = arguments,
            WorkingDirectory = cwd,
            TimeoutSeconds = timeout,
            ExpectedExitCode = exit,
            ExpectedResult = expected!,
            Shell = shell,
            Trust = trust,
            EngineOwnedAssertionId = assertion,
            ProgramSha256 = program,
            Digest = digest
        };
    }

    public static string Sha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var chars = new char[hash.Length * 2];
        for (var i = 0; i < hash.Length; i++) hash[i].TryFormat(chars.AsSpan(i * 2), out _, "x2");
        return new string(chars);
    }

    public static bool SafeId(string? value)
    {
        if (value is not { Length: > 0 and <= 81 }) return false;
        if (!IsIdChar(value[0], first: true)) return false;
        for (var i = 1; i < value.Length; i++)
            if (!IsIdChar(value[i], first: false)) return false;
        return true;
    }

    static bool IsIdChar(char c, bool first) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') || (!first && c is '.' or '_' or '-');

    public static bool TryRelative(string? value, out string relative) => TryRelative(value, allowDot: false, out relative);

    public static bool TryRelative(string? value, bool allowDot, out string relative)
    {
        relative = "";
        if (string.IsNullOrEmpty(value) || value.Contains('\\') || Path.IsPathRooted(value)) return false;
        if (allowDot && value == ".") { relative = "."; return true; }
        var parts = value.Split('/');
        if (parts.Length == 0 || parts.Any(part => part is "" or "." or "..")) return false;
        relative = value;
        return true;
    }

    static bool SafeArgument(string arg)
    {
        if (arg.Length == 0 || arg.Contains('\0')) return false;
        if (Path.IsPathRooted(arg)) return false;
        var parts = arg.Replace('\\', '/').Split('/');
        return !parts.Any(part => part == "..");
    }

    static bool TryArtifact(string expected, out string relative, out string hash)
    {
        relative = "";
        hash = "";
        var body = expected["artifact-sha256:".Length..];
        var split = body.LastIndexOf(':');
        if (split <= 0 || split == body.Length - 1) return false;
        if (!TryRelative(body[..split], out relative) || !IsSha256(body[(split + 1)..])) return false;
        hash = body[(split + 1)..].ToLowerInvariant();
        return true;
    }

    static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    static bool IsCommit(string? value) =>
        value is { Length: 40 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    static string? Text(JsonObject obj, string key) => obj[key] is { } node ? TextValue(node) : null;

    static string? TextValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static bool SameRevision(JsonNode? node, int revision)
    {
        if (TryInt(node, out var number)) return number == revision;
        return TextValue(node) is { } text
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number == revision;
    }

    static bool TryLong(JsonNode? node, out long number)
    {
        number = 0;
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<long>(out number)) return true;
        if (value.TryGetValue<int>(out var small)) { number = small; return true; }
        return false;
    }

    static bool TryInt(JsonNode? node, out int number)
    {
        number = 0;
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<int>(out number)) return true;
        if (value.TryGetValue<long>(out var wide) && wide is >= int.MinValue and <= int.MaxValue)
        {
            number = (int)wide;
            return true;
        }
        return false;
    }

    static bool Inside(string root, string full)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var compare = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.Equals(root, compare) || full.StartsWith(prefix, compare);
    }

    static string? ResolveInside(string root, string relative)
    {
        if (!TryRelative(relative, allowDot: true, out var safe)) return null;
        var rootFull = Path.GetFullPath(root);
        var combined = Path.GetFullPath(Path.Combine(rootFull, safe == "." ? "" : safe.Replace('/', Path.DirectorySeparatorChar)));
        return Inside(rootFull, combined) ? combined : null;
    }

    public sealed class PassClaim
    {
        public required string Stage { get; init; }
        public required string AttemptId { get; init; }
        public required string SnapshotId { get; init; }
        public required string ManifestSha256 { get; init; }
        public required string WorktreeId { get; init; }
        public required string TreeDigest { get; init; }
        public required string WorkingInputDigest { get; init; }
        public required string ArtifactDigest { get; init; }
        public string? CommitSha { get; init; }
        public string? TreeSha { get; init; }
        public required IReadOnlyList<string> CheckIds { get; init; }
    }

    static readonly ConcurrentDictionary<string, SemaphoreSlim> RunLocks = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    static readonly string[] EnvAllow = ["PATH", "PATHEXT", "SYSTEMROOT", "WINDIR", "COMSPEC", "TEMP", "TMP", "HOME", "USERPROFILE", "LANG", "LC_ALL", "LC_CTYPE", "TZ"];

    sealed class Exec
    {
        public required string CheckId { get; init; }
        public required string CheckDigest { get; init; }
        public string Outcome { get; set; } = "";
        public int? ExitCode { get; set; }
        public string Output { get; set; } = "";
        public string OutputDigest { get; set; } = "";
        public long Started { get; set; }
        public long Ended { get; set; }
        public string? ResumedFrom { get; set; }
    }

    public static async Task<Outcome> Run(Request request)
    {
        var now = request.Now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (!string.Equals(request.InitiatedBy, "engine", StringComparison.Ordinal))
            return Early(request, "blocked", "not-engine");
        if (request.Stage is not ("pre-review" or "post-sync"))
            return Early(request, "blocked", "bad-stage");
        if (!SafeId(request.AttemptId) || !SafeId(request.WorktreeId))
            return Early(request, "error", "path-escape");
        if (request.CommitSha is not null && !IsCommit(request.CommitSha))
            return Early(request, "blocked", "stale");
        if (request.TreeSha is not null && !IsCommit(request.TreeSha))
            return Early(request, "blocked", "stale");
        var attemptsFull = Path.GetFullPath(request.AttemptsRoot);
        var workFull = Path.GetFullPath(request.WorktreePath);
        if (Inside(attemptsFull, workFull) || Inside(workFull, attemptsFull))
            return Early(request, "error", "path-escape");
        var loaded = Load(request.AttemptsRoot, request.ProjectId, request.JobId, request.SnapshotId, request.ManifestSha256, request.BaseCommit);
        if (loaded.Status != "ok") return Early(request, loaded.Status, loaded.Reason, loaded);
        if (loaded.Checks.Where(check => check.Shell && !loaded.ShellChecks.Contains(check.Id, StringComparer.Ordinal)).Select(check => check.Id).ToArray() is { Length: > 0 } shells)
            return Record(request, loaded, null, "blocked", "needs-owner-review", shells, "", now);
        var outputs = ArtifactPaths(loaded.Checks);
        var pin = Pin(request.WorktreePath, request.TrackedRelativePaths, request.WorkingInputs, outputs);
        if (pin.Status != "pinned")
            return Record(request, loaded, pin, "blocked", pin.Reason, [], "", now);
        var gate = RunLocks.GetOrAdd(loaded.JobDirectory, _ => new SemaphoreSlim(1, 1));
        try { await gate.WaitAsync(request.Cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Record(request, loaded, pin, "blocked", "cancelled", [], "", now); }
        try
        {
            pin = Pin(request.WorktreePath, request.TrackedRelativePaths, request.WorkingInputs, outputs);
            if (pin.Status != "pinned")
                return Record(request, loaded, pin, "blocked", pin.Reason, [], "", now);
            var prior = Resumable(loaded.JobDirectory, request, pin);
            var execs = new List<Exec>();
            var log = new StringBuilder();
            foreach (var check in loaded.Checks)
            {
                if (request.Cancellation.IsCancellationRequested)
                {
                    execs.Add(new Exec { CheckId = check.Id, CheckDigest = check.Digest, Outcome = "cancelled", Started = now(), Ended = now() });
                    continue;
                }
                if (check.Trust != "candidate-code" && prior.TryGetValue(check.Id, out var resumed) && resumed.CheckDigest == check.Digest && TrustedBytesMatch(loaded.JobDirectory, check))
                {
                    execs.Add(resumed);
                    continue;
                }
                var exec = await Execute(request, loaded, check, now).ConfigureAwait(false);
                execs.Add(exec);
                if (exec.Outcome != "pass" && exec.Outcome != "exit-ok")
                    log.Append(check.Id).Append('\n').Append(ActivityEvidence.Redact(exec.Output)).Append('\n');
            }
            foreach (var check in loaded.Checks.Where(item => item.Trust == "candidate-code"))
            {
                var exec = execs.First(item => item.CheckId == check.Id);
                if (exec.Outcome != "exit-ok") continue;
                var assertion = execs.FirstOrDefault(item => item.CheckId == check.EngineOwnedAssertionId);
                exec.Outcome = assertion?.Outcome == "pass" ? "pass" : "assertion-failed";
                if (exec.Outcome != "pass")
                    log.Append(check.Id).Append('\n').Append(ActivityEvidence.Redact(exec.Output)).Append('\n');
            }
            var again = Pin(request.WorktreePath, request.TrackedRelativePaths, request.WorkingInputs, outputs);
            var changed = again.Status != "pinned" || again.TreeDigest != pin.TreeDigest || again.WorkingInputDigest != pin.WorkingInputDigest;
            if (again.Status == "pinned") pin.ArtifactDigest = again.ArtifactDigest;
            var (status, reason) = Rollup(execs, changed);
            var failing = execs.Where(item => item.Outcome != "pass").Select(item => item.CheckId).Distinct().ToArray();
            if (changed && status == "blocked" && reason == "changed-during-run")
                failing = loaded.CheckIds;
            var text = log.ToString();
            if (text.Length > MaxLogChars) text = "..." + text[^MaxLogChars..];
            return Record(request, loaded, pin, status, reason, failing, text, now, execs);
        }
        finally { gate.Release(); }
    }

    static bool HandsBack(string stage, string status, string reason) =>
        stage == "pre-review" && status is "blocked" or "error"
        && reason is not ("not-engine" or "needs-owner-review" or "repository-port-missing");

    static string[] ArtifactPaths(IEnumerable<Check> checks)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in checks)
        {
            if (!check.ExpectedResult.StartsWith("artifact-sha256:", StringComparison.Ordinal)) continue;
            if (!TryArtifact(check.ExpectedResult, out var relative, out _)) continue;
            if (seen.Add(relative)) list.Add(relative);
        }
        return list.ToArray();
    }

    static Outcome Early(Request request, string status, string reason, Loaded? loaded = null) =>
        new(status, reason, null, [], "",
            HandsBack(request.Stage, status, reason),
            false,
            request.Stage == "post-sync" && status != "pass",
            loaded?.ManifestSha256 ?? "",
            loaded?.CheckIds ?? [],
            "", "", "");

    static async Task<Exec> Execute(Request request, Loaded loaded, Check check, Func<long> now)
    {
        var exec = new Exec { CheckId = check.Id, CheckDigest = check.Digest, Started = now() };
        var attempts = Path.GetFullPath(request.AttemptsRoot);
        if (!ProgramPath(request, loaded, check, out var executable, out var problem))
        {
            exec.Outcome = problem;
            exec.Ended = now();
            return exec;
        }
        var cwd = check.WorkingDirectory == "."
            ? Path.GetFullPath(request.WorktreePath)
            : ResolveInside(request.WorktreePath, check.WorkingDirectory);
        if (cwd is null || !Directory.Exists(cwd) || !Inside(Path.GetFullPath(request.WorktreePath), cwd))
        {
            exec.Outcome = "path-escape";
            exec.Ended = now();
            return exec;
        }
        if (check.Arguments.Any(arg => arg.Contains(attempts, StringComparison.OrdinalIgnoreCase) || arg.Contains(loaded.JobDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            exec.Outcome = "path-escape";
            exec.Ended = now();
            return exec;
        }
        var output = "";
        var truncated = false;
        var done = false;
        int? exit = null;
        string? spawn = null;
        var sync = new object();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new ProcessEvents
        {
            Output = chunk =>
            {
                lock (sync)
                {
                    if (output.Length >= MaxOutputChars) { truncated = true; return; }
                    var room = MaxOutputChars - output.Length;
                    if (chunk.Length > room) { output += chunk[..room]; truncated = true; }
                    else output += chunk;
                }
            },
            Exit = code =>
            {
                lock (sync) { if (done) return; done = true; exit = code; }
                completion.TrySetResult(true);
            },
            Error = message =>
            {
                lock (sync) { if (done) return; done = true; spawn = message ?? "spawn failed"; }
                completion.TrySetResult(true);
            }
        };
        var proc = new ProcessRequest
        {
            Executable = executable,
            Arguments = check.Arguments,
            WorkingDirectory = cwd,
            Environment = RestrictedEnvironment(attempts),
            TimeoutMs = check.TimeoutSeconds * 1000,
            Shell = check.Shell,
            CheckId = check.Id
        };
        try { (request.Start ?? DefaultStart)(proc, events); }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            exec.Outcome = "spawn-error";
            exec.Output = error.Message;
            exec.Ended = now();
            return exec;
        }
        var pending = completion.Task;
        if (!pending.IsCompleted)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation);
            timeout.CancelAfter(check.TimeoutSeconds * 1000);
            var delay = Task.Delay(Timeout.Infinite, timeout.Token);
            var finished = await Task.WhenAny(pending, delay).ConfigureAwait(false);
            if (finished != pending)
            {
                try { await delay.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                try { events.KillTree(); } catch (Exception killError) when (killError is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { }
                string timed;
                lock (sync) timed = output;
                exec.Outcome = request.Cancellation.IsCancellationRequested ? "cancelled" : "timeout";
                exec.Output = timed;
                exec.OutputDigest = Sha256(Utf8.GetBytes(timed));
                exec.Ended = now();
                return exec;
            }
            timeout.Cancel();
            try { await delay.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        await pending.ConfigureAwait(false);
        string snapshot;
        bool cut;
        int? code;
        string? spawnMessage;
        lock (sync) { snapshot = output; cut = truncated; code = exit; spawnMessage = spawn; }
        exec.Output = snapshot;
        exec.OutputDigest = Sha256(Utf8.GetBytes(snapshot));
        exec.Ended = now();
        if (spawnMessage is not null) { exec.Outcome = "spawn-error"; exec.Output = snapshot + spawnMessage; return exec; }
        if (cut) { exec.Outcome = "output-limit"; return exec; }
        if (code is null) { exec.Outcome = "crashed"; return exec; }
        exec.ExitCode = code;
        if (code != check.ExpectedExitCode) { exec.Outcome = "nonzero-exit"; return exec; }
        if (check.Trust == "candidate-code") { exec.Outcome = "exit-ok"; return exec; }
        exec.Outcome = AssertionHolds(check, request.WorktreePath, snapshot) ? "pass" : "assertion-failed";
        return exec;
    }

    static bool ProgramPath(Request request, Loaded loaded, Check check, out string executable, out string problem)
    {
        executable = "";
        problem = "spawn-error";
        if (check.Trust == "candidate-code")
        {
            var path = ResolveInside(request.WorktreePath, check.Executable);
            if (path is null || IsReparse(request.WorktreePath, check.Executable)) { problem = "path-escape"; return false; }
            if (!File.Exists(path)) return false;
            executable = path;
            return true;
        }
        if (!TryRelative("check-store/" + check.Id + "/" + check.Executable, out var relative)) { problem = "path-escape"; return false; }
        var stored = ResolveInside(loaded.JobDirectory, relative);
        if (stored is null || IsReparse(loaded.JobDirectory, relative)) { problem = "path-escape"; return false; }
        if (!File.Exists(stored)) return false;
        if (!TryHash(stored, out var hash) || !hash.Equals(check.ProgramSha256, StringComparison.OrdinalIgnoreCase))
        {
            problem = "tamper";
            return false;
        }
        executable = stored;
        return true;
    }

    static bool TrustedBytesMatch(string jobDir, Check check)
    {
        if (check.Trust == "candidate-code") return true;
        if (!TryRelative("check-store/" + check.Id + "/" + check.Executable, out var relative)) return false;
        var stored = ResolveInside(jobDir, relative);
        return stored is not null && TryHash(stored, out var hash) && hash.Equals(check.ProgramSha256, StringComparison.OrdinalIgnoreCase);
    }

    static (string status, string reason) Rollup(List<Exec> execs, bool changed)
    {
        if (execs.Any(item => item.Outcome == "path-escape")) return ("error", "path-escape");
        if (execs.Any(item => item.Outcome == "spawn-error")) return ("error", "spawn-error");
        if (execs.Any(item => item.Outcome == "crashed")) return ("error", "crashed");
        if (execs.Any(item => item.Outcome == "cancelled")) return ("blocked", "cancelled");
        if (execs.Any(item => item.Outcome == "timeout")) return ("blocked", "timeout");
        if (changed) return ("blocked", "changed-during-run");
        if (execs.Any(item => item.Outcome == "tamper")) return ("blocked", "tamper");
        var failed = execs.FirstOrDefault(item => item.Outcome != "pass");
        return failed is null ? ("pass", "") : ("blocked", failed.Outcome);
    }

    static bool AssertionHolds(Check check, string worktree, string output)
    {
        if (!check.ExpectedResult.StartsWith("artifact-sha256:", StringComparison.Ordinal))
            return output.Contains(check.ExpectedResult, StringComparison.Ordinal);
        if (!TryArtifact(check.ExpectedResult, out var relative, out var want)) return false;
        if (IsReparse(worktree, relative)) return false;
        var file = ResolveInside(worktree, relative);
        if (file is null || !File.Exists(file) || !TryHash(file, out var hash)) return false;
        return hash.Equals(want, StringComparison.OrdinalIgnoreCase);
    }

    static Dictionary<string, string> RestrictedEnvironment(string attemptsRoot)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var env = new Dictionary<string, string>(comparer);
        foreach (var name in EnvAllow)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value)) continue;
            if (value.Contains(attemptsRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (value.Contains("ghp_", StringComparison.Ordinal) || value.Contains("github_pat_", StringComparison.Ordinal) || value.Contains("sk-", StringComparison.Ordinal)) continue;
            env[name] = value;
        }
        return env;
    }

    static Outcome Record(Request request, Loaded loaded, Pinned? pin, string status, string reason, string[] failing, string log, Func<long> now, List<Exec>? execs = null)
    {
        string? runId = null;
        try
        {
            var dir = Path.Combine(loaded.JobDirectory, "gate-runs");
            Directory.CreateDirectory(dir);
            runId = "r-" + now().ToString("x", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var obj = new JsonObject
            {
                ["summary"] = "Gate run " + request.Stage,
                ["runId"] = runId,
                ["jobId"] = request.JobId,
                ["projectId"] = request.ProjectId,
                ["stage"] = request.Stage,
                ["attemptId"] = request.AttemptId,
                ["snapshotId"] = request.SnapshotId,
                ["manifestSha256"] = loaded.ManifestSha256,
                ["baseCommit"] = loaded.BaseCommit,
                ["commitSha"] = request.CommitSha ?? "",
                ["treeSha"] = request.TreeSha ?? "",
                ["treeDigest"] = pin?.TreeDigest ?? "",
                ["workingInputDigest"] = pin?.WorkingInputDigest ?? "",
                ["artifactDigest"] = pin?.ArtifactDigest ?? "",
                ["worktreeId"] = request.WorktreeId,
                ["startedAt"] = now(),
                ["outcome"] = status,
                ["reason"] = reason,
                ["commands"] = Commands(execs)
            };
            obj["endedAt"] = now();
            var bytes = Utf8.GetBytes(obj.ToJsonString());
            var final = Path.Combine(dir, runId + ".json");
            var partial = final + ".partial";
            File.WriteAllBytes(partial, bytes);
            File.Move(partial, final);
            try { request.BeforeSeal?.Invoke(); }
            catch (Exception error) { throw new IOException(error.Message, error); }
            File.WriteAllText(Path.Combine(dir, runId + ".seal"), Sha256(File.ReadAllBytes(final)) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new Outcome("error", "evidence-failure", runId, failing, log, HandsBack(request.Stage, "error", "evidence-failure"), false, request.Stage == "post-sync", loaded.ManifestSha256, loaded.CheckIds, pin?.TreeDigest ?? "", pin?.WorkingInputDigest ?? "", pin?.ArtifactDigest ?? "");
        }
        var pass = status == "pass";
        return new Outcome(status, reason, runId, failing, log,
            HandsBack(request.Stage, status, reason),
            pass && request.Stage == "post-sync",
            request.Stage == "post-sync" && !pass,
            loaded.ManifestSha256, loaded.CheckIds, pin?.TreeDigest ?? "", pin?.WorkingInputDigest ?? "", pin?.ArtifactDigest ?? "");
    }

    static JsonArray Commands(List<Exec>? execs)
    {
        var list = new JsonArray();
        if (execs is null) return list;
        foreach (var exec in execs)
        {
            var row = new JsonObject
            {
                ["commandId"] = exec.CheckId,
                ["checkDigest"] = exec.CheckDigest,
                ["startedAt"] = exec.Started,
                ["endedAt"] = exec.Ended,
                ["outcome"] = exec.Outcome,
                ["exitCode"] = exec.ExitCode,
                ["outputDigest"] = exec.OutputDigest,
                ["outputTail"] = Tail(ActivityEvidence.Redact(exec.Output)),
                ["resumedFrom"] = exec.ResumedFrom ?? ""
            };
            list.Add((JsonNode)row);
        }
        return list;
    }

    static string Tail(string text) => text.Length > MaxLogChars ? "..." + text[^MaxLogChars..] : text;

    static Dictionary<string, Exec> Resumable(string jobDir, Request request, Pinned pin)
    {
        var found = new Dictionary<string, Exec>(StringComparer.Ordinal);
        var dir = Path.Combine(jobDir, "gate-runs");
        if (!Directory.Exists(dir)) return found;
        string[] files;
        try { files = Directory.EnumerateFiles(dir, "r-*.json").ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return found; }
        foreach (var file in files)
        {
            JsonObject run;
            try
            {
                if (!SealMatches(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject parsed) continue;
                run = parsed;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                continue;
            }
            if (!Bound(run, request, pin)) continue;
            var runId = Text(run, "runId") ?? "";
            if (run["commands"] is not JsonArray commands) continue;
            foreach (var node in commands)
            {
                if (node is not JsonObject row || Text(row, "outcome") != "pass") continue;
                var id = Text(row, "commandId");
                var digest = Text(row, "checkDigest");
                if (id is null || digest is null || !SafeId(id) || digest.Length == 0) continue;
                found[id] = new Exec
                {
                    CheckId = id,
                    CheckDigest = digest,
                    Outcome = "pass",
                    ExitCode = TryInt(row["exitCode"], out var exit) ? exit : null,
                    OutputDigest = Text(row, "outputDigest") ?? "",
                    Started = TryLong(row["startedAt"], out var started) ? started : 0,
                    Ended = TryLong(row["endedAt"], out var ended) ? ended : 0,
                    ResumedFrom = runId
                };
            }
        }
        return found;
    }

    static bool Bound(JsonObject run, Request request, Pinned pin) =>
        Text(run, "jobId") == request.JobId
        && Text(run, "stage") == request.Stage
        && Text(run, "attemptId") == request.AttemptId
        && Text(run, "snapshotId") == request.SnapshotId
        && string.Equals(Text(run, "manifestSha256"), request.ManifestSha256, StringComparison.OrdinalIgnoreCase)
        && Text(run, "worktreeId") == request.WorktreeId
        && Text(run, "treeDigest") == pin.TreeDigest
        && Text(run, "workingInputDigest") == pin.WorkingInputDigest
        && Text(run, "artifactDigest") == pin.ArtifactDigest
        && (request.CommitSha is null || Text(run, "commitSha") == request.CommitSha)
        && (request.TreeSha is null || Text(run, "treeSha") == request.TreeSha);

    public static bool IsAcceptedPass(string attemptsRoot, string projectId, string jobId, string runId, PassClaim claim)
    {
        if (!SafeId(projectId) || !SafeId(jobId) || runId is not ['r', '-', ..] || runId.Any(c => c is '/' or '\\' or '.')) return false;
        var jobDir = Path.GetFullPath(Path.Combine(Path.GetFullPath(attemptsRoot), "pipeline", projectId, "jobs", jobId));
        if (!Inside(Path.GetFullPath(attemptsRoot), jobDir)) return false;
        var file = Path.Combine(jobDir, "gate-runs", runId + ".json");
        if (!File.Exists(file) || !SealMatches(file)) return false;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject run) return false;
            if (Text(run, "outcome") != "pass" || Text(run, "runId") != runId) return false;
            if (Text(run, "jobId") != jobId || Text(run, "projectId") != projectId) return false;
            if (Text(run, "stage") != claim.Stage || Text(run, "attemptId") != claim.AttemptId) return false;
            if (Text(run, "snapshotId") != claim.SnapshotId) return false;
            if (!string.Equals(Text(run, "manifestSha256"), claim.ManifestSha256, StringComparison.OrdinalIgnoreCase)) return false;
            if (Text(run, "worktreeId") != claim.WorktreeId) return false;
            if (Text(run, "treeDigest") != claim.TreeDigest || Text(run, "workingInputDigest") != claim.WorkingInputDigest) return false;
            if ((Text(run, "artifactDigest") ?? "") != claim.ArtifactDigest) return false;
            if ((claim.CommitSha ?? "") != (Text(run, "commitSha") ?? "")) return false;
            if ((claim.TreeSha ?? "") != (Text(run, "treeSha") ?? "")) return false;
            if (run["commands"] is not JsonArray commands) return false;
            var passed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in commands)
            {
                if (node is JsonObject row && Text(row, "outcome") == "pass" && Text(row, "commandId") is { } id)
                    passed.Add(id);
            }
            return claim.CheckIds.Count > 0 && claim.CheckIds.All(passed.Contains);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    static bool SealMatches(string jsonFile)
    {
        var seal = jsonFile[..^".json".Length] + ".seal";
        try
        {
            if (!File.Exists(seal)) return false;
            var expect = File.ReadAllText(seal).Trim();
            return expect.Equals(Sha256(File.ReadAllBytes(jsonFile)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public sealed class Pinned
    {
        public required string Status { get; init; }
        public required string Reason { get; init; }
        public string TreeDigest { get; init; } = "";
        public string WorkingInputDigest { get; init; } = "";
        public string ArtifactDigest { get; set; } = "";
        public string[] Included { get; init; } = [];
    }

    // Pins the caller-supplied tracked paths plus engine-declared working inputs.
    // A null tracked list means the repository port is absent: stop, do not invent a pin.
    public static Pinned Pin(string worktree, IReadOnlyList<string>? tracked, IReadOnlyList<string>? workingInputs, IReadOnlyList<string>? outputs = null)
    {
        if (tracked is null) return Unsafe("repository-port-missing");
        var root = Path.GetFullPath(worktree);
        if (!Directory.Exists(root)) return Unsafe("unsafe-snapshot");
        var outputSet = new HashSet<string>(StringComparer.Ordinal);
        if (outputs is not null)
        {
            foreach (var relative in outputs)
            {
                if (!TryRelative(relative, out var safe)) return Unsafe("path-escape");
                if (IsPrivate(safe)) return Unsafe("private-input");
                outputSet.Add(safe);
            }
        }
        var rules = new IgnoreRules();
        rules.Read(Path.Combine(root, ".gitignore"));
        var exclude = Path.Combine(root, ".git", "info", "exclude");
        if (File.Exists(exclude)) rules.Read(exclude);
        if (rules.Unparsed) return Unsafe("ignore-unparsed");
        if (!TryListFiles(root, out var present, out var problem)) return Unsafe(problem);
        var tree = new List<(string path, string hash)>();
        var extras = new List<(string path, string hash)>();
        var included = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relative in tracked)
        {
            if (!TryTake(root, relative, out var hash, out var reason)) return Unsafe(reason);
            if (!included.Add(relative)) continue;
            tree.Add((relative, hash));
        }
        foreach (var relative in workingInputs ?? [])
        {
            if (included.Contains(relative)) continue;
            if (!TryTake(root, relative, out var hash, out var reason)) return Unsafe(reason);
            if (rules.Ignores(relative)) return Unsafe("ignored-input");
            included.Add(relative);
            extras.Add((relative, hash));
        }
        foreach (var control in new[] { ".gitignore", ".git/info/exclude" })
        {
            var file = ResolveInside(root, control);
            if (file is null || !File.Exists(file) || included.Contains(control)) continue;
            if (IsReparse(root, control)) return Unsafe("symlink");
            if (!TryHash(file, out var hash)) return Unsafe("unreadable");
            included.Add(control);
            tree.Add((control, hash));
        }
        foreach (var relative in present)
        {
            if (included.Contains(relative) || outputSet.Contains(relative) || IsPrivate(relative) || rules.Ignores(relative)) continue;
            return Unsafe("undeclared-input");
        }
        var artifacts = new List<(string path, string hash)>();
        foreach (var relative in outputSet.OrderBy(path => path, StringComparer.Ordinal))
        {
            if (IsReparse(root, relative)) return Unsafe("symlink");
            var file = ResolveInside(root, relative);
            if (file is null) return Unsafe("path-escape");
            if (!File.Exists(file)) { artifacts.Add((relative, "missing")); continue; }
            if (Directory.Exists(file)) return Unsafe("unsafe-snapshot");
            if (!TryHash(file, out var hash)) return Unsafe("unreadable");
            artifacts.Add((relative, hash));
        }
        return new Pinned
        {
            Status = "pinned",
            Reason = "",
            TreeDigest = DigestOf(tree),
            WorkingInputDigest = DigestOf(extras),
            ArtifactDigest = DigestOf(artifacts),
            Included = included.OrderBy(path => path, StringComparer.Ordinal).ToArray()
        };
    }

    static Pinned Unsafe(string reason) => new() { Status = "unsafe", Reason = reason };

    static bool TryTake(string root, string? relative, out string hash, out string reason)
    {
        hash = "";
        reason = "unsafe-snapshot";
        if (!TryRelative(relative, out var safe)) { reason = "path-escape"; return false; }
        if (IsPrivate(safe)) { reason = "private-input"; return false; }
        if (IsReparse(root, safe)) { reason = "symlink"; return false; }
        var file = ResolveInside(root, safe);
        if (file is null) { reason = "path-escape"; return false; }
        if (!File.Exists(file) || Directory.Exists(file)) { reason = "unsafe-snapshot"; return false; }
        if (!TryHash(file, out hash)) { reason = "unreadable"; return false; }
        return true;
    }

    static bool TryListFiles(string root, out List<string> relative, out string problem)
    {
        relative = new List<string>();
        problem = "";
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(dir).GetFileSystemInfos(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                problem = "unreadable";
                return false;
            }
            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    problem = "symlink";
                    return false;
                }
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    if (entry.Name is ".git" or ".planwharf") continue;
                    pending.Push(entry.FullName);
                    continue;
                }
                var rel = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
                relative.Add(rel);
            }
        }
        return true;
    }

    static bool IsReparse(string root, string relative)
    {
        var current = Path.GetFullPath(root);
        foreach (var part in relative.Split('/'))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) return false;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    static bool IsPrivate(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Any(part => part is ".git" or ".planwharf")) return true;
        var name = parts[^1];
        if (name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("credentials.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) || name.Equals("id_dsa", StringComparison.OrdinalIgnoreCase)) return true;
        return name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase);
    }

    static bool TryHash(string file, out string hash)
    {
        hash = "";
        try { hash = Sha256(File.ReadAllBytes(file)); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    static string DigestOf(List<(string path, string hash)> files)
    {
        var text = string.Join('\n', files.OrderBy(item => item.path, StringComparer.Ordinal).Select(item => item.path + "\0" + item.hash));
        return Sha256(Utf8.GetBytes(text));
    }

    public static void DefaultStart(ProcessRequest request, ProcessEvents events)
    {
        var process = new Process();
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.WorkingDirectory = request.WorkingDirectory;
        process.StartInfo.Environment.Clear();
        foreach (var pair in request.Environment) process.StartInfo.Environment[pair.Key] = pair.Value;
        if (request.Shell)
        {
            var command = Quote(request.Executable) + string.Concat(request.Arguments.Select(arg => " " + Quote(arg)));
            if (OperatingSystem.IsWindows())
            {
                var shell = Environment.GetEnvironmentVariable("ComSpec");
                process.StartInfo.FileName = string.IsNullOrEmpty(shell) ? "cmd.exe" : shell;
                process.StartInfo.ArgumentList.Add("/d");
                process.StartInfo.ArgumentList.Add("/s");
                process.StartInfo.ArgumentList.Add("/c");
                process.StartInfo.ArgumentList.Add(command);
            }
            else
            {
                process.StartInfo.FileName = "/bin/sh";
                process.StartInfo.ArgumentList.Add("-c");
                process.StartInfo.ArgumentList.Add(command);
            }
        }
        else
        {
            process.StartInfo.FileName = request.Executable;
            foreach (var arg in request.Arguments) process.StartInfo.ArgumentList.Add(arg);
        }
        process.EnableRaisingEvents = true;
        try
        {
            if (!process.Start())
            {
                events.Error("spawn failed");
                process.Dispose();
                return;
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            events.Error(error.Message);
            process.Dispose();
            return;
        }
        try { process.StandardInput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        var stdout = Pump(process.StandardOutput.BaseStream, events.Output);
        var stderr = Pump(process.StandardError.BaseStream, events.Output);
        var closed = 0;
        void OnExit(object? sender, EventArgs args)
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            try { Task.WaitAll(stdout, stderr); } catch (Exception error) when (error is AggregateException or ObjectDisposedException) { }
            try { events.Exit(process.ExitCode); }
            finally { process.Dispose(); }
        }
        events.KillTree = () =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or NotSupportedException) { }
        };
        process.Exited += OnExit;
        if (process.HasExited) OnExit(null, EventArgs.Empty);
    }

    static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    static async Task Pump(Stream stream, Action<string> onOutput)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0) return;
                onOutput(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe closed because the child exited or was killed.
        }
    }

    sealed class IgnoreRules
    {
        public bool Unparsed;
        readonly List<(string pattern, bool neg)> rules = new();

        public void Read(string path)
        {
            if (Unparsed || !File.Exists(path)) return;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { Unparsed = true; return; }
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var neg = line[0] == '!';
                if (neg) line = line[1..];
                if (line.Length == 0 || line.Contains("**") || line.Contains('[') || line.Contains('\\')) { Unparsed = true; return; }
                rules.Add((line, neg));
            }
        }

        public bool Ignores(string relative)
        {
            var ignored = false;
            foreach (var (pattern, neg) in rules)
                if (Match(pattern, relative)) ignored = !neg;
            return ignored;
        }

        static bool Match(string pattern, string relative)
        {
            var dirOnly = pattern.EndsWith('/');
            if (dirOnly) pattern = pattern[..^1];
            var rooted = pattern.StartsWith('/');
            if (rooted) pattern = pattern[1..];
            if (pattern.Contains('/'))
                return relative == pattern || relative.StartsWith(pattern + "/", StringComparison.Ordinal);
            if (rooted)
                return dirOnly
                    ? relative == pattern || relative.StartsWith(pattern + "/", StringComparison.Ordinal)
                    : Glob(pattern, relative);
            var parts = relative.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                if (!Glob(pattern, parts[i])) continue;
                if (dirOnly || i == parts.Length - 1) return true;
            }
            return false;
        }

        static bool Glob(string pattern, string name)
        {
            var pi = 0;
            var ni = 0;
            var star = -1;
            var mark = -1;
            while (ni < name.Length)
            {
                if (pi < pattern.Length && (pattern[pi] == '?' || pattern[pi] == name[ni])) { pi++; ni++; }
                else if (pi < pattern.Length && pattern[pi] == '*') { star = pi++; mark = ni; }
                else if (star >= 0) { pi = star + 1; ni = ++mark; }
                else return false;
            }
            while (pi < pattern.Length && pattern[pi] == '*') pi++;
            return pi == pattern.Length;
        }
    }
}
