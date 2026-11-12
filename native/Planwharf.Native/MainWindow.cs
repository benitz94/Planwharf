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
    readonly Dictionary<string, RepoView> repos = new();
    readonly HashSet<string> repoLoading = new();
    readonly Dictionary<string, Dictionary<string, int?>> quotaStart = new();
    readonly Dictionary<string, string> gitBusy = new();
    readonly Dictionary<string, (bool Ok, string Text)> gitSaid = new();
    readonly Dictionary<string, string> commitMessages = new();
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

    // For the window tests: the repository as the server would report it.
    public void ShowRepo(string projectId, string workId, RepoView repo) { repos[projectId + "|" + workId] = repo; threadSig = null; Render(); }

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

    // What the work card shows besides the steps, read once per refresh; it also decides when to redraw.
    sealed record CardData(IReadOnlyList<string> Summary, IReadOnlyList<(string Model, string Text)> Usage, (string Who, string Text) Activity,
        IReadOnlyList<AlertView> Alerts, RepoView? Repo, string Since, string Took, bool AutoApprove);

    CardData? Card(ProjectView view)
    {
        if (view.Work is not { Row: { } row } work) return new CardData([], [], ("", ""), WorkDetails.Alerts(snapshot!, view.Id), null, "", "", false);
        var team = TeamViews.Team(snapshot!);
        string Label(string id) => team.Models.FirstOrDefault(model => model.Id == id)?.Label ?? "";
        int? Quota(string id) => team.Models.FirstOrDefault(model => model.Id == id)?.QuotaLeft;
        // Quota when the work started, to show before -> after (kept while the app is open).
        if (!quotaStart.TryGetValue(work.Id, out var start)) quotaStart[work.Id] = start = new Dictionary<string, int?>();
        if (work.Steps.Any(step => step.Status == "working"))
            foreach (var id in work.Steps.Select(step => step.ModelId).Where(id => id.Length > 0).Distinct()) start.TryAdd(id, Quota(id));
        var auto = snapshot!["autonomy"]?[view.Id];
        var stop = auto?["stopReason"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        RepoView? repo = null;
        if (work.Status == "COMPLETE")
        {
            // Read again for each finished piece of work: the folder changed.
            if (!repos.TryGetValue(view.Id + "|" + work.Id, out repo)) _ = LoadRepoAsync(view.Id, work.Id, false);
        }
        var changed = repo is { Git: true } ? repo.Uncommitted : null;
        return new CardData(
            work.Status == "COMPLETE" || IsStopped(work) ? WorkDetails.Summary(row, stop, work.Status == "COMPLETE" ? changed : null, Label) : [],
            WorkDetails.Usage(snapshot!, row, start, Label, Quota),
            WorkDetails.Activity(row, Label), WorkDetails.Alerts(snapshot!, view.Id), repo,
            WorkDetails.Duration(WorkDetails.Between(work.StartedAt, DateTimeOffset.UtcNow.ToString("o"))),
            WorkDetails.Duration(WorkDetails.Between(work.StartedAt, work.CompletedAt)),
            auto?["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on) && on);
    }

    static bool IsStopped(WorkView work) => work.Status is "FAILED" or "BLOCKED" or "PAUSED" or "STOPPED" or "CHECKPOINTED" || work.CanResume && work.Status != "COMPLETE";

    void RenderThread(ProjectView view)
    {
        var team = TeamViews.Team(snapshot!);
        var card = Card(view)!;
        var sig = string.Join("|", view.Messages.Select(m => m.At.ToUnixTimeMilliseconds() + m.Text.Length)) + pending + view.NextGoal
            + (view.Work is { } w ? w.Title + w.Status + w.NextAction + w.Waiting + string.Join(",", w.Steps.Select(s => s.ModelId + s.Status)) + w.Results.Count : "")
            + string.Join(",", team.Models.Select(m => m.Id + m.Build + m.Verify + m.Review + m.QuotaLeft))
            + string.Join("", card.Summary) + string.Join("", card.Usage) + card.Activity + string.Join(",", card.Alerts.Select(a => a.Id)) + card.Repo + card.Since + card.AutoApprove
            + gitBusy.GetValueOrDefault(view.Id) + gitSaid.GetValueOrDefault(view.Id);
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
        if (card.Alerts.Count > 0) thread.Children.Add(AlertsCard(view.Id, card.Alerts));
        if (view.Work != null) thread.Children.Add(WorkCard(view.Id, view.Work, team, card));
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

    static TextBlock Line(string text, IBrush? color = null, double size = 14, FontWeight? weight = null) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = color ?? Palette.Text, FontSize = size, FontWeight = weight ?? FontWeight.Normal };

    static Control List(string heading, IEnumerable<string> lines)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(0, 4, 0, 0), Children = { Line(heading, Palette.Text, 13, FontWeight.SemiBold) } };
        foreach (var line in lines) panel.Children.Add(Line("• " + line, Palette.Muted, 13));
        return panel;
    }

    // A section that opens and closes, in the card's colours.
    static Expander Fold(string header, Control content)
    {
        var fold = new Expander { Header = header, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch, Content = content, Background = Palette.Card };
        foreach (var key in new[] { "ExpanderHeaderBackground", "ExpanderHeaderBackgroundPointerOver", "ExpanderHeaderBackgroundPressed", "ExpanderContentBackground" })
            fold.Resources[key] = Palette.Card;
        foreach (var key in new[] { "ExpanderHeaderBorderBrush", "ExpanderContentBorderBrush", "ExpanderHeaderBorderBrushPointerOver", "ExpanderHeaderBorderBrushPressed" })
            fold.Resources[key] = Palette.Line;
        return fold;
    }

    Control AlertsCard(string projectId, IReadOnlyList<AlertView> alerts)
    {
        var panel = new StackPanel { Spacing = 8, Children = { Line("Something needs a look", Palette.Warn, 17, FontWeight.Bold) } };
        foreach (var alert in alerts)
        {
            var ignore = new Button { Content = "Ignore", FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
            ignore.Click += async (_, _) => await DoAsync(() => api!.IgnoreAlertAsync(projectId, alert.Id), "Ignored.");
            var row = new DockPanel { Children = { ignore, new StackPanel { Children = { Line(alert.Title, Palette.Text, 14, FontWeight.SemiBold), Line(alert.Message, Palette.Muted, 13) } } } };
            DockPanel.SetDock(ignore, Dock.Right);
            panel.Children.Add(row);
        }
        panel.Children.Add(Line("Planwharf checks the work itself; this is a reason to look, not proof that something broke.", Palette.Muted, 12));
        return new Border { BorderBrush = Palette.Warn, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 14), Child = panel };
    }

    Control WorkCard(string projectId, WorkView work, TeamView team, CardData card)
    {
        var done = work.Status == "COMPLETE";
        var stopped = !done && IsStopped(work);
        var proposal = work.Status == "READY_FOR_APPROVAL";
        var heading = done ? "Work finished" : stopped ? "Work stopped" : proposal ? "The lead proposes this work" : "Crew at work" + (card.Since.Length > 0 ? " · " + card.Since : "");
        var panel = new StackPanel { Spacing = 8, Children = { Line(heading, done ? Palette.Accent : stopped ? Palette.Warn : Palette.Text, 17, FontWeight.Bold) } };
        panel.Children.Add(Line(stopped && work.NextAction.Length > 0 ? work.NextAction : work.Title));
        if (done)
        {
            var changed = card.Repo is { Git: true } repo ? $"{repo.Uncommitted} files not yet committed" : card.Repo == null ? "Counting changed files…" : "Changed files: unknown";
            panel.Children.Add(Line(string.Join("   ·   ", new[] { $"{work.Done}/{work.Total} steps done", card.Took, changed }.Where(part => part.Length > 0)), Palette.Muted, 13));
        }
        if (card.Summary.Count > 0) panel.Children.Add(List("Summary", card.Summary));
        if (done && work.Results.Count > 0) panel.Children.Add(List("What was done", work.Results));
        if (!done && !proposal)
        {
            panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, work.Total), Value = work.Done, Height = 6, Foreground = Palette.Accent });
            panel.Children.Add(Line($"{work.Done} of {work.Total} steps done", Palette.Muted, 12));
            if (work.Waiting.Length > 0 && !stopped) panel.Children.Add(Line(work.Waiting, Palette.Warn, 13));
        }
        foreach (var step in work.Steps) panel.Children.Add(StepRow(step, team, editable: !done && step.Status != "done"));
        if (proposal) panel.Children.Add(Line(card.AutoApprove ? "The crew approves this by itself." : "Press Approve and start below, or tell the lead what to change.", Palette.Muted, 13));
        if (!done && !stopped && !proposal && card.Activity.Text.Length > 0)
            panel.Children.Add(Fold($"What {card.Activity.Who} is doing", new SelectableTextBlock { Text = card.Activity.Text, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12, Foreground = Palette.Muted }));
        if ((done || stopped) && card.Usage.Count > 0)
        {
            var usage = new StackPanel { Spacing = 3 };
            foreach (var (model, text) in card.Usage)
                usage.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Inlines = new Avalonia.Controls.Documents.InlineCollection {
                    new Avalonia.Controls.Documents.Run(model) { FontWeight = FontWeight.SemiBold, Foreground = Palette.Text }, new Avalonia.Controls.Documents.Run(" · " + text) { Foreground = Palette.Muted } } });
            panel.Children.Add(Fold("What this work used", usage));
        }
        if (done)
        {
            panel.Children.Add(Line("Try the result in the project folder. Planwharf did not commit or publish anything.", Palette.Muted, 13));
            if (GitBox(projectId, work, card.Repo) is { } git) panel.Children.Add(git);
        }
        return new Border { BorderBrush = done ? Palette.Accent : stopped ? Palette.Warn : Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 14), Child = panel };
    }

    Control StepRow(WorkStep step, TeamView team, bool editable)
    {
        Control model = new TextBlock { Text = step.Model.Length > 0 ? step.Model : "not assigned", Foreground = Palette.Text, VerticalAlignment = VerticalAlignment.Center };
        // Open work: the model of a step that is not done can be changed, among the team models with that role.
        if (editable)
        {
            var choices = TeamViews.ModelsFor(team, step.Role).ToList();
            if (choices.All(m => m.Id != step.ModelId)) choices.Insert(0, new TeamModel(step.ModelId, step.Model.Length > 0 ? step.Model : "not assigned", "", null, true, null, false, false, false, false, false));
            var menu = new ComboBox { ItemsSource = choices.Select(m => m.Label + (m.QuotaLeft is { } left ? $" · {left}% left" : "")).ToList(), SelectedIndex = choices.FindIndex(m => m.Id == step.ModelId), MinWidth = 220 };
            menu.SelectionChanged += async (_, _) => { if (menu.SelectedIndex >= 0 && choices[menu.SelectedIndex].Id != step.ModelId) await ChangeStepAsync(step, choices[menu.SelectedIndex]); };
            model = menu;
        }
        var main = new StackPanel { Spacing = 2, Children = {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = step.Role + " ·", Foreground = Palette.Text, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, model } } } };
        if (step.Title.Length > 0) main.Children.Add(Line(step.Title, Palette.Muted, 12));
        var status = new TextBlock { Text = step.Status, Foreground = step.Status == "done" ? Palette.Accent : step.Status is "blocked" or "failed" ? Palette.Danger : Palette.Warn, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var row = new DockPanel { Children = { status, main } };
        DockPanel.SetDock(status, Dock.Right);
        return new Border { Background = Palette.Card, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = row };
    }

    // Commit and push the finished work, or push commits already made. What git said stays in view.
    Control? GitBox(string projectId, WorkView work, RepoView? repo)
    {
        var busy = gitBusy.GetValueOrDefault(projectId);
        var said = gitSaid.GetValueOrDefault(projectId);
        var changed = repo is { Git: true } ? repo.Uncommitted ?? 0 : 0;
        var ahead = repo?.Ahead ?? 0;
        if (repo is not { Git: true } || (changed == 0 && ahead == 0 && busy == null && said == default)) return null;
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        var message = new TextBox { MaxLength = 500, Text = commitMessages.TryGetValue(projectId, out var kept) ? kept : DefaultMessage(work.Title) };
        message.TextChanged += (_, _) => commitMessages[projectId] = message.Text ?? "";
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button Git(string label, bool push, bool primary)
        {
            var button = new Button { Content = label, IsEnabled = busy == null };
            if (primary) { button.Background = Palette.Accent; button.Foreground = Palette.AccentText; }
            button.Click += async (_, _) => await CommitAsync(projectId, work.Id, changed > 0, ahead, changed > 0 ? message.Text?.Trim() ?? "" : "push", push);
            return button;
        }
        if (changed > 0)
        {
            panel.Children.Add(Line("Save it in git when you are happy with it", Palette.Text, 13));
            panel.Children.Add(message);
            buttons.Children.Add(Git("Commit", false, false)); buttons.Children.Add(Git("Commit and push", true, true));
        }
        else if (ahead > 0)
        {
            panel.Children.Add(Line($"{ahead} commit{(ahead == 1 ? " is" : "s are")} not on GitHub yet.", Palette.Text, 13));
            buttons.Children.Add(Git($"Push {ahead} commit{(ahead == 1 ? "" : "s")} to GitHub", true, true));
        }
        panel.Children.Add(buttons);
        if (busy != null) panel.Children.Add(Line(busy, Palette.Muted, 13));
        else if (said != default) panel.Children.Add(Line(said.Text, said.Ok ? Palette.Accent : Palette.Danger, 13));
        return panel;
    }

    static string DefaultMessage(string title) => title.Length == 0 ? "Work by the crew" : title[..Math.Min(200, title.Length)];

    async Task CommitAsync(string projectId, string workId, bool changes, int ahead, string message, bool push)
    {
        if (api == null) return;
        if (changes && message.Length == 0) { hint.Text = "Write a commit message."; hint.Foreground = Palette.Warn; return; }
        // The user's own action: say exactly what will happen before doing it. Never a force push.
        var what = changes ? $"Commit all changes in the project folder{(push ? " and push them to GitHub" : "")}, with the message \"{message}\"?" : $"Push {ahead} commit{(ahead == 1 ? "" : "s")} to GitHub?";
        if (!await new ConfirmDialog(what, push ? "Push" : "Commit").ShowDialog<bool>(this)) return;
        gitBusy[projectId] = push ? (changes ? "Committing, then pushing to GitHub. If GitHub asks you to sign in, a window opens." : "Pushing to GitHub. If GitHub asks you to sign in, a window opens.") : "Committing…";
        gitSaid.Remove(projectId); threadSig = null; Render();
        (bool, string) said;
        try
        {
            var result = await api.CommitAsync(projectId, message, push);
            string Get(string key) => result?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
            var pushed = result?["pushed"]?.GetValue<bool>() == true;
            said = (true, pushed ? $"{(Get("commit").Length > 0 ? $"Committed {Get("commit")} and pushed" : "Pushed")} to {Get("branch")} on GitHub." : $"Committed {Get("commit")} on {Get("branch")}. Not pushed yet.");
        }
        catch (Exception error) { said = (false, error.Message); }
        gitBusy.Remove(projectId); gitSaid[projectId] = said; commitMessages.Remove(projectId);
        hint.Text = said.Item2; hint.Foreground = said.Item1 ? Palette.Accent : Palette.Danger;
        await LoadRepoAsync(projectId, workId, true);
    }

    async Task LoadRepoAsync(string projectId, string workId, bool refresh)
    {
        var key = projectId + "|" + workId;
        if (api == null || !repoLoading.Add(key)) return;
        try { repos[key] = WorkDetails.Repo(await api.RepositoryAsync(projectId, refresh)); }
        catch (Exception error) { repos[key] = new RepoView(false, null, 0, error.Message); }
        finally { repoLoading.Remove(key); threadSig = null; Render(); }
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
