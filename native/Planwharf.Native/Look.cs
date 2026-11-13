using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;

namespace Planwharf.Native;

// The chat page's building blocks for the native window: the same icons, buttons, tiles and chips,
// so both windows look like one app. Sizes and colours follow chat.css.
static class Look
{
    // The chat page's icons (ICONS in chat-ui.js), drawn on a 24×24 grid with a 2px round stroke.
    static readonly Dictionary<string, string> Icons = new()
    {
        ["plus"] = """<path d="M12 5v14M5 12h14"/>""",
        ["team"] = """<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20c.8-3.6 3.3-5.5 6.5-5.5s5.7 1.9 6.5 5.5M16 4.6a3.5 3.5 0 0 1 0 6.8M18 14.8c2 .7 3.2 2.4 3.6 5.2"/>""",
        ["progress"] = """<path d="M5 20v-6M12 20V5M19 20v-10"/>""",
        ["usage"] = """<path d="M3.5 18a9 9 0 1 1 17 0"/><path d="M12 14l4-4"/>""",
        ["file"] = """<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/>""",
        ["settings"] = """<path d="M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1"/><circle cx="15" cy="6" r="2"/><circle cx="9" cy="12" r="2"/><circle cx="17" cy="18" r="2"/>""",
        ["folder"] = """<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>""",
        ["build"] = """<path d="M8 8l-4 4 4 4M16 8l4 4-4 4M13.5 5l-3 14"/>""",
        ["verify"] = """<path d="M12 3l8 3v6c0 4.5-3.4 8-8 9-4.6-1-8-4.5-8-9V6z"/><path d="M8.5 12l2.5 2.5 4.5-5"/>""",
        ["review"] = """<path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>""",
        ["watch"] = """<path d="M6 16v-5a6 6 0 0 1 12 0v5l2 2H4z"/><path d="M10 21h4"/>""",
        ["lead"] = """<circle cx="12" cy="12" r="8.5"/><path d="M12 4.5v2M12 17.5v2M4.5 12h2M17.5 12h2"/><path d="M15.2 8.8 13 13l-4.2 2.2L11 11z"/>""",
        ["check"] = """<path d="M5 12.5l4.5 4.5L19 7.5"/>""",
        ["alert"] = """<path d="M12 3.5l9.5 17h-19z"/><path d="M12 10v4.5M12 17.5v.5"/>""",
        ["play"] = """<path d="M7 5v14l12-7z"/>""",
        ["stop"] = """<rect x="6" y="6" width="12" height="12" rx="2"/>""",
        ["pencil"] = """<path d="M4 20h4L19 9l-4-4L4 16z"/>""",
        ["clock"] = """<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>""",
        ["box"] = """<path d="M4 8l8-4 8 4v8l-8 4-8-4z"/><path d="M4 8l8 4 8-4M12 12v8"/>""",
        ["refresh"] = """<path d="M20 11a8 8 0 0 0-14.6-4.5L4 8M4 4v4h4M4 13a8 8 0 0 0 14.6 4.5L20 16M20 20v-4h-4"/>""",
        ["trash"] = """<path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/>""",
        ["power"] = """<path d="M12 3v9"/><path d="M6.4 6.4a8 8 0 1 0 11.2 0"/>""",
        ["plug"] = """<path d="M9 3v5M15 3v5M6 8h12v3a6 6 0 0 1-12 0zM12 17v4"/>""",
        ["chip"] = """<rect x="7" y="7" width="10" height="10" rx="1.5"/><path d="M10 3v4M14 3v4M10 17v4M14 17v4M3 10h4M3 14h4M17 10h4M17 14h4"/>""",
        ["rocket"] = """<path d="M9 15l-3-3c1.5-4 5-9 14-9 0 9-5 12.5-9 14z"/><path d="M5.5 14.5c-1 1-1.5 3.5-1.5 5.5 2 0 4.5-.5 5.5-1.5"/><circle cx="15" cy="9" r="1.5"/>""",
        ["send"] = """<path d="M4 12l16-8-6 16-2.5-6.5z"/><path d="M11.5 13.5L20 4"/>""",
        ["pause"] = """<path d="M9 5v14M15 5v14"/>""",
        ["research"] = """<circle cx="11" cy="11" r="6.5"/><path d="M16 16l4.5 4.5"/>""",
        ["copy"] = """<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V6a2 2 0 0 1 2-2h9"/>""",
        ["close"] = """<path d="M6 6l12 12M18 6L6 18"/>""",
        ["chevron"] = """<path d="M9 6l6 6-6 6"/>""",
    };

    static readonly Regex Element = new(@"<(path|circle|rect)\s+([^>]*?)/>");
    static readonly Regex Attribute = new(@"([a-z]+)=""([^""]*)""");
    static readonly Dictionary<string, string[]> Parsed = new();

    // Every shape of an icon as path data: circles and rectangles become arcs and lines.
    public static IReadOnlyList<string> Shapes(string name)
    {
        if (Parsed.TryGetValue(name, out var done)) return done;
        var shapes = new List<string>();
        foreach (Match element in Element.Matches(Icons.GetValueOrDefault(name, "")))
        {
            var a = Attribute.Matches(element.Groups[2].Value).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            double N(string key) => a.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : 0;
            string F(double v) => v.ToString(CultureInfo.InvariantCulture);
            shapes.Add(element.Groups[1].Value switch
            {
                "circle" => $"M{F(N("cx") - N("r"))},{F(N("cy"))} A{F(N("r"))},{F(N("r"))} 0 1 0 {F(N("cx") + N("r"))},{F(N("cy"))} A{F(N("r"))},{F(N("r"))} 0 1 0 {F(N("cx") - N("r"))},{F(N("cy"))} Z",
                "rect" => RoundRect(N("x"), N("y"), N("width"), N("height"), N("rx")),
                _ => a.GetValueOrDefault("d", ""),
            });
        }
        return Parsed[name] = [.. shapes];
    }

    static string RoundRect(double x, double y, double w, double h, double r)
    {
        string F(double v) => v.ToString(CultureInfo.InvariantCulture);
        if (r <= 0) return $"M{F(x)},{F(y)} H{F(x + w)} V{F(y + h)} H{F(x)} Z";
        return $"M{F(x + r)},{F(y)} H{F(x + w - r)} A{F(r)},{F(r)} 0 0 1 {F(x + w)},{F(y + r)} V{F(y + h - r)} A{F(r)},{F(r)} 0 0 1 {F(x + w - r)},{F(y + h)} H{F(x + r)} A{F(r)},{F(r)} 0 0 1 {F(x)},{F(y + h - r)} V{F(y + r)} A{F(r)},{F(r)} 0 0 1 {F(x + r)},{F(y)} Z";
    }

    // An icon in the colour of the text around it, unless a colour is given.
    public static Control Icon(string name, double size = 16, IBrush? color = null)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (var data in Shapes(name))
        {
            var path = new Path { Data = Geometry.Parse(data), StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
            if (color != null) path.Stroke = color;
            else path.Bind(Shape.StrokeProperty, path.GetObservable(TextElement.ForegroundProperty));
            canvas.Children.Add(path);
        }
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }

    // A rounded square with an icon: in headings, projects and steps.
    public static Border Tile(string icon, IBrush color, IBrush soft, double size = 32, double iconSize = 18, double radius = 10) =>
        new() { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = soft, Child = new Panel { Children = { Centered(Icon(icon, iconSize, color)) } } };

    static Control Centered(Control c) { c.HorizontalAlignment = HorizontalAlignment.Center; c.VerticalAlignment = VerticalAlignment.Center; return c; }

    // A section heading with its tile, as h3.head in the side panels.
    public static Control Head(string icon, IBrush color, IBrush soft, string text, double size = 15) =>
        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = {
            Tile(icon, color, soft, 28, 16, 8),
            new TextBlock { Text = text, FontSize = size, FontWeight = FontWeight.Bold, Foreground = Palette.Text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap } } };

    public enum Kind { Normal, Primary, Danger, Quiet, Link, Small }

    // Icon and text side by side, as the chat page's buttons.
    public static Control Label(string text, string? icon, double iconSize = 16, double spacing = 6)
    {
        if (icon == null) return new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, Children = { Icon(icon, iconSize), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } };
    }

    // .btn, .btn-primary, .btn-danger, .btn-small, .copy-msg and .link-quiet.
    public static Button Button(string text, string? icon = null, Kind kind = Kind.Normal)
    {
        var button = new Button { Content = Label(text, icon, kind is Kind.Quiet or Kind.Link or Kind.Small ? 14 : 16, kind is Kind.Quiet ? 5 : 6) };
        Paint(button, kind);
        return button;
    }

    public static void Paint(Button button, Kind kind)
    {
        var (bg, fg, border, hover) = kind switch
        {
            Kind.Primary => (Palette.Accent, Palette.AccentText, Palette.Accent, Brush.Parse("#72dba8")),
            Kind.Danger => (Palette.DangerSoft, Palette.Danger, Palette.Danger, Brush.Parse("#4a2828")),
            Kind.Quiet or Kind.Link => ((IBrush)Brushes.Transparent, Palette.Muted, (IBrush)Brushes.Transparent, (IBrush)Palette.Card),
            _ => (Palette.Card, Palette.Text, Palette.Line, Palette.Surface2),
        };
        button.Background = bg; button.Foreground = fg; button.BorderBrush = border; button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(kind is Kind.Small or Kind.Quiet ? 8 : 10);
        button.Padding = kind switch { Kind.Small => new Thickness(10, 4), Kind.Quiet => new Thickness(8, 3), Kind.Link => new Thickness(0), _ => new Thickness(14, 9) };
        button.FontSize = kind is Kind.Small or Kind.Link ? 13 : kind is Kind.Quiet ? 12 : 15;
        if (kind == Kind.Primary) button.FontWeight = FontWeight.SemiBold;
        button.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        button.Resources["ButtonBackgroundPointerOver"] = hover;
        button.Resources["ButtonBackgroundPressed"] = hover;
        button.Resources["ButtonForegroundPointerOver"] = kind is Kind.Quiet or Kind.Link ? Palette.Text : fg;
        button.Resources["ButtonForegroundPressed"] = kind is Kind.Quiet or Kind.Link ? Palette.Text : fg;
        button.Resources["ButtonBorderBrushPointerOver"] = kind is Kind.Quiet ? Palette.Line : border;
        button.Resources["ButtonBorderBrushPressed"] = kind is Kind.Quiet ? Palette.Line : border;
        button.Resources["ButtonBackgroundDisabled"] = bg;
        button.Resources["ButtonForegroundDisabled"] = fg;
        button.Resources["ButtonBorderBrushDisabled"] = border;
        Dim(button);
    }

    // A disabled control is shown faded, as in the chat page.
    public static void Dim(Control control)
    {
        control.Opacity = control.IsEnabled ? 1 : .55;
        control.PropertyChanged += (_, change) => { if (change.Property == InputElement.IsEnabledProperty) control.Opacity = control.IsEnabled ? 1 : .55; };
    }

    // The round tabs at the top (Team, Progress, …): green when open.
    public static ToggleButton Tab(string text, string icon)
    {
        var tab = new ToggleButton { Content = Label(text, icon), CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 6), Background = Palette.Card, Foreground = Palette.Text,
            BorderBrush = Palette.Line, BorderThickness = new Thickness(1), FontSize = 15, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        foreach (var (key, value) in new (string, IBrush)[] {
            ("ToggleButtonBackgroundPointerOver", Palette.Surface2), ("ToggleButtonBackgroundPressed", Palette.Surface2),
            ("ToggleButtonForegroundPointerOver", Palette.Text), ("ToggleButtonBorderBrushPointerOver", Palette.Line),
            ("ToggleButtonBackgroundChecked", Palette.Accent), ("ToggleButtonBackgroundCheckedPointerOver", Palette.Accent), ("ToggleButtonBackgroundCheckedPressed", Palette.Accent),
            ("ToggleButtonForegroundChecked", Palette.AccentText), ("ToggleButtonForegroundCheckedPointerOver", Palette.AccentText), ("ToggleButtonForegroundCheckedPressed", Palette.AccentText),
            ("ToggleButtonBorderBrushChecked", Palette.Accent), ("ToggleButtonBorderBrushCheckedPointerOver", Palette.Accent), ("ToggleButtonBorderBrushCheckedPressed", Palette.Accent) })
            tab.Resources[key] = value;
        return tab;
    }

    // A small round label, as .badge.
    public static Border Badge(string text, IBrush color, IBrush soft) =>
        new() { Background = soft, CornerRadius = new CornerRadius(99), Padding = new Thickness(8, 1), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = color } };

    // A card, as .card: the surface colour, a thin line and round corners.
    public static Border Card(Control child, IBrush? border = null, double padding = 18) =>
        new() { Background = Palette.Card, BorderBrush = border ?? Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(padding), Child = child };

    // Each provider's colour, as the dots of the chat page (PROVIDER_COLOURS).
    public static IBrush ProviderColour(string provider) => Brush.Parse(provider.ToLowerInvariant() switch
    {
        "codex" or "openai" or "chatgpt" => "#10a37f",
        "claude" or "anthropic" or "claude code" => "#d97757",
        "grok" or "xai" => "#6b7280",
        "antigravity" or "gemini" or "google" => "#4285f4",
        "api" => "#8b5cf6",
        _ => "#9ca3af",
    });

    public static Ellipse Dot(IBrush color) => new() { Width = 9, Height = 9, Fill = color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };

    // A quota bar, as .bar: green to blue, amber when low.
    public static Control Bar(int percent, bool? low = null)
    {
        var left = Math.Clamp(percent, 0, 100);
        var fill = new Border { CornerRadius = new CornerRadius(99), Background = low ?? left < 20 ? Palette.Warn : new LinearGradientBrush {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#5fcf98"), 0), new GradientStop(Color.Parse("#7fa9f5"), 1) } } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{Math.Max(left, 0.001)}*,{Math.Max(100 - left, 0.001)}*") };
        grid.Children.Add(fill);
        return new Border { Height = 6, CornerRadius = new CornerRadius(99), Background = Palette.Surface2, ClipToBounds = true, Margin = new Thickness(0, 4, 0, 0), Child = grid };
    }

    // A round chip that can be pressed, as .chip: in its role's colour when on.
    public static ToggleButton Chip(string text, string? icon, IBrush color, IBrush soft, bool on)
    {
        var chip = new ToggleButton { Content = Label(text, icon, 13, 4), IsChecked = on, CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 3), FontSize = 12,
            Background = Palette.Card, Foreground = Palette.Text, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 4, 4),
            FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal, Cursor = new Cursor(StandardCursorType.Hand) };
        foreach (var (key, value) in new (string, IBrush)[] {
            ("ToggleButtonBackgroundPointerOver", Palette.Surface2), ("ToggleButtonBackgroundPressed", Palette.Surface2), ("ToggleButtonForegroundPointerOver", Palette.Text), ("ToggleButtonBorderBrushPointerOver", Palette.Line),
            ("ToggleButtonBackgroundChecked", soft), ("ToggleButtonBackgroundCheckedPointerOver", soft), ("ToggleButtonBackgroundCheckedPressed", soft),
            ("ToggleButtonForegroundChecked", color), ("ToggleButtonForegroundCheckedPointerOver", color), ("ToggleButtonForegroundCheckedPressed", color),
            ("ToggleButtonBorderBrushChecked", color), ("ToggleButtonBorderBrushCheckedPointerOver", color), ("ToggleButtonBorderBrushCheckedPressed", color),
            ("ToggleButtonBackgroundDisabled", Palette.Card), ("ToggleButtonForegroundDisabled", Palette.Muted), ("ToggleButtonBorderBrushDisabled", Palette.Line),
            ("ToggleButtonBackgroundCheckedDisabled", soft), ("ToggleButtonForegroundCheckedDisabled", color), ("ToggleButtonBorderBrushCheckedDisabled", color) })
            chip.Resources[key] = value;
        Dim(chip);
        return chip;
    }

    // A section of a side panel: a heading with its tile, an explanation, then its content.
    public static StackPanel Section(string icon, IBrush color, IBrush soft, string title, string? explain = null)
    {
        var section = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 14), Children = { Head(icon, color, soft, title) } };
        if (explain != null) section.Children.Add(new TextBlock { Text = explain, Foreground = Palette.Muted, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        return section;
    }

    // A text box as the chat page's fields: dark inside, round corners.
    public static T Field<T>(T box) where T : TextBox
    {
        box.Background = Palette.Background; box.BorderBrush = Palette.Line; box.CornerRadius = new CornerRadius(10); box.Padding = new Thickness(12, 9);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" }) box.Resources[key] = Palette.Background;
        box.Resources["TextControlBorderBrush"] = Palette.Line;
        box.Resources["TextControlBorderBrushPointerOver"] = Palette.Muted;
        box.Resources["TextControlBorderBrushFocused"] = Palette.Accent;
        return box;
    }
}

static class ControlExtensions
{
    // Sets a few properties inline, where an object initializer cannot reach.
    public static T With<T>(this T control, Action<T> set) where T : Control { set(control); return control; }
}
