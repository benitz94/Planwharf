// Port of engine/skills.cjs.
using System.Text;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

// Settings > Skills and instructions: what each installed CLI reads from the
// user's own folders. Planwharf lists and opens these; it never writes their content.
public static class Skills
{
    public sealed record Place(string Cli, string Label, string Home, string? Skills, string? Instructions, string Settings);
    public sealed record Skill(string Name, string Description, string Source = "yours");
    public sealed record Overview(string Cli, string Label, string? SkillsFolder, Skill[] Skills, string? Instructions, bool InstructionsExist, string Settings, bool SettingsExist);
    public sealed record OpenPath(string File, bool Folder);

    public static Place[] Places(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Root(string name) => Path.Combine(home, name);
        return [
            new("claude", "Claude Code", Root(".claude"), Path.Combine(Root(".claude"), "skills"), Path.Combine(Root(".claude"), "CLAUDE.md"), Path.Combine(Root(".claude"), "settings.json")),
            new("codex", "Codex", Root(".codex"), Path.Combine(Root(".codex"), "skills"), Path.Combine(Root(".codex"), "AGENTS.md"), Path.Combine(Root(".codex"), "config.toml")),
            new("grok", "Grok", Root(".grok"), null, null, Path.Combine(Root(".grok"), "config.toml"))
        ];
    }

    // The name and first line of the description from SKILL.md front matter.
    public static Skill? ReadSkill(string file)
    {
        string text;
        try { text = Encoding.UTF8.GetString(File.ReadAllBytes(file)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        text = text[..Math.Min(text.Length, 8000)];
        var front = Regex.Match(text, @"^---\r?\n([\s\S]*?)\r?\n---", RegexOptions.ECMAScript).Groups[1].Value;
        string Field(string name)
        {
            // JS \s includes Unicode whitespace and can consume a newline after the colon.
            var match = Regex.Match(front, "^" + name + ":" + @"[\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*([^\r\n\u2028\u2029]*)", RegexOptions.Multiline | RegexOptions.ECMAScript);
            return match.Success ? Regex.Replace(StructuredFinal.Trim(match.Groups[1].Value), "^[\"']|[\"']$", "", RegexOptions.ECMAScript) : "";
        }
        var name = Field("name");
        var description = Field("description");
        return new Skill(name.Length > 0 ? name : Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(file)))!, description[..Math.Min(description.Length, 300)]);
    }

    public static Skill[] ListSkills(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return [];
        var found = new Dictionary<string, Skill>(StringComparer.Ordinal);
        void Walk(string folder, int depth, string source)
        {
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(folder).GetFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToArray(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            if (entries.Any(e => e.Name == "SKILL.md" && (e.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0))
            {
                var skill = ReadSkill(Path.Combine(folder, "SKILL.md"));
                if (skill is not null) found.TryAdd(skill.Name, skill with { Source = source });
                return;
            }
            if (depth == 0) return;
            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.Directory) == 0 || (entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.Name == ".trash") continue;
                var next = depth == 3 && entry.Name == "synced" ? "Claude app" : depth == 3 && entry.Name == ".system" ? "built in" : source;
                Walk(entry.FullName, depth - 1, next);
            }
        }
        Walk(dir, 3, "yours");
        return found.Values.OrderBy(s => s.Name, StringComparer.CurrentCulture).ToArray();
    }

    public static Overview[] SkillsOverview(string? home = null) => Places(home).Where(p => Path.Exists(p.Home))
        .Select(p => new Overview(p.Cli, p.Label, p.Skills, ListSkills(p.Skills), p.Instructions, p.Instructions is not null && Path.Exists(p.Instructions), p.Settings, Path.Exists(p.Settings))).ToArray();

    // Only fixed places can be opened. Missing skills folders or instructions are
    // created empty on request so there is somewhere to write; existing files never change.
    public static OpenPath? PathToOpen(string cli, string what, string? home = null)
    {
        var place = Places(home).FirstOrDefault(p => p.Cli == cli);
        var file = what switch { "skills" => place?.Skills, "instructions" => place?.Instructions, "settings" => place?.Settings, _ => null };
        if (file is null) return null;
        if (what == "skills") Directory.CreateDirectory(file);
        if (what == "instructions" && !Path.Exists(file)) { using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write); }
        if (what == "settings" && !Path.Exists(file)) return null;
        return new OpenPath(file, what == "skills");
    }
}
