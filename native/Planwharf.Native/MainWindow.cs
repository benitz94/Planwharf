using Planwharf.Engine;
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
    readonly ListBox projectList = new() { Background = Brushes.Transparent, Padding = new Thickness(0) };
    readonly TextBlock title = new() { FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Palette.Text, VerticalAlignment = VerticalAlignment.Center };
    Border? badge;
    readonly TextBlock badgeText = new() { FontSize = 12, Foreground = Palette.Muted };
    readonly StackPanel thread = new() { Spacing = 14, Margin = new Thickness(20, 20), MaxWidth = 780 };
    readonly ScrollViewer threadScroll;
    readonly Button actionButton = Look.Button(Strings.T("Let the crew do it"), "rocket", Look.Kind.Primary);
    // Next to Resume: close the stopped work to start something new.
    readonly Button dropButton = Look.Button(Strings.T("Drop this work"), "trash");
    readonly TextBlock hint = new() { Foreground = Palette.Muted, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    readonly TextBox input = Look.Field(new TextBox { Watermark = Strings.T("Tell the lead what you want built\u2026"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 46, MaxHeight = 220, FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center });
    readonly Button send = Look.Button(Strings.T("Send"), "send", Look.Kind.Primary);
    readonly TextBlock now = new() { FontSize = 12, Foreground = Palette.Muted, Margin = new Thickness(0, 0, 0, 8) };
    readonly TextBlock sideTitle = new() { FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Palette.Text, VerticalAlignment = VerticalAlignment.Center };
    // The panels on the right, as in the chat page.
    static (string Mode, string Label, string Icon)[] Panels => [("team", Strings.T("Team"), "team"), ("progress", Strings.T("Progress"), "progress"), ("usage", Strings.T("Usage"), "usage"), ("files", Strings.T("Files"), "file"), ("settings", Strings.T("Settings"), "settings")];
    readonly Dictionary<string, ToggleButton> sideButtons = Panels.ToDictionary(panel => panel.Mode, panel => { var tab = Look.Tab(panel.Label, panel.Icon); tab.Margin = new Thickness(6, 0, 0, 0); return tab; });
    readonly Dictionary<string, (JsonNode? Json, string? Error)> repoRaw = new();
    readonly StackPanel side = new() { Spacing = 10, Margin = new Thickness(16) };
    readonly Dictionary<string, string> sideTitles = Panels.ToDictionary(panel => panel.Mode, panel => panel.Mode == "files" ? Strings.T("Changed files") : panel.Label);
    readonly Border sidePanel;
    readonly string version;

    PlanwharfApi? api;
    DesktopAlerts? desktopAlerts;
    internal Action<WorkAlert>? AlertReceived { get; set; }
    JsonNode? snapshot;
    string? projectId, threadSig, listSig, sideSig, sideMode, projectsRoot;
    bool busy, menuOpen, refreshing, closed;
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
        Closed += (_, _) => closed = true;
        Title = Strings.T("Planwharf"); Width = 1360; Height = 860; MinWidth = 960; MinHeight = 560; Background = Palette.Background;
        threadScroll = new ScrollViewer { Content = thread, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(0, 0, 0, 12), Children = {
            BrandMark(),
            new TextBlock { Text = Strings.T("Planwharf"), FontSize = 18, FontWeight = FontWeight.Bold, Foreground = Palette.Text, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = version, FontSize = 12, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) } } };
        var newProject = Look.Button(Strings.T("New project"), "plus", Look.Kind.Primary);
        newProject.HorizontalAlignment = HorizontalAlignment.Stretch; newProject.HorizontalContentAlignment = HorizontalAlignment.Center; newProject.Margin = new Thickness(0, 0, 0, 12);
        newProject.Click += async (_, _) => await NewProjectAsync();
        projectList.SelectionChanged += (_, _) => { if (projectList.SelectedItem is ListBoxItem { Tag: string id } && id != projectId) { projectId = id; threadSig = sideSig = null; _ = RefreshAsync(readOnly: true); } };
        // The open project as in the chat page: a soft green row with round corners.
        foreach (var (key, value) in new (string, IBrush)[] { ("ListBoxItemBackgroundPointerOver", Palette.Surface2), ("ListBoxItemBackgroundPressed", Palette.Surface2),
            ("ListBoxItemBackgroundSelected", Palette.AccentSoft), ("ListBoxItemBackgroundSelectedPointerOver", Palette.AccentSoft), ("ListBoxItemBackgroundSelectedPressed", Palette.AccentSoft),
            ("SystemControlHighlightListAccentLowBrush", Palette.AccentSoft), ("SystemControlHighlightListAccentMediumBrush", Palette.AccentSoft), ("SystemControlHighlightListAccentHighBrush", Palette.AccentSoft),
            ("SystemControlHighlightListLowBrush", Palette.Surface2), ("SystemControlHighlightListMediumBrush", Palette.Surface2) })
            projectList.Resources[key] = value;
        var top = new StackPanel { Children = { brand, newProject } };
        var settingsLink = Look.Button(Strings.T("Settings"), "settings", Look.Kind.Link);
        settingsLink.Click += (_, _) => OpenSide("settings");
        var foot = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 0), Child = new DockPanel { Children = { settingsLink } } };
        DockPanel.SetDock(settingsLink, Dock.Right); settingsLink.HorizontalAlignment = HorizontalAlignment.Right;
        var bottom = new StackPanel { Children = { now, foot } };
        var left = new Border { Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(12, 16),
            Child = new DockPanel { Children = { top, bottom, projectList } } };
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        ShowNow();
        new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => ShowNow()).Start();

        badge = new Border { Background = Palette.Surface2, CornerRadius = new CornerRadius(99), Padding = new Thickness(8, 1), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = badgeText };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (mode, button) in sideButtons)
        {
            tools.Children.Add(button);
            button.Click += (_, _) => OpenSide(button.IsChecked == true ? mode : null);
        }
        // Quit, as the red tab of the chat page: unfinished work is saved.
        var checkPrs = Look.Tab(Strings.T("Check PRs"), "git");
        checkPrs.Click += async (_, _) => { checkPrs.IsChecked = false; await CheckPrsAsync(); };
        tools.Children.Add(checkPrs);
        var quit = Look.Tab(Strings.T("Quit"), "power");
        quit.Foreground = Palette.Danger; quit.Margin = new Thickness(6, 0, 0, 0);
        quit.Resources["ToggleButtonForegroundPointerOver"] = Palette.Danger; quit.Resources["ToggleButtonForegroundPressed"] = Palette.Danger;
        quit.Click += async (_, _) =>
        {
            quit.IsChecked = false;
            if (await Confirm(Strings.T("Quit Planwharf? Unfinished work is saved, and Resume continues it next time."), Strings.T("Yes, quit")))
                (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        };
        tools.Children.Add(quit);
        // The title gives way to the tabs: it ends in … when the side panel is open, as in the chat page.
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        DockPanel.SetDock(badge, Dock.Right);
        var named = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 12, 0), Children = { badge, title } };
        var headerRow = new DockPanel { Children = { tools, named } };
        DockPanel.SetDock(tools, Dock.Right);
        title.FontSize = 18;
        var header = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(20, 12), Child = headerRow };

        dropButton.Margin = new Thickness(8, 0, 0, 0); dropButton.IsVisible = false;
        ToolTip.SetTip(dropButton, Strings.T("Close the stopped work, so the crew can start a new goal. Files in the project folder stay as they are."));
        dropButton.Click += async (_, _) => await DropWorkAsync();
        var actions = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Children = { actionButton, dropButton, hint } };
        DockPanel.SetDock(actionButton, Dock.Left); DockPanel.SetDock(dropButton, Dock.Left);
        send.Margin = new Thickness(8, 0, 0, 0); send.VerticalAlignment = VerticalAlignment.Stretch; send.VerticalContentAlignment = VerticalAlignment.Center;
        input.CornerRadius = new CornerRadius(14);
        var composer = new DockPanel { Children = { send, input } };
        DockPanel.SetDock(send, Dock.Right);
        var footer = new Border { Background = Palette.Card, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 8, 20, 14), Child = new StackPanel { MaxWidth = 780, Children = { timedRow, actions, composer,
            new TextBlock { FontSize = 12, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Inlines = new Avalonia.Controls.Documents.InlineCollection {
                new Avalonia.Controls.Documents.Run(Strings.T("Talk with the lead to shape the plan, then press ")), new Avalonia.Controls.Documents.Run(Strings.T("Let the crew do it")) { FontWeight = FontWeight.Bold, Foreground = Palette.Text },
                new Avalonia.Controls.Documents.Run(Strings.T(". The crew builds, checks and stops when the goal is reached.")) } } } } };
        var middle = new DockPanel { Children = { header, footer, threadScroll } };
        DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);
        var close = Look.Button("", "close", Look.Kind.Quiet);
        close.Content = Look.Icon("close", 18); close.Padding = new Thickness(6); close.Foreground = Palette.Text;
        ToolTip.SetTip(close, Strings.T("Close"));
        close.Click += (_, _) => OpenSide(null);
        var sideHead = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(16, 10, 10, 10), Child = new DockPanel { Children = { close, sideTitle } } };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(sideHead, Dock.Top);
        sidePanel = new Border { Width = 380, IsVisible = false, Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new DockPanel { Children = { sideHead, new ScrollViewer { Content = side, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } } } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*,Auto") };
        grid.Children.Add(left); grid.Children.Add(middle); grid.Children.Add(sidePanel);
        Grid.SetColumn(middle, 1); Grid.SetColumn(sidePanel, 2);
        Content = grid;

        send.Click += async (_, _) => await SendAsync();
        // Enter sends; Shift+Enter starts a new line. The box keeps every line of a pasted message
        // (a single-line box would keep only the first). Handled before the box adds the line break.
        input.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            await SendAsync();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        actionButton.Click += async (_, _) => await ActAsync();
        title.Text = Strings.T("Starting Planwharf\u2026"); hint.Text = Strings.T("Starting the local Planwharf server.");
        actionButton.IsEnabled = send.IsEnabled = false;
    }

    void ShowNow() => now.Text = DateTime.Now.ToString("ddd, MMM d · HH:mm", Strings.Culture);

    // The logo's needle, turning while anything is being worked on.
    Canvas? needle;
    System.Threading.CancellationTokenSource? needleTurning;
    void SpinBrand(bool on)
    {
        if (needle == null || on == (needleTurning != null)) return;
        if (!on) { needleTurning!.Cancel(); needleTurning = null; ((RotateTransform)needle.RenderTransform!).Angle = 40; return; }
        needleTurning = new System.Threading.CancellationTokenSource();
        var turn = new Avalonia.Animation.Animation { Duration = TimeSpan.FromSeconds(2.4), IterationCount = Avalonia.Animation.IterationCount.Infinite, Children = {
            new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0), Setters = { new Avalonia.Styling.Setter(RotateTransform.AngleProperty, 40d) } },
            new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1), Setters = { new Avalonia.Styling.Setter(RotateTransform.AngleProperty, 400d) } } } };
        _ = turn.RunAsync(needle, needleTurning.Token);
    }

    // The compass follows the selected main colour.
    Control BrandMark()
    {
        var canvas = new Canvas { Width = 64, Height = 64 };
        var green = Palette.Accent;
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M11.5,32 A20.5,20.5 0 1 0 52.5,32 A20.5,20.5 0 1 0 11.5,32 Z M32 12v5.5M32 46.5V52M12 32h5.5M46.5 32H52"), Stroke = green, StrokeThickness = 4.2, StrokeLineCap = PenLineCap.Round });
        needle = new Canvas { Width = 64, Height = 64, RenderTransform = new RotateTransform(40), RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative) };
        needle.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M32 16 36 32H28Z"), Fill = green });
        needle.Children.Add(new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M32 48 36 32H28Z"), Fill = Brush.Parse("#eef3ef") });
        canvas.Children.Add(needle);
        return new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = Brush.Parse("#101312"), Child = new Viewbox { Child = canvas } };
    }

    public void Connect(PlanwharfApi connected)
    {
        api = connected;
        if (desktopAlerts == null)
        {
            desktopAlerts = new DesktopAlerts(this);
            AlertReceived = desktopAlerts.Show;
            Activated += (_, _) => desktopAlerts.ClearAttention();
            Closing += (_, _) => desktopAlerts.Dispose();
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) => await RefreshAsync(readOnly: ticks++ % 5 != 0);
        timer.Start();
        // A window replaced by a new look stops asking the server.
        Closed += (_, _) => timer.Stop();
        _ = RefreshAsync(readOnly: false);
    }

    // The same window drawn again with the current look: same project, same panel, same server.
    public MainWindow Rebuilt()
    {
        var fresh = new MainWindow(version) { Width = Width, Height = Height, Position = Position, WindowState = WindowState };
        fresh.projectId = projectId;
        fresh.projectsRoot = projectsRoot;
        if (api != null) fresh.Connect(api);
        if (snapshot != null) fresh.Show(snapshot);
        fresh.OpenSide(sideMode);
        return fresh;
    }

    public void ShowError(string message) { title.Text = Strings.T("Planwharf could not start"); hint.Text = message; hint.Foreground = Palette.Danger; }

    // Shows a snapshot of the server's state; the window redraws only what changed.
    public void Show(JsonNode state)
    {
        // A refresh started before closing can complete after a replacement window opens.
        if (closed) return;
        var alerts = WorkAlerts.Decide(snapshot, state, Prefs.Alerts);
        snapshot = state;
        foreach (var alert in alerts) AlertReceived?.Invoke(alert);
        Render();
    }

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
        sideTitle.Text = mode != null && sideTitles.TryGetValue(mode, out var named) ? named : "";
        foreach (var (name, button) in sideButtons) button.IsChecked = name == mode;
        if (mode == "files" && projectId != null && !repoRaw.ContainsKey(projectId)) _ = LoadRepoAsync(projectId, CurrentWorkId(), false);
        sidePanel.IsVisible = mode != null;
        Render();
    }


    async Task RefreshAsync(bool readOnly)
    {
        if (api == null || refreshing) return;
        refreshing = true;
        try { Show(await api.SnapshotAsync(readOnly, projectId)); }
        catch (Exception error) { hint.Text = Strings.T("Planwharf is not answering: ") + error.Message; }
        finally { refreshing = false; }
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
            title.Text = Strings.T("Welcome"); badgeText.Text = "";
            hint.Text = Strings.T("Create a project to begin: \u002B New project."); hint.Foreground = Palette.Muted; actionButton.IsEnabled = false;
            RenderWelcome();
            RenderSide(null);
            if (badge != null) badge.IsVisible = false;
            return;
        }
        title.Text = view.Name;
        var workingNow = WorkingNow(view.Id);
        badgeText.Text = workingNow is { } work ? ChatViews.WorkingText(work.Who, work.Since, DateTimeOffset.UtcNow) : view.Status;
        // Green while working or when the goal is reached, as the chat page's badge.
        var good = workingNow != null || view.Status == Strings.T("Done") || view.Status == Strings.T("Goal reached");
        badgeText.Foreground = good ? Palette.Accent : Palette.Muted;
        if (badge != null) badge.Background = good ? Palette.AccentSoft : Palette.Surface2;
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

    // Work running again after Resume is at work, even while an older checkpoint is still saved.
    static bool IsStopped(WorkView work) => work.Status is "FAILED" or "BLOCKED" or "PAUSED" or "STOPPED" or "CHECKPOINTED"
        || work.CanResume && work.Status is not ("COMPLETE" or "RUNNING") && !work.Steps.Any(step => step.Status == "working");

    static Control Note(string text) => new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10),
        Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Muted, FontSize = 13 } };

    // A dashed note with a sign in front, as the chat page's hint under the lead's plan.
    static Control Hint(string icon, Control body)
    {
        var dashes = new Avalonia.Controls.Shapes.Rectangle { Stroke = Palette.Line, StrokeThickness = 1, StrokeDashArray = [4, 3], RadiusX = 12, RadiusY = 12 };
        var content = new Border { Padding = new Thickness(14, 12), Child = new DockPanel { Children = { Look.Icon(icon, 16, Palette.Muted), body } } };
        var sign = (Control)((DockPanel)content.Child!).Children[0];
        sign.VerticalAlignment = VerticalAlignment.Top; sign.Margin = new Thickness(0, 1, 8, 0);
        DockPanel.SetDock(sign, Dock.Left);
        return new Panel { Children = { dashes, content } };
    }

    static TextBlock Line(string text, IBrush? color = null, double size = 14, FontWeight? weight = null) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = color ?? Palette.Text, FontSize = size, FontWeight = weight ?? FontWeight.Normal };

    static Control List(string heading, IEnumerable<string> lines)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(0, 4, 0, 0), Children = { Line(heading, Palette.Text, 13, FontWeight.SemiBold) } };
        foreach (var line in lines) panel.Children.Add(Line("• " + line, Palette.Muted, 13));
        return panel;
    }

    // A section that opens and closes, as the chat page's details: a small line with a triangle, the
    // content in a soft box below.
    static FoldBox Fold(string header, Control content) => new(header, content);

    sealed class FoldBox : StackPanel
    {
        readonly Border body;
        readonly TextBlock arrow = new() { FontSize = 10, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center, Width = 12 };
        bool open;
        public event Action<bool>? Toggled;

        public FoldBox(string header, Control content)
        {
            Spacing = 6;
            var head = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Foreground = Palette.Muted, FontSize = 13,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { arrow, new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center } } } };
            head.Resources["ButtonBackgroundPointerOver"] = Brushes.Transparent; head.Resources["ButtonBackgroundPressed"] = Brushes.Transparent;
            head.Resources["ButtonForegroundPointerOver"] = Palette.Text; head.Resources["ButtonForegroundPressed"] = Palette.Text;
            head.Click += (_, _) => { IsExpanded = !open; Toggled?.Invoke(open); };
            body = new Border { Background = Palette.Surface2, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8), Child = content };
            Children.Add(head); Children.Add(body);
            IsExpanded = true;
        }

        public bool IsExpanded
        {
            get => open;
            set { open = value; body.IsVisible = value; arrow.Text = value ? "▼" : "▶"; }
        }
    }

    Control AlertsCard(string projectId, IReadOnlyList<AlertView> alerts)
    {
        var panel = new StackPanel { Spacing = 8, Children = { Look.Head("alert", Palette.Warn, Palette.WarnSoft, Strings.T("Something needs a look"), 17) } };
        foreach (var alert in alerts)
        {
            var ignore = Small(Strings.T("Ignore")); ignore.VerticalAlignment = VerticalAlignment.Top;
            ignore.Click += async (_, _) => await DoAsync(() => api!.IgnoreAlertAsync(projectId, alert.Id), Strings.T("Ignored."));
            var row = new DockPanel { Children = { ignore, new StackPanel { Children = { Line(alert.Title, Palette.Text, 14, FontWeight.SemiBold), Line(alert.Message, Palette.Muted, 13) } } } };
            DockPanel.SetDock(ignore, Dock.Right);
            panel.Children.Add(row);
        }
        panel.Children.Add(Line(Strings.T("Planwharf checks the work itself; this is a reason to look, not proof that something broke."), Palette.Muted, 12));
        return new Border { BorderBrush = Palette.Warn, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 14), Child = panel };
    }

    Control WorkCard(string projectId, WorkView work, TeamView team, CardData card)
    {
        var done = work.Status == "COMPLETE";
        var stopped = !done && IsStopped(work);
        var proposal = work.Status == "READY_FOR_APPROVAL";
        var heading = done ? Strings.T("Work finished") : stopped ? Strings.T("Work stopped") : proposal ? Strings.T("The lead proposes this work") : Strings.T("Crew at work") + (card.Since.Length > 0 ? " · " + card.Since : "");
        var (icon, tone, soft) = done ? ("check", Palette.Accent, Palette.AccentSoft) : stopped ? ("alert", Palette.Warn, Palette.WarnSoft) : proposal ? ("lead", Palette.Lead, Palette.LeadSoft) : ("rocket", Palette.Accent, Palette.AccentSoft);
        var panel = new StackPanel { Spacing = 8, Children = { Look.Head(icon, tone, soft, heading, 17) } };
        panel.Children.Add(Line(stopped && work.NextAction.Length > 0 ? work.NextAction : work.Title));
        if (done)
        {
            var changed = card.Repo is { Git: true } repo ? Strings.Format("{0} files not yet committed", repo.Uncommitted) : card.Repo == null ? Strings.T("Counting changed files\u2026") : Strings.T("Changed files: unknown");
            panel.Children.Add(Line(string.Join("   ·   ", new[] { Strings.Format("{0}/{1} steps done", work.Done, work.Total), card.Took, changed }.Where(part => part.Length > 0)), Palette.Muted, 13));
        }
        if ((done || stopped) && ProjectViews.Decisions(work.Results) is { Count: > 0 } decisions)
        {
            var asks = new StackPanel { Spacing = 3, Children = { Line(Strings.T("Needs your decision"), Palette.Warn, 13, FontWeight.SemiBold) } };
            foreach (var decision in decisions) asks.Children.Add(Line("• " + decision, Palette.Text, 13));
            panel.Children.Add(new Border { Background = Palette.WarnSoft, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8), Child = asks });
        }
        if (card.Summary.Count > 0) panel.Children.Add(List(Strings.T("Summary"), card.Summary));
        if (done && work.Results.Count > 0) panel.Children.Add(List(Strings.T("What was done"), work.Results));
        if (!done && !proposal)
        {
            panel.Children.Add(Look.Bar(work.Total == 0 ? 0 : work.Done * 100 / work.Total));
            panel.Children.Add(Line(Strings.Format("{0} of {1} steps done", work.Done, work.Total), Palette.Muted, 12));
            if (work.Waiting.Length > 0 && !stopped) panel.Children.Add(Line(work.Waiting, Palette.Warn, 13));
        }
        foreach (var step in work.Steps) panel.Children.Add(StepRow(step, team, editable: !done && step.Status != "done"));
        if (proposal && work.OpenDecisions.Count > 0)
        {
            var asks = new StackPanel { Spacing = 8, Children = { Line(Strings.T("Needs your decision"), Palette.Warn, 13, FontWeight.SemiBold) } };
            foreach (var decision in work.OpenDecisions) asks.Children.Add(DecisionCard(decision.Id, decision.Text));
            asks.Children.Add(Line(Strings.T("Answer these before you approve, so the crew knows what to do."), Palette.Muted, 12));
            panel.Children.Add(new Border { Background = Palette.WarnSoft, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8), Child = asks });
        }
        if (proposal) panel.Children.Add(Line(card.AutoApprove ? Strings.T("The crew approves this by itself.") : Strings.T("Press Approve and start below, or tell the lead what to change."), Palette.Muted, 13));
        if (!done && !stopped && !proposal && card.Activity.Text.Length > 0)
            panel.Children.Add(Fold(Strings.Format("What {0} is doing", card.Activity.Who), new SelectableTextBlock { Text = card.Activity.Text, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12, Foreground = Palette.Muted }));
        if ((done || stopped) && card.Usage.Count > 0)
        {
            // As the chat page: a small heading, then each model with its provider's dot.
            var usage = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0), Children = {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Look.Icon("usage", 16, Palette.Text), new TextBlock { Text = Strings.T("What this work used"), FontSize = 13, Foreground = Palette.Text } } } } };
            foreach (var (model, text) in card.Usage)
            {
                var provider = team.Models.FirstOrDefault(m => m.Label == model) is { } found ? (found.ProviderId.Length > 0 ? found.ProviderId : found.Provider) : "";
                usage.Children.Add(new DockPanel { Children = { Look.Dot(Look.ProviderColour(provider)).With(d => { d.Margin = new Thickness(4, 0, 12, 0); DockPanel.SetDock(d, Dock.Left); }),
                    new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Inlines = new Avalonia.Controls.Documents.InlineCollection {
                        new Avalonia.Controls.Documents.Run(model) { FontWeight = FontWeight.Bold, FontSize = 15, Foreground = Palette.Text }, new Avalonia.Controls.Documents.Run(" · " + text) { Foreground = Palette.Muted } } } } });
            }
            panel.Children.Add(usage);
        }
        if (done)
        {
            panel.Children.Add(Line(Strings.T("Try the result in the project folder. Nothing is published until you push it."), Palette.Muted, 13));
            var seeFiles = Look.Button(Strings.T("See changed files"), "file", Look.Kind.Small);
            seeFiles.Click += (_, _) => OpenSide("files");
            var seeUsage = Look.Button(Strings.T("See usage"), "usage", Look.Kind.Small);
            seeUsage.Click += (_, _) => OpenSide("usage");
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { seeFiles, seeUsage } });
            if (GitBox(projectId, work, card.Repo) is { } git) panel.Children.Add(git);
        }
        // As .card.crew: a soft wash of the state's colour at the top.
        var wash = done || stopped || proposal ? soft : null;
        var box = Look.Card(panel, done ? Palette.Accent : stopped ? Palette.Warn : proposal ? Palette.Lead : Palette.Accent);
        if (wash is ISolidColorBrush top)
            box.Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute), EndPoint = new RelativePoint(0, 90, RelativeUnit.Absolute),
                GradientStops = { new GradientStop(top.Color, 0), new GradientStop(((ISolidColorBrush)Palette.Card).Color, 1) } };
        return box;
    }

    Control StepRow(WorkStep step, TeamView team, bool editable)
    {
        var stepModel = team.Models.FirstOrDefault(m => m.Id == step.ModelId);
        Control model = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = {
            Look.Dot(Look.ProviderColour(stepModel is { } sm ? (sm.ProviderId.Length > 0 ? sm.ProviderId : sm.Provider) : "")).With(d => d.Margin = new Thickness(0, 0, 5, 0)),
            new TextBlock { Text = step.Model.Length > 0 ? step.Model : Strings.T("not assigned"), Foreground = Palette.Text, FontSize = 15, VerticalAlignment = VerticalAlignment.Center } } };
        // Open work: the model of a step that is not done can be changed, among the team models with that role.
        if (editable)
        {
            var choices = TeamViews.ModelsFor(team, step.Role).ToList();
            if (choices.All(m => m.Id != step.ModelId)) choices.Insert(0, new TeamModel(step.ModelId, step.Model.Length > 0 ? step.Model : Strings.T("not assigned"), "", null, true, null, false, false, false, false, false));
            var menu = new ComboBox { ItemsSource = choices.Select(m => m.Label + (m.QuotaLeft is { } left ? Strings.Format(" \u00B7 {0}% left", left) : "")).ToList(), SelectedIndex = choices.FindIndex(m => m.Id == step.ModelId), MinWidth = 220 };
            // While the menu is open the chat is not redrawn, or the menu and the choice would vanish under the pointer.
            menu.DropDownOpened += (_, _) => menuOpen = true;
            menu.DropDownClosed += (_, _) => { menuOpen = false; threadSig = null; };
            menu.SelectionChanged += async (_, _) => { if (menu.SelectedIndex >= 0 && choices[menu.SelectedIndex].Id != step.ModelId) await ChangeStepAsync(step, choices[menu.SelectedIndex]); };
            model = menu;
        }
        var (roleColor, roleSoft, roleIcon) = Palette.Role(step.Role);
        var main = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = Strings.T(step.Role), Foreground = roleColor, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "·", Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center }, model } } } };
        if (step.Title.Length > 0) main.Children.Add(Line(step.Title, Palette.Muted, 13));
        var good = step.Status == "done"; var bad = step.Status is "blocked" or "failed";
        var status = Look.Badge((good ? "✓ " : "") + Strings.T(step.Status), good ? Palette.Accent : bad ? Palette.Danger : Palette.Warn, good ? Palette.AccentSoft : bad ? Palette.DangerSoft : Palette.WarnSoft);
        status.VerticalAlignment = VerticalAlignment.Top; status.Margin = new Thickness(8, 2, 0, 0);
        var tile = Look.Tile(roleIcon, roleColor, roleSoft, 34, 18, 10);
        tile.VerticalAlignment = VerticalAlignment.Top; tile.Margin = new Thickness(0, 0, 10, 0);
        var row = new DockPanel { Children = { status, tile, main } };
        DockPanel.SetDock(status, Dock.Right); DockPanel.SetDock(tile, Dock.Left);
        return new Border { Background = Palette.Card, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(10), Child = row };
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
        // A short first line, then the goal and who did what: the user can change all of it.
        var message = Look.Field(new TextBox { MaxLength = 2000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64,
            Text = commitMessages.TryGetValue(projectId, out var kept) ? kept : CommitMessages.Default(work) });
        message.TextChanged += (_, _) => commitMessages[projectId] = message.Text ?? "";
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button Git(string label, bool push, bool primary)
        {
            var button = Small(label, primary);
            button.Content = Look.Label(label, push ? "rocket" : "check", 14, 5);
            button.IsEnabled = busy == null;
            button.Click += async (_, _) => await CommitAsync(projectId, work.Id, changed > 0, ahead, changed > 0 ? message.Text?.Trim() ?? "" : "push", push);
            return button;
        }
        if (changed > 0)
        {
            panel.Children.Add(Line(Strings.T("Save it in git when you are happy with it"), Palette.Text, 13));
            panel.Children.Add(message);
            buttons.Children.Add(Git(Strings.T("Commit"), false, false)); buttons.Children.Add(Git(Strings.T("Commit and push"), true, true));
        }
        else if (ahead > 0)
        {
            panel.Children.Add(Line(Strings.Format(ahead == 1 ? "{0} commit is not on GitHub yet." : "{0} commits are not on GitHub yet.", ahead), Palette.Text, 13));
            buttons.Children.Add(Git(Strings.Format(ahead == 1 ? "Push {0} commit to GitHub" : "Push {0} commits to GitHub", ahead), true, true));
        }
        panel.Children.Add(buttons);
        if (busy != null) panel.Children.Add(Line(busy, Palette.Muted, 13));
        else if (said != default) panel.Children.Add(Line(said.Text, said.Ok ? Palette.Accent : Palette.Danger, 13));
        return panel;
    }


    Control DecisionCard(string id, string text)
    {
        var box = Look.Field(new TextBox { Watermark = Strings.T("Or write your answer") });
        var yes = Small(Strings.T("Yes"), true);
        var no = Small(Strings.T("No"), false);
        async Task Answer(string value)
        {
            if (api == null || projectId == null) return;
            yes.IsEnabled = no.IsEnabled = false;
            try { await api.PostAsync("/api/decisions/answer", new { projectId, decisionId = id, answer = value }); await RefreshAsync(readOnly: true); }
            catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; yes.IsEnabled = no.IsEnabled = true; }
        }
        yes.Click += async (_, _) => await Answer("yes");
        no.Click += async (_, _) => await Answer("no");
        var send = Small(Strings.T("Send"), false);
        send.Click += async (_, _) => { if ((box.Text ?? "").Trim().Length > 0) await Answer(box.Text!.Trim()); };
        return new StackPanel { Spacing = 6, Children = {
            Line(text, Palette.Text, 13),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { yes, no, box, send } }
        } };
    async Task CheckPrsAsync()
    {
        if (api == null || projectId == null) return;
        hint.Text = Strings.T("Checking pull requests…"); hint.Foreground = Palette.Muted;
        try
        {
            var listed = await api.GetAsync("/api/prs?projectId=" + Uri.EscapeDataString(projectId));
            var prs = listed["prs"] as JsonArray ?? new JsonArray();
            if (prs.Count == 0) { hint.Text = Strings.T("No open pull requests."); hint.Foreground = Palette.Text; return; }
            var lines = new List<string>();
            foreach (var pr in prs)
            {
                var number = pr?["number"]?.GetValue<int>() ?? 0;
                var conflicts = pr?["conflicts"]?.GetValue<bool>() == true;
                var failing = pr?["failing"] as JsonArray;
                if (conflicts) lines.Add(Strings.Format("#{0} has conflicts.", number));
                else if (failing is { Count: > 0 }) lines.Add(Strings.Format("#{0} fails: {1}.", number, string.Join(", ", failing.Select(item => item?.GetValue<string>()))));
                else lines.Add(Strings.Format("#{0} is green.", number));
            }
            var broken = prs.Where(pr => pr?["conflicts"]?.GetValue<bool>() == true || pr?["failing"] is JsonArray { Count: > 0 }).ToList();
            if (broken.Count == 0) { hint.Text = string.Join(" ", lines); hint.Foreground = Palette.Accent; return; }
            if (!await Confirm(string.Join("\n", lines) + "\n\n" + Strings.T("Fix the conflicts and hand the failures to the crew?"), Strings.T("Fix them")))
            { hint.Text = string.Join(" ", lines); hint.Foreground = Palette.Warn; return; }
            foreach (var pr in broken)
            {
                var number = pr?["number"]?.GetValue<int>() ?? 0;
                if (pr?["conflicts"]?.GetValue<bool>() == true)
                    await api.PostAsync("/api/prs/resolve", new { projectId = projectId, number });
                var failing = pr?["failing"] as JsonArray;
                if (failing is { Count: > 0 })
                    await api.AskAsync(projectId, Strings.Format("Fix the failing checks on pull request #{0}: {1}.", number, string.Join(", ", failing.Select(item => item?.GetValue<string>()))));
            }
            hint.Text = Strings.T("Pull requests checked. Conflicts merged where clean; failures handed to the crew."); hint.Foreground = Palette.Accent;
        }
        catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; }
    }

    async Task CommitAsync(string projectId, string workId, bool changes, int ahead, string message, bool push)
    {
        if (api == null) return;
        if (changes && message.Length == 0) { hint.Text = Strings.T("Write a commit message."); hint.Foreground = Palette.Warn; return; }
        // The user's own action: say exactly what will happen before doing it. Never a force push.
        var what = changes ? Strings.Format("Commit all changes in the project folder{0}, with the message \u0022{1}\u0022?", (push ? Strings.T(" and push them to GitHub") : ""), message) : Strings.Format(ahead == 1 ? "Push {0} commit to GitHub?" : "Push {0} commits to GitHub?", ahead);
        if (!await new ConfirmDialog(what, push ? Strings.T("Push") : Strings.T("Commit")).ShowDialog<bool>(this)) return;
        gitBusy[projectId] = push ? (changes ? Strings.T("Committing, then pushing to GitHub. If GitHub asks you to sign in, a window opens.") : Strings.T("Pushing to GitHub. If GitHub asks you to sign in, a window opens.")) : Strings.T("Committing\u2026");
        gitSaid.Remove(projectId); threadSig = null; Render();
        (bool, string) said;
        try
        {
            var result = await api.CommitAsync(projectId, message, push);
            string Get(string key) => result?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
            var pushed = result?["pushed"]?.GetValue<bool>() == true;
            var text = pushed ? Strings.Format("{0} to {1} on GitHub.", (Get("commit").Length > 0 ? Strings.Format("Committed {0} and pushed", Get("commit")) : Strings.T("Pushed")), Get("branch")) : Strings.Format("Committed {0} on {1}. Not pushed yet.", Get("commit"), Get("branch"));
            if (pushed && Get("prUrl").Length > 0) text += " " + Get("prUrl");
            said = (true, text);
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
        if (sideMode == "team") sig += snapshot["projectToolSettings"]?[projectId ?? ""]?.ToJsonString();
        var progress = sideMode == "progress" && projectId != null ? PanelViews.Progress(snapshot, projectId, view?.Status ?? "") : null;
        var usage = sideMode == "usage" ? PanelViews.Usage(snapshot) : null;
        var raw = projectId != null && repoRaw.TryGetValue(projectId, out var found) ? found : (null, null);
        var files = sideMode == "files" ? PanelViews.Files(raw.Json, folder, raw.Error) : null;
        sig += progress is { } p ? p.Status + p.Next + p.TimeLeft + string.Join(",", p.Steps) + string.Join(",", p.Recent) : "";
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

    // A line of a status list: a small round label, then the text (as .status-list in the chat page).
    static Control Item(string badge, string text, string? sub = null, IBrush? color = null)
    {
        var body = new StackPanel { Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text, FontSize = 13 } } };
        if (!string.IsNullOrEmpty(sub)) body.Children.Add(Explain(sub));
        var soft = color == Palette.Accent ? Palette.AccentSoft : color == Palette.Danger ? Palette.DangerSoft : Palette.Surface2;
        var tag = Look.Badge(badge, color ?? Palette.Muted, soft);
        tag.VerticalAlignment = VerticalAlignment.Top; tag.Margin = new Thickness(0, 1, 8, 0);
        DockPanel.SetDock(tag, Dock.Left);
        return new DockPanel { Children = { tag, body } };
    }

    // A time and what happened, as the chat page's activity list.
    static Control Timed(string at, string text)
    {
        var time = new TextBlock { Text = at, Foreground = Palette.Muted, FontSize = 13, Width = 46, VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(time, Dock.Left);
        return new DockPanel { Children = { time, new TextBlock { Text = text, Foreground = Palette.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap } } };
    }

    static void OpenWorkPlan(string file)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = file, UseShellExecute = true }); }
        catch { /* the line stays; opening the file is best effort */ }
    }

    void ProgressSide(ProgressPanel panel)
    {
        var nowSection = Look.Section("play", Palette.Accent, Palette.AccentSoft, Strings.T("Now"));
        nowSection.Children.Add(Look.Badge(panel.Status, Palette.Muted, Palette.Surface2).With(b => b.HorizontalAlignment = HorizontalAlignment.Left));
        if (panel.TimeLeft.Length > 0) nowSection.Children.Add(Line(panel.TimeLeft, Palette.Text, 13));
        if (panel.Next.Length > 0) nowSection.Children.Add(Line(panel.Next, Palette.Text, 13));
        if (panel.WorkPlan.Length > 0)
        {
            var openPlan = Look.Button(Strings.T("Work plan"), "file", Look.Kind.Small);
            openPlan.HorizontalAlignment = HorizontalAlignment.Left;
            openPlan.Click += (_, _) => OpenWorkPlan(panel.WorkPlan);
            nowSection.Children.Add(openPlan);
        }
        side.Children.Add(nowSection);
        if (panel.Steps.Count > 0)
        {
            var steps = Look.Section("progress", Palette.Build, Palette.BuildSoft, Strings.T("Steps"));
            foreach (var step in panel.Steps) steps.Children.Add(Item(Strings.T(step.Status), step.Title, step.Model, step.Status == "complete" ? Palette.Accent : step.Status is "failed" or "blocked" ? Palette.Danger : null));
            side.Children.Add(steps);
        }
        var recent = Look.Section("clock", Palette.Lead, Palette.LeadSoft, Strings.T("Recent activity"));
        if (panel.Recent.Count == 0) recent.Children.Add(Explain(Strings.T("Nothing yet.")));
        foreach (var (at, text) in panel.Recent) recent.Children.Add(Timed(at, text));
        side.Children.Add(recent);
        side.Children.Add(Explain(Strings.T("Folder: ") + (panel.Folder.Length > 0 ? panel.Folder : Strings.T("not set"))));
    }

    void UsageSide(UsagePanel panel)
    {
        var quota = Look.Section("usage", Palette.Accent, Palette.AccentSoft, Strings.T("Quota left"));
        if (panel.Plans.Count == 0) quota.Children.Add(Explain(Strings.T("No provider reports quota.")));
        foreach (var plan in panel.Plans)
        {
            var card = new StackPanel { Spacing = 2, Children = { Line(plan.Label, Palette.Text, 15, FontWeight.SemiBold) } };
            if (plan.Note != null) card.Children.Add(Explain(plan.Note).With(t => t.FontSize = 12));
            foreach (var window in plan.Windows)
            {
                card.Children.Add(new TextBlock { FontSize = 12, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Inlines = new Avalonia.Controls.Documents.InlineCollection {
                    new Avalonia.Controls.Documents.Run(window.Name.Length > 0 ? window.Name + ": " : "") { FontWeight = FontWeight.Bold, Foreground = Palette.Text },
                    new Avalonia.Controls.Documents.Run(Strings.Format("{0}% left", window.LeftPct) + (window.Resets.Length > 0 ? " · " + window.Resets : "")) } });
                card.Children.Add(Look.Bar(window.LeftPct));
            }
            quota.Children.Add(new Border { Background = Palette.Card, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(10), Child = card });
        }
        side.Children.Add(quota);
        foreach (var (heading, icon, color, soft, rows) in new[] { (Strings.T("Lead calls"), "lead", Palette.Lead, Palette.LeadSoft, panel.Lead), (Strings.T("Crew work"), "build", Palette.Build, Palette.BuildSoft, panel.Crew) })
        {
            var section = Look.Section(icon, color, soft, heading);
            if (rows.Count == 0) section.Children.Add(Explain(Strings.T("None yet.")));
            // As .kv: the model on the left, the numbers on the right.
            foreach (var (model, text) in rows)
            {
                var value = new TextBlock { Text = text, Foreground = Palette.Text, FontSize = 14, Margin = new Thickness(12, 0, 0, 0), TextAlignment = TextAlignment.Right };
                DockPanel.SetDock(value, Dock.Right);
                section.Children.Add(new DockPanel { Children = { value, new TextBlock { Text = model, Foreground = Palette.Text, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis } } });
            }
            side.Children.Add(section);
        }
        side.Children.Add(Explain(Strings.T("Calls \u00B7 time \u00B7 output tokens. A provider that does not report tokens shows \u0022not reported\u0022, never a guess.")));
    }

    void FilesSide(FilesPanel panel, string folder)
    {
        var again = Look.Button(Strings.T("Check again"), "refresh", Look.Kind.Small);
        again.HorizontalAlignment = HorizontalAlignment.Left;
        again.Click += async (_, _) => { if (projectId != null) await LoadRepoAsync(projectId, CurrentWorkId(), true); };
        if (panel.Message != null)
        {
            side.Children.Add(Line(panel.Message, Palette.Muted, 13));
            if (folder.Length > 0) side.Children.Add(again);
            return;
        }
        var section = Look.Section("file", Palette.Build, Palette.BuildSoft, Strings.T("Not yet saved in Git"));
        section.Children.Add(new TextBlock { FontSize = 13, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, Inlines = new Avalonia.Controls.Documents.InlineCollection {
            new Avalonia.Controls.Documents.Run(Strings.T("Files the crew or you changed since the last commit on ")),
            new Avalonia.Controls.Documents.Run(panel.Branch.Length > 0 ? panel.Branch : Strings.T("the current branch")) { FontWeight = FontWeight.Bold, Foreground = Palette.Text }, new Avalonia.Controls.Documents.Run(".") } });
        if (panel.Files.Count == 0) section.Children.Add(Line(Strings.T("Nothing changed. The folder matches the last commit."), Palette.Text, 13));
        foreach (var file in panel.Files) section.Children.Add(Item(Strings.T(file.Kind), file.Path, null, file.Kind == "conflict" ? Palette.Danger : file.Kind is "new" or "added" ? Palette.Accent : null));
        if (panel.Total > panel.Files.Count) section.Children.Add(Explain(Strings.Format("Showing {0} of {1}.", panel.Files.Count, panel.Total)));
        side.Children.Add(section);
        side.Children.Add(Explain(folder + (panel.CheckedAt.Length > 0 ? Strings.T(" \u00B7 checked ") + panel.CheckedAt : "")));
        side.Children.Add(again);
    }

    static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Palette.Text, Margin = new Thickness(0, 8, 0, 0) };
    static TextBlock Explain(string text) => new() { Text = text, Foreground = Palette.Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap };

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
        if (step.Status == "working" && !await new ConfirmDialog(Strings.Format("Switch this step to {0}? Its current worker stops and the step starts again with the new model. Files already written stay.", model.Label), Strings.T("Switch")).ShowDialog<bool>(this))
        { threadSig = null; Render(); return; }
        await DoAsync(() => api!.ChangeStepModelAsync(projectId!, step.Id, model.Id), Strings.Format("{0} now uses {1}.", Strings.T(step.Role), model.Label));
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
            hint.Text = Strings.T("Project created. Choose its team, then tell the lead what to build."); hint.Foreground = Palette.Accent;
        }
        catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; }
        await RefreshAsync(readOnly: false);
        if (TeamViews.Team(snapshot!).LeadId == null) OpenSide("team");
    }

    async Task DropWorkAsync()
    {
        if (api == null || snapshot == null || projectId == null || ProjectViews.Project(snapshot, projectId) is not { } view) return;
        var title = view.Work?.Title is { Length: > 0 } t ? t[..Math.Min(160, t.Length)] : Strings.T("this work");
        if (!await new ConfirmDialog(Strings.Format("Drop the stopped work \u0022{0}\u0022? It will not be resumed. Files in the project folder stay as they are, and you can then start a new goal.", title), Strings.T("Drop it")).ShowDialog<bool>(this)) return;
        await DoAsync(() => api.DropWorkAsync(projectId), Strings.T("Stopped work dropped. You can start a new goal."));
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
                        if (!await new ConfirmDialog(Strings.Format("The crew will work on: {0}", view.NextGoal), Strings.T("Start")).ShowDialog<bool>(this)) break;
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
