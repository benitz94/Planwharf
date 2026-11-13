using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Planwharf.Native;

// A new project: a name and the folder on this computer. What to build is said later, in the chat.
public sealed class ProjectDialog : Window
{
    public ProjectDialog()
    {
        Title = "New project"; Width = 520; SizeToContent = SizeToContent.Height; CanResize = false; Background = Palette.Card;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var name = Look.Field(new TextBox { Watermark = "My website" });
        var folder = Look.Field(new TextBox { Watermark = OperatingSystem.IsWindows() ? @"C:\Projects\my-website" : "~/Projects/my-website" });
        var error = new TextBlock { Foreground = Palette.Danger, TextWrapping = TextWrapping.Wrap };
        var browse = Look.Button("Browse…", "folder"); browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Click += async (_, _) => { if (await Pick(this) is { } path) folder.Text = path; };
        var create = Look.Button("Create", "plus", Look.Kind.Primary); create.Margin = new Thickness(0, 0, 8, 0);
        var cancel = Look.Button("Cancel");
        create.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = "Enter a name."; return; }
            Close((name.Text.Trim(), folder.Text?.Trim() ?? ""));
        };
        cancel.Click += (_, _) => Close(null);
        var folderRow = new DockPanel { Children = { browse, folder } };
        DockPanel.SetDock(browse, Dock.Right);
        Content = new StackPanel { Margin = new Thickness(20), Spacing = 10, Children = {
            new TextBlock { Text = "Name", Foreground = Palette.Text }, name,
            new TextBlock { Text = "Folder on your computer", Foreground = Palette.Text }, folderRow,
            new TextBlock { Text = "An existing folder. Leave it empty and Planwharf creates a new one for this project.", Foreground = Palette.Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Then tell the lead in the chat what to build.", Foreground = Palette.Muted, FontSize = 12 },
            error,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { create, cancel } } } };
    }

    public static async Task<string?> Pick(TopLevel owner)
    {
        var picked = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Project folder", AllowMultiple = false });
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }
}
