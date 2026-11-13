using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Planwharf.Native.Core;

namespace Planwharf.Native;

// Settings, as in the chat page: this project, its tools, new projects, approvals, model switch,
// skills, moving a project, updates, start over, remove, quit.
public sealed partial class MainWindow
{
    JsonNode? autoSwitch, skills, updates;
    readonly HashSet<string> githubDeclined = new();

    // Extra tools for one project's team, all off by default (same words as the chat page).
    static readonly (string Name, string Label, string Explain)[] Tools =
    [
        ("mcp", "MCP servers", "Your own MCP servers, as set up in Claude Code and Codex. They can act outside the project folder, and a server that controls the computer can move your mouse and type."),
        ("browser", "Browser", "Claude in Chrome: open and try web pages in a browser tab. It does not move your mouse."),
        ("hooks", "Hooks", "Your Claude Code hooks: commands that run by themselves before or after the model acts."),
        ("memory", "Memory", "Notes Claude keeps for itself from one session to the next.")
    ];

    static string Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    // For the window tests: what the server would answer for these sections.
    public void ShowSettings(JsonNode autoSwitchState, JsonNode programs, JsonNode updateState)
    {
        autoSwitch = autoSwitchState; skills = programs; updates = updateState; sideSig = null; Render();
    }

    async Task LoadSettingsAsync(bool checkUpdates = false)
    {
        if (api == null) return;
        try { projectsRoot = await api.ProjectsRootAsync(); } catch { /* shown as empty */ }
        try { autoSwitch = await api.AutoSwitchAsync(); } catch { /* section hidden */ }
        try { skills = (await api.SkillsAsync())["programs"]; } catch { skills = new JsonArray(); }
        try { updates = await api.UpdatesAsync(checkUpdates); } catch { /* offline checks never interrupt work */ }
        if (projectId != null && !repoRaw.ContainsKey(projectId)) _ = LoadRepoAsync(projectId, CurrentWorkId(), false);
        sideSig = null; Render();
    }

    JsonObject? CurrentRow() => (snapshot?["projects"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => Str(item["id"]) == projectId);

    // The folder's own GitHub origin is offered once when the project has no link.
    string GithubOffer(JsonObject? row)
    {
        if (row == null || Str(row["github"]).Length > 0 || Str(row["folder"]).Length == 0 || githubDeclined.Contains(Str(row["id"]))) return "";
        var origin = projectId != null && repoRaw.TryGetValue(projectId, out var raw) ? Str(raw.Json?["local"]?["originRepository"]) : "";
        return origin.Length > 0 ? "https://github.com/" + origin : "";
    }

    string SettingsSignature() => (CurrentRow()?["github"]?.ToJsonString() ?? "") + GithubOffer(CurrentRow()) + snapshot?["mode"]?.ToJsonString()
        + snapshot?["projectTools"]?[projectId ?? ""]?.ToJsonString() + autoSwitch?.ToJsonString() + skills?.ToJsonString() + updates?.ToJsonString();

    // The heading of a Settings section, with the same icon and colour as in the chat page.
    static Control SettingsHead(string text)
    {
        var (icon, color, soft) = text switch
        {
            "This project" => ("folder", Palette.Accent, Palette.AccentSoft),
            "Tools" => ("box", Palette.Lead, Palette.LeadSoft),
            "New projects" => ("plus", Palette.Build, Palette.BuildSoft),
            "Approvals" => ("verify", Palette.Verify, Palette.VerifySoft),
            "Model switch" => ("usage", Palette.Accent, Palette.AccentSoft),
            "Skills and instructions" => ("file", Palette.Accent, Palette.AccentSoft),
            "Move a project" => ("box", Palette.Build, Palette.BuildSoft),
            "Start over" => ("refresh", Palette.Build, Palette.BuildSoft),
            "Remove project" => ("trash", Palette.Danger, Palette.DangerSoft),
            "Quit" => ("power", Palette.Muted, Palette.Surface2),
            "Updates" => ("refresh", Palette.Accent, Palette.AccentSoft),
            _ => ("settings", Palette.Muted, Palette.Surface2),
        };
        return Look.Head(icon, color, soft, text).With(head => head.Margin = new Thickness(0, 12, 0, 0));
    }

    static Button Small(string label, bool primary = false, bool danger = false)
    {
        var button = Look.Button(label, null, primary ? Look.Kind.Primary : danger ? Look.Kind.Danger : Look.Kind.Small);
        // .btn-small: the same size whatever its colour.
        button.Padding = new Thickness(10, 4); button.FontSize = 13; button.CornerRadius = new CornerRadius(8);
        return button;
    }

    static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    async Task<bool> Confirm(string question, string yes) => await new ConfirmDialog(question, yes).ShowDialog<bool>(this);

    async Task OpenLink(string url)
    {
        try { await Launcher.LaunchUriAsync(new Uri(url)); } catch (Exception error) { hint.Text = error.Message; }
    }

    void SettingsPanel(string folder, string name)
    {
        var row = CurrentRow();
        if (row != null)
        {
            side.Children.Add(SettingsHead("This project"));
            side.Children.Add(Explain($"Folder on your computer for {name}."));
            var box = Look.Field(new TextBox { Text = folder, Watermark = OperatingSystem.IsWindows() ? @"C:\Projects\my-website" : "~/Projects/my-website" });
            var browse = Small("Browse…");
            browse.Click += async (_, _) => { if (await ProjectDialog.Pick(this) is { } path) box.Text = path; };
            side.Children.Add(box);
            side.Children.Add(browse);
            side.Children.Add(Explain("GitHub repository (optional)"));
            var offered = GithubOffer(row);
            var github = Look.Field(new TextBox { Text = Str(row["github"]) is { Length: > 0 } link ? link : offered, Watermark = "https://github.com/you/my-website" });
            side.Children.Add(github);
            if (offered.Length > 0) side.Children.Add(Explain("Filled in from this folder's git settings. Save to link it, or empty the field and save if you do not want a link."));
            var save = Small("Save", primary: true);
            save.Click += async (_, _) =>
            {
                var place = box.Text?.Trim() ?? "";
                if (place.Length == 0) { hint.Text = "Enter a folder."; hint.Foreground = Palette.Warn; return; }
                var link = (github.Text ?? "").Trim().TrimEnd('/');
                if (link.EndsWith(".git")) link = link[..^4];
                if (link.Length == 0) githubDeclined.Add(Str(row["id"]));
                await DoAsync(() => api!.SetPlaceAsync(projectId!, place, link), "Saved.");
                if (projectId != null) repoRaw.Remove(projectId);
            };
            var buttons = Row(save);
            if (Str(row["github"]) is { Length: > 0 } saved)
            {
                var open = Small("Open on GitHub");
                open.Click += async (_, _) => await OpenLink(saved);
                buttons.Children.Add(open);
            }
            side.Children.Add(buttons);

            side.Children.Add(SettingsHead("Tools"));
            side.Children.Add(Explain($"Extra tools for the team of {name}. All off by default. Claude follows all four, Codex follows MCP, Grok uses its own settings. Subagents stay off: the lead splits the work."));
            var on = snapshot?["projectTools"]?[Str(row["id"])];
            foreach (var (tool, label, explain) in Tools)
            {
                var toggle = new ToggleSwitch { OnContent = label, OffContent = label, IsChecked = Flag(on?[tool]) };
                ToolTip.SetTip(toggle, explain);
                toggle.IsCheckedChanged += async (_, _) =>
                {
                    var wanted = toggle.IsChecked == true;
                    if (wanted == Flag(on?[tool])) return;
                    // MCP servers can act outside the project folder: the user says yes knowing that.
                    if (wanted && tool == "mcp" && !await Confirm(explain + " Turn it on for this project?", "Yes, turn on MCP servers")) { sideSig = null; Render(); return; }
                    await DoAsync(() => api!.SetToolsAsync(projectId!, new Dictionary<string, bool> { [tool] = wanted }), wanted ? $"{label} on for {name}." : "Turned off.");
                };
                side.Children.Add(toggle);
            }
        }

        side.Children.Add(SettingsHead("New projects"));
        side.Children.Add(Explain("Default folder for new projects. When you leave the folder empty on a new project, Planwharf creates it here."));
        var root = Look.Field(new TextBox { Text = projectsRoot ?? "", Watermark = OperatingSystem.IsWindows() ? @"C:\Projects" : "~/Projects" });
        var rootBrowse = Small("Browse…");
        rootBrowse.Click += async (_, _) => { if (await ProjectDialog.Pick(this) is { } path) root.Text = path; };
        var rootSave = Small("Save", primary: true);
        rootSave.Click += async (_, _) => await DoAsync(async () => { await api!.SetProjectsRootAsync(root.Text?.Trim() ?? ""); projectsRoot = await api.ProjectsRootAsync(); }, "Saved.");
        side.Children.Add(root);
        side.Children.Add(Row(rootBrowse, rootSave));

        side.Children.Add(SettingsHead("Approvals"));
        var auto = Str(snapshot?["mode"]) == "auto";
        side.Children.Add(Explain(auto ? "The lead may start approved work without asking each time." : "The lead asks you before each piece of work starts."));
        var mode = new ToggleSwitch { OnContent = "Let the lead start approved work by itself", OffContent = "Let the lead start approved work by itself", IsChecked = auto };
        mode.IsCheckedChanged += async (_, _) => { if ((mode.IsChecked == true) != auto) await DoAsync(() => api!.SetModeAsync(mode.IsChecked == true ? "auto" : "manual"), "Saved."); };
        side.Children.Add(mode);

        if (autoSwitch != null)
        {
            var enabled = autoSwitch["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var flag) || flag;
            var threshold = autoSwitch["thresholdPercent"] is JsonValue t && t.TryGetValue<double>(out var pct) ? (int)pct : 20;
            side.Children.Add(SettingsHead("Model switch"));
            side.Children.Add(Explain(enabled ? $"When a model working on a step drops to {threshold}% quota left or less, Planwharf hands the step to another model with the same role and more quota, with a handoff of what was done."
                : "Off: a model that runs out of quota stops its step, and you choose the next model."));
            var toggle = new ToggleSwitch { OnContent = "Switch model automatically", OffContent = "Switch model automatically", IsChecked = enabled };
            toggle.IsCheckedChanged += async (_, _) => { if ((toggle.IsChecked == true) != enabled) await DoAsync(async () => autoSwitch = await api!.SetAutoSwitchAsync(toggle.IsChecked == true), "Model switch saved."); };
            side.Children.Add(toggle);
            if (enabled)
            {
                var at = new NumericUpDown { Minimum = 0, Maximum = 90, Increment = 5, Value = threshold, FormatString = "0", Width = 130 };
                var saveAt = Small("Save");
                saveAt.Click += async (_, _) => await DoAsync(async () => autoSwitch = await api!.SetAutoSwitchAsync(true, (int)(at.Value ?? threshold)), "Model switch saved.");
                side.Children.Add(Row(new TextBlock { Text = "Switch at", VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Text }, at,
                    new TextBlock { Text = "% left", VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Text }, saveAt));
            }
        }

        if (skills is JsonArray programs && programs.Count > 0)
        {
            side.Children.Add(SettingsHead("Skills and instructions"));
            side.Children.Add(Explain("Skills are instructions a model reads when it needs them. The team uses the ones you have in each program. Add or change them there; Planwharf only shows and opens them."));
            foreach (var program in programs.OfType<JsonObject>())
            {
                var list = (program["skills"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                var content = new StackPanel { Spacing = 4 };
                if (list.Count == 0) content.Children.Add(Explain(Str(program["skillsFolder"]).Length > 0 ? "No skills yet." : "No skills folder for this program."));
                foreach (var skill in list)
                {
                    var source = Str(skill["source"]);
                    content.Children.Add(Line(Str(skill["name"]) + (source.Length > 0 && source != "yours" ? $" ({source})" : ""), Palette.Text, 13, FontWeight.SemiBold));
                    if (Str(skill["description"]).Length > 0) content.Children.Add(Explain(Str(skill["description"])));
                }
                var cli = Str(program["cli"]);
                var open = new WrapPanel();
                foreach (var (what, label, show) in new[] { ("skills", "Open skills folder", Str(program["skillsFolder"]).Length > 0), ("instructions", "Edit instructions", Str(program["instructions"]).Length > 0), ("settings", "Open settings file", Flag(program["settingsExist"])) })
                {
                    if (!show) continue;
                    var button = Small(label);
                    button.Margin = new Thickness(0, 4, 8, 0);
                    button.Click += async (_, _) => await DoAsync(async () => { var result = await api!.OpenSkillsAsync(cli, what); hint.Text = "Opened " + Str(result?["path"]); }, "Opened.");
                    open.Children.Add(button);
                }
                content.Children.Add(open);
                var fold = Fold($"{Str(program["label"])} · {list.Count} skill{(list.Count == 1 ? "" : "s")}", content);
                fold.IsExpanded = false;
                side.Children.Add(fold);
            }
            var again = Small("Read again");
            again.Click += async (_, _) => await LoadSettingsAsync();
            side.Children.Add(again);
        }

        side.Children.Add(SettingsHead("Move a project"));
        side.Children.Add(Explain("Save a project to a file, or open one saved on another computer. Code stays in the project folder; the file holds the plan, history and saved points."));
        var move = new WrapPanel();
        if (row != null)
        {
            var export = Small($"Save {name} to a file");
            export.Margin = new Thickness(0, 0, 8, 6);
            export.Click += async (_, _) => await ExportAsync(Str(row["id"]), name);
            move.Children.Add(export);
        }
        var import = Small("Open a project file");
        import.Margin = new Thickness(0, 0, 8, 6);
        import.Click += async (_, _) => await ImportAsync();
        move.Children.Add(import);
        side.Children.Add(move);

        UpdatesSection();

        if (row != null)
        {
            side.Children.Add(SettingsHead("Start over"));
            side.Children.Add(Explain($"Clears the chat, plan and run history of {name} so you can begin again. The folder, its files, the GitHub link and the team stay. Nothing is deleted: the old history is kept in Planwharf's archive."));
            var startOver = Small("Start over");
            startOver.Click += async (_, _) => { if (await Confirm($"Start {name} over?", "Yes, start over")) await DoAsync(() => api!.StartOverAsync(projectId!), $"{name} starts over. Tell the lead what to build."); };
            side.Children.Add(startOver);
            side.Children.Add(SettingsHead("Remove project"));
            side.Children.Add(Explain($"Removes {name} from Planwharf. The folder and its files stay on your computer."));
            var remove = Small("Remove project", danger: true);
            remove.Click += async (_, _) =>
            {
                if (!await Confirm($"Remove {name} from Planwharf? The folder and its files stay on your computer.", "Yes, remove it")) return;
                await DoAsync(async () => { await api!.RemoveProjectAsync(projectId!); projectId = null; listSig = null; }, $"{name} removed. Its folder is untouched.");
            };
            side.Children.Add(remove);
        }

        side.Children.Add(SettingsHead("Quit"));
        side.Children.Add(Explain("Unfinished work is saved and can be resumed next time."));
        var quit = Small("Quit Planwharf");
        quit.Click += async (_, _) => { if (await Confirm("Quit Planwharf now? Unfinished work is saved.", "Yes, quit now")) Close(); };
        side.Children.Add(quit);
        side.Children.Add(Explain($"Planwharf {version}, native app."));
    }

    void UpdatesSection()
    {
        side.Children.Add(SettingsHead("Updates"));
        var check = Small("Check again");
        check.Click += async (_, _) => { updates = new JsonObject { ["state"] = "checking" }; sideSig = null; Render(); await LoadSettingsAsync(checkUpdates: true); };
        var state = Str(updates?["state"]);
        var busy = state is "downloading" or "checkpointing" or "restarting" or "checking";
        var line = updates == null ? "" : busy ? state switch { "downloading" => "Downloading the update…", "checkpointing" => "Saving unfinished work…", "restarting" => "Restarting…", _ => "Checking…" }
            : state == "error" ? "Could not check: " + (Str(updates["reason"]) is { Length: > 0 } reason ? reason : "unknown error")
            : Flag(updates["available"]) ? $"Planwharf {Str(updates["latestVersion"])} is available{(Str(updates["currentVersion"]) is { Length: > 0 } current ? $" (you have {current})" : "")}."
            : Str(updates["currentVersion"]) is { Length: > 0 } now && state != "unchecked" ? $"Planwharf {now} is up to date." : "";
        if (line.Length > 0) side.Children.Add(Explain(line));
        check.IsEnabled = !busy;
        if (updates == null || state == "unchecked") check.Content = "Check for updates";
        var buttons = Row(check);
        // The native app is replaced by downloading the new version, like the desktop installer.
        if (Flag(updates?["available"]) && Str(updates?["releaseUrl"]) is { Length: > 0 } url)
        {
            var download = Small("Download it", primary: true);
            download.Click += async (_, _) => await OpenLink(url);
            buttons.Children.Insert(0, download);
        }
        side.Children.Add(buttons);
    }

    async Task ExportAsync(string id, string name)
    {
        if (api == null) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Save {name}", SuggestedFileName = System.Text.RegularExpressions.Regex.Replace(name, @"[^\w.-]+", "-") + "-planwharf.json", DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("Planwharf project") { Patterns = ["*.json"] }]
            });
            if (file == null) return;
            var json = await api.ExportProjectAsync(id);
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            hint.Text = $"{name} saved to {file.Name}."; hint.Foreground = Palette.Accent;
        }
        catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; }
    }

    async Task ImportAsync()
    {
        if (api == null) return;
        try
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open a project file", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Planwharf project") { Patterns = ["*.json"] }] });
            if (picked.FirstOrDefault() is not { } file) return;
            if ((await file.GetBasicPropertiesAsync()).Size > 4 * 1024 * 1024) throw new InvalidOperationException("This file is larger than 4 MB.");
            await using var stream = await file.OpenReadAsync();
            var (id, resume) = await api.ImportProjectAsync(await new StreamReader(stream).ReadToEndAsync());
            if (id != null) projectId = id;
            listSig = threadSig = sideSig = null;
            hint.Text = resume ? "Project opened. Check its folder in Settings, then press Resume." : "Project opened. Check its folder in Settings."; hint.Foreground = Palette.Accent;
            await RefreshAsync(readOnly: false);
        }
        catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; }
    }
}
