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
    void TeamPanel(TeamView team, string name)
    {
        // Which project this team is for, in a soft green box.
        side.Children.Add(new Border { Background = Palette.AccentSoft, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 6),
            Child = new DockPanel { Children = { Look.Icon("folder", 16).With(i => { i.VerticalAlignment = VerticalAlignment.Top; i.Margin = new Thickness(0, 1, 8, 0); DockPanel.SetDock(i, Dock.Left); }),
                new TextBlock { FontSize = 13, Foreground = Palette.Text, TextWrapping = TextWrapping.Wrap, Inlines = new Avalonia.Controls.Documents.InlineCollection {
                    new Avalonia.Controls.Documents.Run("Team for "), new Avalonia.Controls.Documents.Run(name) { FontWeight = FontWeight.Bold },
                    new Avalonia.Controls.Documents.Run(". Each project has its own team; changes here apply only to this project.") } } } } });

        var lead = Look.Section("lead", Palette.Lead, Palette.LeadSoft, "Lead", "Talks with you, plans the work and picks who does what.");
        var leads = TeamViews.Leads(team);
        var leadMenu = new ComboBox { ItemsSource = leads.Select(m => m.Label).ToList(), SelectedIndex = leads.FindIndex(m => m.Id == team.LeadId), PlaceholderText = "Choose your lead…", MinWidth = 180,
            Background = Palette.Background, BorderBrush = Palette.Line, CornerRadius = new CornerRadius(10), Padding = new Thickness(11, 8), FontSize = 15 };
        leadMenu.SelectionChanged += async (_, _) => { if (leadMenu.SelectedIndex >= 0 && leads[leadMenu.SelectedIndex].Id != team.LeadId) await DoAsync(() => api!.SetLeadAsync(projectId!, leads[leadMenu.SelectedIndex].Id, leads[leadMenu.SelectedIndex].Lead), "Lead chosen."); };
        lead.Children.Add(leadMenu);
        lead.Children.Add(Explain(team.LeadBackups.Count > 0 ? $"If the lead runs low on quota, Planwharf hands over to: {string.Join(", ", team.LeadBackups)}, in this order." : "No other model can take over as lead now."));
        side.Children.Add(lead);

        // Models the AI tools on this computer no longer offer are hidden: they could not work anyway.
        var offered = team.Models.Where(m => !m.NotOffered).ToList();
        var hidden = team.Models.Count - offered.Count;
        var shown = offered.Where(Matches).ToList();
        side.Children.Add(FilterBar(team, offered, shown));
        var active = shown.Where(m => !m.Bench).ToList();
        var bench = shown.Where(m => m.Bench).ToList();
        var onTeam = Look.Section("team", Palette.Accent, Palette.AccentSoft, $"On the team · {active.Count}");
        onTeam.Children.Add(new TextBlock { FontSize = 13, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, Inlines = new Avalonia.Controls.Documents.InlineCollection {
            new Avalonia.Controls.Documents.Run("Build") { Foreground = Palette.Build, FontWeight = FontWeight.Bold }, new Avalonia.Controls.Documents.Run(" writes code and tests. "),
            new Avalonia.Controls.Documents.Run("Verify") { Foreground = Palette.Verify, FontWeight = FontWeight.Bold }, new Avalonia.Controls.Documents.Run(" checks the work and fixes it, ideally a model from another company. "),
            new Avalonia.Controls.Documents.Run("Review") { Foreground = Palette.Review, FontWeight = FontWeight.Bold }, new Avalonia.Controls.Documents.Run(" is optional. Planwharf always watches running work by itself: if something stalls or fails, the lead is told.") } });
        if (active.Count == 0) onTeam.Children.Add(Explain("Nobody yet. Bring models in from the bench."));
        foreach (var model in active) onTeam.Children.Add(Member(model, team.LeadId));
        side.Children.Add(onTeam);

        var benchSection = Look.Section("pause", Palette.Muted, Palette.Surface2, $"Bench · {bench.Count}", "Not working on this project. Press a role to bring a model in.");
        if (bench.Count == 0) benchSection.Children.Add(Explain("The bench is empty."));
        else
        {
            var list = new StackPanel { Spacing = 8 };
            foreach (var model in bench) list.Children.Add(Member(model, team.LeadId));
            var fold = Fold(bench.Count <= 4 ? "Models on the bench" : $"Show the {bench.Count} models on the bench", list);
            fold.IsExpanded = bench.Count <= 4 || teamFilter.Values.Any(value => value.Length > 0);
            benchSection.Children.Add(fold);
        }
        if (hidden > 0) benchSection.Children.Add(Explain($"{hidden} model{(hidden == 1 ? " is" : "s are")} not shown: the AI tools on this computer do not offer {(hidden == 1 ? "it" : "them")} any more."));
        side.Children.Add(benchSection);

        var tools = Look.Section("chip", Palette.Build, Palette.BuildSoft, "AI tools", "Planwharf finds the models of the AI tools signed in on this computer. A tool that is off is never used.");
        foreach (var provider in team.Providers)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Children = { Look.Dot(Look.ProviderColour(provider.Id)), new TextBlock { Text = provider.Label, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text } } };
            // Not installed: never shown as On; say how to install it (the command can be copied).
            if (!provider.Installed)
            {
                label.Children.Add(Look.Badge("Not installed", Palette.Muted, Palette.Surface2).With(b => b.Margin = new Thickness(8, 0, 0, 0)));
                tools.Children.Add(MemberCard(new StackPanel { Spacing = 4, Children = { label,
                    new SelectableTextBlock { Text = provider.InstallHint ?? "Install it and sign in, then look for models again.", Foreground = Palette.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap } } }, false));
                continue;
            }
            var onOff = Look.Chip(provider.Enabled ? "On" : "Off", null, Palette.Accent, Palette.AccentSoft, provider.Enabled);
            onOff.Click += async (_, _) => await DoAsync(() => api!.SetProviderEnabledAsync(provider.Id, !provider.Enabled), $"{provider.Label} turned {(!provider.Enabled ? "on" : "off")}.");
            // The user's OK to edit files and run commands without asking each time; off until given.
            var unasked = Look.Chip("Works without asking", null, Palette.Accent, Palette.AccentSoft, provider.WorksUnasked);
            ToolTip.SetTip(unasked, "Edits files and runs commands in the project folder without asking each time");
            unasked.Click += async (_, _) => await DoAsync(() => api!.SetWorksUnaskedAsync(provider.Id, !provider.WorksUnasked), !provider.WorksUnasked ? $"{provider.Label} works without asking now." : $"{provider.Label} asks again before it builds.");
            tools.Children.Add(MemberCard(new StackPanel { Spacing = 6, Children = { new StackPanel { Children = { label,
                new TextBlock { Text = $"{provider.Models} model{(provider.Models == 1 ? "" : "s")}", Foreground = Palette.Muted, FontSize = 12 } } }, new WrapPanel { Children = { onOff, unasked } } } }, false));
        }
        var look = Look.Button("Look for models again", "refresh", Look.Kind.Small);
        look.HorizontalAlignment = HorizontalAlignment.Left;
        look.Click += async (_, _) => await DoAsync(() => api!.RefreshModelsAsync(), "Model list updated.");
        tools.Children.Add(look);
        side.Children.Add(tools);

        var connect = Look.Section("plug", Palette.Lead, Palette.LeadSoft, "Connect a model by API", "For a model you reach through an API, such as DeepSeek or Kimi. The key stays in an environment variable on this computer; Planwharf stores only the variable's name.");
        var fold2 = Fold("Add an API connection", ConnectionForm());
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
        var section = Look.Section("research", Palette.Build, Palette.BuildSoft, "Find models");
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
        section.Children.Add(Choices("company", [("", "All companies"), .. companies]));
        section.Children.Add(Choices("weight", ("", "Any weight"), ("light", "Light"), ("default", "Default"), ("premium", "Premium")));
        section.Children.Add(Choices("state", ("", "Anywhere"), ("team", "On the team"), ("bench", "On the bench"), ("low", "Under its limit")));
        section.Children.Add(Explain($"For the {shown.Count} model{(shown.Count == 1 ? "" : "s")} shown:"));
        var bulk = new WrapPanel();
        foreach (var (role, label) in new[] { ("worker", "Build"), ("verifier", "Verify"), ("reviewer", "Review") })
        {
            bulk.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Palette.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 4) });
            foreach (var (on, text) in new[] { (true, "All"), (false, "None") })
            {
                var chip = Look.Chip(text, null, Palette.Accent, Palette.AccentSoft, false);
                chip.Click += async (_, _) =>
                {
                    chip.IsChecked = false;
                    var ids = shown.Select(m => m.Id).ToList();
                    if (ids.Count > 0) await DoAsync(() => api!.SetRolesForManyAsync(projectId, ids, role, on), $"{label} {(on ? "on" : "off")} for {ids.Count} model{(ids.Count == 1 ? "" : "s")}.");
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
        var protocol = new ComboBox { ItemsSource = new[] { "OpenAI-compatible", "Anthropic Messages" }, SelectedIndex = 0, MinWidth = 220 };
        var env = Look.Field(new TextBox { Watermark = "DEEPSEEK_API_KEY" });
        var ids = Look.Field(new TextBox { Watermark = "deepseek-chat" });
        var tools = new CheckBox { Content = "These models can use tools (needed to build code)" };
        var error = new TextBlock { Foreground = Palette.Danger, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var connect = Small("Connect", primary: true);
        connect.Click += async (_, _) =>
        {
            error.Text = "";
            var name = label.Text?.Trim() ?? "";
            var models = (ids.Text ?? "").Split(',').Select(id => id.Trim()).Where(id => id.Length > 0).ToList();
            if (name.Length == 0 || string.IsNullOrWhiteSpace(url.Text) || models.Count == 0) { error.Text = "Enter a name, the API address and at least one model ID."; return; }
            try
            {
                await api!.AddConnectionAsync(name, url.Text.Trim(), protocol.SelectedIndex == 1 ? "anthropic" : "openai", env.Text?.Trim(), models, tools.IsChecked == true);
                hint.Text = $"{name} connected. Give its models a role to use them."; hint.Foreground = Palette.Accent;
                sideSig = null; await RefreshAsync(readOnly: false);
            }
            catch (Exception failure) { error.Text = failure.Message.Replace("API_CONFIGURATION_INVALID:", "").Trim(); }
        };
        return new StackPanel { Spacing = 6, Children = {
            Explain("Name"), label, Explain("API address"), url, Explain("Type"), protocol, Explain("Environment variable with the key"), env,
            Explain("Exact model IDs, separated by commas"), ids, tools, error, connect } };
    }

    Control Member(TeamModel model, string? leadId)
    {
        var status = !model.Available ? "not available now" + (model.UnavailableReason != null ? ": " + model.UnavailableReason[..Math.Min(140, model.UnavailableReason.Length)] : "") + (model.RetrySoon ? " (tried again in a few minutes)" : "")
            : model.FiveHourLeft != null || model.WeeklyLeft != null
                ? string.Join(" · ", new[] { model.FiveHourLeft is { } f ? $"5h {f}%" : "", model.WeeklyLeft is { } w ? $"week {w}%" : "" }.Where(part => part.Length > 0)) + " left" + (model.LowQuota ? " · under its limit" : "")
            : model.QuotaLeft is { } left ? $"{left}% quota left" + (model.LowQuota ? " · under its limit" : "") : "quota not reported";
        var chips = new WrapPanel();
        if (model.Bench) chips.Children.Add(new TextBlock { Text = "Bring in as:", Foreground = Palette.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) });
        chips.Children.Add(RoleChip(model, "Build", "worker", model.Build, model.CanBuild, "Writes the code and the tests"));
        chips.Children.Add(RoleChip(model, "Verify", "verifier", model.Verify, model.CanVerify, "Checks the work and fixes what fails"));
        if (model.CanReview) chips.Children.Add(RoleChip(model, "Review", "reviewer", model.Review, true, "Optional: reads the work and reports problems, without changing files"));
        if (!model.Bench)
        {
            var bench = Look.Chip("Bench", null, Palette.Muted, Palette.Surface2, false);
            ToolTip.SetTip(bench, "Take this model out of the team");
            bench.Click += async (_, _) => { bench.IsChecked = false; await DoAsync(() => api!.BenchAsync(projectId!, model.Id), $"{model.Label} is on the bench."); };
            chips.Children.Add(bench);
        }
        var name = new StackPanel { Orientation = Orientation.Horizontal, Children = {
            Look.Dot(Look.ProviderColour(model.ProviderId.Length > 0 ? model.ProviderId : model.Provider)),
            new TextBlock { Text = model.Label, Foreground = Palette.Text, FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center } } };
        if (model.Lead || model.Id == leadId) name.Children.Add(Look.Badge("Lead", Palette.Lead, Palette.LeadSoft).With(b => b.Margin = new Thickness(8, 0, 0, 0)));
        var info = new StackPanel { Spacing = 2, Children = { name,
            new TextBlock { Text = string.Join(" · ", new[] { model.Provider, status }.Where(part => part.Length > 0)), Foreground = model.Available ? Palette.Muted : Palette.Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap } } };
        if (model.QuotaLeft is { } quota && !model.Bench) info.Children.Add(Look.Bar(quota, model.LowQuota));
        var card = new StackPanel { Spacing = 8, Children = { info, chips } };
        if (!model.Bench) card.Children.Add(LimitEditor(model));
        return MemberCard(card, !model.Bench, model.Bench);
    }

    // The model's quota limit: the general one, its own two thresholds, or no limit at all.
    Control LimitEditor(TeamModel model)
    {
        var said = model.LimitNone ? "No limit"
            : $"{(model.LimitCustom ? "Own limit" : "Limit")}: " + (model.LimitFiveHour == model.LimitWeekly ? $"{model.LimitFiveHour}%" : $"5h {model.LimitFiveHour}% · week {model.LimitWeekly}%");
        var five = new NumericUpDown { Minimum = 0, Maximum = 90, Increment = 5, Value = model.LimitFiveHour ?? 35, FormatString = "0", Width = 110 };
        var week = new NumericUpDown { Minimum = 0, Maximum = 90, Increment = 5, Value = model.LimitWeekly ?? 35, FormatString = "0", Width = 110 };
        var save = Small("Save");
        save.Click += async (_, _) => await DoAsync(() => api!.SetQuotaLimitAsync(model.Id, (int)(five.Value ?? 35), (int)(week.Value ?? 35)), $"Quota limit of {model.Label} saved.");
        var none = Small("No limit");
        none.Click += async (_, _) => await DoAsync(() => api!.SetNoQuotaLimitAsync(model.Id), $"{model.Label} has no quota limit now.");
        var buttons = new WrapPanel { Children = { save.With(b => b.Margin = new Thickness(0, 0, 6, 4)), none.With(b => b.Margin = new Thickness(0, 0, 6, 4)) } };
        if (model.LimitCustom)
        {
            var reset = Small("Use the general limit");
            reset.Click += async (_, _) => await DoAsync(() => api!.ResetQuotaLimitAsync(model.Id), $"{model.Label} follows the general limit.");
            buttons.Children.Add(reset);
        }
        var form = new StackPanel { Spacing = 6, Children = {
            Row(new TextBlock { Text = "5 hours", Width = 52, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Text, FontSize = 13 }, five),
            Row(new TextBlock { Text = "Week", Width = 52, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Text, FontSize = 13 }, week,
                new TextBlock { Text = "% left", VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Muted, FontSize = 13 }),
            buttons } };
        var fold = Fold(said, form);
        fold.IsExpanded = false;
        return fold;
    }

    Control RoleChip(TeamModel model, string label, string role, bool on, bool eligible, string help)
    {
        var (color, soft, icon) = Palette.Role(label);
        var chip = Look.Chip(label, icon, color, soft, on);
        chip.IsEnabled = model.Available && eligible;
        ToolTip.SetTip(chip, help);
        chip.Click += async (_, _) => await DoAsync(() => api!.SetRolesAsync(projectId!, model.Id, new Dictionary<string, bool> { [role] = !on }), "Team updated.");
        return chip;
    }
}
