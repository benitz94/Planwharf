// Port of engine/pr-check.cjs.
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public sealed record PrCommandResult(int Status, string Stdout = "", string Stderr = "");
public delegate Task<PrCommandResult> PrCommand(string folder, string command, string[] arguments);

public sealed class PrCheck(PrCommand? command = null)
{
    readonly PrCommand run = command ?? RunProcess;
    static readonly string[] Red = ["FAILURE", "TIMED_OUT", "CANCELLED", "ACTION_REQUIRED", "STARTUP_FAILURE", "ERROR"];
    static readonly string[] NotRun = ["CANCELLED", "STARTUP_FAILURE"];

    // Git and gh are deliberately invoked with discrete arguments and an explicit working folder.
    static async Task<PrCommandResult> RunProcess(string folder, string command, string[] arguments)
    {
        var start = new ProcessStartInfo(command) { WorkingDirectory = folder, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Match ProjectWorkspace.gitEnvironment: repair commands run outside a worker's Git overrides.
        foreach (var key in start.Environment.Keys.Cast<string>().Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        try
        {
            using var process = new Process { StartInfo = start };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new(1, "", error.Message); }
    }

    static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); } catch { return null; }
    }
    static string Value(JsonNode? node) => node is JsonValue value ? value.ToString() : "";
    static string Field(JsonNode? node, string key) => node is JsonObject obj ? Value(obj[key]) : "";
    static string Conclusion(JsonNode? check)
    {
        var conclusion = Field(check, "conclusion");
        return (conclusion.Length == 0 ? Field(check, "state") : conclusion).ToUpperInvariant();
    }
    static IEnumerable<JsonNode?> Checks(JsonNode? rollup) => rollup is JsonArray array ? array : [];
    public static string[] FailingNames(JsonNode? rollup) => Checks(rollup)
        .Where(check => Red.Contains(Conclusion(check)) && !NotRun.Contains(Conclusion(check)))
        .Select(check => { var name = Field(check, "name"); return (name.Length == 0 ? Field(check, "context") : name).Trim(); })
        .Where(name => name.Length > 0).ToArray();
    public static JsonArray NotRunChecks(JsonNode? rollup)
    {
        var result = new JsonArray();
        foreach (var check in Checks(rollup).Where(check => NotRun.Contains(Conclusion(check))))
        {
            var name = Field(check, "name");
            name = (name.Length == 0 ? Field(check, "context") : name).Trim();
            if (name.Length == 0) continue;
            var url = Field(check, "detailsUrl");
            if (url.Length == 0) url = Field(check, "targetUrl");
            var match = Regex.Match(url, @"/actions/runs/(\d+)", RegexOptions.ECMAScript);
            result.Add((JsonNode)new JsonObject { ["name"] = name, ["runId"] = match.Success ? match.Groups[1].Value : null });
        }
        return result;
    }
    static JsonObject Error(string reason) => new() { ["ok"] = false, ["reason"] = reason };
    static string FirstLine(string text) => Regex.Split(text.Trim(), "\r?\n")[0];
    static JsonArray Lines(string text)
    {
        var result = new JsonArray();
        foreach (var line in Regex.Split(text, "\r?\n").Where(line => line.Length > 0)) result.Add((JsonNode?)JsonValue.Create(line));
        return result;
    }
    static JsonArray Strings(IEnumerable<string> values) { var result = new JsonArray(); foreach (var value in values) result.Add((JsonNode?)JsonValue.Create(value)); return result; }
    Task<PrCommandResult> Run(string folder, string command, params string[] args) => run(folder, command, args);

    public async Task<JsonObject> ListPrs(string folder)
    {
        var listed = await Run(folder, "gh", "pr", "list", "--state", "open", "--json", "number,title,mergeable,headRefName,statusCheckRollup,url");
        if (listed.Status != 0) return Error(FirstLine(listed.Stderr.Length > 0 ? listed.Stderr : "Could not read pull requests."));
        var prs = Parse(listed.Stdout.Length > 0 ? listed.Stdout : "[]");
        if (prs is not JsonArray array) return Error("Pull request list was not readable.");
        var mapped = new JsonArray();
        foreach (var pr in array)
        {
            if (pr is not JsonObject item) return Error("Pull request list was not readable.");
            mapped.Add((JsonNode)new JsonObject { ["number"] = item["number"]?.DeepClone(), ["title"] = item["title"]?.DeepClone(),
                ["branch"] = item["headRefName"]?.DeepClone(), ["url"] = item["url"]?.DeepClone(),
                ["conflicts"] = Field(pr, "mergeable") == "CONFLICTING", ["failing"] = Strings(FailingNames(item["statusCheckRollup"])) });
        }
        return new() { ["ok"] = true, ["prs"] = mapped };
    }

    public async Task<JsonObject> ResolveConflicts(string folder, int number)
    {
        var view = await Run(folder, "gh", "pr", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "headRefName,baseRefName,mergeable");
        if (view.Status != 0) return Error("Pull request not found.");
        var pr = Parse(view.Stdout);
        if (pr is not JsonObject) return Error("Pull request was not readable.");
        if (Field(pr, "mergeable") != "CONFLICTING") return new() { ["ok"] = true, ["changed"] = false, ["reason"] = "No conflicts." };
        var checkout = await Run(folder, "gh", "pr", "checkout", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (checkout.Status != 0) return Error("Could not check out the pull request.");
        await Run(folder, "git", "fetch", "origin", Field(pr, "baseRefName"));
        var merged = await Run(folder, "git", "merge", "origin/" + Field(pr, "baseRefName"), "--no-edit");
        if (merged.Status != 0)
        {
            await Run(folder, "git", "merge", "--abort");
            // Source asks for conflicts after abort; Git may already have cleared this list.
            var files = await Run(folder, "git", "diff", "--name-only", "--diff-filter=U");
            return new() { ["ok"] = false, ["reason"] = "Conflicts need a decision.", ["files"] = Lines(files.Stdout) };
        }
        var pushed = await Run(folder, "git", "push");
        return pushed.Status == 0
            ? new() { ["ok"] = true, ["changed"] = true, ["reason"] = "Merged the base branch and pushed." }
            : Error("Merged, but the push failed. The commit is saved locally.");
    }

    public async Task<JsonObject> PrepareFix(string folder, int number, string worktree)
    {
        var view = await Run(folder, "gh", "pr", "view", number.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "headRefName,baseRefName,mergeable,statusCheckRollup,state");
        if (view.Status != 0) return Error("Pull request not found.");
        var pr = Parse(view.Stdout);
        if (pr is not JsonObject) return Error("Pull request was not readable.");
        var state = Field(pr, "state");
        if (state.Length > 0 && state.ToUpperInvariant() != "OPEN") return Error("The pull request is not open.");
        var branch = Field(pr, "headRefName");
        var baseBranch = Field(pr, "baseRefName");
        if (baseBranch.Length == 0) baseBranch = "main";
        static bool Valid(string text) => Regex.IsMatch(text, @"^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.ECMAScript);
        if (!Valid(branch) || !Valid(baseBranch)) return Error("The pull request branch has an unusual name.");
        var prepared = await PrepareWorktree(folder, branch, baseBranch, FailingNames(((JsonObject)pr)["statusCheckRollup"]), worktree);
        if (prepared["ok"]?.ToString() == "true") prepared["notRun"] = NotRunChecks(((JsonObject)pr)["statusCheckRollup"]);
        return prepared;
    }

    public async Task<JsonObject> PrepareWorktree(string folder, string branch, string baseBranch, IEnumerable<string> failing, string worktree)
    {
        var fetched = await Run(folder, "git", "fetch", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}", $"+refs/heads/{baseBranch}:refs/remotes/origin/{baseBranch}");
        if (fetched.Status != 0) return Error("Could not fetch the pull request branch.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(worktree))!);
        var added = await Run(folder, "git", "worktree", "add", "--detach", worktree, "origin/" + branch);
        if (added.Status != 0) return Error("Could not make a copy of the folder for the fix.");
        var before = (await Run(worktree, "git", "rev-parse", "HEAD")).Stdout.Trim();
        var merged = await Run(worktree, "git", "merge", "origin/" + baseBranch, "--no-edit");
        var conflicts = merged.Status == 0 ? new JsonArray() : Lines((await Run(worktree, "git", "diff", "--name-only", "--diff-filter=U")).Stdout);
        if (merged.Status != 0 && conflicts.Count == 0) { await RemoveWorktree(folder, worktree); return Error("The base branch could not be merged."); }
        return new() { ["ok"] = true, ["branch"] = branch, ["base"] = baseBranch, ["before"] = before,
            ["conflicts"] = conflicts, ["failing"] = Strings(failing) };
    }

    public async Task<JsonObject> FinishFix(string folder, string worktree, JsonObject fix, int number)
    {
        var conflicts = fix["conflicts"] is JsonArray array ? array.Select(Value).ToArray() : [];
        var unmerged = Lines((await Run(worktree, "git", "diff", "--name-only", "--diff-filter=U")).Stdout);
        if (unmerged.Count > 0) return Error("Conflicts are still open in: " + string.Join(", ", unmerged.Select(Value)) + ".");
        var markerArguments = new[] { "grep", "-l", "-E", "^(<<<<<<< |>>>>>>> )", "--" }.Concat(conflicts.Length > 0 ? conflicts : ["."]).ToArray();
        var markers = await Run(worktree, "git", markerArguments);
        if (markers.Status == 0 && markers.Stdout.Trim().Length > 0)
            return Error("Conflict markers are left in: " + string.Join(", ", Regex.Split(markers.Stdout.Trim(), "\r?\n")) + ".");
        var gitDir = (await Run(worktree, "git", "rev-parse", "--git-dir")).Stdout.Trim();
        if (gitDir.Length > 0 && File.Exists(Path.GetFullPath(Path.Combine(worktree, gitDir, "MERGE_HEAD"))))
        {
            await Run(worktree, "git", "add", "-A");
            var committed = await Run(worktree, "git", "commit", "--no-edit");
            if (committed.Status != 0) return Error("The merge could not be committed.");
        }
        if ((await Run(worktree, "git", "status", "--porcelain")).Stdout.Trim().Length > 0)
        {
            await Run(worktree, "git", "add", "-A");
            var committed = await Run(worktree, "git", "commit", "-m", $"Fix pull request #{number}");
            if (committed.Status != 0) return Error("The fix could not be committed.");
        }
        var head = (await Run(worktree, "git", "rev-parse", "HEAD")).Stdout.Trim();
        if (head == Field(fix, "before")) return new() { ["ok"] = true, ["changed"] = false, ["reason"] = "Nothing needed changing." };
        // No force push: a concurrent update refuses the push and leaves the local commit intact.
        var branch = Field(fix, "branch");
        var pushed = await Run(worktree, "git", "push", "origin", "HEAD:refs/heads/" + branch);
        if (pushed.Status != 0)
        {
            var why = Regex.Split(pushed.Stderr.Trim(), "\r?\n").Where(line => line.Length > 0).LastOrDefault() ?? "the remote refused it";
            return new() { ["ok"] = false, ["kept"] = true, ["reason"] = "The push was refused: " + why + " The commit is saved locally." };
        }
        return new() { ["ok"] = true, ["changed"] = true, ["reason"] = "Pushed to " + branch + "." };
    }

    public async Task RemoveWorktree(string folder, string worktree)
    {
        await Run(folder, "git", "worktree", "remove", "--force", worktree);
        await Run(folder, "git", "worktree", "prune");
    }

    public async Task<JsonObject> RerunNotRun(string folder, JsonNode? notRun)
    {
        var runs = Checks(notRun).Select(check => Field(check, "runId")).Where(id => id.Length > 0).Distinct().ToArray();
        var done = 0;
        foreach (var runId in runs)
            if ((await Run(folder, "gh", "run", "rerun", runId, "--failed")).Status == 0) done++;
        return new() { ["asked"] = runs.Length, ["rerun"] = done };
    }
}
