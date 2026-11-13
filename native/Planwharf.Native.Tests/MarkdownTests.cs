using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public class MarkdownTests
{
    [Fact]
    public void Reads_headings_lists_rules_and_code_instead_of_showing_the_marks()
    {
        var blocks = Markdown.Parse("### Current state\n\nThe app has **24 files**.\n- one\n- two\n\n---\n\n1. first\n2. second\n```\nnpm test\n```\n> quoted");
        Assert.Equal([BlockKind.Heading, BlockKind.Paragraph, BlockKind.Bullet, BlockKind.Bullet, BlockKind.Rule, BlockKind.Numbered, BlockKind.Numbered, BlockKind.Code, BlockKind.Quote], blocks.Select(b => b.Kind));
        Assert.Equal("Current state", blocks[0].Text);
        Assert.Equal(3, blocks[0].Level);
        Assert.Equal("The app has **24 files**.", blocks[1].Text);
        Assert.Equal("2.", blocks[6].Marker);
        Assert.Equal("npm test", blocks[7].Text);
    }

    [Fact]
    public void Keeps_plain_text_and_line_breaks_of_a_paragraph()
    {
        var blocks = Markdown.Parse("Line one\nline two\n\nAfter a gap");
        Assert.Equal(2, blocks.Count);
        Assert.Equal("Line one\nline two", blocks[0].Text);
        Assert.Equal("After a gap", blocks[1].Text);
    }
}
