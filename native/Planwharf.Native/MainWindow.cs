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

// The main window: projects on the left, the chat with the lead and the crew's work on the right.
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

    PlanwharfApi? api;
    JsonNode? snapshot;
    string? projectId, pending, threadSig, listSig;
    bool busy;
    int ticks;

    public MainWindow(string version)
    {
        Title = "Planwharf"; Width = 1280; Height = 820; MinWidth = 900; MinHeight = 560; Background = Palette.Background;
        threadScroll = new ScrollViewer { Content = thread, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(16, 16, 16, 12), Children = {
            new TextBlock { Text = "Planwharf", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Palette.Text },
            new TextBlock { Text = version, FontSize = 12, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3) } } };
        projectList.SelectionChanged += (_, _) => { if (projectList.SelectedItem is ListBoxItem { Tag: string id } && id != projectId) { projectId = id; threadSig = null; Render(); } };
        var left = new Border { Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = new DockPanel { Children = { brand, projectList } } };
        DockPanel.SetDock(brand, Dock.Top);

        var badge = new Border { Background = Brush.Parse("#1d3328"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = badgeText };
        var header = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(24, 14), Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { title, badge } } };
        var actions = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Children = { actionButton, hint } };
        DockPanel.SetDock(actionButton, Dock.Left);
        var composer = new DockPanel { Children = { send, input } };
        DockPanel.SetDock(send, Dock.Right);
        var footer = new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 12), Child = new StackPanel { MaxWidth = 860, Children = { actions, composer,
            new TextBlock { Text = "Talk with the lead to shape the plan, then press Let the crew do it. The crew builds, checks and stops when the goal is reached.", Foreground = Palette.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) } } } };
        var right = new DockPanel { Children = { header, footer, threadScroll } };
        DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(footer, Dock.Bottom);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        grid.Children.Add(left); grid.Children.Add(right); Grid.SetColumn(right, 1);
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

    async Task RefreshAsync(bool readOnly)
    {
        if (api == null) return;
        try { Show(await api.SnapshotAsync(readOnly)); }
        catch (Exception error) { hint.Text = "Planwharf is not answering: " + error.Message; }
    }

    void Render()
    {
        if (snapshot == null) return;
        var projects = ProjectViews.Projects(snapshot);
        projectId ??= projects.FirstOrDefault()?.Id;
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
        if (view == null) { title.Text = "Welcome"; hint.Text = "Create a project to begin."; return; }
        title.Text = view.Name; badgeText.Text = view.Status;
        actionButton.Content = view.ActionText;
        actionButton.Background = view.Action is CrewAction.StopTheCrew or CrewAction.StopWork ? Palette.Danger : Palette.Accent;
        actionButton.IsEnabled = !busy; send.IsEnabled = !busy && pending == null;
        if (pending == null) { hint.Text = view.Hint; hint.Foreground = Palette.Muted; }
        RenderThread(view);
    }

    void RenderThread(ProjectView view)
    {
        var sig = string.Join("|", view.Messages.Select(m => m.At.ToUnixTimeMilliseconds() + m.Text.Length)) + pending + view.NextGoal
            + (view.Work is { } w ? w.Title + w.Status + string.Join(",", w.Steps.Select(s => s.Model + s.Status)) + w.Results.Count : "");
        if (sig == threadSig) return;
        var atBottom = threadScroll.Offset.Y + threadScroll.Viewport.Height >= threadScroll.Extent.Height - 120;
        threadSig = sig;
        thread.Children.Clear();
        foreach (var message in view.Messages) thread.Children.Add(Bubble(message.FromUser, message.Who, message.Text));
        if (pending != null) { thread.Children.Add(Bubble(true, "You", pending)); thread.Children.Add(Bubble(false, "Lead", "Thinking…")); }
        else if (view.NextGoal != null && view.Action == CrewAction.LetTheCrewDoIt)
            thread.Children.Add(Note($"Nothing has started yet. When the plan is right, press Let the crew do it: the crew will work on\n{view.NextGoal}"));
        if (view.Work != null) thread.Children.Add(WorkCard(view.Work));
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

    static Control WorkCard(WorkView work)
    {
        var done = work.Status == "COMPLETE";
        var panel = new StackPanel { Spacing = 8, Children = {
            new TextBlock { Text = done ? "Work finished" : work.Status is "PAUSED" or "FAILED" ? "Work stopped" : work.Status == "READY_FOR_APPROVAL" ? "The lead proposes this work" : "Crew at work", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Palette.Text },
            new TextBlock { Text = work.Title, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text },
            new ProgressBar { Minimum = 0, Maximum = Math.Max(1, work.Total), Value = work.Done, Height = 6, Foreground = Palette.Accent },
            new TextBlock { Text = $"{work.Done} of {work.Total} steps done", Foreground = Palette.Muted, FontSize = 12 } } };
        foreach (var step in work.Steps)
            panel.Children.Add(new Border { Background = Palette.Card, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = new DockPanel { Children = {
                new TextBlock { Text = step.Status, Foreground = step.Status == "done" ? Palette.Accent : step.Status is "blocked" or "failed" ? Palette.Danger : Palette.Warn, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right },
                new TextBlock { Text = $"{step.Role} · {step.Model}", Foreground = Palette.Text, FontWeight = FontWeight.SemiBold } } } });
        if (done && work.Results.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "What was done", FontWeight = FontWeight.SemiBold, Foreground = Palette.Text, Margin = new Thickness(0, 4, 0, 0) });
            foreach (var result in work.Results) panel.Children.Add(new TextBlock { Text = "• " + result, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Muted });
        }
        return new Border { BorderBrush = done ? Palette.Accent : Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 14), Child = panel };
    }

    async Task SendAsync()
    {
        var text = input.Text?.Trim();
        if (api == null || string.IsNullOrEmpty(text) || pending != null || projectId == null) return;
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
