// Port of engine/repository.cjs.
using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public record RepositoryAddress(string Provider, string Repository, string Origin, string BaseUrl, string Url);
public record RepoExecResult(bool Ok, string Stdout, string? Error);

public static class Repository
{
    private static readonly Regex GithubRegex = new(@"^(?:https://github\.com/|git@github\.com:)([\w.-]+)/([\w.-]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ValidPartRegex = new(@"^[\w.-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex ScrubUrlRegex = new(@"https://[^\s/@]+:[^\s/@]+@", RegexOptions.CultureInvariant);
    private static readonly Regex ScrubTokenRegex = new(@"\b(?:gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)\b", RegexOptions.CultureInvariant);
    private static readonly Regex CountsRegex = new(@"^(\d+)\s+(\d+)$", RegexOptions.CultureInvariant);
    private static readonly Regex ShaRegex = new(@"^[a-f0-9]{40,64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? GithubRepo(string? value)
    {
        var text = (value ?? "").Trim();
        var match = GithubRegex.Match(text);
        return match.Success ? $"{match.Groups[1].Value}/{match.Groups[2].Value}" : null;
    }

    public static RepositoryAddress? GetRepositoryAddress(string? value)
    {
        var github = GithubRepo(value);
        if (github != null)
            return new RepositoryAddress("github", github, "https://github.com", "https://github.com", $"https://github.com/{github}");

        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                return null;

            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                path = path[..^4];

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count < 2 || parts.Any(part => !ValidPartRegex.IsMatch(part) || part == "." || part == ".."))
                return null;

            var repository = $"{parts[^2]}/{parts[^1]}";
            parts.RemoveRange(parts.Count - 2, 2);
            var origin = $"{uri.Scheme}://{uri.Authority}";
            var baseUrl = origin + (parts.Count > 0 ? "/" + string.Join("/", parts) : "");
            return new RepositoryAddress("gitea", repository, origin, baseUrl, baseUrl + "/" + repository);
        }
        catch { return null; }
    }

    public static RepositoryAddress? repositoryAddress(string? value) => GetRepositoryAddress(value);

    public static string? GiteaToken(RepositoryAddress address, IDictionary<string, string>? env = null)
    {
        if (address == null) return null;
        try
        {
            var hostKey = Regex.Replace(new Uri(address.Origin).Host.ToUpperInvariant(), @"[^A-Z0-9]", "_");
            var sourceEnv = env ?? EnvironmentVariables();
            if (sourceEnv.TryGetValue("PLANWHARF_GITEA_TOKEN_" + hostKey, out var token) && !string.IsNullOrEmpty(token))
                return token;
            if (sourceEnv.TryGetValue("PLANWHARF_GITEA_ORIGIN", out var configuredOrigin) && configuredOrigin == address.Origin)
            {
                if (sourceEnv.TryGetValue("PLANWHARF_GITEA_TOKEN", out var genToken) && !string.IsNullOrEmpty(genToken))
                    return genToken;
            }
        }
        catch { }
        return null;
    }

    public static string CleanError(string? value)
    {
        var text = value ?? "";
        text = ScrubUrlRegex.Replace(text, "https://[redacted]@");
        text = ScrubTokenRegex.Replace(text, "[redacted]");
        if (text.Length > 900)
            text = text[^900..];
        return text.Trim();
    }

    public static string Relation(int ahead, int behind)
    {
        return (ahead > 0 && behind > 0) ? "diverged" : (ahead > 0 ? "ahead" : (behind > 0 ? "behind" : "in-sync"));
    }

    public static async Task<RepoExecResult> DefaultExecuteAsync(string command, IReadOnlyList<string> args, JsonObject? options = null)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            var env = ProjectWorkspace.GitEnvironment();
            env["GIT_TERMINAL_PROMPT"] = "0";
            env["GCM_INTERACTIVE"] = "never";
            env["GH_PROMPT_DISABLED"] = "1";
            if (options?["env"] is JsonObject optEnv)
            {
                foreach (var kvp in optEnv)
                    if (kvp.Value != null) env[kvp.Key] = kvp.Value.ToString();
            }
            foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

            using var process = Process.Start(psi);
            if (process == null) return new RepoExecResult(false, "", "Process failed to start");
            using var cts = new CancellationTokenSource(25000);
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(cts.Token);
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();
            return new RepoExecResult(process.ExitCode == 0, stdout, process.ExitCode == 0 ? null : CleanError(stderr));
        }
        catch (Exception ex)
        {
            return new RepoExecResult(false, "", CleanError(ex.Message));
        }
    }

    public static Func<JsonObject, bool, Task<JsonObject>> CreateRepositoryInspector(
        Func<string, IReadOnlyList<string>, JsonObject?, Task<RepoExecResult>>? execute = null,
        int ttlMs = 60000,
        Func<string, JsonObject?, Task<(bool Ok, int Status, string Body)>>? fetchRemote = null,
        IDictionary<string, string>? env = null)
    {
        var exec = execute ?? DefaultExecuteAsync;
        var cache = new Dictionary<string, (long At, JsonObject Value)>();
        var pending = new Dictionary<string, Task<JsonObject>>();
        var sourceEnv = env ?? EnvironmentVariables();

        async Task<JsonObject> InspectAsync(JsonObject project)
        {
            var folderVal = project["folder"]?.ToString();
            var folder = ProjectWorkspace.LinkedWorkspace(folderVal);
            var githubVal = project["github"]?.ToString();

            var localObj = new JsonObject
            {
                ["exists"] = false,
                ["readable"] = false,
                ["writable"] = false,
                ["git"] = false
            };
            var remoteObj = new JsonObject
            {
                ["accessible"] = false
            };
            var comparisonObj = new JsonObject
            {
                ["status"] = "unknown",
                ["ahead"] = null,
                ["behind"] = null
            };

            var result = new JsonObject
            {
                ["checkedAt"] = DateTime.UtcNow.ToString("o"),
                ["folder"] = folder,
                ["github"] = githubVal,
                ["local"] = localObj,
                ["remote"] = remoteObj,
                ["comparison"] = comparisonObj
            };

            Task<RepoExecResult> GitAsync(params string[] args)
            {
                var combined = new List<string> { "-C", folder ?? "" };
                combined.AddRange(args);
                return exec("git", combined, null);
            }

            if (!string.IsNullOrEmpty(folder))
            {
                try
                {
                    localObj["exists"] = Directory.Exists(folder);
                    // Test readable
                    Directory.EnumerateFileSystemEntries(folder).Take(1).ToList();
                    localObj["readable"] = true;
                    try
                    {
                        var testFile = Path.Combine(folder, ".pw-write-test-" + Guid.NewGuid().ToString("N"));
                        File.WriteAllText(testFile, "test");
                        File.Delete(testFile);
                        localObj["writable"] = true;
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    localObj["error"] = CleanError(ex.Message);
                }

                if (localObj["readable"]?.GetValue<bool>() == true && ProjectWorkspace.SafeGitWorkspace(folder) != null)
                {
                    var root = await GitAsync("rev-parse", "--show-toplevel");
                    var isGit = root.Ok && ProjectWorkspace.LinkedWorkspace(root.Stdout) == folder;
                    localObj["git"] = isGit;
                    if (isGit)
                    {
                        var symbolicRef = await GitAsync("symbolic-ref", "--quiet", "--short", "HEAD");
                        var revParseHead = await GitAsync("rev-parse", "--verify", "HEAD");
                        var statusPorcelain = await GitAsync("status", "--ignore-submodules=all", "--porcelain");
                        var remoteUrl = await GitAsync("remote", "get-url", "origin");
                        var shallowCheck = await GitAsync("rev-parse", "--is-shallow-repository");

                        localObj["gitRoot"] = root.Stdout;
                        localObj["branch"] = symbolicRef.Ok ? symbolicRef.Stdout : null;
                        localObj["head"] = revParseHead.Ok ? revParseHead.Stdout : null;
                        var changes = statusPorcelain.Ok ? statusPorcelain.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
                        if (changes != null)
                        {
                            var chArr = new JsonArray();
                            foreach (var ch in changes) chArr.Add((JsonNode)ch);
                            localObj["changes"] = chArr;
                            localObj["uncommitted"] = changes.Length;
                        }
                        else
                        {
                            localObj["changes"] = null;
                            localObj["uncommitted"] = null;
                        }
                        localObj["originRepository"] = remoteUrl.Ok ? GithubRepo(remoteUrl.Stdout) : null;
                        localObj["shallow"] = shallowCheck.Stdout == "true";
                    }
                }
            }

            var address = GetRepositoryAddress(githubVal);
            if (address == null)
            {
                remoteObj["error"] = !string.IsNullOrEmpty(githubVal) ? "Invalid repository URL." : "No repository linked.";
                return result;
            }

            remoteObj["provider"] = address.Provider;
            if (address.Provider == "gitea")
            {
                await InspectGiteaAsync(address, result, GitAsync);
                return result;
            }

            var repo = address.Repository;
            remoteObj["repository"] = repo;

            var meta = await exec("gh", new[] { "api", $"repos/{repo}", "--jq", "{name: .full_name, private: .private, defaultBranch: .default_branch, permissions: .permissions}" }, null);
            if (!meta.Ok)
            {
                remoteObj["error"] = $"GitHub access failed. Check gh auth status and repository permissions. {meta.Error}";
                comparisonObj["status"] = "access-error";
                return result;
            }

            try
            {
                using var doc = JsonDocument.Parse(meta.Stdout);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    remoteObj[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
                remoteObj["accessible"] = true;
            }
            catch
            {
                remoteObj["error"] = "GitHub returned unreadable repository metadata.";
                return result;
            }

            if (localObj["git"]?.GetValue<bool>() != true || string.IsNullOrEmpty(localObj["head"]?.ToString()))
            {
                comparisonObj["status"] = "no-local-history";
                return result;
            }

            var originRepo = localObj["originRepository"]?.ToString();
            if (!string.IsNullOrEmpty(originRepo) && !string.Equals(originRepo, repo, StringComparison.OrdinalIgnoreCase))
            {
                result["warning"] = "Local origin differs from the linked GitHub repository. Comparison uses the explicitly linked repository.";
            }

            var defaultBranch = remoteObj["defaultBranch"]?.ToString() ?? "main";
            var branch = localObj["branch"]?.ToString() ?? defaultBranch;
            var remoteBranch = await exec("gh", new[] { "api", $"repos/{repo}/branches/{Uri.EscapeDataString(branch)}", "--jq", ".commit.sha" }, null);
            remoteObj["localBranchPublished"] = remoteBranch.Ok;
            if (!remoteBranch.Ok && Regex.IsMatch(remoteBranch.Error ?? "", @"404|Not Found", RegexOptions.IgnoreCase) && branch != defaultBranch)
            {
                branch = defaultBranch;
                remoteBranch = await exec("gh", new[] { "api", $"repos/{repo}/branches/{Uri.EscapeDataString(branch)}", "--jq", ".commit.sha" }, null);
            }
            remoteObj["branch"] = branch;

            if (!remoteBranch.Ok || !ShaRegex.IsMatch(remoteBranch.Stdout))
            {
                remoteObj["error"] = remoteBranch.Error ?? "Remote branch has no readable commit.";
                return result;
            }
            remoteObj["head"] = remoteBranch.Stdout;

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{repo}/{branch}"))).ToLowerInvariant()[..24];
            var inspectionRef = $"refs/planwharf/inspection/{hash}";

            var fetched = await GitAsync("-c", "credential.helper=", "-c", "credential.helper=!gh auth git-credential",
                "fetch", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", $"https://github.com/{repo}.git", $"+refs/heads/{branch}:{inspectionRef}");
            if (!fetched.Ok)
            {
                comparisonObj["error"] = $"Cannot fetch current remote history: {fetched.Error}";
                return result;
            }

            var fetchedHead = await GitAsync("rev-parse", "--verify", inspectionRef);
            if (!fetchedHead.Ok)
            {
                comparisonObj["error"] = fetchedHead.Error;
                return result;
            }

            remoteObj["head"] = fetchedHead.Stdout;
            remoteObj["ref"] = inspectionRef;
            remoteObj["fetchedAt"] = DateTime.UtcNow.ToString("o");

            var localHead = localObj["head"]!.ToString();
            var isShallow = localObj["shallow"]?.GetValue<bool>() == true;

            var baseRes = await GitAsync("merge-base", localHead, inspectionRef);
            if (!baseRes.Ok)
            {
                comparisonObj["status"] = isShallow ? "shallow-history" : "unrelated-history";
                return result;
            }

            var counts = await GitAsync("rev-list", "--left-right", "--count", $"{localHead}...{inspectionRef}");
            var m = CountsRegex.Match(counts.Stdout);
            if (!counts.Ok || !m.Success)
            {
                comparisonObj["error"] = counts.Error ?? "Unreadable commit comparison.";
                return result;
            }

            var ahead = int.Parse(m.Groups[1].Value);
            var behind = int.Parse(m.Groups[2].Value);
            comparisonObj["status"] = Relation(ahead, behind);
            comparisonObj["ahead"] = ahead;
            comparisonObj["behind"] = behind;
            comparisonObj["localBranch"] = localObj["branch"]?.ToString();
            comparisonObj["remoteBranch"] = branch;
            comparisonObj["localBranchPublished"] = remoteObj["localBranchPublished"]?.GetValue<bool>() == true;
            comparisonObj["mergeBase"] = baseRes.Stdout;

            return result;
        }

        async Task InspectGiteaAsync(RepositoryAddress address, JsonObject result, Func<string[], Task<RepoExecResult>> git)
        {
            var localObj = (JsonObject)result["local"]!;
            var remoteObj = (JsonObject)result["remote"]!;
            var comparisonObj = (JsonObject)result["comparison"]!;
            var folder = result["folder"]?.ToString() ?? "";

            var token = GiteaToken(address, sourceEnv);

            async Task<JsonNode?> RequestAsync(string resource)
            {
                if (fetchRemote == null)
                    throw new Exception("HTTP fetcher not configured for Gitea.");
                var options = new JsonObject();
                var headers = new JsonObject { ["Accept"] = "application/json" };
                if (!string.IsNullOrEmpty(token)) headers["Authorization"] = "token " + token;
                options["headers"] = headers;
                options["redirect"] = "error";

                var res = await fetchRemote(address.BaseUrl + "/api/v1/repos/" + address.Repository + resource, options);
                if (!res.Ok)
                    throw new Exception("Gitea HTTP " + res.Status + ". Check repository access and the token configured for this host.");
                return JsonNode.Parse(res.Body);
            }

            try
            {
                var metadata = await RequestAsync("");
                var defaultBranch = metadata?["default_branch"]?.ToString();
                if (string.IsNullOrEmpty(defaultBranch))
                    throw new Exception("Gitea returned unreadable repository metadata (default_branch missing).");

                remoteObj["accessible"] = true;
                remoteObj["repository"] = address.Repository;
                remoteObj["private"] = metadata?["private"]?.GetValue<bool>() == true;
                remoteObj["defaultBranch"] = defaultBranch;
                remoteObj["permissions"] = metadata?["permissions"] != null ? (JsonNode)metadata["permissions"]!.DeepClone() : null;

                if (localObj["git"]?.GetValue<bool>() != true || string.IsNullOrEmpty(localObj["head"]?.ToString()))
                {
                    comparisonObj["status"] = "no-local-history";
                    return;
                }

                var branch = localObj["branch"]?.ToString() ?? defaultBranch;
                JsonNode? remoteBranch = null;
                try
                {
                    remoteBranch = await RequestAsync("/branches/" + Uri.EscapeDataString(branch));
                    remoteObj["localBranchPublished"] = true;
                }
                catch (Exception ex)
                {
                    if (!Regex.IsMatch(ex.Message, @"HTTP 404\b") || branch == defaultBranch) throw;
                    remoteObj["localBranchPublished"] = false;
                    branch = defaultBranch;
                    remoteBranch = await RequestAsync("/branches/" + Uri.EscapeDataString(branch));
                }

                var commitSha = remoteBranch?["commit"]?["id"]?.ToString() ?? "";
                if (!ShaRegex.IsMatch(commitSha))
                    throw new Exception("Gitea returned unreadable branch metadata (commit.id missing).");

                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{address.Url}/{branch}"))).ToLowerInvariant()[..24];
                var inspectionRef = $"refs/planwharf/inspection/{hash}";

                var gitFetchOptions = new JsonObject();
                if (!string.IsNullOrEmpty(token))
                {
                    var optEnv = new JsonObject
                    {
                        ["GIT_TERMINAL_PROMPT"] = "0",
                        ["GCM_INTERACTIVE"] = "never",
                        ["GIT_CONFIG_COUNT"] = "1",
                        ["GIT_CONFIG_KEY_0"] = "http." + address.BaseUrl + "/.extraheader",
                        ["GIT_CONFIG_VALUE_0"] = "Authorization: token " + token
                    };
                    gitFetchOptions["env"] = optEnv;
                }

                var fetched = await exec("git", new[] { "-C", folder, "-c", "http.followRedirects=false", "fetch", "--no-tags", "--no-write-fetch-head", "--no-recurse-submodules", address.Url + ".git", $"+refs/heads/{branch}:{inspectionRef}" }, gitFetchOptions);
                if (!fetched.Ok) throw new Exception("Cannot fetch Gitea history: " + fetched.Error);

                var head = await git(new[] { "rev-parse", "--verify", inspectionRef });
                if (!head.Ok) throw new Exception(head.Error ?? "Cannot read fetched Gitea commit.");

                remoteObj["branch"] = branch;
                remoteObj["head"] = head.Stdout;
                remoteObj["ref"] = inspectionRef;
                remoteObj["fetchedAt"] = DateTime.UtcNow.ToString("o");

                var localHead = localObj["head"]!.ToString();
                var baseRes = await git(new[] { "merge-base", localHead, inspectionRef });
                if (!baseRes.Ok)
                {
                    comparisonObj["status"] = localObj["shallow"]?.GetValue<bool>() == true ? "shallow-history" : "unrelated-history";
                    return;
                }

                var counts = await git(new[] { "rev-list", "--left-right", "--count", $"{localHead}...{inspectionRef}" });
                var m = CountsRegex.Match(counts.Stdout);
                if (!counts.Ok || !m.Success) throw new Exception(counts.Error ?? "Unreadable commit comparison.");

                var ahead = int.Parse(m.Groups[1].Value);
                var behind = int.Parse(m.Groups[2].Value);
                comparisonObj["status"] = Relation(ahead, behind);
                comparisonObj["ahead"] = ahead;
                comparisonObj["behind"] = behind;
                comparisonObj["localBranch"] = localObj["branch"]?.ToString();
                comparisonObj["remoteBranch"] = branch;
                comparisonObj["localBranchPublished"] = remoteObj["localBranchPublished"]?.GetValue<bool>() == true;
                comparisonObj["mergeBase"] = baseRes.Stdout;
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                if (!string.IsNullOrEmpty(token)) msg = msg.Replace(token, "[redacted]");
                remoteObj["error"] = CleanError(msg);
                comparisonObj["status"] = remoteObj["accessible"]?.GetValue<bool>() == true ? "unknown" : "access-error";
            }
        }

        return async (project, force) =>
        {
            var key = (project["folder"]?.ToString() ?? "") + "\0" + (project["github"]?.ToString() ?? "");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (!force && cache.TryGetValue(key, out var cached) && now - cached.At < ttlMs)
                return (JsonObject)cached.Value.DeepClone();

            Task<JsonObject> workTask;
            lock (pending)
            {
                if (pending.TryGetValue(key, out var existingTask))
                    workTask = existingTask;
                else
                {
                    workTask = Task.Run(async () =>
                    {
                        try
                        {
                            return await InspectAsync(project);
                        }
                        catch (Exception ex)
                        {
                            return new JsonObject
                            {
                                ["checkedAt"] = DateTime.UtcNow.ToString("o"),
                                ["folder"] = project["folder"]?.ToString(),
                                ["github"] = project["github"]?.ToString(),
                                ["error"] = CleanError(ex.Message),
                                ["comparison"] = new JsonObject
                                {
                                    ["status"] = "unknown",
                                    ["ahead"] = null,
                                    ["behind"] = null
                                }
                            };
                        }
                    });
                    pending[key] = workTask;
                }
            }

            try
            {
                var val = await workTask;
                lock (cache)
                {
                    cache[key] = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), (JsonObject)val.DeepClone());
                }
                return (JsonObject)val.DeepClone();
            }
            finally
            {
                lock (pending) { pending.Remove(key); }
            }
        };
    }

    private static Dictionary<string, string> EnvironmentVariables()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry de in Environment.GetEnvironmentVariables())
            if (de.Key is string k && de.Value is string v) dict[k] = v;
        return dict;
    }
}
