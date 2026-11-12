using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class SkillsTests : IDisposable
{
    readonly string home = Directory.CreateTempSubdirectory("pw home é ").FullName;
    public void Dispose() => Directory.Delete(home, true);
    string Write(string relative, string text)
    {
        var file = Path.Combine(home, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    [Fact]
    public void Skills_are_listed_per_program_only_the_fixed_places_open_existing_files_are_never_changed()
    {
        Write(".claude/skills/mine/SKILL.md", "---\nname: my-skill\ndescription: Does a thing\n---\nbody");
        Write(".claude/skills/.trash/old/gone/SKILL.md", "---\nname: gone\n---");
        Write(".codex/skills/.system/tool/SKILL.md", "---\nname: tool\ndescription: \"Built in\"\n---");
        var instructions = Write(".codex/AGENTS.md", "my own rules");
        var overview = Skills.SkillsOverview(home);
        Assert.Equal(new[] { "claude", "codex" }, overview.Select(x => x.Cli));
        Assert.Equal(new[] { new Skills.Skill("my-skill", "Does a thing", "yours") }, overview[0].Skills);
        Assert.Equal(new[] { new Skills.Skill("tool", "Built in", "built in") }, overview[1].Skills);
        Assert.Null(Skills.PathToOpen("claude", "../outside", home));
        Assert.Null(Skills.PathToOpen("someone", "skills", home));
        Skills.PathToOpen("codex", "instructions", home);
        Assert.Equal("my own rules", File.ReadAllText(instructions));
        var opened = Skills.PathToOpen("claude", "instructions", home);
        Assert.NotNull(opened);
        Assert.False(opened.Folder);
        Assert.Equal("", File.ReadAllText(opened.File));
    }

    [Fact]
    public void Discovery_limits_depth_and_keeps_first_duplicate()
    {
        Write(".claude/skills/a/SKILL.md", "---\nname: same\ndescription: first\n---");
        Write(".claude/skills/b/SKILL.md", "---\nname: same\ndescription: second\n---");
        Write(".claude/skills/a/nested/SKILL.md", "---\nname: hidden\n---");
        Write(".claude/skills/synced/app/SKILL.md", "---\nname: app\n---");
        Write(".claude/skills/one/two/three/SKILL.md", "---\nname: depth-three\n---");
        Write(".claude/skills/deep/two/three/four/SKILL.md", "---\nname: too-deep\n---");
        var skills = Skills.SkillsOverview(home)[0].Skills;
        Assert.Equal(new[] { "app", "depth-three", "same" }, skills.Select(x => x.Name));
        Assert.Equal("Claude app", skills[0].Source);
        Assert.Equal("first", skills[2].Description);
        Assert.Empty(Skills.ListSkills(null));
        Assert.Empty(Skills.ListSkills(Path.Combine(home, "missing")));
    }

    [Fact]
    public void Front_matter_preserves_JS_limits_fallbacks_quotes_CRLF_and_BOM_behavior()
    {
        Assert.Null(Skills.ReadSkill(Path.Combine(home, "missing")));
        var file = Write("fallback/SKILL.md", "body");
        Assert.Equal(new Skills.Skill("fallback", ""), Skills.ReadSkill(file));
        Write("fallback/SKILL.md", "---\r\nname: 'quoted'\r\ndescription: \"" + new string('x', 350) + "\"\r\n---");
        var skill = Skills.ReadSkill(file)!;
        Assert.Equal("quoted", skill.Name);
        Assert.Equal(new string('x', 300), skill.Description);
        Write("fallback/SKILL.md", "\uFEFF---\nname: ignored\n---");
        Assert.Equal("fallback", Skills.ReadSkill(file)!.Name);
        Write("fallback/SKILL.md", "---\nname: ignored\ndescription: " + new string('x', 8000) + "\n---");
        Assert.Equal("fallback", Skills.ReadSkill(file)!.Name);
        Write("fallback/SKILL.md", "---\nname:\nnext: value\n---");
        Assert.Equal("next: value", Skills.ReadSkill(file)!.Name);
    }

    [Fact]
    public void Fixed_places_preserve_settings_and_create_only_requested_paths()
    {
        Assert.Equal(new[] { "claude", "codex", "grok" }, Skills.Places(home).Select(x => x.Cli));
        Assert.Null(Skills.PathToOpen("grok", "skills", home));
        Assert.Null(Skills.PathToOpen("grok", "instructions", home));
        Assert.Null(Skills.PathToOpen("grok", "settings", home));
        var settings = Write(".grok/config.toml", "user settings");
        Assert.Equal(new Skills.OpenPath(settings, false), Skills.PathToOpen("grok", "settings", home));
        Assert.Equal("user settings", File.ReadAllText(settings));
        var opened = Skills.PathToOpen("codex", "skills", home)!;
        Assert.True(opened.Folder);
        Assert.True(Directory.Exists(opened.File));
        Assert.False(File.Exists(Path.Combine(home, ".codex", "AGENTS.md")));
    }
}
