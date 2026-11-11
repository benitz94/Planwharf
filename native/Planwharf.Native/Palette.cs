using Avalonia.Media;

namespace Planwharf.Native;

// The same dark palette as the chat page.
static class Palette
{
    public static readonly IBrush Background = Brush.Parse("#101312");
    public static readonly IBrush Panel = Brush.Parse("#161a18");
    public static readonly IBrush Card = Brush.Parse("#1b201d");
    public static readonly IBrush Line = Brush.Parse("#2a312d");
    public static readonly IBrush Text = Brush.Parse("#eef3ef");
    public static readonly IBrush Muted = Brush.Parse("#9aa59f");
    public static readonly IBrush Accent = Brush.Parse("#5fd29a");
    public static readonly IBrush AccentText = Brush.Parse("#0d1a13");
    public static readonly IBrush UserBubble = Brush.Parse("#5fd29a");
    public static readonly IBrush Warn = Brush.Parse("#e0a54b");
    public static readonly IBrush Danger = Brush.Parse("#e06c6c");
}
