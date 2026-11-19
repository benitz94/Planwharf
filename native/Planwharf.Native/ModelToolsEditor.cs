using Planwharf.Engine;
using Avalonia.Controls;
using Avalonia.Layout;
using Planwharf.Native.Core;

namespace Planwharf.Native;

// The callback saves a complete override, or null to follow the current project default.
public static class ModelToolsEditor
{
    public static Expander Create(ModelToolsView view, Func<IDictionary<string, bool>?, Task> save)
    {
        var form = new StackPanel { Spacing = 6 };
        var same = new CheckBox { Content = Strings.T("Same as the project"), IsChecked = view.SameAsProject };
        form.Children.Add(same);
        var boxes = new Dictionary<string, CheckBox>();
        var labels = new[] { Strings.T("MCP servers"), Strings.T("Browser"), Strings.T("Hooks"), Strings.T("Memory") };
        foreach (var (name, label) in ModelToolsView.Names.Zip(labels))
        {
            var box = new CheckBox { Content = label, IsChecked = view.Tools[name], IsEnabled = !view.SameAsProject };
            boxes[name] = box;
            box.Click += async (_, _) => await Save();
            form.Children.Add(box);
        }
        Task Save() => save(same.IsChecked == true ? null : boxes.ToDictionary(pair => pair.Key, pair => pair.Value.IsChecked == true));
        same.Click += async (_, _) =>
        {
            foreach (var box in boxes.Values) box.IsEnabled = same.IsChecked != true;
            await Save();
        };
        return new Expander { Header = Strings.T("Tools"), Content = form, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    }
}
