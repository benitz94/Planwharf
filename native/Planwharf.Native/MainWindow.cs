using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Planwharf.Native.Core;

namespace Planwharf.Native;

// The main window: projects on the left, the chat with the lead and the crew's work in the middle,
// and Team or Settings on the right when opened.
public sealed class MainWindow : Window
{
    readonly ListBox projectList = new() { Background = Brushes.Transparent };
    readonly TextBlock title = new() { FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Palette.Text, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock badgeText = new() { FontSize = 12, Foreground = Palette.Accent };
    readonly StackPanel thread = new() { Spacing = 12, Margin = new Thickness(24, 16), MaxWidth = 860 };
    readonly ScrollViewer threadScroll;
    readonly Button actionButton = new() { Background = Palette.Accent, Foreground = Palette.AccentText, FontWeight = FontWeight.SemiBold, Padding = new Thickness(16, 8) };
    readonly TextBlock hint = new() { Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly TextBox input = new() { Watermark = "Tell the lead what you want built…", AcceptsReturn = false, MinHeight = 40 };
    readonly Button send = new() { Content = "Send", Background = Palette.Accent, Foreground = Palette.AccentText, Padding = new Thickness(16, 8), Margin = new Thickness(8, 0, 0, 0) };
    readonly ToggleButton teamButton = new() { Content = "Team", Margin = new Thickness(0, 0, 8, 0) };
    readonly ToggleButton settingsButton = new() { Content = "Settings" };
    readonly StackPanel side = new() { Spacing = 10, Margin = new Thickness(16) };
    readonly Border sidePanel;
    readonly string version;

    PlanwharfApi? api;
    JsonNode? snapshot;
    string? projectId, pending, threadSig, listSig, sideSig, sideMode, projectsRoot;
    bool busy;
    int ticks;

    public MainWindow(string version)
    {
        this.version = version;
        Title = "Planwharf"; Width = 1360; Height = 860; MinWidth = 960; MinHeight = 560; Background = Palette.Background;
        threadScroll = new ScrollViewer { Content = thread, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(16, 16, 16, 12), Children = {
            new TextBlock { Text = "Planwharf", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Palette.Text },
            new TextBlock { Text = version, FontSize = 12, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3) } } };
        var newProject = new Button { Content = "+ New project", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = Palette.Accent, Foreground = Palette.AccentText, FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 0, 12, 10), Padding = new Thickness(8) };
        newProject.Click += async (_, _) => await NewProjectAsync();
        projectList.SelectionChanged += (_, _) => { if (projectList.SelectedItem is ListBoxItem { Tag: string id } && id != projectId) { projectId = id; threadSig = sideSig = null; _ = RefreshAsync(readOnly: true); } };
        var top = new StackPanel { Children = { brand, newProject } };
        var left = new Border { Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = new DockPanel { Children = { top, projectList } } };
        DockPanel.SetDock(top, Dock.Top);

        var badge = new Border { Background = Brush.Parse("#1d3328"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = badgeText };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { teamButton, settingsButton } };
        var headerRow = new DockPanel { Children = { tools, new StackPanel { Orientation = Orientation.Horizontal, Children = { title, badge } } } };
        DockPanel.SetDock(tools, Dock.Right);
        var header = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(24, 12), Child = headerRow };
        teamButton.Click += (_, _) => OpenSide(teamButton.IsChecked == true ? "team" : null);
        settingsButton.Click += (_, _) => OpenSide(settingsButton.IsChecked == true ? "settings" : null);

        var actions = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Children = { actionButton, hint } };
        DockPanel.SetDock(actionButton, Dock.Left);
        var composer = new DockPanel { Children = { send, input } };
        DockPanel.SetDock(send, Dock.Right);
        var footer = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 12), Child = new StackPanel { MaxWidth = 860, Children = { actions, composer,
            new TextBlock { Text = "Talk with the lead to shape the plan, then press Let the crew do it. The crew builds, checks and stops when the goal is reached.", Foreground = Palette.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) } } } };
        var middle = new DockPanel { Children = { header, footer, threadScroll } };
        DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);
        sidePanel = new Border { Width = 360, IsVisible = false, Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new ScrollViewer { Content = side, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*,Auto") };
        grid.Children.Add(left); grid.Children.Add(middle); grid.Children.Add(sidePanel);
        Grid.SetColumn(middle, 1); Grid.SetColumn(sidePanel, 2);
        Content = grid;

        send.Click += async (_, _) => await SendAsync();
        input.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await SendAsync(); };
        actionButton.Click += async (_, _) => await ActAsync();
        title.Text = "Starting Planwharf…"; hint.Text = "Starting the local Planwharf server.";
        actionButton.Content = "Let the crew do it"; actionButton.IsEnabled = send.IsEnabled = false;
    }

    public void Connect(PlanwharfApi connected)
    {
        api = connected;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) => await RefreshAsync(readOnly: ticks++ % 5 != 0);
        timer.Start();
        _ = RefreshAsync(readOnly: false);
    }

    public void ShowError(string message) { title.Text = "Planwharf could not start"; hint.Text = message; hint.Foreground = Palette.Danger; }

    // Shows a snapshot of the server's state; the window redraws only what changed.
    public void Show(JsonNode state) { snapshot = state; Render(); }

    public void OpenSide(string? mode)
    {
        if (mode == "settings" && api != null) _ = LoadRootAsync();
        sideMode = mode; sideSig = null;
        teamButton.IsChecked = mode == "team"; settingsButton.IsChecked = mode == "settings";
        sidePanel.IsVisible = mode != null;
        Render();
    }

    async Task LoadRootAsync()
    {
        try { projectsRoot = await api!.ProjectsRootAsync(); sideSig = null; Render(); } catch { /* shown as empty */ }
    }

    async Task RefreshAsync(bool readOnly)
    {
        if (api == null) return;
        try { Show(await api.SnapshotAsync(readOnly, projectId)); }
        catch (Exception error) { hint.Text = "Planwharf is not answering: " + error.Message; }
    }

    void Render()
    {
        if (snapshot == null) return;
        var projects = ProjectViews.Projects(snapshot);
        if (projectId == null || projects.All(p => p.Id != projectId)) projectId = projects.FirstOrDefault()?.Id;
        var sig = string.Join("|", projects.Select(p => p.Id + p.Name + p.Status)) + projectId;
        if (sig != listSig)
        {
            listSig = sig;
            projectList.ItemsSource = projects.Select(p => new ListBoxItem { Tag = p.Id, Padding = new Thickness(12, 8), Content = new StackPanel { Children = {
                new TextBlock { Text = p.Name, Foreground = Palette.Text, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = p.Status, Foreground = Palette.Muted, FontSize = 12 } } } }).ToList();
            projectList.SelectedIndex = projects.ToList().FindIndex(p => p.Id == projectId);
        }
        var view = ProjectViews.Project(snapshot, projectId);
        if (view == null) { title.Text = "Welcome"; RenderSide(null); hint.Text = "Create a project to begin: + New project."; actionButton.IsEnabled = false; return; }
        title.Text = view.Name; badgeText.Text = view.Status;
        // Before the crew can start: a folder, a lead and a builder, as in the chat page.
        var missing = view.Action == CrewAction.LetTheCrewDoIt ? TeamViews.Missing(snapshot, projectId) : [];
        actionButton.Content = view.ActionText;
        actionButton.Background = view.Action is CrewAction.StopTheCrew or CrewAction.StopWork ? Palette.Danger : Palette.Accent;
        actionButton.IsEnabled = !busy && missing.Count == 0;
        send.IsEnabled = !busy && pending == null;
        if (pending == null) { hint.Text = missing.FirstOrDefault() ?? view.Hint; hint.Foreground = missing.Count > 0 ? Palette.Warn : Palette.Muted; }
        RenderThread(view);
        RenderSide(view);
    }

    void RenderThread(ProjectView view)
    {
        var team = TeamViews.Team(snapshot!);
        var sig = string.Join("|", view.Messages.Select(m => m.At.ToUnixTimeMilliseconds() + m.Text.Length)) + pending + view.NextGoal
            + (view.Work is { } w ? w.Title + w.Status + string.Join(",", w.Steps.Select(s => s.ModelId + s.Status)) + w.Results.Count : "")
            + string.Join(",", team.Models.Select(m => m.Id + m.Build + m.Verify + m.Review + m.QuotaLeft));
        if (sig == threadSig) return;
        var atBottom = threadScroll.Offset.Y + threadScroll.Viewport.Height >= threadScroll.Extent.Height - 120;
        threadSig = sig;
        thread.Children.Clear();
        if (view.Messages.Count == 0 && view.Work == null && pending == null)
            thread.Children.Add(Note("Tell the lead in the chat what you want built. The lead helps you shape the plan and suggests a goal; then press Let the crew do it."));
        foreach (var message in view.Messages) thread.Children.Add(Bubble(message.FromUser, message.Who, message.Text));
        if (pending != null) { thread.Children.Add(Bubble(true, "You", pending)); thread.Children.Add(Bubble(false, "Lead", "Thinking…")); }
        else if (view.NextGoal != null && view.Action == CrewAction.LetTheCrewDoIt)
            thread.Children.Add(Note($"Nothing has started yet. When the plan is right, press Let the crew do it: the crew will work on\n{view.NextGoal}"));
        if (view.Work != null) thread.Children.Add(WorkCard(view.Work, team));
        if (atBottom || pending != null) Dispatcher.UIThread.Post(() => threadScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    static Control Bubble(bool fromUser, string who, string text)
    {
        var body = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = fromUser ? Palette.AccentText : Palette.Text, Inlines = new Avalonia.Controls.Documents.InlineCollection() };
        foreach (var run in TextRuns.Parse(text))
            body.Inlines.Add(new Avalonia.Controls.Documents.Run(run.Text) { FontWeight = run.Bold ? FontWeight.Bold : FontWeight.Normal, FontFamily = run.Code ? new FontFamily("Consolas, Menlo, monospace") : FontFamily.Default });
        var bubble = new Border { Background = fromUser ? Palette.UserBubble : Palette.Card, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Child = body, MaxWidth = 680,
            HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        return fromUser ? bubble : new StackPanel { Spacing = 4, Children = { new TextBlock { Text = who, Foreground = Palette.Muted, FontSize = 12 }, bubble } };
    }

    static Control Note(string text) => new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Muted } };

    Control WorkCard(WorkView work, TeamView team)
    {
        var done = work.Status == "COMPLETE";
        var panel = new StackPanel { Spacing = 8, Children = {
            new TextBlock { Text = done ? "Work finished" : work.Status is "PAUSED" or "FAILED" ? "Work stopped" : work.Status == "READY_FOR_APPROVAL" ? "The lead proposes this work" : "Crew at work", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Palette.Text },
            new TextBlock { Text = work.Title, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text },
            new ProgressBar { Minimum = 0, Maximum = Math.Max(1, work.Total), Value = work.Done, Height = 6, Foreground = Palette.Accent },
            new TextBlock { Text = $"{work.Done} of {work.Total} steps done", Foreground = Palette.Muted, FontSize = 12 } } };
        foreach (var step in work.Steps)
        {
            Control model = new TextBlock { Text = step.Model, Foreground = Palette.Text, VerticalAlignment = VerticalAlignment.Center };
            // Open work: the model of a step that is not done can be changed, among the team models with that role.
            if (!done && step.Status != "done")
            {
                var choices = TeamViews.ModelsFor(team, step.Role).ToList();
                if (choices.All(m => m.Id != step.ModelId)) choices.Insert(0, new TeamModel(step.ModelId, step.Model, "", null, true, null, false, false, false, false, false));
                var menu = new ComboBox { ItemsSource = choices.Select(m => m.Label + (m.QuotaLeft is { } left ? $" · {left}% left" : "")).ToList(), SelectedIndex = choices.FindIndex(m => m.Id == step.ModelId), MinWidth = 220 };
                menu.SelectionChanged += async (_, _) => { if (menu.SelectedIndex >= 0 && choices[menu.SelectedIndex].Id != step.ModelId) await ChangeStepAsync(step, choices[menu.SelectedIndex]); };
                model = menu;
            }
            var row = new DockPanel { Children = {
                new TextBlock { Text = step.Status, Foreground = step.Status == "done" ? Palette.Accent : step.Status is "blocked" or "failed" ? Palette.Danger : Palette.Warn, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = step.Role + " ·", Foreground = Palette.Text, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, model } } } };
            panel.Children.Add(new Border { Background = Palette.Card, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = row });
        }
        if (done && work.Results.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "What was done", FontWeight = FontWeight.SemiBold, Foreground = Palette.Text, Margin = new Thickness(0, 4, 0, 0) });
            foreach (var result in work.Results) panel.Children.Add(new TextBlock { Text = "• " + result, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Muted });
        }
        return new Border { BorderBrush = done ? Palette.Accent : Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 14), Child = panel };
    }

    // Team and Settings, on the right.
    void RenderSide(ProjectView? view)
    {
        if (sideMode == null || snapshot == null) return;
        var team = TeamViews.Team(snapshot);
        var row = (snapshot["projects"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => item["id"]?.GetValue<string>() == projectId);
        var folder = row?["folder"]?.GetValue<string>() ?? "";
        var sig = sideMode + projectId + folder + projectsRoot + team.LeadId + string.Join(",", team.Models.Select(m => m.Id + m.Build + m.Verify + m.Review + m.QuotaLeft + m.Available));
        if (sig == sideSig) return;
        sideSig = sig;
        side.Children.Clear();
        if (sideMode == "team") TeamPanel(team, view?.Name ?? "");
        else SettingsPanel(folder, view?.Name ?? "");
    }

    static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Palette.Text, Margin = new Thickness(0, 6, 0, 0) };
    static TextBlock Explain(string text) => new() { Text = text, Foreground = Palette.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    void TeamPanel(TeamView team, string name)
    {
        side.Children.Add(Heading("Team"));
        side.Children.Add(Explain($"Team for {name}. Each project has its own team."));
        side.Children.Add(Heading("Lead"));
        side.Children.Add(Explain("Talks with you, plans the work and picks who does what."));
        var leads = team.Models.Where(m => m.CanLead && m.Available).ToList();
        var leadMenu = new ComboBox { ItemsSource = leads.Select(m => m.Label).ToList(), SelectedIndex = leads.FindIndex(m => m.Id == team.LeadId), PlaceholderText = "Choose your lead…", MinWidth = 240 };
        leadMenu.SelectionChanged += async (_, _) => { if (leadMenu.SelectedIndex >= 0 && leads[leadMenu.SelectedIndex].Id != team.LeadId) await DoAsync(() => api!.SetLeadAsync(projectId!, leads[leadMenu.SelectedIndex].Id, leads[leadMenu.SelectedIndex].Lead), "Lead chosen."); };
        side.Children.Add(leadMenu);
        side.Children.Add(Heading($"On the team · {team.Models.Count(m => m.Build || m.Verify || m.Review)}"));
        side.Children.Add(Explain("Build writes code and tests. Verify checks the work and fixes it, ideally a model from another company. Review is optional."));
        foreach (var model in team.Models)
        {
            var status = !model.Available ? "not available" + (model.UnavailableReason != null ? ": " + model.UnavailableReason : "") : model.QuotaLeft is { } left ? $"{left}% quota left" : "quota not reported";
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = {
                RoleChip(model, "Build", "worker", model.Build), RoleChip(model, "Verify", "verifier", model.Verify), RoleChip(model, "Review", "reviewer", model.Review) } };
            side.Children.Add(new Border { Background = Palette.Card, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8),
                Child = new StackPanel { Spacing = 6, Children = {
                    new TextBlock { Text = model.Label + (model.Lead ? "  · Lead" : ""), Foreground = Palette.Text, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = $"{model.Provider} · {status}", Foreground = model.Available ? Palette.Muted : Palette.Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    chips } } });
        }
    }

    Control RoleChip(TeamModel model, string label, string role, bool on)
    {
        var chip = new ToggleButton { Content = label, IsChecked = on, IsEnabled = model.Available, FontSize = 12, Padding = new Thickness(10, 3) };
        chip.Click += async (_, _) => await DoAsync(() => api!.SetRolesAsync(projectId!, model.Id, new Dictionary<string, bool> { [role] = !on }), "Team updated.");
        return chip;
    }

    void SettingsPanel(string folder, string name)
    {
        side.Children.Add(Heading("Settings"));
        side.Children.Add(Heading("This project"));
        side.Children.Add(Explain($"Folder on your computer for {name}."));
        var box = new TextBox { Text = folder, Watermark = OperatingSystem.IsWindows() ? @"C:\Projects\my-website" : "~/Projects/my-website" };
        var browse = new Button { Content = "Browse…", Margin = new Thickness(0, 0, 8, 0) };
        browse.Click += async (_, _) => { if (await ProjectDialog.Pick(this) is { } path) box.Text = path; };
        var save = new Button { Content = "Save", Background = Palette.Accent, Foreground = Palette.AccentText };
        save.Click += async (_, _) => { if (!string.IsNullOrWhiteSpace(box.Text)) await DoAsync(() => api!.SetFolderAsync(projectId!, box.Text.Trim()), "Folder saved."); };
        side.Children.Add(box);
        side.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { browse, save } });
        side.Children.Add(Heading("Projects folder"));
        side.Children.Add(Explain("New projects without a folder get one here."));
        var root = new TextBox { Text = projectsRoot ?? "", Watermark = OperatingSystem.IsWindows() ? @"C:\Projects" : "~/Projects" };
        var rootBrowse = new Button { Content = "Browse…", Margin = new Thickness(0, 0, 8, 0) };
        rootBrowse.Click += async (_, _) => { if (await ProjectDialog.Pick(this) is { } path) root.Text = path; };
        var rootSave = new Button { Content = "Save", Background = Palette.Accent, Foreground = Palette.AccentText };
        rootSave.Click += async (_, _) => await DoAsync(async () => { await api!.SetProjectsRootAsync(root.Text?.Trim() ?? ""); projectsRoot = await api.ProjectsRootAsync(); }, "Saved.");
        side.Children.Add(root);
        side.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { rootBrowse, rootSave } });
        side.Children.Add(Heading("About"));
        side.Children.Add(Explain($"Planwharf {version}, native app (preview). Usage, updates and the other settings are in the desktop or web app for now."));
    }

    // Runs a change, shows its outcome in the hint, and refreshes.
    async Task DoAsync(Func<Task> work, string done)
    {
        if (api == null || projectId == null) return;
        try { await work(); hint.Text = done; hint.Foreground = Palette.Accent; }
        catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; }
        threadSig = sideSig = null;
        await RefreshAsync(readOnly: false);
    }

    async Task ChangeStepAsync(WorkStep step, TeamModel model)
    {
        if (step.Status == "working" && !await new ConfirmDialog($"Switch this step to {model.Label}? Its current worker stops and the step starts again with the new model. Files already written stay.", "Switch").ShowDialog<bool>(this))
        { threadSig = null; Render(); return; }
        await DoAsync(() => api!.ChangeStepModelAsync(projectId!, step.Id, model.Id), $"{step.Role} now uses {model.Label}.");
    }

    async Task NewProjectAsync()
    {
        if (api == null) return;
        if (await new ProjectDialog().ShowDialog<(string Name, string Folder)?>(this) is not { } created) return;
        try
        {
            var id = await api.CreateProjectAsync(created.Name, created.Folder);
            if (id != null) projectId = id;
            listSig = threadSig = sideSig = null;
            hint.Text = "Project created. Choose its team, then tell the lead what to build."; hint.Foreground = Palette.Accent;
        }
        catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; }
        await RefreshAsync(readOnly: false);
        if (TeamViews.Team(snapshot!).LeadId == null) OpenSide("team");
    }

    async Task SendAsync()
    {
        var text = input.Text?.Trim();
        if (api == null || string.IsNullOrEmpty(text) || pending != null || projectId == null) return;
        if (snapshot != null && TeamViews.Team(snapshot).LeadId == null) { hint.Text = "Choose a lead first (Team)."; hint.Foreground = Palette.Warn; OpenSide("team"); return; }
        pending = text; input.Text = ""; Render();
        try { await api.AskAsync(projectId, text); }
        catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; }
        finally { pending = null; threadSig = null; await RefreshAsync(readOnly: false); }
    }

    async Task ActAsync()
    {
        if (api == null || snapshot == null || projectId == null || ProjectViews.Project(snapshot, projectId) is not { } view) return;
        busy = true; Render();
        try
        {
            switch (view.Action)
            {
                case CrewAction.LetTheCrewDoIt:
                    if (view.NextGoal != null)
                    {
                        // The plan agreed in the chat becomes the goal, after the user sees it.
                        if (!await new ConfirmDialog($"The crew will work on: {view.NextGoal}", "Start").ShowDialog<bool>(this)) break;
                        await api.SaveGoalAsync(projectId, view.NextGoal);
                    }
                    if (snapshot["mode"]?.GetValue<string>() != "auto") await api.SetModeAsync("auto");
                    await api.SetAutonomyAsync(projectId, true);
                    break;
                case CrewAction.StopTheCrew:
                    await api.SetAutonomyAsync(projectId, false);
                    try { await api.StopAsync(projectId); } catch { /* nothing running */ }
                    break;
                case CrewAction.Approve when view.MegaTaskId != null: await api.ApproveAsync(projectId, view.MegaTaskId); break;
                case CrewAction.Resume: await api.ResumeAsync(projectId); break;
                case CrewAction.StopWork: await api.StopAsync(projectId); break;
            }
        }
        catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; }
        finally { busy = false; threadSig = null; await RefreshAsync(readOnly: false); }
    }
}
