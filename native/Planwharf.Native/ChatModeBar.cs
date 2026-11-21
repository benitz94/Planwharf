using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Planwharf.Engine;

namespace Planwharf.Native;

// The switch above the chat: Talk or Plan. Pipeline is reserved for a later job and is not a position.
public sealed class ChatModeBar : StackPanel
{
    public const string Talk = "talk";
    public const string Plan = "plan";
    static readonly string[] Modes = [Talk, Plan];

    readonly Dictionary<string, ToggleButton> buttons = new();
    readonly TextBlock hint = new() { FontSize = 12, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
    Func<string, Task>? pick;
    string mode = Plan;
    bool applying;

    public ChatModeBar()
    {
        Spacing = 0;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var name in Modes)
        {
            var button = Look.Tab(LabelOf(name), IconOf(name));
            button.Tag = name;
            button.Click += OnClick;
            buttons[name] = button;
            row.Children.Add(button);
        }
        Children.Add(row);
        Children.Add(hint);
        Show(Plan);
    }

    public string Mode => mode;
    public TextBlock HintLine => hint;
    public IReadOnlyList<ToggleButton> Buttons => Modes.Select(name => buttons[name]).ToList();

    // One click calls this once. The window sends that single call to the engine.
    public void Bind(Func<string, Task> onPick) => pick = onPick;

    // Shows the mode the engine saved. An unknown value, including Pipeline, stays on Plan.
    public void Show(string? saved)
    {
        var normalized = (saved ?? "").Trim().ToLowerInvariant();
        mode = normalized == Talk ? Talk : Plan;
        applying = true;
        foreach (var (name, button) in buttons)
        {
            button.IsChecked = name == mode;
            button.Content = Look.Label(LabelOf(name), IconOf(name));
        }
        hint.Text = HintOf(mode);
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

    public static string LabelOf(string name) => name switch
    {
        Talk => Strings.T("Talk"),
        _ => Strings.T("Plan")
    };

    public static string HintOf(string name) => name switch
    {
        Talk => Strings.T("A conversation with the lead. Nothing starts."),
        _ => Strings.T("The lead shapes a goal. You press Let the crew do it.")
    };

    static string IconOf(string name) => name == Talk ? "send" : "check";
}
