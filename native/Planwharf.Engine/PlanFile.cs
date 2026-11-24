// Port of engine/plan-file.cjs.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public sealed record PlanStep(string Label = "", bool Done = false, string Commit = "", string Role = "", string Model = "", string Title = "", bool Reset = false, bool ExplicitUndone = false);
public sealed record PlanSpec(string Goal = "", string Base = "", string Branch = "", IReadOnlyList<PlanStep>? Steps = null, string? Done = null);
public sealed record ParsedPlan(string Goal, string Base, string Branch, string Rules, string Done, IReadOnlyList<PlanStep> Steps);
public sealed record PlanPrepareOptions(string Goal = "", string? Branch = null, IReadOnlyList<PlanStep>? Steps = null, bool KeepWorkPlan = false);
public sealed record PlanPrepareResult(bool Ok, bool Git = false, string? Branch = null, string? Base = null, bool Offline = false, string? Chat = null, string? Reason = null);
public sealed record PlanGitResult(bool Ok, string Stdout = "", string Stderr = "", string? Error = null);
public interface IPlanGit
{
    PlanGitResult Run(string folder, IReadOnlyList<string> args, int timeoutMs = 20000);
    PlanGitResult RunGh(string folder, IReadOnlyList<string> args, int timeoutMs = 20000) => new(false);
}

public sealed class ProcessPlanGit : IPlanGit
{
    public PlanGitResult Run(string folder, IReadOnlyList<string> args, int timeoutMs = 20000) => RunExecutable("git", folder, args, timeoutMs);
    public PlanGitResult RunGh(string folder, IReadOnlyList<string> args, int timeoutMs = 20000) => RunExecutable("gh", folder, args, timeoutMs);
    static PlanGitResult RunExecutable(string executable, string folder, IReadOnlyList<string> args, int timeoutMs)
    {
        try
        {
            var info = new ProcessStartInfo(executable) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
            info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            info.Environment["GIT_TERMINAL_PROMPT"] = "0";
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            if (process is null) return new(false, Error: "git did not start");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs)) { process.Kill(entireProcessTree: true); return new(false, Error: "git timed out"); }
            return new(process.ExitCode == 0, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception error) { return new(false, Error: error.Message); }
    }
}

public sealed class PlanFile(IPlanGit? git = null, Func<DateTimeOffset>? clock = null)
{
    public const string ExcludeLine = "/.planwharf/";
    public const string Rules = "No push or merge. Files outside the goal are not touched.";
    const string ExcludeMarker = "# Planwharf managed work plan";
    readonly IPlanGit _git = git ?? new ProcessPlanGit();
    readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    PlanGitResult Git(string folder, int timeout, params string[] args) => _git.Run(folder, args, timeout);
    PlanGitResult Git(string folder, params string[] args) => Git(folder, 20000, args);
    static string FirstLine(string text) => text.Split('\n')[0].TrimEnd('\r');
    public static string PlanPath(string folder) => Path.Combine(folder, ".planwharf", "plan.md");
    public static string Read(string folder) { try { return File.ReadAllText(PlanPath(folder)); } catch { return ""; } }
    public bool IsGit(string folder) => !string.IsNullOrEmpty(folder) && Git(folder, "rev-parse", "--is-inside-work-tree") is { Ok: true, Stdout: var output } && output.Trim() == "true";
    static string DirtyName(string line) { var body = line.Length > 3 ? line[3..] : ""; return body.Split(" -> ").Last().Trim() is { Length: > 0 } name ? name : line.Trim(); }
    public static bool IsNoise(string line)
    {
        var name = DirtyName(line).Replace('\\', '/');
        return Regex.IsMatch(name, @"(^|/)__pycache__(/|$)|(^|/)\.(pytest_cache|mypy_cache|ruff_cache)(/|$)|\.(pyc|pyo)$|(^|/)\.DS_Store$");
    }
    public static string DirtyMessage(IReadOnlyList<string> lines)
    {
        var names = lines.Select(DirtyName).Where(name => name.Length > 0).Take(8).ToArray();
        var more = lines.Count > names.Length ? $", and {lines.Count - names.Length} more" : "";
        return $"Commit or stash the changes in the folder first: {string.Join(", ", names)}{more}, then press Let the crew do it again.";
    }
    static string ShortSha(string? sha) => (sha ?? "")[..Math.Min((sha ?? "").Length, 7)];
    public static string ShortGoal(string? goal)
    {
        var slug = Regex.Replace((goal is { Length: > 0 } ? goal : "work").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "work" : slug[..Math.Min(slug.Length, 40)].TrimEnd('-');
    }
    public static string? NamedBranch(string? text)
    {
        var source = text ?? "";
        string[] patterns = [@"\bbranch\s+`([^`]+)`", @"\bbranch\s+'([^']+)'", "\\bbranch\\s+\"([^\"]+)\"", @"\b(?:on|onto)\s+`(?!origin/)([A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9._/-]*[A-Za-z0-9_-])`", @"\bbranch\s+(?:named|called)\s+([A-Za-z0-9][A-Za-z0-9._/-]*)", @"\b(?:on|to|onto|into|new|use|switch|checkout)\s+(?:the\s+|a\s+)?branch\s+([A-Za-z0-9][A-Za-z0-9._/-]*)", @"\bbranch\s+((?=[A-Za-z0-9._/-]*(?:[0-9]|[._/-][A-Za-z0-9]))[A-Za-z0-9][A-Za-z0-9._/-]*)"];
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(source, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var name = match.Groups[1].Value.Trim().TrimEnd('.', ',', ';', ':');
            if (Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._/-]*$") && !Regex.IsMatch(name, "^(from|the|a|an|on|name|named|called|is|that|this|and|or)$", RegexOptions.IgnoreCase)) return name;
        }
        return null;
    }
    public static string BranchName(string? goal, string? requested) => requested is not null && Regex.IsMatch(requested, "^[A-Za-z0-9][A-Za-z0-9._/-]*$") ? requested : NamedBranch(goal) ?? "planwharf/" + ShortGoal(goal);
    public static string PullRequestTitle(string? goal, IReadOnlyList<string>? stepTitles = null)
    {
        var first = Regex.Split(goal ?? "", "\\r?\\n")[0].Trim();
        var cut = Regex.Match(first, @"\s+(?:on|onto)\s+`|\s+from\s+(?:the\s+)?(?:refreshed\s+)?`|,\s*following\s", RegexOptions.IgnoreCase);
        var shortTitle = (cut.Success && cut.Index > 0 ? first[..cut.Index] : first).Replace("`", "").TrimEnd(' ', '.', ',', ';', ':').Trim();
        var fuller = (stepTitles ?? []).Select(text => text.Trim()).FirstOrDefault(text => shortTitle.Length > 0 && text.Length > shortTitle.Length && text.StartsWith(shortTitle, StringComparison.OrdinalIgnoreCase) && !text.Contains('`') && !text.Contains('\n'));
        var title = fuller ?? shortTitle;
        return title[..Math.Min(title.Length, 120)];
    }
    public sealed record CrewMember(string Role, string Name);
    public sealed record CrewSubTask(string Id, string Title, string AssignedModelId);
    public sealed record CrewResult(string SubTaskId, string ModelId);
    public sealed record CrewModel(string Id, string Label);
    public sealed record CrewRow(string LeadLabel = "", IReadOnlyList<CrewMember>? Crew = null, IReadOnlyList<CrewSubTask>? SubTasks = null, IReadOnlyList<CrewResult>? Results = null);
    public static string CrewSection(CrewRow? row = null, IReadOnlyList<CrewModel>? models = null)
    {
        row ??= new();
        string Label(string? id) => models?.FirstOrDefault(model => model.Id == id)?.Label ?? id ?? "";
        var lead = row.LeadLabel.Length > 0 ? row.LeadLabel : row.Crew?.FirstOrDefault(member => member.Role == "Orchestrator")?.Name ?? "";
        var lines = new List<string>();
        if (lead.Length > 0) lines.Add("- Lead: " + lead);
        foreach (var sub in row.SubTasks ?? [])
        {
            var who = new List<string>();
            foreach (var result in row.Results ?? [])
            {
                var label = Label(result.ModelId);
                if (result.SubTaskId == sub.Id && result.ModelId.Length > 0 && !who.Contains(label)) who.Add(label);
            }
            var last = Label(sub.AssignedModelId);
            if (last.Length > 0 && (who.Count == 0 || who[^1] != last)) who.Add(last);
            if (who.Count == 0) continue;
            var role = Regex.IsMatch(sub.Title, "^Verify the work independently", RegexOptions.IgnoreCase) ? "Verify" : "Build";
            var title = sub.Title.Split('\n')[0];
            lines.Add($"- {role}: {string.Join(" → ", who)}{(role == "Build" && title.Length > 0 ? " (" + title[..Math.Min(title.Length, 80)] + ")" : "")}");
        }
        return lines.Count > 0 ? "## Crew\n\n" + string.Join("\n", lines) : "";
    }
    public static string StepLabel(PlanStep step, IReadOnlyList<PlanStep>? all)
    {
        var role = step.Role.Length > 0 ? step.Role : "Build";
        var model = step.Model.Length > 0 ? step.Model : "unassigned";
        var twins = (all ?? []).Count(other => (other.Role.Length > 0 ? other.Role : "Build") == role && (other.Model.Length > 0 ? other.Model : "unassigned") == model);
        var title = twins > 1 && step.Title.Length > 0 ? ": " + Regex.Replace(step.Title, @"\s+", " ").Trim()[..Math.Min(Regex.Replace(step.Title, @"\s+", " ").Trim().Length, 80)] : "";
        return $"{role} ({model}){title}";
    }
    public static string Block(string? text) => Regex.Replace((text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim(), @"(?m)^(#+\s)", @"\$1");
    public static ParsedPlan Parse(string? text)
    {
        var sections = new Dictionary<string, List<string>>();
        var name = "";
        foreach (var line in Regex.Split(text ?? "", "\\r?\\n"))
        {
            var heading = Regex.Match(line, @"^##\s+(.+?)\s*$");
            if (heading.Success) { name = heading.Groups[1].Value.Trim().ToLowerInvariant(); sections[name] = []; continue; }
            if (name.Length > 0) sections[name].Add(line);
        }
        string Body(string key) => sections.TryGetValue(key, out var lines) ? string.Join("\n", lines).Trim() : "";
        var steps = sections.GetValueOrDefault("steps")?.Where(line => Regex.IsMatch(line, @"^- \[[ xX]\] ")).Select(line =>
        {
            var done = Regex.IsMatch(line, @"^- \[[xX]\]");
            return new PlanStep(line, done, done ? line.Split(" — ").Last().Trim() : "");
        }).ToArray() ?? [];
        return new(Body("goal"), Body("base"), Body("branch"), Body("rules"), Body("done"), steps);
    }
    public static string Render(PlanSpec spec)
    {
        var steps = (spec.Steps ?? []).Select(step => step.Label.StartsWith("- [", StringComparison.Ordinal) ? step.Label : $"- [{(step.Done ? "x" : " ")}] {(step.Label.Length > 0 ? step.Label : StepLabel(step, spec.Steps))}{(step.Done ? " — " + (step.Commit.Length > 0 ? step.Commit : "not committed") : "")}").ToArray();
        string[] lines = ["# Work plan", "", "## Goal", "", Block(spec.Goal), "", "## Base", "", Block(spec.Base), "", "## Branch", "", Block(spec.Branch), "", "## Steps", "", ..(steps.Length > 0 ? steps : ["- [ ] Build (unassigned)"]), "", "## Rules", "", Rules, "", "## Done", "", Block(spec.Done), "", ""];
        return string.Join("\n", lines);
    }

    public string? Archive(string folder, string? goal)
    {
        var file = PlanPath(folder);
        if (!File.Exists(file)) return null;
        var dir = Path.Combine(folder, ".planwharf", "done");
        Directory.CreateDirectory(dir);
        var basename = $"{_clock().UtcDateTime:yyyy-MM-dd}-{ShortGoal(goal)}";
        var destination = Path.Combine(dir, basename + ".md");
        for (var number = 2; File.Exists(destination); number++) destination = Path.Combine(dir, $"{basename}-{number}.md");
        File.Copy(file, destination);
        return destination;
    }
    static string ParsedLabel(PlanStep step) => Regex.Replace(Regex.Replace(step.Label, @"^- \[[ xX]\] ", ""), " — .*", "");
    public string WritePlan(string folder, PlanSpec spec)
    {
        var previous = Parse(Read(folder));
        var goal = Block(spec.Goal);
        var same = previous.Goal.Length > 0 && previous.Goal == goal;
        // finish() already archived a finished plan; a new goal archives unfinished work only.
        if (previous.Goal.Length > 0 && !same && previous.Done.Length == 0) Archive(folder, previous.Goal);
        IReadOnlyList<PlanStep> steps;
        if (spec.Steps is { Count: > 0 })
        {
            steps = spec.Steps.Select(step =>
            {
                var label = step.Label.Length > 0 ? step.Label : StepLabel(step, spec.Steps);
                var kept = same && !step.Reset && !step.ExplicitUndone ? previous.Steps.FirstOrDefault(old => old.Done && old.Label.Contains(label, StringComparison.Ordinal)) : null;
                return kept is not null ? new PlanStep(label, true, kept.Commit) : step with { Label = label, Commit = step.Commit };
            }).ToArray();
        }
        else steps = same ? previous.Steps.Select(step => new PlanStep(ParsedLabel(step), step.Done, step.Commit)).ToArray() : [];
        Directory.CreateDirectory(Path.GetDirectoryName(PlanPath(folder))!);
        var rendered = Render(new(goal, spec.Base.Length > 0 ? spec.Base : previous.Base, spec.Branch.Length > 0 ? spec.Branch : previous.Branch, steps, spec.Done ?? (same ? previous.Done : "")));
        File.WriteAllText(PlanPath(folder), rendered);
        return rendered;
    }
    public bool TickStep(string folder, PlanStep spec)
    {
        var file = PlanPath(folder);
        if (!File.Exists(file)) return false;
        var lines = Regex.Split(File.ReadAllText(file), "\\r?\\n");
        var title = spec.Title.Length > 0 ? Regex.Replace(spec.Title, @"\s+", " ").Trim() : "";
        title = title[..Math.Min(title.Length, 80)];
        var index = title.Length > 0 ? Array.FindIndex(lines, line => line.StartsWith("- [ ]", StringComparison.Ordinal) && line.Contains(title, StringComparison.Ordinal)) : -1;
        if (index < 0 && spec.Role.Length > 0) index = Array.FindIndex(lines, line => line.StartsWith("- [ ]", StringComparison.Ordinal) && line.Contains($"{spec.Role} ({spec.Model}", StringComparison.Ordinal));
        if (index < 0) index = Array.FindIndex(lines, line => line.StartsWith("- [ ]", StringComparison.Ordinal));
        if (index < 0) return false;
        lines[index] = lines[index].Replace("- [ ]", "- [x]", StringComparison.Ordinal) + " — " + (spec.Commit.Length > 0 ? spec.Commit : "not committed");
        File.WriteAllText(file, string.Join("\n", lines));
        return true;
    }
    public bool CurrentBranchExists(string folder, string name) => Git(folder, "show-ref", "--verify", "--quiet", "refs/heads/" + name).Ok;
    public string CurrentBranch(string folder) => IsGit(folder) ? Git(folder, "branch", "--show-current").Stdout.Trim() : "";
    public bool Pushed(string folder, string? branch = null)
    {
        var name = branch ?? CurrentBranch(folder);
        if (name.Length == 0 || !IsGit(folder)) return false;
        var remote = Git(folder, "rev-parse", "origin/" + name);
        var head = Git(folder, "rev-parse", "HEAD");
        return remote.Ok && head.Ok && remote.Stdout.Trim() == head.Stdout.Trim();
    }
    string[] DirtyLines(string folder, bool allUntracked = false)
    {
        var result = allUntracked ? Git(folder, "status", "--porcelain", "--untracked-files=all") : Git(folder, "status", "--porcelain");
        return result.Ok ? result.Stdout.Split('\n').Select(line => line.TrimEnd('\r', '\n')).Where(line => line.Length > 0).ToArray() : [];
    }
    public string CommitNote(string folder)
    {
        if (!IsGit(folder) || DirtyLines(folder).Length > 0) return "not committed";
        var head = Git(folder, "rev-parse", "HEAD");
        if (!head.Ok) return "not committed";
        var full = head.Stdout.Trim();
        var match = Regex.Match(Parse(Read(folder)).Base, @"\b([0-9a-f]{7,40})\b", RegexOptions.IgnoreCase);
        var recorded = match.Success ? match.Groups[1].Value : "";
        if (recorded.Length > 0 && (full.StartsWith(recorded, StringComparison.OrdinalIgnoreCase) || recorded.StartsWith(full[..Math.Min(full.Length, recorded.Length)], StringComparison.OrdinalIgnoreCase))) return "not committed";
        var shortHead = Git(folder, "rev-parse", "--short=7", "HEAD");
        return shortHead.Ok ? shortHead.Stdout.Trim() : "not committed";
    }
    public bool Finish(string folder, string? summary = null, string? branch = null, bool? pushed = null)
    {
        if (!File.Exists(PlanPath(folder))) return false;
        var parsed = Parse(Read(folder));
        var chosenBranch = branch ?? (parsed.Branch.Length > 0 ? parsed.Branch : CurrentBranch(folder));
        if (chosenBranch.Length == 0) chosenBranch = "(none)";
        var sent = pushed ?? Pushed(folder, chosenBranch);
        var lines = new List<string>();
        if (parsed.Goal.Length > 0) lines.Add("Goal: " + parsed.Goal);
        if (!string.IsNullOrWhiteSpace(summary)) lines.Add("Summary: " + summary.Trim());
        lines.Add("Branch: " + chosenBranch);
        lines.Add("Pushed: " + (sent ? "yes" : "no"));
        WritePlan(folder, new(parsed.Goal, parsed.Base, chosenBranch, parsed.Steps.Select(step => new PlanStep(ParsedLabel(step), step.Done, step.Commit)).ToArray(), string.Join("\n", lines)));
        Archive(folder, parsed.Goal);
        return true;
    }
    public void EnsureExclude(string folder, bool keepWorkPlan = false)
    {
        var located = Git(folder, "rev-parse", "--git-path", "info/exclude");
        if (!located.Ok || located.Stdout.Trim().Length == 0) return;
        var raw = located.Stdout.Trim();
        var file = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(folder, raw));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var current = File.Exists(file) ? File.ReadAllText(file) : "";
        if (keepWorkPlan)
        {
            var next = Regex.Replace(current, "(?m)^" + Regex.Escape(ExcludeMarker) + "\\r?\\n/\\.planwharf/\\r?\\n", "");
            if (next != current) File.WriteAllText(file, next);
            return;
        }
        if (Regex.Split(current, "\\r?\\n").Any(line => line.Trim() == ExcludeLine)) return;
        var suffix = current.Length > 0 && !current.EndsWith('\n') ? "\n" : "";
        File.WriteAllText(file, current + suffix + ExcludeMarker + "\n" + ExcludeLine + "\n");
    }
    string? PullRequestBranch(string folder, string text)
    {
        var match = Regex.Match(text, @"\bpull request #(\d+)\b", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        // The PR lookup is optional. A failed lookup falls through to normal branch naming.
        var result = _git.RunGh(folder, ["pr", "view", match.Groups[1].Value, "--json", "headRefName", "--jq", ".headRefName"]);
        var name = result.Ok ? result.Stdout.Trim() : "";
        return Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._/-]*$") ? name : null;
    }
    string? LeadingBranch(string folder, string text)
    {
        var match = Regex.Match(text, @"^\s*on\s+([A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9._/-]*[A-Za-z0-9_-])\s*[,:]", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var name = match.Groups[1].Value;
        return CurrentBranchExists(folder, name) || Git(folder, "show-ref", "--verify", "--quiet", "refs/remotes/origin/" + name).Ok ? name : null;
    }
    string? RemoteDefault(string folder)
    {
        var symbolic = Git(folder, "symbolic-ref", "--short", "refs/remotes/origin/HEAD");
        if (symbolic.Ok && symbolic.Stdout.Trim().Length > 0) return symbolic.Stdout.Trim();
        var listed = Git(folder, 30000, "ls-remote", "--symref", "origin", "HEAD");
        var match = listed.Ok ? Regex.Match(listed.Stdout, @"ref:\s+refs/heads/(\S+)\s+HEAD") : Match.Empty;
        if (match.Success) return "origin/" + match.Groups[1].Value;
        if (Git(folder, "rev-parse", "--verify", "--quiet", "origin/main").Ok) return "origin/main";
        if (Git(folder, "rev-parse", "--verify", "--quiet", "origin/master").Ok) return "origin/master";
        return null;
    }
    PlanPrepareResult SwitchTo(string folder, string name, string? startPoint)
    {
        if (CurrentBranchExists(folder, name))
        {
            if (Git(folder, "branch", "--show-current").Stdout.Trim() == name) return new(true);
            var switched = Git(folder, "switch", name);
            return switched.Ok ? new(true) : new(false, Reason: $"Could not switch to {name}: {FirstLine(switched.Stderr.Length > 0 ? switched.Stderr : switched.Error ?? "git switch failed")}");
        }
        var args = startPoint is null ? new[] { "switch", "-c", name } : new[] { "switch", "--no-track", "-c", name, startPoint };
        var made = Git(folder, args);
        return made.Ok ? new(true) : new(false, Reason: $"Could not create {name}: {FirstLine(made.Stderr.Length > 0 ? made.Stderr : made.Error ?? "git switch failed")}");
    }
    public PlanPrepareResult Prepare(string folder, PlanPrepareOptions options)
    {
        if (!IsGit(folder))
        {
            var goal = options.Goal.Trim();
            if (goal.Length > 0 || options.Steps is { Count: > 0 }) WritePlan(folder, new(goal, "Not a git repository.", "(none)", options.Steps, ""));
            return new(true, Git: false);
        }
        if (!options.KeepWorkPlan) EnsureExclude(folder);
        var goalText = options.Goal.Trim();
        var asked = options.Branch is not null && Regex.IsMatch(options.Branch, "^[A-Za-z0-9][A-Za-z0-9._/-]*$") ? options.Branch : null;
        var name = asked ?? NamedBranch(goalText) ?? PullRequestBranch(folder, goalText) ?? LeadingBranch(folder, goalText) ?? BranchName(goalText, null);
        var dirty = DirtyLines(folder, options.KeepWorkPlan).Where(line => !IsNoise(line)).ToList();
        if (options.KeepWorkPlan)
        {
            var previous = Parse(Read(folder));
            if (previous.Goal.Length > 0 && previous.Goal == Block(options.Goal) && previous.Branch == CurrentBranch(folder) && previous.Done.Length == 0)
                dirty.RemoveAll(line => line == "?? .planwharf/plan.md");
        }
        var setAside = "";
        if (dirty.Count > 0 && CurrentBranch(folder) != name)
        {
            var names = dirty.Select(DirtyName).Where(value => value.Length > 0).Take(8);
            var stashed = Git(folder, "stash", "push", "-u", "-m", "Planwharf set these aside to start " + name);
            if (!stashed.Ok) return new(false, Reason: DirtyMessage(dirty));
            setAside = $"Set aside uncommitted changes so the crew could start: {string.Join(", ", names)}. They are in the git stash.";
        }
        if (options.KeepWorkPlan) EnsureExclude(folder, true);
        var fetched = Git(folder, 30000, "fetch", "origin");
        string baseText, baseSha;
        string? startPoint = null, chat = null;
        if (!fetched.Ok)
        {
            var head = Git(folder, "rev-parse", "HEAD");
            baseSha = head.Ok ? head.Stdout.Trim() : "";
            var shown = ShortSha(baseSha);
            baseText = $"Offline: the remote could not be read, so this work starts on the current commit {shown}.";
            chat = $"The remote could not be read, so the crew starts on the current commit {shown}.";
        }
        else
        {
            var remote = RemoteDefault(folder);
            if (remote is null) return new(false, Reason: "The remote has no default branch. Set origin/HEAD, then press Let the crew do it again.");
            var sha = Git(folder, "rev-parse", remote);
            if (!sha.Ok) return new(false, Reason: $"Could not read {remote}. Fetch the remote, then press Let the crew do it again.");
            baseSha = sha.Stdout.Trim();
            baseText = $"{remote} {ShortSha(baseSha)}";
            startPoint = remote;
        }
        var remoteOnly = !CurrentBranchExists(folder, name) && Git(folder, "show-ref", "--verify", "--quiet", "refs/remotes/origin/" + name).Ok;
        var existed = CurrentBranchExists(folder, name) || remoteOnly;
        var switchedResult = SwitchTo(folder, name, remoteOnly ? "origin/" + name : existed ? null : startPoint);
        if (!switchedResult.Ok) return switchedResult;
        if (existed && startPoint is not null)
        {
            var since = Git(folder, "merge-base", "HEAD", startPoint);
            if (!since.Ok || since.Stdout.Trim().Length == 0) return new(false, Reason: $"Branch {name} has no history in common with {startPoint}. Delete the branch and press Let the crew do it again.");
            baseSha = since.Stdout.Trim();
            baseText = $"{startPoint} {ShortSha(baseSha)}";
        }
        if (baseSha.Length > 0 && !Git(folder, "merge-base", "--is-ancestor", baseSha, "HEAD").Ok)
            return new(false, Reason: $"Branch {name} does not contain {baseText}. Start again from that commit, or delete the branch and press Let the crew do it again.");
        var current = Git(folder, "branch", "--show-current").Stdout.Trim();
        if (current.Length == 0) current = name;
        WritePlan(folder, new(goalText, baseText, current, options.Steps ?? []));
        if (setAside.Length > 0) chat = chat is null ? setAside : setAside + " " + chat;
        return new(true, true, current, baseText, chat is not null, chat);
    }
}
