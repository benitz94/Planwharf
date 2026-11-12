using System.Diagnostics;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class SkillsTests : IDisposable
{
    readonly string home = Directory.CreateTempSubdirectory("pw home é ").FullName;
    public void Dispose() => DeleteTree(home);

    static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var child in new DirectoryInfo(path).GetFileSystemInfos())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (child is DirectoryInfo) Directory.Delete(child.FullName);
                else File.Delete(child.FullName);
            }
            else if (child is DirectoryInfo) DeleteTree(child.FullName);
            else
            {
                child.Attributes = FileAttributes.Normal;
                child.Delete();
            }
        }
        Directory.Delete(path);
    }
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

    [Fact]
    public void Overview_reports_existence_flags_and_skips_a_SKILL_md_directory()
    {
        Directory.CreateDirectory(Path.Combine(home, ".grok"));
        Directory.CreateDirectory(Path.Combine(home, ".claude", "skills", "not-a-file", "SKILL.md"));
        Write(".claude/skills/not-a-file/SKILL.md/nested/SKILL.md", "---\nname: nested-dir\n---");
        var overview = Skills.SkillsOverview(home);
        Assert.Equal(new[] { "claude", "grok" }, overview.Select(x => x.Cli));
        Assert.False(overview[0].InstructionsExist);
        Assert.False(overview[0].SettingsExist);
        Assert.Null(overview[1].SkillsFolder);
        Assert.Empty(overview[1].Skills);
        Assert.False(overview[1].InstructionsExist);
        Assert.False(overview[1].SettingsExist);
        Assert.Equal(new[] { "nested-dir" }, overview[0].Skills.Select(x => x.Name));
        Assert.Null(Skills.PathToOpen("claude", "settings", home));
        Assert.Null(Skills.PathToOpen("claude", "Skills", home));
        Assert.Empty(Skills.ListSkills(""));
        Assert.Null(Skills.ReadSkill(Path.Combine(home, ".claude")));
    }

    [Fact]
    public void ReadSkill_returns_null_when_the_file_is_locked()
    {
        var file = Write("locked/SKILL.md", "---\nname: locked\n---");
        using var hold = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(Skills.ReadSkill(file));
    }

    [Fact]
    public void PathToOpen_is_safe_under_concurrent_creates()
    {
        var errors = 0;
        Parallel.For(0, 16, _ =>
        {
            try { Assert.NotNull(Skills.PathToOpen("claude", "skills", home)); }
            catch { Interlocked.Increment(ref errors); }
        });
        Assert.Equal(0, errors);
        Assert.True(Directory.Exists(Path.Combine(home, ".claude", "skills")));
        Write(".claude/CLAUDE.md", "keep");
        Parallel.For(0, 16, _ =>
        {
            var opened = Skills.PathToOpen("claude", "instructions", home);
            Assert.NotNull(opened);
            Assert.False(opened!.Folder);
        });
        Assert.Equal("keep", File.ReadAllText(Path.Combine(home, ".claude", "CLAUDE.md")));
        var created = 0;
        Directory.CreateDirectory(Path.Combine(home, ".codex"));
        Parallel.For(0, 8, _ =>
        {
            try
            {
                var opened = Skills.PathToOpen("codex", "instructions", home);
                if (opened is not null) Interlocked.Increment(ref created);
            }
            catch (IOException) { }
        });
        Assert.True(created >= 1);
        Assert.Equal("", File.ReadAllText(Path.Combine(home, ".codex", "AGENTS.md")));
    }

    [Fact]
    public void Skill_walks_skip_reparse_points()
    {
        Write("real/SKILL.md", "---\nname: via-link\n---");
        var skills = Path.Combine(home, ".claude", "skills");
        Directory.CreateDirectory(skills);
        if (!TryJunction(Path.Combine(skills, "link"), Path.Combine(home, "real"))) return;
        Assert.Empty(Skills.ListSkills(skills));
    }

    static bool TryJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = "/c mklink /J \"" + link + "\" \"" + target + "\""
        };
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}
