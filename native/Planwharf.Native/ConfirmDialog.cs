using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Planwharf.Native;

// A plain question before an action the user should be sure about.
sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string question, string yes)
    {
        Title = "Planwharf"; Width = 460; SizeToContent = SizeToContent.Height; CanResize = false; Background = Palette.Card;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ok = new Button { Content = yes, Background = Palette.Accent, Foreground = Palette.AccentText, Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = "No" };
        ok.Click += (_, _) => Close(true); no.Click += (_, _) => Close(false);
        Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = {
            new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Text, FontSize = 15 },
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, no } } } };
    }
}
