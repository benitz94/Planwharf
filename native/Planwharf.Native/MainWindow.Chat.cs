using Planwharf.Engine;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Planwharf.Native.Core;

namespace Planwharf.Native;

// The chat, as in the chat page: projects with who is working and news, the thread with messages and
// work events, the reply forming while the lead thinks, short orders, and a stop at a set time.
public sealed partial class MainWindow
{
    // Each project has its own message waiting for the lead; switching projects never cancels one.
    readonly Dictionary<string, (string Text, DateTimeOffset Since)> pendingByProject = new();
    readonly Dictionary<string, string> chatSessions = new();
    readonly Dictionary<string, DateTimeOffset> seen = new();
    readonly StackPanel timedRow = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8), IsVisible = false };
    (string ProjectId, DateTimeOffset At)? timedStop;
    DispatcherTimer? liveTimer;

    // The lead's live notes stay open or closed as the user left them; the chat follows a new message once.
    bool thinkingOpen = true;
    (string Text, DateTimeOffset Since)? followedPending;
    (string Text, DateTimeOffset Since)? Pending => projectId != null && pendingByProject.TryGetValue(projectId, out var waiting) ? waiting : null;

    string ModelLabel(string id) => (snapshot?["models"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(model => Str(model["id"]) == id) is { } model
        ? Str(model["label"]) is { Length: > 0 } label ? label : id : "";

    JsonObject? RowOf(string id) => (snapshot?["projects"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => Str(item["id"]) == id);

    // A live crew process, or the lead answering a message from this window.
    (string Who, DateTimeOffset? Since)? WorkingNow(string id)
    {
        if (RowOf(id) is { } row && ChatViews.Working(row, ModelLabel) is { } crew) return crew;
        if (pendingByProject.TryGetValue(id, out var waiting))
        {
            var lead = TeamViews.Team(snapshot!).LeadId is { } leadId ? ModelLabel(leadId) : "";
            return (lead.Length > 0 ? lead : Strings.T("The lead"), waiting.Since);
        }
        return null;
    }

    void RenderProjects(IReadOnlyList<Core.ProjectItem> projects)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = projects.Select(project =>
        {
            var work = WorkingNow(project.Id);
            var latest = ChatViews.LastNews(snapshot!, project.Id);
            bool news;
            // The open project is always read; a project seen for the first time starts read.
            if (project.Id == projectId || !seen.ContainsKey(project.Id)) { seen[project.Id] = seen.TryGetValue(project.Id, out var before) && before > latest ? before : latest; news = false; }
            else news = latest > seen[project.Id];
            return (project, sub: work is { } w ? ChatViews.WorkingText(w.Who, w.Since, now) : project.Status, working: work != null, news);
        }).ToList();
        var sig = string.Join("|", rows.Select(row => row.project.Id + row.project.Name + row.project.SavingMode + row.sub + row.news)) + projectId;
        if (sig == listSig) return;
        listSig = sig;
        // The logo's needle turns while anything is being worked on.
        SpinBrand(rows.Any(row => row.working));
        projectList.ItemsSource = rows.Select(row =>
        {
            var open = row.project.Id == projectId;
            var attention = !row.working && Regex.IsMatch(row.sub, Strings.T("^(Stopped|Needs|Failed|Blocked|Waiting)"), RegexOptions.IgnoreCase);
            var done = !row.working && !attention && Regex.IsMatch(row.sub, Strings.T("^(Done|Goal reached|Finished)"), RegexOptions.IgnoreCase);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = {
                new TextBlock { Text = SavingModes.Beside(row.project.Name, row.project.SavingMode), Foreground = open ? Palette.Accent : Palette.Text, FontSize = 15, FontWeight = open ? FontWeight.SemiBold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = row.sub, Foreground = row.working ? Palette.Accent : attention ? Palette.Warn : Palette.Muted, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis } } };
            // The folder tile says the state at a glance, as in the chat page.
            // While the crew works, the tile shows the lead's compass turning, as in the chat page.
            var tile = row.working ? Look.Tile("lead", Palette.Accent, Palette.AccentSoft, 30, 17, 9).With(t => Look.Spin(t.Child is Panel p ? p.Children[0] : t))
                : Look.Tile("folder", done ? Palette.Accent : attention ? Palette.Warn : Palette.Muted, done ? Palette.AccentSoft : attention ? Palette.WarnSoft : Palette.Surface2, 30, 17, 9);
            tile.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(tile, Dock.Left);
            var line = new DockPanel { Children = { tile } };
            if (row.news)
            {
                var dot = new Ellipse { Width = 9, Height = 9, Fill = Palette.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                ToolTip.SetTip(dot, Strings.T("Something happened here since you last looked"));
                DockPanel.SetDock(dot, Dock.Right);
                line.Children.Add(dot);
            }
            line.Children.Add(text);
            return new ListBoxItem { Tag = row.project.Id, Padding = new Thickness(10, 8), Margin = new Thickness(0, 1), CornerRadius = new CornerRadius(10), Content = line };
        }).ToList();
        projectList.SelectedIndex = rows.FindIndex(row => row.project.Id == projectId);
    }

    // When the lead wrote the latest suggestion, so a goal from before the last work is not offered again.
    DateTimeOffset? AskAt(string? id)
    {
        DateTimeOffset? best = null;
        foreach (var ask in (snapshot?["asks"] as JsonArray ?? []).OfType<JsonObject>().Where(ask => Str(ask["projectId"]) == id))
            if (DateTimeOffset.TryParse(Str(ask["at"]), out var at) && (best == null || at >= best)) best = at;
        return best;
    }

    void RenderFooter(ProjectView? view)
    {
        timedRow.Children.Clear();
        timedRow.IsVisible = false;
        if (view == null) return;
        var row = RowOf(view.Id);
        var goalReady = TeamViews.GoalReady(row, view.NextGoal, AskAt(view.Id), view.Work?.StartedAt);
        var missing = view.Action == CrewAction.LetTheCrewDoIt ? TeamViews.Missing(snapshot!, view.Id, goalReady) : [];
        // Right after start Planwharf shows saved data while it checks the AI tools again.
        var checking = view.Action == CrewAction.LetTheCrewDoIt && Flag(snapshot?["modelsChecking"]);
        var stopping = view.Action is CrewAction.StopTheCrew or CrewAction.StopWork;
        chatModes.IsVisible = true;
        chatModes.Show(view.ChatMode);
        actionButton.IsVisible = view.Action != CrewAction.None;
        actionButton.Content = Look.Label(view.ActionText, stopping ? "stop" : view.Action switch { CrewAction.Approve => "check", CrewAction.Resume => "play", _ => "rocket" });
        Look.Paint(actionButton, stopping ? Look.Kind.Danger : Look.Kind.Primary);
        actionButton.IsEnabled = !busy && missing.Count == 0 && !checking;
        // After finished work the box asks for what comes next.
        input.Watermark = TeamViews.GoalStarted(row) ? Strings.T("Tell the lead what you want built next\u2026") : Strings.T("Tell the lead what you want built\u2026");
        dropButton.IsVisible = view.Action == CrewAction.Resume;
        dropButton.IsEnabled = !busy;
        send.IsEnabled = !busy && Pending == null;
        if (Pending == null && !StickyError())
        {
            var waiting = view.Action == CrewAction.StopTheCrew && view.Work?.Waiting.Length > 0;
            var line = checking ? Strings.T("Checking your AI tools\u2026") : missing.FirstOrDefault()
                ?? (waiting ? Strings.T("Waiting for quota: the crew starts again by itself. You can change models in Team or in a step\u0027s menu at any time.") : view.Hint);
            hint.Text = view.TimeLeft.Length > 0 ? line + "\n" + view.TimeLeft : line;
            hint.Foreground = missing.Count > 0 ? Palette.Warn : Palette.Muted;
        }
        if (timedStop is { } timed && timed.ProjectId == view.Id)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((timed.At - DateTimeOffset.UtcNow).TotalMinutes));
            var cancel = Small(Strings.T("Cancel"));
            cancel.Click += (_, _) => { timedStop = null; Render(); };
            timedRow.Children.Add(Look.Icon("clock", 16, Palette.Warn));
            timedRow.Children.Add(new TextBlock { Text = Strings.Format("Stops at {0:HH:mm} (in {1} min)", timed.At.ToLocalTime(), minutes), Foreground = Palette.Warn, VerticalAlignment = VerticalAlignment.Center });
            timedRow.Children.Add(cancel);
            timedRow.IsVisible = true;
        }
    }

    void CheckTimedStop()
    {
        if (timedStop is { } due && DateTimeOffset.UtcNow >= due.At) { timedStop = null; _ = StopProjectAsync(due.ProjectId); }
    }

    // Stops a project's work now: the crew mode first, then any running step. Work so far stays saved.
    async Task StopProjectAsync(string id)
    {
        if (api == null) return;
        if (timedStop?.ProjectId == id) timedStop = null;
        var auto = snapshot?["autonomy"]?[id];
        var working = Flag(auto?["enabled"]) && Str(auto?["stopReason"]).Length == 0;
        if (!working && !Flag(RowOf(id)?["canStop"])) { hint.Text = Strings.T("Nothing is running on this project."); hint.Foreground = Palette.Warn; return; }
        await DoAsync(async () =>
        {
            var after = working ? await api.SetAutonomyAsync(id, false) : null;
            var row = after != null ? (after["projects"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(item => Str(item["id"]) == id) : RowOf(id);
            if (Flag(row?["canStop"])) await api.StopAsync(id);
        }, Strings.T("Stopped. Work so far is saved; Resume continues from there."));
    }

    // Short orders Planwharf handles itself; everything else goes to the lead.
    async Task<bool> HandleCommandAsync(string text)
    {
        var command = ChatViews.Command(text);
        switch (command.Kind)
        {
            case CommandKind.StopNow when projectId != null: await StopProjectAsync(projectId); return true;
            case CommandKind.StopIn when projectId != null:
                timedStop = (projectId, DateTimeOffset.UtcNow.AddMinutes(command.Minutes));
                hint.Text = Strings.Format(command.Minutes == 1 ? "The crew stops in 1 minute. You can cancel it below." : "The crew stops in {0} minutes. You can cancel it below.", command.Minutes); hint.Foreground = Palette.Accent;
                Render(); return true;
            case CommandKind.OpenPanel: OpenSide(command.Panel); return true;
            case CommandKind.Bench when projectId != null:
                var model = TeamViews.Team(snapshot!).Models.FirstOrDefault(item => item.Label.Contains(command.Model, StringComparison.OrdinalIgnoreCase) || item.Id.Equals(command.Model, StringComparison.OrdinalIgnoreCase));
                if (model == null) { hint.Text = Strings.Format("No model named \u201C{0}\u201D.", command.Model); hint.Foreground = Palette.Warn; return true; }
                if (await Confirm(Strings.Format("Put {0} on the bench? It will have no role until you bring it back.", model.Label), Strings.T("Yes, bench it")))
                    await DoAsync(() => api!.BenchAsync(projectId, model.Id), Strings.Format("{0} is on the bench.", model.Label));
                return true;
            default: return false;
        }
    }

    // "Always approve" is off until the user says yes: the question comes before the work, never halfway.
    // The OK is saved per AI tool; false means the user said no and nothing starts.
    async Task<bool> EnsureOkAsync(bool forLead = false)
    {
        if (api == null || snapshot == null) return false;
        var tools = TeamViews.ToolsNeedingOk(TeamViews.Team(snapshot), forLead);
        if (tools.Count == 0) return true;
        var names = string.Join(Strings.T(" and "), tools.Select(tool => tool.Label));
        if (!await new ConfirmDialog(TeamViews.OkQuestion(tools), Strings.T("Allow ") + names, Strings.T("Planwharf needs your OK"), Strings.T("Cancel")).ShowDialog<bool>(this))
        {
            hint.Text = Strings.T("Nothing started."); hint.Foreground = Palette.Warn;
            return false;
        }
        foreach (var tool in tools) await api.SetWorksUnaskedAsync(tool.Id, true);
        await RefreshAsync(readOnly: false);
        return true;
    }

    async Task SendAsync()
    {
        var text = input.Text?.Trim();
        if (api == null || string.IsNullOrEmpty(text) || projectId == null || Pending != null) return;
        if (await HandleCommandAsync(text)) { input.Text = ""; return; }
        if (snapshot != null && TeamViews.Team(snapshot).LeadId == null) { hint.Text = Strings.T("Choose a lead first (Team panel)."); hint.Foreground = Palette.Warn; OpenSide("team"); return; }
        // Antigravity cannot even answer without the OK.
        try { if (!await EnsureOkAsync(forLead: true)) return; }
        catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; return; }
        var id = projectId;
        if (!chatSessions.TryGetValue(id, out var session)) chatSessions[id] = session = "chat-" + Guid.NewGuid().ToString("N")[..8];
        pendingByProject[id] = (text, DateTimeOffset.UtcNow);
        input.Text = ""; threadSig = listSig = null; Render();
        // While the lead thinks, its reply and what it is doing show as they form.
        liveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(900), DispatcherPriority.Background, async (_, _) =>
        {
            if (pendingByProject.Count == 0) { liveTimer?.Stop(); return; }
            await RefreshAsync(readOnly: true);
        });
        liveTimer.Start();
        try { await api.AskAsync(id, text, session); }
        catch (Exception error) { hint.Text = error.Message; hint.Foreground = Palette.Danger; }
        finally { pendingByProject.Remove(id); threadSig = listSig = null; await RefreshAsync(readOnly: false); }
    }

    // ---------- the thread ----------

    void RenderWelcome()
    {
        var sig = "welcome" + TeamViews.Team(snapshot!).LeadId + (snapshot?["projects"] as JsonArray)?.Count;
        if (sig == threadSig) return;
        threadSig = sig;
        thread.Children.Clear();
        thread.Children.Add(WelcomeCard());
    }

    void RenderThread(ProjectView view)
    {
        var team = TeamViews.Team(snapshot!);
        var card = Card(view)!;
        var items = ChatViews.Thread(snapshot!, view.Id, ModelLabel);
        var live = snapshot?["thinkingByProject"]?[view.Id];
        var row = RowOf(view.Id);
        var goalReady = TeamViews.GoalReady(row, view.NextGoal, AskAt(view.Id), view.Work?.StartedAt);
        var sig = string.Join("|", items.Select(item => item is AskItem ask ? ask.At.ToUnixTimeMilliseconds() + ":" + ask.Answer.Length + ask.Elapsed : item.At.ToUnixTimeMilliseconds() + ((EventItem)item).Text))
            + Pending + Str(live?["draft"]).Length + ":" + Str(live?["thinking"]).Length + view.NextGoal + view.Action + view.ChatMode + view.SavingMode + Str(row?["folder"]) + Str(row?["goal"]) + TeamViews.GoalStarted(row)
            + string.Join(",", TeamViews.Missing(snapshot!, view.Id, goalReady))
            + (view.Work is { } w ? w.Title + w.Status + w.NextAction + w.Waiting + string.Join(",", w.Steps.Select(s => s.ModelId + s.Status)) + w.Results.Count : "")
            + string.Join(",", team.Models.Select(m => m.Id + m.Build + m.Verify + m.Review + m.QuotaLeft))
            + string.Join("", card.Summary) + string.Join("", card.Usage) + card.Activity + string.Join(",", card.Alerts.Select(a => a.Id)) + card.Repo + card.Since + card.AutoApprove
            + gitBusy.GetValueOrDefault(view.Id) + gitSaid.GetValueOrDefault(view.Id) + snapshot?["projectTools"]?[view.Id]?.ToJsonString() + snapshot?["projectToolSettings"]?[view.Id]?.ToJsonString();
        if (sig == threadSig || menuOpen) return;
        var atBottom = threadScroll.Offset.Y + threadScroll.Viewport.Height >= threadScroll.Extent.Height - 120;
        // The chat follows a new message once; after that it moves only if the user is at the bottom.
        var justSent = Pending != null && Pending != followedPending;
        followedPending = Pending;
        threadSig = sig;
        thread.Children.Clear();

        var asked = items.OfType<AskItem>().Any();
        if (!asked)
        {
            if (team.LeadId == null) thread.Children.Add(WelcomeCard());
            thread.Children.Add(StartCard(view, row, goalReady));
        }
        // Finished work is history: it takes its place in the conversation instead of staying at the bottom.
        var finished = view.Work is { Status: "COMPLETE" } done ? done : null;
        var finishedAt = finished != null && DateTimeOffset.TryParse(finished.CompletedAt, out var at) ? at : DateTimeOffset.MaxValue;
        var placed = false;
        foreach (var item in items)
        {
            if (finished != null && !placed && item.At > finishedAt) { thread.Children.Add(WorkCard(view.Id, finished, team, card, view.SavingMode)); placed = true; }
            thread.Children.Add(item switch { AskItem ask => AskControls(view.Id, ask), EventItem e => EventLine(e), _ => new Panel() });
        }
        if (finished != null && !placed) thread.Children.Add(WorkCard(view.Id, finished, team, card, view.SavingMode));

        if (Pending == null && view.NextGoal != null && view.Action == CrewAction.LetTheCrewDoIt
            && TeamViews.SuggestionIsCurrent(AskAt(view.Id), view.Work?.StartedAt))
            thread.Children.Add(Hint("alert", new StackPanel { Spacing = 6, Children = {
                new TextBlock { FontSize = 14, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, Inlines = new InlineCollection {
                    new Run(Strings.T("Nothing has started yet. When the plan is right, press ")), new Run(Strings.T("Let the crew do it")) { FontWeight = FontWeight.Bold, Foreground = Palette.Text }, new Run(Strings.T(": the crew will work on")) } },
                new TextBlock { Text = view.NextGoal, FontSize = 14, FontWeight = FontWeight.Bold, Foreground = Palette.Text, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = Strings.T("Not right yet? Keep talking with the lead."), FontSize = 14, Foreground = Palette.Muted } } }));
        // The user wrote exactly what to do, and the lead suggested no goal: the message itself can be the goal.
        if (Pending == null && view.NextGoal == null && view.Action == CrewAction.LetTheCrewDoIt
            && !TeamViews.GoalReady(row, null)
            && items.OfType<AskItem>().OrderBy(ask => ask.At).LastOrDefault() is { Question.Length: > 0 } mine && mine.Question.Trim() != Str(row?["goal"]).Trim()
            // A message written before the last work started was already acted on: it is not offered again.
            && !(view.Work is { } last && DateTimeOffset.TryParse(last.StartedAt, out var started) && mine.At <= started))
        {
            var use = Look.Button(Strings.T("Use my message as the goal"), "check", Look.Kind.Small);
            use.HorizontalAlignment = HorizontalAlignment.Left;
            use.Click += async (_, _) => await DoAsync(() => api!.SaveGoalAsync(view.Id, mine.Question.Trim()), Strings.T("Goal saved. Press Let the crew do it to start."));
            thread.Children.Add(Hint("alert", new StackPanel { Spacing = 8, Children = {
                new TextBlock { Text = Strings.T("No goal suggested yet. Keep talking with the lead, or give the crew exactly what you wrote."), FontSize = 14, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap }, use } }));
        }
        if (view.Work != null && finished == null) thread.Children.Add(WorkCard(view.Id, view.Work, team, card, view.SavingMode));
        if (card.Alerts.Count > 0) thread.Children.Add(AlertsCard(view.Id, card.Alerts));
        if (Pending is { } waiting)
        {
            thread.Children.Add(Bubble(true, Strings.T("You"), waiting.Text));
            var lead = team.LeadId is { } leadId ? ModelLabel(leadId) : Strings.T("Lead");
            var answer = new StackPanel { Spacing = 4, Children = { Bubble(false, lead, Str(live?["draft"]) is { Length: > 0 } draft ? draft : Strings.T("Thinking\u2026")) } };
            if (Str(live?["thinking"]) is { Length: > 0 } thinking)
            {
                // Redrawn about every second: it stays open or closed as the user left it.
                var notes = Fold(Strings.T("What the lead is doing"), Mono(thinking.Length > 4000 ? thinking[^4000..] : thinking));
                notes.IsExpanded = thinkingOpen;
                notes.Toggled += opened => thinkingOpen = opened;
                answer.Children.Add(notes);
            }
            thread.Children.Add(answer);
        }
        if (atBottom || justSent) Dispatcher.UIThread.Post(() => threadScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    static SelectableTextBlock Mono(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12, Foreground = Palette.Muted };

    static Control Bubble(bool fromUser, string who, string text, bool failed = false)
    {
        var fg = fromUser ? Palette.AccentText : Palette.Text;
        Control body = fromUser ? Rich(text, fg) : RichBlocks(text, fg);
        // As .msg .bubble: the user's in green on the right, the lead's in a card on the left; the corner
        // next to the speaker is sharper.
        var bubble = new Border { Background = fromUser ? Palette.UserBubble : failed ? Palette.DangerSoft : Palette.Card, Padding = new Thickness(14, 10), Child = body, MaxWidth = fromUser ? 640 : 720,
            CornerRadius = fromUser ? new CornerRadius(14, 14, 4, 14) : new CornerRadius(14, 14, 14, 4),
            HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch, Margin = fromUser ? default : new Thickness(0, 0, 60, 0), BorderBrush = fromUser || failed ? null : Palette.Line, BorderThickness = new Thickness(fromUser || failed ? 0 : 1) };
        return fromUser ? bubble : new StackPanel { Spacing = 4, Children = { new TextBlock { Text = who, Foreground = Palette.Muted, FontSize = 14 }, bubble } };
    }

    // One line or paragraph with **bold** and `code` shown as such.
    static SelectableTextBlock Rich(string text, IBrush fg, double size = 15, FontWeight? weight = null)
    {
        var body = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = fg, FontSize = size, LineHeight = size * 1.5, Inlines = new InlineCollection() };
        foreach (var run in TextRuns.Parse(text))
            body.Inlines.Add(run.Code
                ? new Run(run.Text) { FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = size - 1.5, Background = Palette.Surface2, FontWeight = weight ?? FontWeight.Normal }
                : new Run(run.Text) { FontWeight = run.Bold ? FontWeight.Bold : weight ?? FontWeight.Normal });
        return body;
    }

    // The lead's Markdown: headings, lists, rules and code blocks, not the marks.
    static Control RichBlocks(string text, IBrush fg)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var block in Markdown.Parse(text))
            panel.Children.Add(block.Kind switch
            {
                BlockKind.Heading => Rich(block.Text, fg, block.Level <= 1 ? 19 : block.Level == 2 ? 17 : 16, FontWeight.Bold).With(t => t.Margin = new Thickness(0, 4, 0, 0)),
                BlockKind.Rule => new Border { Height = 1, Background = Palette.Line, Margin = new Thickness(0, 4) },
                BlockKind.Code => new Border { Background = Palette.Background, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8),
                    Child = new SelectableTextBlock { Text = block.Text, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 14, Foreground = fg, TextWrapping = TextWrapping.Wrap } },
                BlockKind.Quote => new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Child = Rich(block.Text, Palette.Muted) },
                BlockKind.Bullet or BlockKind.Numbered => ListItem(block, fg),
                _ => Rich(block.Text, fg),
            });
        // List items sit closer together than paragraphs.
        for (var i = 1; i < panel.Children.Count; i++)
            if (panel.Children[i].Tag is "item" && panel.Children[i - 1].Tag is "item") panel.Children[i].Margin = new Thickness(panel.Children[i].Margin.Left, -5, 0, 0);
        return panel;
    }

    static Control ListItem(TextBlockView block, IBrush fg)
    {
        var marker = new TextBlock { Text = block.Marker, Foreground = Palette.Muted, FontSize = 15, LineHeight = 22.5, MinWidth = block.Kind == BlockKind.Bullet ? 14 : 22 };
        DockPanel.SetDock(marker, Dock.Left);
        return new DockPanel { Tag = "item", Margin = new Thickness(6 + block.Level * 18, 0, 0, 0), Children = { marker, Rich(block.Text, fg) } };
    }

    Control AskControls(string id, AskItem ask)
    {
        var panel = new StackPanel { Spacing = 12, Children = { Bubble(true, Strings.T("You"), ask.Question) } };
        // The user's own message can be copied, or put back in the box to send again (for commands used often).
        if (ask.Question.Trim().Length > 0)
        {
            var copyMine = Look.Button(Strings.T("Copy"), "copy", Look.Kind.Quiet);
            ToolTip.SetTip(copyMine, Strings.T("Copy your message"));
            copyMine.Click += async (_, _) => { if (Clipboard != null) { await Clipboard.SetTextAsync(ask.Question); copyMine.Content = Look.Label(Strings.T("Copied"), "check", 14, 5); copyMine.Foreground = Palette.Accent; } };
            var again = Look.Button(Strings.T("Use again"), "refresh", Look.Kind.Quiet);
            ToolTip.SetTip(again, Strings.T("Put this message in the box, to send it again or change it first"));
            again.Click += (_, _) => { input.Text = ask.Question; input.CaretIndex = input.Text.Length; input.Focus(); };
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, -8, 0, 0), Children = { copyMine, again } });
        }
        var reply = new StackPanel { Spacing = 4 };
        var meta = string.Join(" · ", new[] { ask.Who, ask.At == DateTimeOffset.MinValue ? "" : ask.At.ToLocalTime().ToString("HH:mm", Strings.Culture), ask.Elapsed }.Where(part => part.Length > 0));
        reply.Children.Add(Bubble(false, meta, ask.Answer, ask.Failed));
        var under = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (ask.Answer.Length > 0)
        {
            var copy = Look.Button(Strings.T("Copy"), "copy", Look.Kind.Quiet);
            ToolTip.SetTip(copy, Strings.T("Copy this reply"));
            copy.Click += async (_, _) => { if (Clipboard != null) { await Clipboard.SetTextAsync(ask.Answer); copy.Content = Look.Label(Strings.T("Copied"), "check", 14, 5); copy.Foreground = Palette.Accent; } };
            under.Children.Add(copy);
        }
        var addition = ProjectViews.AdditionText(ask.Answer);
        var row = RowOf(id);
        var running = row != null && (Str(row["megaTask"]?["status"]) is "RUNNING" or "PAUSED" || Flag(row["canStop"]) || (row["dispatch"] as JsonArray)?.Any(part => Flag(part?["alive"])) == true) && Str(row["chatMode"]).ToLowerInvariant() != "talk";
        if (addition != null && running)
        {
            var addBtn = Look.Button(Strings.T("Add to this work"), "plus", Look.Kind.Small);
            addBtn.Click += async (_, _) =>
            {
                if (!addBtn.IsEnabled) return;
                addBtn.IsEnabled = false;
                await DoAsync(() => api!.AddToWorkAsync(id, addition), Strings.Format(Strings.T("Added to this work: {0}"), addition));
            };
            under.Children.Add(addBtn);
        }
        // The lead asked to turn on a tool: one button, and only a click from the user turns it on.
        if (Tools.FirstOrDefault(tool => tool.Name == ask.ToolRequest) is { Name.Length: > 0 } wanted && !ModelToolsView.Read(snapshot, id, ask.ModelId).Tools[wanted.Name])
        {
            var turnOn = Small(Strings.Format("Turn on {0} for {1}", wanted.Label, ask.Who));
            turnOn.Click += async (_, _) =>
            {
                if (wanted.Name == "mcp" && !await Confirm(wanted.Explain + Strings.Format(" Turn it on for {0}?", ask.Who), Strings.T("Yes, turn on MCP servers"))) return;
                await DoAsync(() => api!.SetToolsAsync(id, new Dictionary<string, bool> { [wanted.Name] = true }, ask.ModelId.Length > 0 ? ask.ModelId : null), Strings.Format("{0} on for {1}.", wanted.Label, ask.Who));
            };
            under.Children.Add(turnOn);
        }
        if (under.Children.Count > 0) reply.Children.Add(under);
        if (ask.Thinking.Length > 0)
        {
            var fold = Fold(Strings.T("How the lead got there"), Mono(ask.Thinking));
            fold.IsExpanded = false;
            reply.Children.Add(fold);
        }
        panel.Children.Add(reply);
        return panel;
    }

    // As .event: a round icon in the event's colour, the text, the time on the right.
    static Control EventLine(EventItem e)
    {
        var (icon, color, soft) = e.Kind switch
        {
            "good" => ("check", Palette.Accent, Palette.AccentSoft),
            "bad" => ("alert", Palette.Danger, Palette.DangerSoft),
            "start" => ("play", Palette.Build, Palette.BuildSoft),
            "plan" => ("pencil", Palette.Lead, Palette.LeadSoft),
            _ => ("clock", Palette.Muted, Palette.Surface2),
        };
        var mark = Look.Tile(icon, color, soft, 24, 14, 12);
        mark.VerticalAlignment = VerticalAlignment.Top; mark.Margin = new Thickness(0, 0, 10, 0);
        var time = new TextBlock { Text = e.At == DateTimeOffset.MinValue ? "" : e.At.ToLocalTime().ToString("HH:mm", Strings.Culture), Foreground = Palette.Muted, FontSize = 14, Margin = new Thickness(10, 3, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var fullText = e.Count > 1 ? $"{e.Text} (×{e.Count})" : e.Text;
        var firstLine = fullText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? fullText;
        var isLong = fullText.Length > 85 || fullText.Contains('\n') || fullText.Contains('\r');
        var shortText = firstLine.Length > 80 ? firstLine[..80].TrimEnd() + "…" : firstLine;

        Control content;
        if (!isLong)
        {
            content = new TextBlock { Text = fullText, Foreground = Palette.Muted, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        }
        else
        {
            var textBlock = new TextBlock { Text = shortText, Foreground = Palette.Muted, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 4, 0) };
            var more = Look.Button(Strings.T("more"), null, Look.Kind.Link);
            more.VerticalAlignment = VerticalAlignment.Top;
            var expanded = false;
            more.Click += (_, _) =>
            {
                expanded = !expanded;
                textBlock.Text = expanded ? fullText : shortText;
                more.Content = Look.Label(expanded ? Strings.T("less") : Strings.T("more"), null, 14);
            };
            content = new WrapPanel { Orientation = Orientation.Horizontal, Children = { textBlock, more } };
        }

        var line = new DockPanel { Children = { time, mark, content } };
        DockPanel.SetDock(time, Dock.Right);
        DockPanel.SetDock(mark, Dock.Left);
        return line;
    }

    Control WelcomeCard()
    {
        var team = TeamViews.Team(snapshot!);
        var hasProject = (snapshot?["projects"] as JsonArray)?.Count > 0;
        Control Step(int n, bool done, string text) => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = {
            new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = done ? Palette.AccentSoft : Palette.Surface2,
                Child = done ? Look.Icon("check", 13, Palette.Accent).With(i => i.HorizontalAlignment = HorizontalAlignment.Center)
                    : new TextBlock { Text = n.ToString(), Foreground = Palette.Muted, FontSize = 14, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
            new TextBlock { Text = text, Foreground = done ? Palette.Muted : Palette.Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center } } };
        var leads = TeamViews.Leads(team);
        var leadMenu = new ComboBox { ItemsSource = leads.Select(model => model.Label).ToList(), SelectedIndex = leads.FindIndex(model => model.Id == team.LeadId), PlaceholderText = Strings.T("Choose your lead\u2026"), MinWidth = 220 };
        leadMenu.SelectionChanged += async (_, _) =>
        {
            if (leadMenu.SelectedIndex < 0) return;
            var chosen = leads[leadMenu.SelectedIndex];
            await DoAsync(() => api!.SetLeadAsync(projectId, chosen.Id, chosen.Lead), Strings.Format("{0} is now the lead.", chosen.Label));
        };
        var row = Row(leadMenu);
        if (!hasProject)
        {
            var create = Look.Button(Strings.T("New project"), "plus", Look.Kind.Primary);
            create.Click += async (_, _) => await NewProjectAsync();
            row.Children.Add(create);
        }
        return new Border { BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18),
            Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Palette.ColorOf(Palette.AccentSoft), 0), new GradientStop(Palette.ColorOf(Palette.Card), 0.55), new GradientStop(Palette.ColorOf(Palette.BuildSoft), 1) } },
            Child = new StackPanel { Spacing = 10, Children = {
            Look.Head("rocket", Palette.Accent, Palette.AccentSoft, Strings.T("Welcome aboard, captain"), 19),
            Line(Strings.T("Planwharf runs a team of AI models for you. You give the goal; a lead plans it, a builder writes the code and a verifier checks and fixes it."), Palette.Text, 14),
            Step(1, team.LeadId != null, Strings.T("Choose your lead.")),
            Step(2, hasProject, Strings.T("Create a project: a name and a folder.")),
            Step(3, false, Strings.T("Talk with the lead about what to build. When the plan is right, press Let the crew do it.")),
            row } } };
    }

    Control StartCard(ProjectView view, JsonObject? row, bool goalReady)
    {
        var missing = TeamViews.Missing(snapshot!, view.Id, goalReady);
        var panel = new StackPanel { Spacing = 8, Children = {
            Look.Head("folder", Palette.Accent, Palette.AccentSoft, view.Name, 17),
            Line(TeamViews.HasGoal(row) ? Str(row?["goal"]) : view.ChatMode == "talk" ? Strings.T("Write below. This is a conversation with the lead. Nothing starts.") : Strings.T("Write below what you want built. When the lead suggests a goal, press Let the crew do it."), Palette.Text, 14),
            Explain(Strings.T("Folder: ") + (Str(row?["folder"]) is { Length: > 0 } folder ? folder : Strings.T("not set"))) } };
        if (missing.Count > 0)
        {
            panel.Children.Add(Line(Strings.T("Before the crew can start:"), Palette.Text, 13, FontWeight.SemiBold));
            var fixes = new WrapPanel();
            foreach (var item in missing)
            {
                var (label, open) = item == Strings.T("Choose a lead in Team.") ? (Strings.T("Choose the lead"), "team") : item == Strings.T("Give at least one model the Build role in Team.") ? (Strings.T("Pick a builder"), "team")
                    : item == Strings.T("Set the project folder in Settings.") ? (Strings.T("Set the folder"), "settings") : (Strings.T("Write what to build"), "");
                var fix = Small(label);
                fix.Margin = new Thickness(0, 0, 8, 6);
                fix.Click += (_, _) => { if (open.Length > 0) OpenSide(open); else input.Focus(); };
                fixes.Children.Add(fix);
            }
            panel.Children.Add(fixes);
        }
        else panel.Children.Add(Line(view.ChatMode == "talk" ? Strings.T("A conversation with the lead. Nothing starts.") : Strings.T("Everything is ready. Press Let the crew do it below, or tell the lead more first."), Palette.Muted, 13));
        return Look.Card(panel);
    }
}
