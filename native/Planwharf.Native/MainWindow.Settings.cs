using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Planwharf.Native.Core;
using Planwharf.Engine;

namespace Planwharf.Native;

// Settings, as in the chat page: this project, its tools, new projects, approvals, model switch,
// skills, moving a project, updates, start over, remove, quit.
public sealed partial class MainWindow
{
    JsonNode? autoSwitch, skills, updates, phone, appSettings;
    readonly HashSet<string> githubDeclined = new();

    // Extra tools for one project's team, all off by default (same words as the chat page).
    static (string Name, string Label, string Explain)[] Tools =>
    [
        ("mcp", Strings.T("MCP servers"), Strings.T("Lets models use external MCP tools configured on your machine.")),
        ("browser", Strings.T("Browser"), Strings.T("Allows models to open and inspect web pages in Chrome.")),
        ("hooks", Strings.T("Hooks"), Strings.T("Runs custom commands before or after a model acts.")),
        ("memory", Strings.T("Memory"), Strings.T("Keeps notes between sessions so models remember past context."))
    ];

    static string Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
    static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    // For the window tests: what the server would answer for these sections.
    public void ShowSettings(JsonNode autoSwitchState, JsonNode programs, JsonNode updateState, JsonNode? phoneState = null, JsonNode? applicationSettings = null)
    {
        autoSwitch = autoSwitchState; skills = programs; updates = updateState; phone = phoneState; appSettings = applicationSettings; sideSig = null; Render();
    }

    async Task LoadSettingsAsync(bool checkUpdates = false)
    {
        if (api == null) return;
        try { projectsRoot = await api.ProjectsRootAsync(); } catch { /* shown as empty */ }
        try { appSettings = await api.SettingsAsync(); } catch { appSettings = null; }
        try { autoSwitch = await api.AutoSwitchAsync(); } catch { /* section hidden */ }
        try { skills = (await api.SkillsAsync())["programs"]; } catch { skills = new JsonArray(); }
        try { updates = await api.UpdatesAsync(checkUpdates); } catch { /* offline checks never interrupt work */ }
        try { phone = await api.PhoneSettingsAsync(); } catch { phone = new JsonObject(); }
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
        + snapshot?["projectTools"]?[projectId ?? ""]?.ToJsonString() + autoSwitch?.ToJsonString() + skills?.ToJsonString() + updates?.ToJsonString() + phone?.ToJsonString() + appSettings?.ToJsonString();

    // The heading of a Settings section, with the same icon and colour as in the chat page.
    static Control SettingsHead(string text)
    {
        var (icon, color, soft) = text switch
        {
            var heading when heading == Strings.T("This project") => ("folder", Palette.Accent, Palette.AccentSoft),
            var heading when heading == Strings.T("Tools") => ("box", Palette.Lead, Palette.LeadSoft),
            var heading when heading == Strings.T("New projects") => ("plus", Palette.Build, Palette.BuildSoft),
            var heading when heading == Strings.T("Approvals") => ("verify", Palette.Verify, Palette.VerifySoft),
            var heading when heading == Strings.T("Model switch") => ("usage", Palette.Accent, Palette.AccentSoft),
            var heading when heading == Strings.T("Skills and instructions") => ("file", Palette.Accent, Palette.AccentSoft),
            var heading when heading == Strings.T("Move a project") => ("box", Palette.Build, Palette.BuildSoft),
            var heading when heading == Strings.T("Start over") => ("refresh", Palette.Build, Palette.BuildSoft),
            var heading when heading == Strings.T("Remove project") => ("trash", Palette.Danger, Palette.DangerSoft),
            var heading when heading == Strings.T("Quit") => ("power", Palette.Muted, Palette.Surface2),
            var heading when heading == Strings.T("Updates") => ("refresh", Palette.Accent, Palette.AccentSoft),
            var heading when heading == Strings.T("Look") => ("sun", Palette.Lead, Palette.LeadSoft),
            var heading when heading == Strings.T("Phone") => ("phone", Palette.Accent, Palette.AccentSoft),
            _ => ("settings", Palette.Muted, Palette.Surface2),
        };
        return Look.Head(icon, color, soft, text).With(head => head.Margin = new Thickness(0, 12, 0, 0));
    }

    static Button Small(string label, bool primary = false, bool danger = false)
    {
        var button = Look.Button(label, null, primary ? Look.Kind.Primary : danger ? Look.Kind.Danger : Look.Kind.Small);
        // .btn-small: the same size whatever its colour.
        button.Padding = new Thickness(10, 4); button.FontSize = 14; button.CornerRadius = new CornerRadius(8);
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
            side.Children.Add(SettingsHead(Strings.T("This project")));
            side.Children.Add(Explain(Strings.Format("Folder on your computer for {0}.", name)));
            var box = Look.Field(new TextBox { Text = folder, Watermark = OperatingSystem.IsWindows() ? @"C:\Projects\my-website" : "~/Projects/my-website" });
            var browse = Small(Strings.T("Browse\u2026"));
            browse.Click += async (_, _) => { if (await ProjectDialog.Pick(this) is { } path) box.Text = path; };
            side.Children.Add(box);
            side.Children.Add(browse);
            side.Children.Add(Explain(Strings.T("GitHub repository (optional)")));
            var offered = GithubOffer(row);
            var github = Look.Field(new TextBox { Text = Str(row["github"]) is { Length: > 0 } link ? link : offered, Watermark = "https://github.com/you/my-website" });
            side.Children.Add(github);
            if (offered.Length > 0) side.Children.Add(Explain(Strings.T("Filled in from this folder\u0027s git settings. Save to link it, or empty the field and save if you do not want a link.")));
            var save = Small(Strings.T("Save"), primary: true);
            save.Click += async (_, _) =>
            {
                var place = box.Text?.Trim() ?? "";
                if (place.Length == 0) { hint.Text = Strings.T("Enter a folder."); hint.Foreground = Palette.Warn; return; }
                var link = (github.Text ?? "").Trim().TrimEnd('/');
                if (link.EndsWith(".git")) link = link[..^4];
                if (link.Length == 0) githubDeclined.Add(Str(row["id"]));
                await DoAsync(() => api!.SetPlaceAsync(projectId!, place, link), Strings.T("Saved."));
                if (projectId != null) repoRaw.Remove(projectId);
            };
            var buttons = Row(save);
            if (Str(row["github"]) is { Length: > 0 } saved)
            {
                var open = Small(Strings.T("Open on GitHub"));
                open.Click += async (_, _) => await OpenLink(saved);
                buttons.Children.Add(open);
            }
            side.Children.Add(buttons);

        }

        // Everyday settings first: folders, language, look, phone, updates
        side.Children.Add(SettingsHead(Strings.T("New projects")));
        side.Children.Add(Explain(Strings.T("Default folder for new projects. When you leave the folder empty on a new project, Planwharf creates it here.")));
        var root = Look.Field(new TextBox { Text = projectsRoot ?? "", Watermark = OperatingSystem.IsWindows() ? @"C:\Projects" : "~/Projects" });
        var rootBrowse = Small(Strings.T("Browse\u2026"));
        rootBrowse.Click += async (_, _) => { if (await ProjectDialog.Pick(this) is { } path) root.Text = path; };
        var rootSave = Small(Strings.T("Save"), primary: true);
        rootSave.Click += async (_, _) => await DoAsync(async () => { await api!.SetProjectsRootAsync(root.Text?.Trim() ?? ""); projectsRoot = await api.ProjectsRootAsync(); }, Strings.T("Saved."));
        side.Children.Add(root);
        side.Children.Add(Row(rootBrowse, rootSave));

        var keepPlan = new ToggleSwitch { OnContent = Strings.T("Keep the work plan in git"), OffContent = Strings.T("Keep the work plan in git"), IsChecked = Flag(appSettings?["keepWorkPlan"]) };
        keepPlan.IsCheckedChanged += async (_, _) =>
        {
            if ((keepPlan.IsChecked == true) != Flag(appSettings?["keepWorkPlan"]))
                await DoAsync(async () => appSettings = await api!.SetKeepWorkPlanAsync(keepPlan.IsChecked == true), Strings.T("Work plan preference saved."));
        };
        side.Children.Add(keepPlan);

        // Light or dark, and in the dark theme the text colour, as in the chat page.
        side.Children.Add(SettingsHead(Strings.T("Look")));
        side.Children.Add(Explain(Strings.T("Language")));
        var language = new ComboBox
        {
            Name = "LanguagePicker",
            ItemsSource = new[] { Strings.T("English"), Strings.T("Italian") },
            SelectedIndex = Strings.CurrentLanguage == "it" ? 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        language.SelectionChanged += (_, _) =>
        {
            var selected = language.SelectedIndex == 1 ? "it" : "en";
            if (selected == Strings.CurrentLanguage) return;
            Strings.CurrentLanguage = selected;
            Prefs.Save();
            App.Restyle(this);
        };
        side.Children.Add(language);
        side.Children.Add(Explain(Strings.T("Alerts when work is done or needs you")));
        foreach (var alertName in new[] { nameof(AlertOptions.Notification), nameof(AlertOptions.Flash), nameof(AlertOptions.Sound) })
        {
            var enabled = alertName switch { "Notification" => Prefs.Alerts.Notification, "Flash" => Prefs.Alerts.Flash, _ => Prefs.Alerts.Sound };
            var alertToggle = new ToggleSwitch { OnContent = Strings.T(alertName), OffContent = Strings.T(alertName), IsChecked = enabled };
            Avalonia.Automation.AutomationProperties.SetName(alertToggle, Strings.Format("Alerts: {0}", alertName.ToLowerInvariant()));
            alertToggle.IsCheckedChanged += (_, _) =>
            {
                var onAlert = alertToggle.IsChecked == true;
                Prefs.Save(alerts: alertName switch { "Notification" => Prefs.Alerts with { Notification = onAlert },
                    "Flash" => Prefs.Alerts with { Flash = onAlert }, _ => Prefs.Alerts with { Sound = onAlert } });
                if (alertName == "Flash" && !onAlert) desktopAlerts?.ClearAttention();
            };
            side.Children.Add(alertToggle);
        }
        var themes = new WrapPanel();
        foreach (var (value, icon, label) in new[] { ("light", "sun", Strings.T("Light")), ("dark", "moon", Strings.T("Dark")) })
        {
            var chip = Look.Chip(label, icon, Palette.Accent, Palette.AccentSoft, Prefs.Theme == value);
            chip.Click += (_, _) => { if (Prefs.Theme == value) { chip.IsChecked = true; return; } Prefs.Save(theme: value); App.Restyle(this); };
            themes.Children.Add(chip);
        }
        side.Children.Add(themes);
        // The main colour of buttons, chips, the open project and links.
        side.Children.Add(Explain(Strings.T("Main colour")));
        var accents = new WrapPanel();
        foreach (var (key, label, darkShade, lightShade) in Palette.Accents)
        {
            var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Brush.Parse(Palette.Dark ? darkShade : lightShade), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            var chip = Look.Chip(label, null, Palette.Accent, Palette.AccentSoft, Prefs.Accent == key);
            chip.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } };
            chip.Click += (_, _) => { if (Prefs.Accent == key) { chip.IsChecked = true; return; } Prefs.Save(accent: key); App.Restyle(this); };
            accents.Children.Add(chip);
        }
        side.Children.Add(accents);
        if (Palette.Dark)
        {
            side.Children.Add(Explain(Strings.T("Text colour")));
            var inks = new WrapPanel();
            foreach (var (key, label, colour) in Palette.Inks)
            {
                var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Brush.Parse(colour), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                var chip = Look.Chip(label, null, Palette.Accent, Palette.AccentSoft, Prefs.Ink == key);
                chip.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } };
                chip.Click += (_, _) => { if (Prefs.Ink == key) { chip.IsChecked = true; return; } Prefs.Save(ink: key); App.Restyle(this); };
                inks.Children.Add(chip);
            }
            side.Children.Add(inks);
        }

        PhoneSection();
        UpdatesSection();

        // Advanced ones in a section closed by default: MCP, hooks, memory, browser, permissions, model switch
        var adv = new StackPanel { Spacing = 8 };

        if (row != null)
        {
            adv.Children.Add(SettingsHead(Strings.T("Tools")));
            var on = snapshot?["projectTools"]?[Str(row["id"])];
            foreach (var (tool, label, explain) in Tools)
            {
                var toggle = new ToggleSwitch { OnContent = label, OffContent = label, IsChecked = Flag(on?[tool]) };
                ToolTip.SetTip(toggle, explain);
                toggle.IsCheckedChanged += async (_, _) =>
                {
                    var wanted = toggle.IsChecked == true;
                    if (wanted == Flag(on?[tool])) return;
                    if (wanted && tool == "mcp" && !await Confirm(explain + Strings.T(" Turn it on for this project?"), Strings.T("Yes, turn on MCP servers"))) { sideSig = null; Render(); return; }
                    await DoAsync(() => api!.SetToolsAsync(projectId!, new Dictionary<string, bool> { [tool] = wanted }), wanted ? Strings.Format("{0} on for {1}.", label, name) : Strings.T("Turned off."));
                };
                adv.Children.Add(toggle);
                adv.Children.Add(Explain(explain));
            }
        }

        adv.Children.Add(SettingsHead(Strings.T("Approvals")));
        var auto = Str(snapshot?["mode"]) == "auto";
        adv.Children.Add(Explain(Strings.T("Choose whether the lead asks for your approval before starting work.")));
        var mode = new ToggleSwitch { OnContent = Strings.T("Let the lead start approved work by itself"), OffContent = Strings.T("Let the lead start approved work by itself"), IsChecked = auto };
        mode.IsCheckedChanged += async (_, _) => { if ((mode.IsChecked == true) != auto) await DoAsync(() => api!.SetModeAsync(mode.IsChecked == true ? "auto" : "manual"), Strings.T("Saved.")); };
        adv.Children.Add(mode);

        if (autoSwitch != null)
        {
            var enabled = autoSwitch["enabled"] is not JsonValue e || !e.TryGetValue<bool>(out var flag) || flag;
            int Percent(string key, int fallback) => autoSwitch[key] is JsonValue v && v.TryGetValue<double>(out var pct) ? (int)pct : fallback;
            var both = Percent("thresholdPercent", 35);
            var five = Percent("fiveHourPercent", both);
            var week = Percent("weeklyPercent", both);
            adv.Children.Add(SettingsHead(Strings.T("Model switch")));
            adv.Children.Add(Explain(Strings.T("Switches to another model automatically when quota runs low.")));
            var toggle = new ToggleSwitch { OnContent = Strings.T("Switch model automatically"), OffContent = Strings.T("Switch model automatically"), IsChecked = enabled };
            toggle.IsCheckedChanged += async (_, _) => { if ((toggle.IsChecked == true) != enabled) await DoAsync(async () => autoSwitch = await api!.SetAutoSwitchAsync(toggle.IsChecked == true), Strings.T("Model switch saved.")); };
            adv.Children.Add(toggle);
            if (enabled)
            {
                var atFive = new NumericUpDown { Minimum = 0, Maximum = 90, Increment = 5, Value = five, FormatString = "0", Width = 120 };
                var atWeek = new NumericUpDown { Minimum = 0, Maximum = 90, Increment = 5, Value = week, FormatString = "0", Width = 120 };
                var saveAt = Small(Strings.T("Save"));
                saveAt.Click += async (_, _) => await DoAsync(async () => autoSwitch = await api!.SetAutoSwitchAsync(true, (int)(atFive.Value ?? five), (int)(atWeek.Value ?? week)), Strings.T("Model switch saved."));
                adv.Children.Add(Row(new TextBlock { Text = Strings.T("5 hours"), VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Text }, atFive));
                adv.Children.Add(Row(new TextBlock { Text = Strings.T("Week"), VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Text, Width = 52 }, atWeek,
                    new TextBlock { Text = Strings.T("% left"), VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Text }, saveAt));
            }
        }

        if (skills is JsonArray programs && programs.Count > 0)
        {
            adv.Children.Add(SettingsHead(Strings.T("Skills and instructions")));
            adv.Children.Add(Explain(Strings.T("Skills are instructions a model reads when it needs them. The team uses the ones you have in each program. Add or change them there; Planwharf only shows and opens them.")));
            foreach (var program in programs.OfType<JsonObject>())
            {
                var list = (program["skills"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                var content = new StackPanel { Spacing = 4 };
                if (list.Count == 0) content.Children.Add(Explain(Str(program["skillsFolder"]).Length > 0 ? Strings.T("No skills yet.") : Strings.T("No skills folder for this program.")));
                foreach (var skill in list)
                {
                    var source = Str(skill["source"]);
                    content.Children.Add(Line(Str(skill["name"]) + (source.Length > 0 && source != "yours" ? $" ({Strings.T(source)})" : ""), Palette.Text, 14, FontWeight.SemiBold));
                    if (Str(skill["description"]).Length > 0) content.Children.Add(Explain(Str(skill["description"])));
                }
                var cli = Str(program["cli"]);
                var open = new WrapPanel();
                foreach (var (what, label, show) in new[] { ("skills", Strings.T("Open skills folder"), Str(program["skillsFolder"]).Length > 0), ("instructions", Strings.T("Edit instructions"), Str(program["instructions"]).Length > 0), ("settings", Strings.T("Open settings file"), Flag(program["settingsExist"])) })
                {
                    if (!show) continue;
                    var button = Small(label);
                    button.Margin = new Thickness(0, 4, 8, 0);
                    button.Click += async (_, _) => await DoAsync(async () => { var result = await api!.OpenSkillsAsync(cli, what); hint.Text = Strings.T("Opened ") + Str(result?["path"]); }, Strings.T("Opened."));
                    open.Children.Add(button);
                }
                content.Children.Add(open);
                var skillFold = Fold(Strings.Format(list.Count == 1 ? "{0} · {1} skill" : "{0} · {1} skills", Str(program["label"]), list.Count), content);
                skillFold.IsExpanded = false;
                adv.Children.Add(skillFold);
            }
            var again = Small(Strings.T("Read again"));
            again.Click += async (_, _) => await LoadSettingsAsync();
            adv.Children.Add(again);
        }

        var advFold = Fold(Strings.T("Advanced settings"), adv);
        advFold.IsExpanded = false;
        side.Children.Add(advFold);

        if (row != null)
        {
            side.Children.Add(SettingsHead(Strings.T("Start over")));
            side.Children.Add(Explain(Strings.Format("Clears the chat, plan and run history of {0} so you can begin again. The folder, its files, the GitHub link and the team stay. Nothing is deleted: the old history is kept in Planwharf\u0027s archive.", name)));
            var startOver = Small(Strings.T("Start over"));
            startOver.Click += async (_, _) => { if (await Confirm(Strings.Format("Start {0} over?", name), Strings.T("Yes, start over"))) await DoAsync(() => api!.StartOverAsync(projectId!), Strings.Format("{0} starts over. Tell the lead what to build.", name)); };
            side.Children.Add(startOver);
            side.Children.Add(SettingsHead(Strings.T("Remove project")));
            side.Children.Add(Explain(Strings.Format("Removes {0} from Planwharf. The folder and its files stay on your computer.", name)));
            var remove = Small(Strings.T("Remove project"), danger: true);
            remove.Click += async (_, _) =>
            {
                if (!await Confirm(Strings.Format("Remove {0} from Planwharf? The folder and its files stay on your computer.", name), Strings.T("Yes, remove it"))) return;
                await DoAsync(async () => { await api!.RemoveProjectAsync(projectId!); projectId = null; listSig = null; }, Strings.Format("{0} removed. Its folder is untouched.", name));
            };
            side.Children.Add(remove);
        }

        side.Children.Add(SettingsHead(Strings.T("Quit")));
        side.Children.Add(Explain(Strings.T("Unfinished work is saved and can be resumed next time.")));
        var quit = Small(Strings.T("Quit Planwharf"));
        quit.Click += async (_, _) => { if (await Confirm(Strings.T("Quit Planwharf now? Unfinished work is saved."), Strings.T("Yes, quit now"))) Close(); };
        side.Children.Add(quit);
        side.Children.Add(Explain(Strings.Format("Planwharf {0}, native app.", version)));
    }

    void UpdatesSection()
    {
        side.Children.Add(SettingsHead(Strings.T("Updates")));
        var check = Small(Strings.T("Check again"));
        check.Click += async (_, _) => { updates = new JsonObject { ["state"] = "checking" }; sideSig = null; Render(); await LoadSettingsAsync(checkUpdates: true); };
        var state = Str(updates?["state"]);
        var busy = state is "downloading" or "checkpointing" or "restarting" or "checking";
        var line = updates == null ? "" : busy ? state switch { "downloading" => Strings.T("Downloading the update\u2026"), "checkpointing" => Strings.T("Saving unfinished work\u2026"), "restarting" => Strings.T("Restarting\u2026"), _ => Strings.T("Checking\u2026") }
            : state == "error" ? Flag(updates["updateFailed"])
                ? (Str(updates["reason"]) is { Length: > 0 } why ? why : Strings.T("The update stopped. Nothing was changed."))
                : Strings.T("Could not check: ") + (Str(updates["reason"]) is { Length: > 0 } reason ? reason : Strings.T("unknown error"))
            : Flag(updates["available"]) ? Strings.Format("Planwharf {0} is available{1}.", Str(updates["latestVersion"]), (Str(updates["currentVersion"]) is { Length: > 0 } current ? Strings.Format(" (you have {0})", current) : ""))
            : Str(updates["currentVersion"]) is { Length: > 0 } now && state != "unchecked" ? Strings.Format("Planwharf {0} is up to date.", now) : "";
        if (line.Length > 0) side.Children.Add(Explain(line));
        check.IsEnabled = !busy;
        if (updates == null || state == "unchecked") check.Content = Strings.T("Check for updates");
        var buttons = Row(check);
        // A newer release is installed from its checked zip. The release page stays as a way to download it by hand.
        if (Flag(updates?["available"]))
        {
            var update = Small(Strings.T("Update now"), primary: true);
            update.IsEnabled = api != null && !busy;
            update.Click += async (_, _) => await UpdateNowAsync();
            buttons.Children.Insert(0, update);
            if (Str(updates?["releaseUrl"]) is { Length: > 0 } url)
            {
                var page = Small(Strings.T("Release page"));
                page.IsEnabled = !busy;
                page.Click += async (_, _) => await OpenLink(url);
                buttons.Children.Add(page);
            }
        }
        side.Children.Add(buttons);
    }

    void PhoneSection()
    {
        side.Children.Add(SettingsHead(Strings.T("Phone")));
        side.Children.Add(Explain(Strings.T("Alerts on your phone when work finishes or needs you, and short Telegram commands.")));

        var ntfyEnabled = Flag(phone?["ntfy"]?["enabled"]);
        var ntfyToggle = new ToggleSwitch { OnContent = Strings.T("ntfy alerts"), OffContent = Strings.T("ntfy alerts"), IsChecked = ntfyEnabled };
        var ntfyTopicBox = Look.Field(new TextBox { Text = Str(phone?["ntfy"]?["topic"]), Watermark = Strings.T("Topic name, e.g. pw-my-alerts") });
        side.Children.Add(ntfyToggle);
        side.Children.Add(Explain(Strings.T("ntfy (no account): pick a private topic name, then install the ntfy app and subscribe to it.")));
        side.Children.Add(ntfyTopicBox);

        var tgEnabled = Flag(phone?["telegram"]?["enabled"]);
        var tgToggle = new ToggleSwitch { OnContent = Strings.T("Telegram bot"), OffContent = Strings.T("Telegram bot"), IsChecked = tgEnabled };
        side.Children.Add(tgToggle);
        side.Children.Add(Explain(Strings.T("Create a bot with @BotFather and paste its token here.")));
        var tokenBox = Look.Field(new TextBox { Watermark = Flag(phone?["telegram"]?["tokenConfigured"]) ? Strings.Format("Token saved ({0}). Paste new token to replace.", Str(phone?["telegram"]?["tokenPreview"])) : Strings.T("Bot token from @BotFather") });
        side.Children.Add(tokenBox);

        var paired = Flag(phone?["telegram"]?["paired"]);
        if (paired)
        {
            var who = Str(phone?["telegram"]?["pairedUsername"]) is { Length: > 0 } u ? $" (@{u})" : "";
            side.Children.Add(Explain(Strings.Format("Connected to chat {0}{1}. Responds to /status, /stop \u003Cproject\u003E, /resume \u003Cproject\u003E.", Str(phone?["telegram"]?["pairedChatId"]), who)));
            var unpairBtn = Small(Strings.T("Unpair"));
            unpairBtn.Click += async (_, _) =>
            {
                await DoAsync(async () => {
                    phone = await api!.SetPhoneSettingsAsync(new { telegram = new { unpair = true } });
                    sideSig = null; Render();
                }, Strings.T("Telegram unpaired."));
            };
            side.Children.Add(unpairBtn);
        }
        else
        {
            var code = Str(phone?["telegram"]?["startCode"]);
            if (code.Length > 0)
            {
                side.Children.Add(Explain(Strings.Format("Open your bot in Telegram and send this code to connect:\n{0}", code)));
                var newCodeBtn = Small(Strings.T("New code"));
                newCodeBtn.Click += async (_, _) =>
                {
                    await DoAsync(async () => {
                        phone = await api!.SetPhoneSettingsAsync(new { telegram = new { resetStartCode = true } });
                        sideSig = null; Render();
                    }, Strings.T("New start code generated."));
                };
                side.Children.Add(newCodeBtn);
            }
        }

        var repliesOn = phone?["ollama"]?["enabled"] == null || Flag(phone?["ollama"]?["enabled"]);
        var repliesToggle = new ToggleSwitch { OnContent = Strings.T("Local replies"), OffContent = Strings.T("Local replies"), IsChecked = repliesOn };
        var savedModel = Str(phone?["ollama"]?["model"]);
        var modelBox = Look.Field(new TextBox { Text = savedModel.Length > 0 ? savedModel : "qwen2.5:3b", Watermark = "qwen2.5:3b" });
        side.Children.Add(repliesToggle);
        side.Children.Add(Explain(Strings.T("Local replies use Ollama on this computer. The default model is qwen2.5:3b. Planwharf does not restart Ollama or change other programs' models.")));
        side.Children.Add(modelBox);

        var saveBtn = Small(Strings.T("Save Phone settings"), primary: true);
        saveBtn.Click += async (_, _) =>
        {
            var topic = ntfyTopicBox.Text?.Trim() ?? "";
            var token = tokenBox.Text?.Trim();
            var patch = new Dictionary<string, object>
            {
                ["ntfy"] = new Dictionary<string, object>
                {
                    ["enabled"] = ntfyToggle.IsChecked == true,
                    ["topic"] = topic
                },
                ["telegram"] = new Dictionary<string, object>
                {
                    ["enabled"] = tgToggle.IsChecked == true
                },
                ["ollama"] = new Dictionary<string, object>
                {
                    ["enabled"] = repliesToggle.IsChecked == true,
                    ["model"] = string.IsNullOrWhiteSpace(modelBox.Text) ? "qwen2.5:3b" : modelBox.Text.Trim()
                }
            };
            if (!string.IsNullOrEmpty(token))
            {
                patch["telegramToken"] = token;
            }
            await DoAsync(async () => {
                phone = await api!.SetPhoneSettingsAsync(patch);
                sideSig = null; Render();
            }, Strings.T("Phone settings saved."));
        };

        var modelBtn = Small(Strings.T("Test local model"));
        modelBtn.Click += async (_, _) =>
        {
            await DoAsync(async () =>
            {
                var probed = await api!.ProbeOllamaAsync();
                if (!Flag(probed?["ok"]) || !Flag(probed?["installed"])) throw new InvalidOperationException(Strings.T("The local model did not answer. Commands on Telegram still work."));
            }, Strings.Format("Local model {0} is ready.", Str(phone?["ollama"]?["model"]) is { Length: > 0 } ready ? ready : "qwen2.5:3b"));
        };

        var testBtn = Small(Strings.T("Send test alert"));
        testBtn.Click += async (_, _) =>
        {
            await DoAsync(async () => {
                var sent = await api!.TestPhoneAlertAsync();
                var ntfy = sent?["result"]?["ntfy"];
                var telegram = sent?["result"]?["telegram"];
                if (ntfy == null && telegram == null) throw new InvalidOperationException(Strings.T("Turn on a phone channel and save it before sending a test."));
                if ((ntfy != null && !Flag(ntfy["ok"])) || (telegram != null && !Flag(telegram["ok"]))) throw new InvalidOperationException(Strings.T("The test alert could not be delivered."));
            }, Strings.T("Test alert sent."));
        };

        side.Children.Add(Row(saveBtn, testBtn, modelBtn));
    }

    // Downloads the checked native zip, saves running work the same way as Stop the crew, then quits so the new copy can replace this folder.
    async Task UpdateNowAsync()
    {
        if (updating || api == null) return;
        updating = true;
        try
        {
            if (!NativeUpdater.MayUpdate(NativeUpdater.StepIsRunning(snapshot), confirmed: false))
            {
                if (!await Confirm(Strings.T("A step is still running. Planwharf will save that work, then update. After it restarts, press Resume."), Strings.T("Save and update"))) return;
            }
            ShowUpdate("downloading");
            await NativeUpdater.RunAsync(NativeUpdater.SharedHttp, NativeUpdater.InstallFolder, version, snapshot, confirmed: true, async ids =>
            {
                ShowUpdate("checkpointing");
                foreach (var id in ids)
                {
                    try { await api.SetAutonomyAsync(id, false); } catch { /* the crew mode may already be off */ }
                    try { await api.StopAsync(id); }
                    catch (InvalidOperationException error) when (AlreadyStopped(error.Message)) { /* this project was already saved */ }
                }
            }, (exe, folder) => NativeUpdater.Launch(exe, folder), state => ShowUpdate(state));
            Close();
        }
        catch (Exception error)
        {
            ShowUpdate("error", error.Message);
            hint.Text = error.Message; hint.Foreground = Palette.Danger;
        }
        finally { updating = false; }
    }

    bool updating;

    static bool AlreadyStopped(string message) =>
        message.Contains("no running", StringComparison.OrdinalIgnoreCase) || message.Contains("nothing to stop", StringComparison.OrdinalIgnoreCase);

    void ShowUpdate(string state, string? reason = null)
    {
        var next = new JsonObject
        {
            ["state"] = state,
            ["available"] = true,
            ["updateFailed"] = state == "error",
            ["latestVersion"] = Str(updates?["latestVersion"]),
            ["currentVersion"] = Str(updates?["currentVersion"]),
            ["releaseUrl"] = Str(updates?["releaseUrl"])
        };
        if (!string.IsNullOrEmpty(reason)) next["reason"] = reason;
        updates = next; sideSig = null; Render();
    }

    async Task ExportAsync(string id, string name)
    {
        if (api == null) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.Format("Save {0}", name), SuggestedFileName = System.Text.RegularExpressions.Regex.Replace(name, @"[^\w.-]+", "-") + "-planwharf.json", DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType(Strings.T("Planwharf project")) { Patterns = ["*.json"] }]
            });
            if (file == null) return;
            var json = await api.ExportProjectAsync(id);
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            hint.Text = Strings.Format("{0} saved to {1}.", name, file.Name); hint.Foreground = Palette.Accent;
        }
        catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; }
    }

    async Task ImportAsync()
    {
        if (api == null) return;
        try
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Strings.T("Open a project file"), AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(Strings.T("Planwharf project")) { Patterns = ["*.json"] }] });
            if (picked.FirstOrDefault() is not { } file) return;
            if ((await file.GetBasicPropertiesAsync()).Size > 4 * 1024 * 1024) throw new InvalidOperationException(Strings.T("This file is larger than 4 MB."));
            await using var stream = await file.OpenReadAsync();
            var (id, resume) = await api.ImportProjectAsync(await new StreamReader(stream).ReadToEndAsync());
            if (id != null) projectId = id;
            listSig = threadSig = sideSig = null;
            hint.Text = resume ? Strings.T("Project opened. Check its folder in Settings, then press Resume.") : Strings.T("Project opened. Check its folder in Settings."); hint.Foreground = Palette.Accent;
            await RefreshAsync(readOnly: false);
        }
        catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; }
    }
}
