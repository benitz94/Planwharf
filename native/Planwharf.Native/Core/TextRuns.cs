using System.Text.RegularExpressions;

namespace Planwharf.Native.Core;

public sealed record TextRun(string Text, bool Bold, bool Code);

// The lead writes **bold** and `code`; the chat shows them as such instead of the marks.
public static class TextRuns
{
    static readonly Regex Marks = new(@"\*\*(.+?)\*\*|`([^`]+)`", RegexOptions.Singleline);

    public static IReadOnlyList<TextRun> Parse(string text)
    {
        var runs = new List<TextRun>();
        var at = 0;
        foreach (Match match in Marks.Matches(text))
        {
            if (match.Index > at) runs.Add(new TextRun(text[at..match.Index], false, false));
            runs.Add(match.Groups[1].Success ? new TextRun(match.Groups[1].Value, true, false) : new TextRun(match.Groups[2].Value, false, true));
            at = match.Index + match.Length;
        }
        if (at < text.Length) runs.Add(new TextRun(text[at..], false, false));
        return runs;
    }
}
