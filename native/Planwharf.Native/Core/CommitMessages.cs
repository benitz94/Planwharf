using System.Text.RegularExpressions;

namespace Planwharf.Native.Core;

// The commit message offered under finished work. Git's first line is short (72 characters at most)
// and says what this commit holds; the goal and who did what go below it.
public static class CommitMessages
{
    const int Subject = 72;
    static readonly Regex Committed = new(@"\bcommit(?:ted)?\b|\bmade (?:a|one|the single) commit\b", RegexOptions.IgnoreCase);

    public static string Default(WorkView work)
    {
        var goal = OneLine(work.Title);
        var crew = string.Join(" · ", work.Steps.Where(step => step.Model.Length > 0).Select(step => $"{step.Role}: {step.Model}").Distinct());
        // When a step already made its commit, what is left to commit is the work of the steps after it.
        var results = work.StepResults;
        var lastCommit = -1;
        for (var i = 0; i < results.Count; i++) if (Committed.IsMatch(results[i].Summary)) lastCommit = i;
        var after = lastCommit >= 0 && lastCommit < results.Count - 1 ? results[^1] : null;
        var subject = after != null ? Shorten($"{after.Role}: {FirstSentence(after.Summary)}") : goal.Length == 0 ? "Work by the crew" : Shorten(goal);
        var body = new List<string>();
        if (goal.Length > 0 && goal != subject) body.Add(goal);
        if (crew.Length > 0) body.Add(crew);
        return body.Count == 0 ? subject : subject + "\n\n" + string.Join("\n", body);
    }

    static string OneLine(string text) => Regex.Replace(text ?? "", @"\s+", " ").Trim();

    static string FirstSentence(string text)
    {
        var line = OneLine(text);
        var end = Regex.Match(line, @"[.!?](\s|$)");
        return (end.Success ? line[..end.Index] : line).TrimEnd('.', ' ');
    }

    // At most 72 characters, cut between words.
    static string Shorten(string text)
    {
        if (text.Length <= Subject) return text;
        var cut = text[..Subject];
        var space = cut.LastIndexOf(' ');
        return (space > Subject / 2 ? cut[..space] : cut).TrimEnd(' ', ',', ';', ':', '-');
    }
}
