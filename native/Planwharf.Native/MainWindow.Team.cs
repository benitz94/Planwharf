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
        side.Children.Add(Heading("Team"));
        side.Children.Add(Explain($"Team for {name}. Each project has its own team; changes here apply only to this project."));
        side.Children.Add(Heading("Lead"));
        side.Children.Add(Explain("Talks with you, plans the work and picks who does what."));
        var leads = team.Models.Where(m => m.CanLead && m.Available).ToList();
        var leadMenu = new ComboBox { ItemsSource = leads.Select(m => m.Label).ToList(), SelectedIndex = leads.FindIndex(m => m.Id == team.LeadId), PlaceholderText = "Choose your lead…", MinWidth = 240 };
        leadMenu.SelectionChanged += async (_, _) => { if (leadMenu.SelectedIndex >= 0 && leads[leadMenu.SelectedIndex].Id != team.LeadId) await DoAsync(() => api!.SetLeadAsync(projectId!, leads[leadMenu.SelectedIndex].Id, leads[leadMenu.SelectedIndex].Lead), "Lead chosen."); };
        side.Children.Add(leadMenu);

        var active = team.Models.Where(m => !m.Bench).ToList();
        var bench = team.Models.Where(m => m.Bench).ToList();
        side.Children.Add(Heading($"On the team · {active.Count}"));
        side.Children.Add(Explain("Build writes code and tests. Verify checks the work and fixes it, ideally a model from another company. Review is optional. Planwharf always watches running work by itself: if something stalls or fails, the lead is told."));
        if (active.Count == 0) side.Children.Add(Explain("Nobody yet. Bring models in from the bench."));
        foreach (var model in active) side.Children.Add(Member(model));

        side.Children.Add(Heading($"Bench · {bench.Count}"));
        side.Children.Add(Explain("Not working on this project. Press a role to bring a model in."));
        if (bench.Count == 0) side.Children.Add(Explain("The bench is empty."));
        else
        {
            var list = new StackPanel { Spacing = 8 };
            foreach (var model in bench) list.Children.Add(Member(model));
            var fold = Fold(bench.Count <= 4 ? "Models on the bench" : $"Show the {bench.Count} models on the bench", list);
            fold.IsExpanded = bench.Count <= 4;
            side.Children.Add(fold);
        }

        side.Children.Add(Heading("AI tools"));
        side.Children.Add(Explain("Planwharf finds the models of the AI tools signed in on this computer. A tool that is off is never used."));
        foreach (var provider in team.Providers)
        {
            // Not installed: never shown as On; say how to install it (the command can be copied).
            if (!provider.Installed)
            {
                side.Children.Add(new Border { Background = Palette.Card, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8),
                    Child = new StackPanel { Spacing = 4, Children = {
                        new TextBlock { Text = $"{provider.Label} · Not installed", Foreground = Palette.Warn, FontWeight = FontWeight.SemiBold },
                        new SelectableTextBlock { Text = provider.InstallHint ?? "Install it and sign in, then look for models again.", Foreground = Palette.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap } } } });
                continue;
            }
            var text = $"{provider.Label} · {provider.Models} model{(provider.Models == 1 ? "" : "s")}";
            var toggle = new ToggleSwitch { OnContent = text, OffContent = text, IsChecked = provider.Enabled };
            toggle.IsCheckedChanged += async (_, _) => { if ((toggle.IsChecked == true) != provider.Enabled) await DoAsync(() => api!.SetProviderEnabledAsync(provider.Id, toggle.IsChecked == true), $"{provider.Label} turned {(toggle.IsChecked == true ? "on" : "off")}."); };
            side.Children.Add(toggle);
            // The user's OK to edit files and run commands without asking each time; off until given.
            var unasked = new CheckBox { Content = "Works without asking", IsChecked = provider.WorksUnasked, Margin = new Thickness(44, -6, 0, 4), FontSize = 12 };
            ToolTip.SetTip(unasked, "Edits files and runs commands in the project folder without asking each time");
            unasked.IsCheckedChanged += async (_, _) => { if ((unasked.IsChecked == true) != provider.WorksUnasked) await DoAsync(() => api!.SetWorksUnaskedAsync(provider.Id, unasked.IsChecked == true), unasked.IsChecked == true ? $"{provider.Label} works without asking now." : $"{provider.Label} asks again before it builds."); };
            side.Children.Add(unasked);
        }
        var look = new Button { Content = "Look for models again" };
        look.Click += async (_, _) => await DoAsync(() => api!.RefreshModelsAsync(), "Model list updated.");
        side.Children.Add(look);

        side.Children.Add(Heading("Connect a model by API"));
        side.Children.Add(Explain("For a model you reach through an API, such as DeepSeek or Kimi. The key stays in an environment variable on this computer; Planwharf stores only the variable's name."));
        var fold2 = Fold("Add an API connection", ConnectionForm());
        fold2.IsExpanded = false;
        side.Children.Add(fold2);
    }

    Control ConnectionForm()
    {
        var label = new TextBox { Watermark = "DeepSeek", MaxLength = 100 };
        var url = new TextBox { Watermark = "https://api.deepseek.com/v1" };
        var protocol = new ComboBox { ItemsSource = new[] { "OpenAI-compatible", "Anthropic Messages" }, SelectedIndex = 0, MinWidth = 220 };
        var env = new TextBox { Watermark = "DEEPSEEK_API_KEY" };
        var ids = new TextBox { Watermark = "deepseek-chat" };
        var tools = new CheckBox { Content = "These models can use tools (needed to build code)" };
        var error = new TextBlock { Foreground = Palette.Danger, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var connect = new Button { Content = "Connect" };
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

    Control Member(TeamModel model)
    {
        var status = !model.Available ? "not available now" + (model.UnavailableReason != null ? ": " + model.UnavailableReason[..Math.Min(140, model.UnavailableReason.Length)] : "") + (model.RetrySoon ? " (tried again in a few minutes)" : "")
            : model.QuotaLeft is { } left ? $"{left}% quota left" : "quota not reported";
        var chips = new WrapPanel();
        if (model.Bench) chips.Children.Add(new TextBlock { Text = "Bring in as:", Foreground = Palette.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        chips.Children.Add(RoleChip(model, "Build", "worker", model.Build, model.CanBuild, "Writes the code and the tests"));
        chips.Children.Add(RoleChip(model, "Verify", "verifier", model.Verify, model.CanVerify, "Checks the work and fixes what fails"));
        if (model.CanReview) chips.Children.Add(RoleChip(model, "Review", "reviewer", model.Review, true, "Optional: reads the work and reports problems, without changing files"));
        if (!model.Bench)
        {
            var bench = new Button { Content = "Bench", FontSize = 12, Padding = new Thickness(10, 3), Margin = new Thickness(0, 0, 6, 4) };
            ToolTip.SetTip(bench, "Take this model out of the team");
            bench.Click += async (_, _) => await DoAsync(() => api!.BenchAsync(projectId!, model.Id), $"{model.Label} is on the bench.");
            chips.Children.Add(bench);
        }
        var info = new StackPanel { Spacing = 4, Children = {
            new TextBlock { Text = model.Label + (model.Lead ? "  · Lead" : ""), Foreground = model.Bench ? Palette.Muted : Palette.Text, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = $"{model.Provider} · {status}", Foreground = model.Available ? Palette.Muted : Palette.Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap } } };
        if (model.QuotaLeft is { } quota && !model.Bench)
            info.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(quota, 0, 100), Height = 5, Foreground = quota < 20 ? Palette.Danger : Palette.Accent });
        info.Children.Add(chips);
        return new Border { Background = model.Bench ? Palette.Panel : Palette.Card, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = info };
    }

    Control RoleChip(TeamModel model, string label, string role, bool on, bool eligible, string help)
    {
        var chip = new ToggleButton { Content = label, IsChecked = on, IsEnabled = model.Available && eligible, FontSize = 12, Padding = new Thickness(10, 3), Margin = new Thickness(0, 0, 6, 4) };
        ToolTip.SetTip(chip, help);
        chip.Click += async (_, _) => await DoAsync(() => api!.SetRolesAsync(projectId!, model.Id, new Dictionary<string, bool> { [role] = !on }), "Team updated.");
        return chip;
    }
}
