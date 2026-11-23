// Port of engine/push-email.cjs.
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public record ProcessResult(int Status, string Stdout, string Stderr);

public static class PushEmail
{
    private static readonly Regex Gh007Regex = new(@"\bGH007\b", RegexOptions.ECMAScript);
    private static readonly Regex NoreplyRegex = new(@"^\d+\+[A-Za-z0-9-]+@users\.noreply\.github\.com$", RegexOptions.ECMAScript);

    public const string HowTo = "Set your GitHub noreply email (GitHub \u2192 Settings \u2192 Emails) with: git config --global user.email \"<id>+<login>@users.noreply.github.com\", then press Push again.";

    public static bool PrivateEmailRefused(string? text)
    {
        return Gh007Regex.IsMatch(text ?? "");
    }

    public static async Task<string?> GithubNoreplyAsync(Func<IReadOnlyList<string>, Task<ProcessResult>> gh)
    {
        var read = await gh(["api", "user", "--jq", "\"\\(.id)+\\(.login)@users.noreply.github.com\""]);
        var email = (read?.Stdout ?? "").Trim();
        if (read?.Status == 0 && NoreplyRegex.IsMatch(email))
            return email;
        return null;
    }

    public static async Task<(bool Ok, int Rewritten, string? Reason)> UseNoreplyAsync(Func<IReadOnlyList<string>, Task<ProcessResult>> run, string email)
    {
        var set = await run(["config", "user.email", email]);
        if (set.Status != 0)
            return (false, 0, "Could not set the GitHub noreply email for this project.");

        var listed = await run(["rev-list", "--reverse", "HEAD", "--not", "--remotes"]);
        if (listed.Status != 0)
            return (false, 0, "Could not list the commits not yet on GitHub.");

        var commits = (listed.Stdout ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (commits.Length == 0)
            return (true, 0, null);

        var parent = await run(["rev-parse", "--verify", "-q", commits[0] + "^"]);
        var baseArgs = parent.Status == 0 ? new[] { (parent.Stdout ?? "").Trim() } : new[] { "--root" };

        var rebaseArgs = new List<string> { "-c", "core.editor=true", "rebase", "-r", "--exec", "git commit --amend --no-edit --reset-author --allow-empty" };
        rebaseArgs.AddRange(baseArgs);

        var rebased = await run(rebaseArgs);
        if (rebased.Status != 0)
        {
            await run(["rebase", "--abort"]);
            return (false, 0, "Could not change the email of the commits not yet on GitHub; they are as they were.");
        }

        return (true, commits.Length, null);
    }
}
