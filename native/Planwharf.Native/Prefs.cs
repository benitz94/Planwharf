using System.Text.Json.Nodes;
using Planwharf.Native.Core;
using Planwharf.Engine;

namespace Planwharf.Native;

// The window's own preferences, kept on this computer: the theme and the text colour.
static class Prefs
{
    internal static string File { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create) is { Length: > 0 } root ? root
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "Planwharf", "native-window.json");

    public static string Theme { get; private set; } = "dark";
    public static string Ink { get; private set; } = "white";
    public static string Accent { get; private set; } = "green";
    public static AlertOptions Alerts { get; private set; } = new();

    public static void Load()
    {
        Strings.CurrentLanguage = "en";
        try
        {
            var saved = JsonNode.Parse(System.IO.File.ReadAllText(File));
            Strings.CurrentLanguage = saved?["language"] is JsonValue language && language.TryGetValue<string>(out var selectedLanguage) ? selectedLanguage : "en";
            Alerts = AlertOptions.Read(saved);
            if (saved?["theme"]?.GetValue<string>() is "light" or "dark") Theme = saved["theme"]!.GetValue<string>();
            if (saved?["ink"]?.GetValue<string>() is { Length: > 0 } ink) Ink = ink;
            if (saved?["accent"]?.GetValue<string>() is { Length: > 0 } accent) Accent = accent;
        }
        catch { /* first start, or an unreadable file: the defaults stay */ }
    }

    // Only what is given changes; the rest stays as it was.
    public static void Save(string? theme = null, string? ink = null, string? accent = null, AlertOptions? alerts = null)
    {
        if (alerts != null) Alerts = alerts;
        if (theme != null) Theme = theme == "light" ? "light" : "dark";
        if (ink != null) Ink = ink;
        if (accent != null) Accent = accent;
        var temporary = "";
        try
        {
            // A read-only file is a lock: do not replace it. On Windows the attribute already
            // blocks the move; on Linux and macOS it does not, so the check has to be here.
            if (System.IO.File.Exists(File) && (System.IO.File.GetAttributes(File) & FileAttributes.ReadOnly) != 0) return;
            var directory = Path.GetDirectoryName(File);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            temporary = File + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var json = new JsonObject { ["language"] = Strings.CurrentLanguage, ["theme"] = Theme, ["ink"] = Ink, ["accent"] = Accent,
                ["alertNotification"] = Alerts.Notification, ["alertFlash"] = Alerts.Flash, ["alertSound"] = Alerts.Sound }.ToJsonString();
            System.IO.File.WriteAllText(temporary, json);
            System.IO.File.Move(temporary, File, overwrite: true);
        }
        catch
        {
            // The choice still applies until the window closes. A failed replace must not leave a temp file.
            try { if (temporary.Length > 0) System.IO.File.Delete(temporary); } catch { /* nothing else to recover */ }
        }
    }
}
