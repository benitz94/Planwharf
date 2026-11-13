using Avalonia.Media;

namespace Planwharf.Native;

// The chat page's colours (chat.css), in its light and its dark theme: one name per colour there.
// Apply() picks a theme before the window is built; the window is built again when the user changes it.
public static class Palette
{
    public static bool Dark { get; private set; } = true;
    public static IBrush Background { get; private set; } = null!;   // --bg
    public static IBrush Panel { get; private set; } = null!;        // --surface (side bars)
    public static IBrush Card { get; private set; } = null!;         // --surface (cards, bubbles)
    public static IBrush Surface2 { get; private set; } = null!;     // --surface-2 (hover, quiet tiles)
    public static IBrush Line { get; private set; } = null!;         // --line
    public static IBrush Text { get; private set; } = null!;         // --text (or the chosen text colour)
    public static IBrush Muted { get; private set; } = null!;        // --muted
    public static IBrush Accent { get; private set; } = null!;       // --accent
    public static IBrush AccentHover { get; private set; } = null!;
    public static IBrush AccentText { get; private set; } = null!;   // --accent-text
    public static IBrush AccentSoft { get; private set; } = null!;   // --accent-soft
    public static IBrush UserBubble => Accent;
    public static IBrush Warn { get; private set; } = null!;
    public static IBrush WarnSoft { get; private set; } = null!;
    public static IBrush Danger { get; private set; } = null!;       // --bad
    public static IBrush DangerSoft { get; private set; } = null!;
    public static IBrush DangerHover { get; private set; } = null!;
    public static IBrush Lead { get; private set; } = null!;
    public static IBrush LeadSoft { get; private set; } = null!;
    public static IBrush Build { get; private set; } = null!;
    public static IBrush BuildSoft { get; private set; } = null!;
    public static IBrush Verify { get; private set; } = null!;
    public static IBrush VerifySoft { get; private set; } = null!;
    public static IBrush Review { get; private set; } = null!;
    public static IBrush ReviewSoft { get; private set; } = null!;
    public static IBrush Watch { get; private set; } = null!;
    public static IBrush WatchSoft { get; private set; } = null!;

    // The text colours offered in the dark theme, as the chat page's (INKS in chat-ui.js).
    public static readonly (string Key, string Label, string Colour)[] Inks =
    [
        ("white", "White", "#ecefeb"), ("silver", "Silver", "#c9ced6"), ("cream", "Cream", "#f3e6c8"), ("yellow", "Yellow", "#fff3a3"),
        ("amber", "Amber", "#ffd38a"), ("peach", "Peach", "#ffcfb0"), ("coral", "Coral", "#ff9e8a"), ("pink", "Pink", "#ffc4dd"),
        ("lavender", "Lavender", "#dccfff"), ("sky", "Sky", "#bcdcff"), ("turquoise", "Turquoise", "#99f6e4"), ("mint", "Mint", "#b4f0d2"), ("lime", "Lime", "#d9f99d"),
    ];

    static Palette() => Apply(true, null);

    // ink: a key of Inks or a #rrggbb colour; only the dark theme uses it.
    public static void Apply(bool dark, string? ink)
    {
        Dark = dark;
        IBrush B(string hex) => Brush.Parse(hex);
        if (dark)
        {
            Background = B("#141715"); Panel = Card = B("#1d211e"); Surface2 = B("#262b27"); Line = B("#313732");
            Text = B(InkColour(ink)); Muted = B("#a2a9a3");
            Accent = B("#5fcf98"); AccentHover = B("#72dba8"); AccentText = B("#0c1a12"); AccentSoft = B("#1b3a2a");
            Warn = B("#e8b455"); WarnSoft = B("#3a2f1a"); Danger = B("#f08a8a"); DangerSoft = B("#3d2222"); DangerHover = B("#4a2828");
            Lead = B("#a993ff"); LeadSoft = B("#2a2445"); Build = B("#7fa9f5"); BuildSoft = B("#1d2a42");
            Verify = B("#f2a560"); VerifySoft = B("#3b2a1a"); Review = B("#5fcbd6"); ReviewSoft = B("#163437"); Watch = B("#f08bbd"); WatchSoft = B("#3c1f2e");
        }
        else
        {
            Background = B("#f5f7f4"); Panel = Card = B("#ffffff"); Surface2 = B("#eef1ec"); Line = B("#e1e6e0");
            Text = B("#1b211d"); Muted = B("#646b66");
            Accent = B("#1f7a52"); AccentHover = B("#26905f"); AccentText = B("#ffffff"); AccentSoft = B("#e2f3e9");
            Warn = B("#9a5b00"); WarnSoft = B("#fdf0d8"); Danger = B("#b42525"); DangerSoft = B("#fbe4e4"); DangerHover = B("#f7d5d5");
            Lead = B("#6d4fd8"); LeadSoft = B("#eee9fd"); Build = B("#2563c9"); BuildSoft = B("#e4edfc");
            Verify = B("#c2600f"); VerifySoft = B("#fdeedd"); Review = B("#0b8794"); ReviewSoft = B("#dff3f5"); Watch = B("#c0367a"); WatchSoft = B("#fbe6f0");
        }
    }

    public static string InkColour(string? ink) =>
        ink is { Length: 7 } hex && hex[0] == '#' && hex[1..].All(Uri.IsHexDigit) ? hex : Inks.FirstOrDefault(item => item.Key == ink).Colour ?? Inks[0].Colour;

    public static Color ColorOf(IBrush brush) => brush is ISolidColorBrush solid ? solid.Color : Colors.Transparent;

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
