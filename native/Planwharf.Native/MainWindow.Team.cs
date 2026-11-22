using Planwharf.Engine;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Planwharf.Native.Core;

namespace Planwharf.Native;

// The project's team, as in the chat page: lead, models on the team and on the bench, AI tools, API connections.
public sealed partial class MainWindow
{
    // Which folds of the Team panel the owner opened or closed: kept while the panel is drawn again.
    readonly Dictionary<string, bool> folds = new();

    string accountSignInCommand = "";
    void TeamPanel(TeamView team, string name, string savingMode)
    {
        var modes = new SavingModeBar();
        modes.Show(savingMode);
        modes.Bind(async mode =>
        {
            if (api == null || projectId == null) return;
            var id = projectId;
            await DoAsync(() => api.SetSavingModeAsync(id, mode), Strings.T("Saved."));
        });
        side.Children.Add(modes);
        // Which project this team is for, in a soft green box.
        side.Children.Add(new Border { Background = Palette.AccentSoft, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 6),
            Child = new DockPanel { Children = { Look.Icon("folder", 16).With(i => { i.VerticalAlignment = VerticalAlignment.Top; i.Margin = new Thickness(0, 1, 8, 0); DockPanel.SetDock(i, Dock.Left); }),
                new TextBlock { FontSize = 13, Foreground = Palette.Text, TextWrapping = TextWrapping.Wrap, Inlines = new Avalonia.Controls.Documents.InlineCollection {
                    new Avalonia.Controls.Documents.Run(Strings.T("Team for ")), new Avalonia.Controls.Documents.Run(name) { FontWeight = FontWeight.Bold },
                    new Avalonia.Controls.Documents.Run(Strings.T(". Each project has its own team; changes here apply only to this project.")) } } } } });

        var lead = Look.Section("lead", Palette.Lead, Palette.LeadSoft, Strings.T("Lead"), Strings.T("Talks with you, plans the work and picks who does what."));
        var leads = TeamViews.Leads(team);
        var leadMenu = new ComboBox { ItemsSource = leads.Select(m => m.Label).ToList(), SelectedIndex = leads.FindIndex(m => m.Id == team.LeadId), PlaceholderText = Strings.T("Choose your lead\u2026"), MinWidth = 180,
            Background = Palette.Background, BorderBrush = Palette.Line, CornerRadius = new CornerRadius(10), Padding = new Thickness(11, 8), FontSize = 15 };
        leadMenu.SelectionChanged += async (_, _) => { if (leadMenu.SelectedIndex >= 0 && leads[leadMenu.SelectedIndex].Id != team.LeadId) await DoAsync(() => api!.SetLeadAsync(projectId!, leads[leadMenu.SelectedIndex].Id, leads[leadMenu.SelectedIndex].Lead), Strings.T("Lead chosen.")); };
        lead.Children.Add(leadMenu);
        lead.Children.Add(Explain(team.LeadBackups.Count > 0 ? Strings.Format("If the lead runs low on quota, Planwharf hands over to: {0}, in this order.", string.Join(", ", team.LeadBackups)) : Strings.T("No other model can take over as lead now.")));
        side.Children.Add(lead);

        // Models the AI tools on this computer no longer offer are hidden: they could not work anyway.
        var offered = team.Models.Where(m => !m.NotOffered && m.PlanStatus is not ("expired" or "not signed in")).ToList();
        var hidden = team.Models.Count(m => m.NotOffered);
        var shown = offered.Where(Matches).ToList();
        side.Children.Add(FilterBar(team, offered, shown));
        var active = shown.Where(m => !m.Bench).ToList();
        var bench = shown.Where(m => m.Bench).ToList();
        var onTeam = Look.Section("team", Palette.Accent, Palette.AccentSoft, Strings.Format("On the team \u00B7 {0}", active.Count));
        onTeam.Children.Add(new TextBlock { FontSize = 13, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, Inlines = new Avalonia.Controls.Documents.InlineCollection {
            new Avalonia.Controls.Documents.Run(Strings.T("Build")) { Foreground = Palette.Build, FontWeight = FontWeight.Bold }, new Avalonia.Controls.Documents.Run(Strings.T(" writes code and tests. ")),
            new Avalonia.Controls.Documents.Run(Strings.T("Verify")) { Foreground = Palette.Verify, FontWeight = FontWeight.Bold }, new Avalonia.Controls.Documents.Run(Strings.T(" checks the work and fixes it, ideally a model from another company. Planwharf always watches running work by itself: if something stalls or fails, the lead is told.")) } });
        if (active.Count == 0) onTeam.Children.Add(Explain(Strings.T("Nobody yet. Bring models in from the bench.")));
        foreach (var model in active) onTeam.Children.Add(Member(model, team.LeadId));
        side.Children.Add(onTeam);

        var benchSection = Look.Section("pause", Palette.Muted, Palette.Surface2, Strings.Format("Bench \u00B7 {0}", bench.Count), Strings.T("Not working on this project. Press a role to bring a model in."));
        if (bench.Count == 0) benchSection.Children.Add(Explain(Strings.T("The bench is empty.")));
        else
        {
            var list = new StackPanel { Spacing = 8 };
            foreach (var model in bench) list.Children.Add(Member(model, team.LeadId));
            var fold = Fold(bench.Count <= 4 ? Strings.T("Models on the bench") : Strings.Format("Show the {0} models on the bench", bench.Count), list);
            // The panel is drawn again after every change (a role picked, a new reading of the quota): what the
            // owner opened or closed stays so, per project, until the app restarts.
            var benchKey = "bench:" + projectId;
            fold.IsExpanded = folds.TryGetValue(benchKey, out var benchOpen) ? benchOpen : bench.Count <= 4 || teamFilter.Values.Any(value => value.Length > 0);
            fold.Toggled += opened => folds[benchKey] = opened;
            benchSection.Children.Add(fold);
        }
        if (hidden > 0) benchSection.Children.Add(Explain(Strings.Format(hidden == 1 ? "{0} model is not shown: the AI tools on this computer no longer offer {1}, or Planwharf cannot run {2} (such as Codex's own review model)." : "{0} models are not shown: the AI tools on this computer no longer offer {1}, or Planwharf cannot run {2} (such as Codex's own review model).", hidden, Strings.T(hidden == 1 ? "it" : "them"), Strings.T(hidden == 1 ? "it" : "them"))));
        foreach (var group in team.Models.Where(m => !m.NotOffered && m.PlanStatus is "expired" or "not signed in").GroupBy(m => (m.Adapter, m.PlanStatus)))
        {
            var company = group.Key.Adapter switch { "claude" => "Claude", "codex" => "ChatGPT", "grok" => "Grok", "antigravity" => "Gemini", _ => group.Key.Adapter };
            benchSection.Children.Add(Explain(group.Key.PlanStatus == "expired"
                ? Strings.Format(group.Count() == 1 ? "{0} model hidden: {1} plan expired. Renew it or sign in again, then look for models again." : "{0} models hidden: {1} plan expired. Renew it or sign in again, then look for models again.", group.Count(), company)
                : Strings.Format(group.Count() == 1 ? "{0} model hidden: {1} is not signed in. Sign in, then look for models again." : "{0} models hidden: {1} is not signed in. Sign in, then look for models again.", group.Count(), company)));
        }
        side.Children.Add(benchSection);

        var tools = Look.Section("chip", Palette.Build, Palette.BuildSoft, Strings.T("AI tools"), Strings.T("Planwharf finds the models of the AI tools signed in on this computer. A tool that is off is never used."));
        foreach (var provider in team.Providers)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Children = { Look.Dot(Look.ProviderColour(provider.Id)), new TextBlock { Text = provider.Label, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text } } };
            // Not installed: never shown as On; say how to install it (the command can be copied).
            if (!provider.Installed)
            {
                label.Children.Add(Look.Badge(Strings.T("Not installed"), Palette.Muted, Palette.Surface2).With(b => b.Margin = new Thickness(8, 0, 0, 0)));
                tools.Children.Add(MemberCard(new StackPanel { Spacing = 4, Children = { label,
                    new SelectableTextBlock { Text = provider.InstallHint ?? Strings.T("Install it and sign in, then look for models again."), Foreground = Palette.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap } } }, false));
                continue;
            }
            var onOff = Look.Chip(provider.Enabled ? Strings.T("On") : Strings.T("Off"), null, Palette.Accent, Palette.AccentSoft, provider.Enabled);
            onOff.Click += async (_, _) => await DoAsync(() => api!.SetProviderEnabledAsync(provider.Id, !provider.Enabled), Strings.Format("{0} turned {1}.", provider.Label, Strings.T(!provider.Enabled ? "on" : "off")));
            // The user's OK to edit files and run commands without asking each time; off until given.
            var unasked = Look.Chip(Strings.T("Works without asking"), null, Palette.Accent, Palette.AccentSoft, provider.WorksUnasked);
            ToolTip.SetTip(unasked, Strings.T("Edits files and runs commands in the project folder without asking each time"));
            unasked.Click += async (_, _) => await DoAsync(() => api!.SetWorksUnaskedAsync(provider.Id, !provider.WorksUnasked), !provider.WorksUnasked ? Strings.Format("{0} works without asking now.", provider.Label) : Strings.Format("{0} asks again before it builds.", provider.Label));
            var actions = new WrapPanel { Children = { onOff, unasked } };
            if (provider.Id == provider.Adapter && provider.Adapter is "claude" or "codex")
            {
                var add = Small(Strings.T("Add another account"));
                add.Click += async (_, _) => await DoAsync(async () =>
                {
                    var result = await api!.AddAccountAsync(provider.Id);
                    accountSignInCommand = Str(result?["signInCommand"]);
                }, Strings.T("Account folder ready. Run the sign-in command below, then look for models again."));
                actions.Children.Add(add);
            }
            tools.Children.Add(MemberCard(new StackPanel { Spacing = 6, Children = { new StackPanel { Children = { label,
                new TextBlock { Text = Strings.Format(provider.Models == 1 ? "{0} model" : "{0} models", provider.Models), Foreground = Palette.Muted, FontSize = 12 } } }, actions } }, false));
        }
        if (accountSignInCommand.Length > 0) tools.Children.Add(new SelectableTextBlock { Text = accountSignInCommand, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text });
        var look = Look.Button(Strings.T("Look for models again"), "refresh", Look.Kind.Small);
        look.HorizontalAlignment = HorizontalAlignment.Left;
        look.Click += async (_, _) => await DoAsync(() => api!.RefreshModelsAsync(), Strings.T("Model list updated."));
        tools.Children.Add(look);
        side.Children.Add(tools);

        var connect = Look.Section("plug", Palette.Lead, Palette.LeadSoft, Strings.T("Connect a model by API"), Strings.T("For a model you reach through an API, such as DeepSeek or Kimi. The key stays in an environment variable on this computer; Planwharf stores only the variable\u0027s name."));
        var fold2 = Fold(Strings.T("Add an API connection"), ConnectionForm());
        fold2.IsExpanded = false;
        connect.Children.Add(fold2);
        side.Children.Add(connect);
    }

    // Team filters: by company, by weight and by state. "All" and "None" act on the models shown only.
    readonly Dictionary<string, string> teamFilter = new() { ["company"] = "", ["weight"] = "", ["state"] = "" };

    bool Matches(TeamModel model) =>
        (teamFilter["company"].Length == 0 || model.ProviderId == teamFilter["company"])
        && (teamFilter["weight"].Length == 0 || model.Weight == teamFilter["weight"])
        && teamFilter["state"] switch { "team" => !model.Bench, "bench" => model.Bench, "low" => model.LowQuota, _ => true };

    Control FilterBar(TeamView team, IReadOnlyList<TeamModel> offered, IReadOnlyList<TeamModel> shown)
    {
        var section = Look.Section("research", Palette.Build, Palette.BuildSoft, Strings.T("Find models"));
        WrapPanel Choices(string key, params (string Value, string Text)[] options)
        {
            var row = new WrapPanel();
            foreach (var (value, text) in options)
            {
                var chip = Look.Chip(text, null, Palette.Accent, Palette.AccentSoft, teamFilter[key] == value);
                chip.Click += (_, _) => { teamFilter[key] = value; sideSig = null; Render(); };
                row.Children.Add(chip);
            }
            return row;
        }
        var companies = offered.Select(m => m.ProviderId).Where(id => id.Length > 0).Distinct()
            .Select(id => (id, team.Providers.FirstOrDefault(p => p.Id == id)?.Label ?? id)).ToArray();
        section.Children.Add(Choices("company", [("", Strings.T("All companies")), .. companies]));
        section.Children.Add(Choices("weight", ("", Strings.T("Any weight")), ("light", Strings.T("Light")), ("default", Strings.T("Default")), ("premium", Strings.T("Premium"))));
        section.Children.Add(Choices("state", ("", Strings.T("Anywhere")), ("team", Strings.T("On the team")), ("bench", Strings.T("On the bench")), ("low", Strings.T("Under its limit"))));
        section.Children.Add(Explain(Strings.Format(shown.Count == 1 ? "For the {0} model shown:" : "For the {0} models shown:", shown.Count)));
        var bulk = new WrapPanel();
        foreach (var (role, label) in new[] { ("worker", Strings.T("Build")), ("verifier", Strings.T("Verify")) })
        {
            bulk.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Palette.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 4) });
            foreach (var (on, text) in new[] { (true, Strings.T("All")), (false, Strings.T("None")) })
            {
                var chip = Look.Chip(text, null, Palette.Accent, Palette.AccentSoft, false);
                chip.Click += async (_, _) =>
                {
                    chip.IsChecked = false;
                    var ids = shown.Select(m => m.Id).ToList();
                    if (ids.Count > 0) await DoAsync(() => api!.SetRolesForManyAsync(projectId, ids, role, on), Strings.Format(ids.Count == 1 ? "{0} {1} for {2} model." : "{0} {1} for {2} models.", label, Strings.T(on ? "on" : "off"), ids.Count));
                };
                bulk.Children.Add(chip);
            }
            bulk.Children.Add(new Border { Width = 8 });
        }
        section.Children.Add(bulk);
        return section;
    }

    // A model or tool card, as .member; one on the team has a green edge on the left.
    static Border MemberCard(Control content, bool onTeam, bool benched = false)
    {
        var inner = new Border { Padding = new Thickness(10), Child = content };
        Control body = inner;
        if (onTeam)
        {
            var edge = new Border { Width = 4, Background = Palette.Accent };
            DockPanel.SetDock(edge, Dock.Left);
            body = new DockPanel { Children = { edge, inner } };
        }
        return new Border { Background = benched ? Brushes.Transparent : Palette.Card, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            ClipToBounds = true, Child = body, Opacity = benched ? .72 : 1 };
    }

    Control ConnectionForm()
    {
        var label = Look.Field(new TextBox { Watermark = "DeepSeek", MaxLength = 100 });
        var url = Look.Field(new TextBox { Watermark = "https://api.deepseek.com/v1" });
        var protocol = new ComboBox { ItemsSource = new[] { Strings.T("OpenAI-compatible"), Strings.T("Anthropic Messages") }, SelectedIndex = 0, MinWidth = 220 };
        var env = Look.Field(new TextBox { Watermark = "DEEPSEEK_API_KEY" });
        var ids = Look.Field(new TextBox { Watermark = "deepseek-chat" });
        var tools = new CheckBox { Content = Strings.T("These models can use tools (needed to build code)") };
        var error = new TextBlock { Foreground = Palette.Danger, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var connect = Small(Strings.T("Connect"), primary: true);
        connect.Click += async (_, _) =>
        {
            error.Text = "";
            var name = label.Text?.Trim() ?? "";
            var models = (ids.Text ?? "").Split(',').Select(id => id.Trim()).Where(id => id.Length > 0).ToList();
            if (name.Length == 0 || string.IsNullOrWhiteSpace(url.Text) || models.Count == 0) { error.Text = Strings.T("Enter a name, the API address and at least one model ID."); return; }
            try
            {
                await api!.AddConnectionAsync(name, url.Text.Trim(), protocol.SelectedIndex == 1 ? "anthropic" : "openai", env.Text?.Trim(), models, tools.IsChecked == true);
                hint.Text = Strings.Format("{0} connected. Give its models a role to use them.", name); hint.Foreground = Palette.Accent;
                sideSig = null; await RefreshAsync(readOnly: false);
            }
            catch (Exception failure) { error.Text = failure.Message.Replace("API_CONFIGURATION_INVALID:", "").Trim(); }
        };
        return new StackPanel { Spacing = 6, Children = {
            Explain(Strings.T("Name")), label, Explain(Strings.T("API address")), url, Explain(Strings.T("Type")), protocol, Explain(Strings.T("Environment variable with the key")), env,
            Explain(Strings.T("Exact model IDs, separated by commas")), ids, tools, error, connect } };
    }

    Control Member(TeamModel model, string? leadId)
    {
        var status = !model.Available ? Strings.T("not available now") + (model.UnavailableReason != null ? ": " + model.UnavailableReason[..Math.Min(140, model.UnavailableReason.Length)] : "") + (model.RetrySoon ? Strings.T(" (tried again in a few minutes)") : "")
            : model.FiveHourLeft != null || model.WeeklyLeft != null
                ? string.Join(" · ", new[] { model.FiveHourLeft is { } f ? Strings.Format("5h {0}%", f) : "", model.WeeklyLeft is { } w ? Strings.Format("week {0}%", w) : "" }.Where(part => part.Length > 0)) + Strings.T(" left") + (model.LowQuota ? Strings.T(" \u00B7 under its limit") : "")
            : model.QuotaLeft is { } left ? Strings.Format("{0}% quota left", left) + (model.LowQuota ? Strings.T(" \u00B7 under its limit") : "") : Strings.T("quota not reported");
        var chips = new WrapPanel();
        if (model.Bench) chips.Children.Add(new TextBlock { Text = Strings.T("Bring in as:"), Foreground = Palette.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) });
        if (model.CanLead)
        {
            // Whether this model may lead the project: the lead menu and every hand-over choose among these.
            var (leadColor, leadSoft, leadIcon) = Palette.Role("Lead");
            var mayLead = Look.Chip(Strings.T("Lead"), leadIcon, leadColor, leadSoft, model.LeadAllowed || model.Lead);
            mayLead.IsEnabled = model.Available && !model.Lead;
            ToolTip.SetTip(mayLead, model.Lead ? Strings.T("This is the lead. Choose another lead first to switch it off.") : Strings.T("Can lead this project: in the lead menu, and taking over when the lead runs low or does not answer"));
            mayLead.Click += async (_, _) => await DoAsync(() => api!.SetLeadAllowedAsync(projectId!, model.Id, !model.LeadAllowed), model.LeadAllowed ? Strings.Format("{0} will not lead this project.", model.Label) : Strings.Format("{0} can lead this project.", model.Label));
            chips.Children.Add(mayLead);
        }
        chips.Children.Add(RoleChip(model, Strings.T("Build"), "worker", model.Build, model.CanBuild, Strings.T("Writes the code and the tests")));
        chips.Children.Add(RoleChip(model, Strings.T("Verify"), "verifier", model.Verify, model.CanVerify, Strings.T("Checks the work and fixes what fails")));
        if (!model.Bench)
        {
            var bench = Look.Chip(Strings.T("Bench"), null, Palette.Muted, Palette.Surface2, false);
            ToolTip.SetTip(bench, Strings.T("Take this model out of the team"));
            bench.Click += async (_, _) => { bench.IsChecked = false; await DoAsync(() => api!.BenchAsync(projectId!, model.Id), Strings.Format("{0} is on the bench.", model.Label)); };
            chips.Children.Add(bench);
        }
        var name = new StackPanel { Orientation = Orientation.Horizontal, Children = {
            Look.Dot(Look.ProviderColour(model.ProviderId.Length > 0 ? model.ProviderId : model.Provider)),
            new TextBlock { Text = model.Label, Foreground = Palette.Text, FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center } } };
        if (model.Lead || model.Id == leadId) name.Children.Add(Look.Badge(Strings.T("Lead"), Palette.Lead, Palette.LeadSoft).With(b => b.Margin = new Thickness(8, 0, 0, 0)));
        var info = new StackPanel { Spacing = 2, Children = { name,
            new TextBlock { Text = string.Join(" · ", new[] { model.Provider, status }.Where(part => part.Length > 0)), Foreground = model.Available ? Palette.Muted : Palette.Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap } } };
        if (model.QuotaLeft is { } quota && !model.Bench) info.Children.Add(Look.Bar(quota, model.LowQuota));
        var card = new StackPanel { Spacing = 8, Children = { info, chips } };
        if (!model.Bench) card.Children.Add(LimitEditor(model));
        var toolsProject = projectId!;
        var tools = ModelToolsEditor.Create(ModelToolsView.Read(snapshot, toolsProject, model.Id),
            chosen => DoAsync(() => api!.SetToolsAsync(toolsProject, chosen, model.Id), Strings.Format("Tools for {0} saved.", model.Label)));
        var toolsKey = "tools:" + toolsProject + ":" + model.Id;
        if (folds.TryGetValue(toolsKey, out var toolsOpen)) tools.IsExpanded = toolsOpen;
        tools.PropertyChanged += (_, change) => { if (change.Property == Expander.IsExpandedProperty) folds[toolsKey] = tools.IsExpanded; };
        card.Children.Add(tools);
        return MemberCard(card, !model.Bench, model.Bench);
    }

    // The quota limit: the company's (every model of the same quota, such as all the Claude models), or this
    // model's own, or the general one; or no limit at all.
    Control LimitEditor(TeamModel model)
    {
        var company = model.Provider.Length > 0 ? model.Provider : model.Pool;
        var whose = model.LimitSource switch { "model" => Strings.T("Own limit"), "company" => Strings.Format("{0} limit", company), _ => Strings.T("Limit") };
        var said = model.LimitNone ? Strings.Format("No limit{0}", (model.LimitSource == "company" ? Strings.Format(" for {0}", company) : ""))
            : $"{whose}: " + (model.LimitFiveHour == model.LimitWeekly ? $"{model.LimitFiveHour}%" : Strings.Format("5h {0}% \u00B7 week {1}%", model.LimitFiveHour, model.LimitWeekly));
        var five = new NumericUpDown { Minimum = 0, Maximum = 90, Increment = 5, Value = model.LimitFiveHour ?? 35, FormatString = "0", Width = 110 };
        var week = new NumericUpDown { Minimum = 0, Maximum = 90, Increment = 5, Value = model.LimitWeekly ?? 35, FormatString = "0", Width = 110 };
        int Five() => (int)(five.Value ?? 35); int Week() => (int)(week.Value ?? 35);
        var buttons = new WrapPanel();
        void Add(Button button) { button.Margin = new Thickness(0, 0, 6, 4); buttons.Children.Add(button); }
        if (model.Pool.Length > 0)
        {
            var forAll = Small(Strings.Format("Save for all {0}", company), primary: true);
            ToolTip.SetTip(forAll, Strings.Format("Every {0} model without a limit of its own", company));
            forAll.Click += async (_, _) => await DoAsync(() => api!.SetCompanyLimitAsync(model.Pool, Five(), Week()), Strings.Format("Quota limit of {0} saved.", company));
            Add(forAll);
        }
        var save = Small(Strings.T("Only this model"));
        save.Click += async (_, _) => await DoAsync(() => api!.SetQuotaLimitAsync(model.Id, Five(), Week()), Strings.Format("Quota limit of {0} saved.", model.Label));
        Add(save);
        var none = Small(Strings.T("No limit"));
        none.Click += async (_, _) => await DoAsync(() => api!.SetNoQuotaLimitAsync(model.Id), Strings.Format("{0} has no quota limit now.", model.Label));
        Add(none);
        if (model.LimitSource == "model")
        {
            var reset = Small(model.Pool.Length > 0 ? Strings.Format("Use the {0} limit", company) : Strings.T("Use the general limit"));
            reset.Click += async (_, _) => await DoAsync(() => api!.ResetQuotaLimitAsync(model.Id), Strings.Format("{0} follows the {1} limit.", model.Label, (model.Pool.Length > 0 ? company : Strings.T("general"))));
            Add(reset);
        }
        else if (model.LimitSource == "company")
        {
            var reset = Small(Strings.Format("{0}: use the general limit", company));
            reset.Click += async (_, _) => await DoAsync(() => api!.ResetCompanyLimitAsync(model.Pool), Strings.Format("{0} follows the general limit.", company));
            Add(reset);
        }
        var form = new StackPanel { Spacing = 6, Children = {
            Row(new TextBlock { Text = Strings.T("5 hours"), Width = 52, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Text, FontSize = 13 }, five),
            Row(new TextBlock { Text = Strings.T("Week"), Width = 52, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Text, FontSize = 13 }, week,
                new TextBlock { Text = Strings.T("% left"), VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Muted, FontSize = 13 }),
            buttons } };
        var fold = Fold(said, form);
        var limitKey = "limit:" + projectId + ":" + model.Id;
        fold.IsExpanded = folds.TryGetValue(limitKey, out var limitOpen) && limitOpen;
        fold.Toggled += opened => folds[limitKey] = opened;
        return fold;
    }

    Control RoleChip(TeamModel model, string label, string role, bool on, bool eligible, string help)
    {
        var (color, soft, icon) = Palette.Role(label);
        var chip = Look.Chip(label, icon, color, soft, on);
        chip.IsEnabled = model.Available && eligible;
        ToolTip.SetTip(chip, help);
        chip.Click += async (_, _) => await DoAsync(() => api!.SetRolesAsync(projectId!, model.Id, new Dictionary<string, bool> { [role] = !on }), Strings.T("Team updated."));
        return chip;
    }
}
