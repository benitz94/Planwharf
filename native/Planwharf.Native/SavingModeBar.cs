using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Planwharf.Engine;
using Planwharf.Native.Core;

namespace Planwharf.Native;

// The choice at the top of Team: Normal, Safe, or Ultra safe. One click calls the engine once.
public sealed class SavingModeBar : StackPanel
{
    readonly Dictionary<string, ToggleButton> buttons = new();
    readonly TextBlock hint = new() { FontSize = 12, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
    Func<string, Task>? pick;
    string mode = SavingModes.Normal;
    bool applying;

    public SavingModeBar()
    {
        Spacing = 0;
        Children.Add(new TextBlock { Text = Strings.T("Saving"), FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Palette.Text, Margin = new Thickness(0, 0, 0, 6) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var name in SavingModes.All)
        {
            var button = Look.Tab(SavingModes.Label(name), "clock");
            button.Tag = name;
            button.Click += OnClick;
            buttons[name] = button;
            row.Children.Add(button);
        }
        Children.Add(row);
        Children.Add(hint);
        Show(SavingModes.Normal);
    }

    public string Mode => mode;
    public TextBlock HintLine => hint;
    public IReadOnlyList<ToggleButton> Buttons => SavingModes.All.Select(name => buttons[name]).ToList();

    public void Bind(Func<string, Task> onPick) => pick = onPick;

    public void Show(string? saved)
    {
        mode = SavingModes.Normalize(saved);
        applying = true;
        foreach (var (name, button) in buttons)
        {
            button.IsChecked = name == mode;
            button.Content = Look.Label(SavingModes.Label(name), "clock");
        }
        hint.Text = SavingModes.Hint(mode) + " " + Strings.T("It applies from the next step.");
        applying = false;
    }

    async void OnClick(object? sender, RoutedEventArgs e)
    {
        if (applying || sender is not ToggleButton button || button.Tag is not string next || pick == null) return;
        applying = true;
        foreach (var (name, item) in buttons) item.IsChecked = name == mode;
        applying = false;
        await pick(next);
    }
}
