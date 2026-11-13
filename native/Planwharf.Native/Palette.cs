using Avalonia.Media;

namespace Planwharf.Native;

// The same dark palette as the chat page (chat.css, dark theme): one name per colour there.
static class Palette
{
    public static readonly IBrush Background = Brush.Parse("#141715");   // --bg
    public static readonly IBrush Panel = Brush.Parse("#1d211e");        // --surface (side bars)
    public static readonly IBrush Card = Brush.Parse("#1d211e");         // --surface (cards, bubbles)
    public static readonly IBrush Surface2 = Brush.Parse("#262b27");     // --surface-2 (hover, quiet tiles)
    public static readonly IBrush Line = Brush.Parse("#313732");         // --line
    public static readonly IBrush Text = Brush.Parse("#ecefeb");         // --text
    public static readonly IBrush Muted = Brush.Parse("#a2a9a3");        // --muted
    public static readonly IBrush Accent = Brush.Parse("#5fcf98");       // --accent
    public static readonly IBrush AccentText = Brush.Parse("#0c1a12");   // --accent-text
    public static readonly IBrush AccentSoft = Brush.Parse("#1b3a2a");   // --accent-soft
    public static readonly IBrush UserBubble = Accent;
    public static readonly IBrush Warn = Brush.Parse("#e8b455");
    public static readonly IBrush WarnSoft = Brush.Parse("#3a2f1a");
    public static readonly IBrush Danger = Brush.Parse("#f08a8a");       // --bad
    public static readonly IBrush DangerSoft = Brush.Parse("#3d2222");
    public static readonly IBrush Lead = Brush.Parse("#a993ff");
    public static readonly IBrush LeadSoft = Brush.Parse("#2a2445");
    public static readonly IBrush Build = Brush.Parse("#7fa9f5");
    public static readonly IBrush BuildSoft = Brush.Parse("#1d2a42");
    public static readonly IBrush Verify = Brush.Parse("#f2a560");
    public static readonly IBrush VerifySoft = Brush.Parse("#3b2a1a");
    public static readonly IBrush Review = Brush.Parse("#5fcbd6");
    public static readonly IBrush ReviewSoft = Brush.Parse("#163437");
    public static readonly IBrush Watch = Brush.Parse("#f08bbd");
    public static readonly IBrush WatchSoft = Brush.Parse("#3c1f2e");

    // Each role has its own colour, used the same way everywhere (as ROLE_LOOK in the chat page).
    public static (IBrush Color, IBrush Soft, string Icon) Role(string role) => role switch
    {
        "Build" => (Build, BuildSoft, "build"),
        "Verify" => (Verify, VerifySoft, "verify"),
        "Review" or "Research" => (Review, ReviewSoft, role == "Research" ? "research" : "review"),
        "Watch" => (Watch, WatchSoft, "watch"),
        "Lead" => (Lead, LeadSoft, "lead"),
        _ => (Muted, Surface2, "box"),
    };
}
