using System.Text.Json.Nodes;

namespace Planwharf.Native;

// The window's own preferences, kept on this computer: the theme and the text colour.
static class Prefs
{
    static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create) is { Length: > 0 } root ? root
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "Planwharf", "native-window.json");

    public static string Theme { get; private set; } = "dark";
    public static string Ink { get; private set; } = "white";
    public static string Accent { get; private set; } = "green";

    public static void Load()
    {
        try
        {
            var saved = JsonNode.Parse(System.IO.File.ReadAllText(File));
            if (saved?["theme"]?.GetValue<string>() is "light" or "dark") Theme = saved["theme"]!.GetValue<string>();
            if (saved?["ink"]?.GetValue<string>() is { Length: > 0 } ink) Ink = ink;
            if (saved?["accent"]?.GetValue<string>() is { Length: > 0 } accent) Accent = accent;
        }
        catch { /* first start, or an unreadable file: the defaults stay */ }
    }

    // Only what is given changes; the rest stays as it was.
    public static void Save(string? theme = null, string? ink = null, string? accent = null)
    {
        if (theme != null) Theme = theme == "light" ? "light" : "dark";
        if (ink != null) Ink = ink;
        if (accent != null) Accent = accent;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, new JsonObject { ["theme"] = Theme, ["ink"] = Ink, ["accent"] = Accent }.ToJsonString());
        }
        catch { /* the choice still applies until the window closes */ }
    }
}
