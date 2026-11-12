using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Planwharf.Native;

// A plain question before an action the user should be sure about.
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string question, string yes, string? heading = null, string no = "No")
    {
        Title = "Planwharf"; Width = 460; SizeToContent = SizeToContent.Height; CanResize = false; Background = Palette.Card;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ok = new Button { Content = yes, Background = Palette.Accent, Foreground = Palette.AccentText, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = no };
        ok.Click += (_, _) => Close(true); cancel.Click += (_, _) => Close(false);
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        if (heading != null) { Title = heading; body.Children.Add(new TextBlock { Text = heading, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Palette.Text }); }
        body.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text, FontSize = 15 });
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } });
        ok.Margin = new Thickness(8, 0, 0, 0);
        Content = body;
    }
}
