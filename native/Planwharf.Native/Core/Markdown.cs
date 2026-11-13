using System.Text.RegularExpressions;

namespace Planwharf.Native.Core;

public enum BlockKind { Paragraph, Heading, Bullet, Numbered, Rule, Code, Quote }

// One block of a reply: a heading, a list item, a rule, a code block or a paragraph.
public sealed record TextBlockView(BlockKind Kind, string Text, int Level = 0, string Marker = "");

// The lead writes Markdown; the chat shows headings, lists and rules instead of the marks.
// Only what the lead uses: # headings, - and 1. lists, ---, ``` code, > quotes, **bold** and `code`.
public static class Markdown
{
    static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$");
    static readonly Regex Bullet = new(@"^(\s*)[-*•]\s+(.*)$");
    static readonly Regex Numbered = new(@"^(\s*)(\d{1,3})[.)]\s+(.*)$");
    static readonly Regex Rule = new(@"^\s*([-*_])(\s*\1){2,}\s*$");

    public static IReadOnlyList<TextBlockView> Parse(string text)
    {
        var blocks = new List<TextBlockView>();
        var paragraph = new List<string>();
        void Flush() { if (paragraph.Count > 0) { blocks.Add(new TextBlockView(BlockKind.Paragraph, string.Join("\n", paragraph))); paragraph.Clear(); } }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```"))
            {
                Flush();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++) code.Add(lines[i]);
                blocks.Add(new TextBlockView(BlockKind.Code, string.Join("\n", code)));
                continue;
            }
            if (line.Trim().Length == 0) { Flush(); continue; }
            if (Rule.IsMatch(line)) { Flush(); blocks.Add(new TextBlockView(BlockKind.Rule, "")); continue; }
            if (Heading.Match(line) is { Success: true } h) { Flush(); blocks.Add(new TextBlockView(BlockKind.Heading, h.Groups[2].Value.TrimEnd('#', ' '), h.Groups[1].Length)); continue; }
            if (Bullet.Match(line) is { Success: true } b) { Flush(); blocks.Add(new TextBlockView(BlockKind.Bullet, b.Groups[2].Value, b.Groups[1].Value.Length / 2, "•")); continue; }
            if (Numbered.Match(line) is { Success: true } n) { Flush(); blocks.Add(new TextBlockView(BlockKind.Numbered, n.Groups[3].Value, n.Groups[1].Value.Length / 2, n.Groups[2].Value + ".")); continue; }
            if (line.StartsWith(">")) { Flush(); blocks.Add(new TextBlockView(BlockKind.Quote, line.TrimStart('>', ' '))); continue; }
            paragraph.Add(line);
        }
        Flush();
        return blocks;
    }
}
