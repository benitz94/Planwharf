using System.Text.RegularExpressions;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class LiteralStringTests
{
    // Target presentation sinks rather than English prose in model prompts, regexes, JSON,
    // retained execution evidence, command grammars or log messages.
    const string Literal = "(?:\\$?@\"(?:\"\"|[^\"])*\"|\\$?\"(?:\\\\.|[^\"\\\\])*\")";
    static readonly Regex Resources = new(@"Strings\.(?:T|Format)\(\s*" + Literal);
    static readonly Regex Sinks = new(
        @"(?:\b(?:Text|Content|OnContent|OffContent|Header|Title|Watermark)\s*=\s*|" +
        @"\b(?:Button|Small|Explain|Line|SettingsHead|Badge)\(\s*|" +
        @"ToolTip\.SetTip\([^,\n]+,\s*|throw new \w+Exception\(\s*)(?<literal>" + Literal + ")");

    static readonly HashSet<string> Technical = new(StringComparer.Ordinal)
    {
        "Planwharf", // Product name, also used by OS APIs.
        "URI malformed", "Invalid time value", "store.refusals is not an object",
        "C:\\Projects\\my-website", "~/Projects/my-website", "C:\\Projects", "~/Projects",
        "https://github.com/you/my-website", "https://api.deepseek.com/v1", "DeepSeek",
        "DEEPSEEK_API_KEY", "deepseek-chat", "qwen2.5:3b",
        "Invalid account folder.", "Invalid account folders file.", "Account folders file is busy.", "This AI tool does not support another account.",
        "attempt store directory required", "attempt store locked",
        "Import a Planwharf project export (schema 1).",
        "Invalid provider catalog: expected schema 3 providers.", "Provider IDs must be nonempty and unique.", "Model IDs must be nonempty and unique.",
        "Invalid provider policy fields.", "Fallback must be an explicit provider ID or null.",
        "Research source protection is not supported by the requested adapter: ", "Invalid alwaysApprove model policy.",
        "PERMISSION_MODE_UNSUPPORTED: Antigravity has no verified unattended approval bridge. Enable Always approve / YOLO explicitly or select another adapter.",
        "PERMISSION_NEEDED: {provider} needs your OK to work in the project folder without asking each time. Allow it in Team > AI tools, or choose another model for this step.",
        "Invalid Supervisor storage", "Select an existing project.", "Select an eligible Supervisor model; no substitution is allowed.",
        "Select a Supervisor before enabling anomaly analysis.", "This alert is no longer active for the selected project.",
        "A Supervisor answer is already in progress.", "Enter a question of at most 8000 characters.", "Supervisor review handler not configured.",
        "Supervisor returned no answer.",
        "removeTempDir only removes folders inside the temp directory"
    };

    internal static IEnumerable<string> FindUnlocalized(string source)
    {
        var code = Regex.Replace(source, @"(?m)^\s*//[^\r\n]*|/\*[\s\S]*?\*/", "");
        code = Resources.Replace(code, "Strings.Resource(");
        foreach (Match match in Sinks.Matches(code))
        {
            var literal = match.Groups["literal"].Value;
            var value = Regex.Unescape(literal.TrimStart('$', '@').Trim('"'));
            if (!Regex.IsMatch(value, "[A-Za-z]") || Technical.Contains(value) || value.StartsWith("Cannot read properties of ", StringComparison.Ordinal) || value.StartsWith("Cannot destructure ", StringComparison.Ordinal) || value.EndsWith(" is not a function", StringComparison.Ordinal)) continue;
            yield return literal;
        }
    }

    [Fact]
    public void Every_native_source_file_uses_resources_for_presentation_literals()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "native", "Planwharf.Engine"))) root = root.Parent;
        Assert.NotNull(root);
        var native = Path.Combine(root!.FullName, "native");
        var files = new[] { "Planwharf.Engine", "Planwharf.Native" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(native, project), "*.cs", SearchOption.AllDirectories))
            .Where(path => Path.GetFileName(path) != "Strings.cs" && !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .ToList();
        Assert.NotEmpty(files);
        var violations = files.SelectMany(file => FindUnlocalized(File.ReadAllText(file))
            .Select(literal => Path.GetRelativePath(native, file) + ": " + literal));
        Assert.True(!violations.Any(), string.Join("\n", violations));

        var catalog = (Dictionary<string, string>)typeof(Planwharf.Engine.Strings)
            .GetField("English", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        var references = new Regex("Strings\\.(?:T|Format)\\(\\s*\"(?<key>(?:\\\\.|[^\"\\\\])*)\"");
        foreach (var file in files)
            foreach (Match match in references.Matches(File.ReadAllText(file)))
            {
                var key = Regex.Unescape(match.Groups["key"].Value);
                Assert.True(catalog.ContainsKey(key), Path.GetRelativePath(native, file) + ": missing key " + key);
            }
    }

    [Theory]
    [InlineData("new Button { Content = \"Save changes\" };")]
    [InlineData("ToolTip.SetTip(button, \"Choose a model\");")]
    [InlineData("throw new InvalidOperationException(\"Choose a browser.\");")]
    public void Guard_detects_new_untranslated_labels(string code) => Assert.Single(FindUnlocalized(code));

    [Fact]
    public void Guard_allows_resources_and_programmatic_literals()
    {
        const string code = """
            new Button { Content = Strings.T("Save changes") };
            new TextBox { Watermark = "~/Projects/my-website" };
            var payload = new JsonObject { ["status"] = "running" };
            var command = new Regex("^(stop|fermati)$");
            """;
        Assert.Empty(FindUnlocalized(code));
    }
}
