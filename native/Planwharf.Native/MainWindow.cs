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
public sealed partial class MainWindow : Window
{
    readonly ListBox projectList = new() { Background = Brushes.Transparent };
    readonly TextBlock title = new() { FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Palette.Text, VerticalAlignment = VerticalAlignment.Center };
    Border? badge;
    readonly TextBlock badgeText = new() { FontSize = 12, Foreground = Palette.Accent };
    readonly StackPanel thread = new() { Spacing = 12, Margin = new Thickness(24, 16), MaxWidth = 860 };
    readonly ScrollViewer threadScroll;
    readonly Button actionButton = new() { Background = Palette.Accent, Foreground = Palette.AccentText, FontWeight = FontWeight.SemiBold, Padding = new Thickness(16, 8) };
    readonly TextBlock hint = new() { Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly TextBox input = new() { Watermark = "Tell the lead what you want built…", AcceptsReturn = false, MinHeight = 40 };
    readonly Button send = new() { Content = "Send", Background = Palette.Accent, Foreground = Palette.AccentText, Padding = new Thickness(16, 8), Margin = new Thickness(8, 0, 0, 0) };
    // The panels on the right, as in the chat page.
    static readonly (string Mode, string Label)[] Panels = [("team", "Team"), ("progress", "Progress"), ("usage", "Usage"), ("files", "Files"), ("settings", "Settings")];
    readonly Dictionary<string, ToggleButton> sideButtons = Panels.ToDictionary(panel => panel.Mode, panel => new ToggleButton { Content = panel.Label, Margin = new Thickness(6, 0, 0, 0) });
    readonly Dictionary<string, (JsonNode? Json, string? Error)> repoRaw = new();
    readonly StackPanel side = new() { Spacing = 10, Margin = new Thickness(16) };
    readonly Border sidePanel;
    readonly string version;

    PlanwharfApi? api;
    JsonNode? snapshot;
    string? projectId, threadSig, listSig, sideSig, sideMode, projectsRoot;
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

        badge = new Border { Background = Brush.Parse("#1d3328"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = badgeText };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (mode, button) in sideButtons)
        {
            tools.Children.Add(button);
            button.Click += (_, _) => OpenSide(button.IsChecked == true ? mode : null);
        }
        var headerRow = new DockPanel { Children = { tools, new StackPanel { Orientation = Orientation.Horizontal, Children = { title, badge } } } };
        DockPanel.SetDock(tools, Dock.Right);
        var header = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(24, 12), Child = headerRow };

        var actions = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Children = { actionButton, hint } };
        DockPanel.SetDock(actionButton, Dock.Left);
        var composer = new DockPanel { Children = { send, input } };
        DockPanel.SetDock(send, Dock.Right);
        var footer = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 12), Child = new StackPanel { MaxWidth = 860, Children = { timedRow, actions, composer,
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
    public void ShowRepo(string projectId, string workId, JsonNode repository)
    {
        repos[projectId + "|" + workId] = WorkDetails.Repo(repository); repoRaw[projectId] = (repository, null);
        threadSig = sideSig = null; Render();
    }

    public void OpenSide(string? mode)
    {
        if (mode == "settings" && api != null) _ = LoadSettingsAsync();
        sideMode = mode; sideSig = null;
        foreach (var (name, button) in sideButtons) button.IsChecked = name == mode;
        if (mode == "files" && projectId != null && !repoRaw.ContainsKey(projectId)) _ = LoadRepoAsync(projectId, CurrentWorkId(), false);
        sidePanel.IsVisible = mode != null;
        Render();
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
        RenderProjects(projects);
        CheckTimedStop();
        var view = ProjectViews.Project(snapshot, projectId);
        RenderFooter(view);
        if (view == null)
        {
            title.Text = "Welcome"; badgeText.Text = "";
            hint.Text = "Create a project to begin: + New project."; hint.Foreground = Palette.Muted; actionButton.IsEnabled = false;
            RenderWelcome();
            RenderSide(null);
            if (badge != null) badge.IsVisible = false;
            return;
        }
        title.Text = view.Name;
        badgeText.Text = WorkingNow(view.Id) is { } work ? ChatViews.WorkingText(work.Who, work.Since, DateTimeOffset.UtcNow) : view.Status;
        RenderThread(view);
        RenderSide(view);
        if (badge != null) badge.IsVisible = badgeText.Text?.Length > 0;
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
            var seeFiles = new Button { Content = "See changed files" };
            seeFiles.Click += (_, _) => OpenSide("files");
            var seeUsage = new Button { Content = "See usage" };
            seeUsage.Click += (_, _) => OpenSide("usage");
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { seeFiles, seeUsage } });
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
        try { var json = await api.RepositoryAsync(projectId, refresh); repos[key] = WorkDetails.Repo(json); repoRaw[projectId] = (json, null); }
        catch (Exception error) { repos[key] = new RepoView(false, null, 0, error.Message); repoRaw[projectId] = (null, error.Message); }
        finally { repoLoading.Remove(key); threadSig = sideSig = null; Render(); }
    }

    string CurrentWorkId() => snapshot == null ? "" : ProjectViews.Project(snapshot, projectId)?.Work?.Id ?? "";

    // Team and Settings, on the right.
    void RenderSide(ProjectView? view)
    {
        if (sideMode == null || snapshot == null) return;
        var team = TeamViews.Team(snapshot);
        var row = (snapshot["projects"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => item["id"]?.GetValue<string>() == projectId);
        var folder = row?["folder"]?.GetValue<string>() ?? "";
        var sig = sideMode + projectId + folder + projectsRoot + (sideMode == "settings" ? SettingsSignature() : "") + team.LeadId + string.Join(",", team.Models.Select(m => m.Id + m.Build + m.Verify + m.Review + m.QuotaLeft + m.Available + m.Bench + m.Enabled + m.UnavailableReason)) + string.Join(",", team.Providers);
        var progress = sideMode == "progress" && projectId != null ? PanelViews.Progress(snapshot, projectId, view?.Status ?? "") : null;
        var usage = sideMode == "usage" ? PanelViews.Usage(snapshot) : null;
        var raw = projectId != null && repoRaw.TryGetValue(projectId, out var found) ? found : (null, null);
        var files = sideMode == "files" ? PanelViews.Files(raw.Json, folder, raw.Error) : null;
        sig += progress is { } p ? p.Status + p.Next + string.Join(",", p.Steps) + string.Join(",", p.Recent) : "";
        sig += usage is { } u ? string.Join(",", u.Plans.Select(plan => plan.Label + plan.Note + string.Join("", plan.Windows))) + string.Join(",", u.Lead) + string.Join(",", u.Crew) : "";
        sig += files is { } f ? f.Message + f.Branch + f.CheckedAt + f.Total + string.Join(",", f.Files) : "";
        if (sig == sideSig) return;
        sideSig = sig;
        side.Children.Clear();
        switch (sideMode)
        {
            case "team": TeamPanel(team, view?.Name ?? ""); break;
            case "progress" when progress != null: ProgressSide(progress); break;
            case "usage" when usage != null: UsageSide(usage); break;
            case "files" when files != null: FilesSide(files, folder); break;
            default: SettingsPanel(folder, view?.Name ?? ""); break;
        }
    }

    static Control Item(string badge, string text, string? sub = null, IBrush? color = null)
    {
        var body = new StackPanel { Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text, FontSize = 13 } } };
        if (!string.IsNullOrEmpty(sub)) body.Children.Add(Explain(sub));
        var tag = new Border { Background = Palette.Card, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1), Margin = new Thickness(0, 1, 8, 0),
            VerticalAlignment = VerticalAlignment.Top, MinWidth = 64, Child = new TextBlock { Text = badge, FontSize = 11, Foreground = color ?? Palette.Muted, HorizontalAlignment = HorizontalAlignment.Center } };
        var row = new DockPanel { Children = { tag, body } };
        DockPanel.SetDock(tag, Dock.Left);
        return row;
    }

    void ProgressSide(ProgressPanel panel)
    {
        side.Children.Add(Heading("Progress"));
        side.Children.Add(Heading("Now"));
        side.Children.Add(Line(panel.Status, Palette.Accent, 13, FontWeight.SemiBold));
        if (panel.Next.Length > 0) side.Children.Add(Line(panel.Next, Palette.Text, 13));
        if (panel.Steps.Count > 0)
        {
            side.Children.Add(Heading("Steps"));
            foreach (var step in panel.Steps) side.Children.Add(Item(step.Status, step.Title, step.Model, step.Status == "complete" ? Palette.Accent : step.Status is "failed" or "blocked" ? Palette.Danger : null));
        }
        side.Children.Add(Heading("Recent activity"));
        if (panel.Recent.Count == 0) side.Children.Add(Explain("Nothing yet."));
        foreach (var (at, text) in panel.Recent) side.Children.Add(Item(at, text));
        side.Children.Add(Explain("Folder: " + (panel.Folder.Length > 0 ? panel.Folder : "not set")));
    }

    void UsageSide(UsagePanel panel)
    {
        side.Children.Add(Heading("Usage"));
        side.Children.Add(Heading("Quota left"));
        if (panel.Plans.Count == 0) side.Children.Add(Explain("No provider reports quota."));
        foreach (var plan in panel.Plans)
        {
            var card = new StackPanel { Spacing = 4, Children = { Line(plan.Label, Palette.Text, 14, FontWeight.SemiBold) } };
            if (plan.Note != null) card.Children.Add(Explain(plan.Note));
            foreach (var window in plan.Windows)
            {
                card.Children.Add(Explain(string.Join(" · ", new[] { (window.Name.Length > 0 ? window.Name + ": " : "") + $"{window.LeftPct}% left", window.Resets }.Where(part => part.Length > 0))));
                card.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(window.LeftPct, 0, 100), Height = 6, Foreground = window.LeftPct < 20 ? Palette.Danger : Palette.Accent });
            }
            side.Children.Add(new Border { Background = Palette.Card, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = card });
        }
        foreach (var (heading, rows) in new[] { ("Lead calls", panel.Lead), ("Crew work", panel.Crew) })
        {
            side.Children.Add(Heading(heading));
            if (rows.Count == 0) side.Children.Add(Explain("None yet."));
            foreach (var (model, text) in rows)
                side.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Inlines = new Avalonia.Controls.Documents.InlineCollection {
                    new Avalonia.Controls.Documents.Run(model) { FontWeight = FontWeight.SemiBold, Foreground = Palette.Text }, new Avalonia.Controls.Documents.Run(" · " + text) { Foreground = Palette.Muted } } });
        }
        side.Children.Add(Explain("Calls · time · output tokens. A provider that does not report tokens shows \"not reported\", never a guess."));
    }

    void FilesSide(FilesPanel panel, string folder)
    {
        side.Children.Add(Heading("Changed files"));
        var again = new Button { Content = "Check again" };
        again.Click += async (_, _) => { if (projectId != null) await LoadRepoAsync(projectId, CurrentWorkId(), true); };
        if (panel.Message != null)
        {
            side.Children.Add(Line(panel.Message, Palette.Muted, 13));
            if (folder.Length > 0) side.Children.Add(again);
            return;
        }
        side.Children.Add(Heading("Not yet saved in Git"));
        side.Children.Add(Explain($"Files the crew or you changed since the last commit on {(panel.Branch.Length > 0 ? panel.Branch : "the current branch")}."));
        if (panel.Files.Count == 0) side.Children.Add(Line("Nothing changed. The folder matches the last commit.", Palette.Text, 13));
        foreach (var file in panel.Files) side.Children.Add(Item(file.Kind, file.Path, null, file.Kind == "conflict" ? Palette.Danger : file.Kind is "new" or "added" ? Palette.Accent : null));
        if (panel.Total > panel.Files.Count) side.Children.Add(Explain($"Showing {panel.Files.Count} of {panel.Total}."));
        side.Children.Add(Explain(folder + (panel.CheckedAt.Length > 0 ? " · checked " + panel.CheckedAt : "")));
        side.Children.Add(again);
    }

    static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = Palette.Text, Margin = new Thickness(0, 6, 0, 0) };
    static TextBlock Explain(string text) => new() { Text = text, Foreground = Palette.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    // Runs a change, shows its outcome in the hint, and refreshes.
    async Task DoAsync(Func<Task> work, string done)
    {
        if (api == null) return;
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
                    if (!await EnsureOkAsync()) break;
                    if (snapshot["mode"]?.GetValue<string>() != "auto") await api.SetModeAsync("auto");
                    await api.SetAutonomyAsync(projectId, true);
                    break;
                case CrewAction.StopTheCrew:
                    await api.SetAutonomyAsync(projectId, false);
                    try { await api.StopAsync(projectId); } catch { /* nothing running */ }
                    break;
                case CrewAction.Approve when view.MegaTaskId != null: if (await EnsureOkAsync()) await api.ApproveAsync(projectId, view.MegaTaskId); break;
                case CrewAction.Resume: if (await EnsureOkAsync()) await api.ResumeAsync(projectId); break;
                case CrewAction.StopWork: await api.StopAsync(projectId); break;
            }
        }
        catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; }
        finally { busy = false; threadSig = null; await RefreshAsync(readOnly: false); }
    }
}
