using Planwharf.Engine;

namespace Planwharf.Native.Core;

// The three per-project saving modes. Normal is today's picking.
public static class SavingModes
{
    public const string Normal = "normal";
    public const string Safe = "safe";
    public const string UltraSafe = "ultra-safe";
    public static readonly string[] All = [Normal, Safe, UltraSafe];

    public static string Normalize(string? value)
    {
        var mode = (value ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        if (mode is "safe" or "sicura" or "sicuro") return Safe;
        if (mode is "ultra-safe" or "ultra safe" or "ultrasafe" or "ultra sicura" or "ultra sicuro") return UltraSafe;
        return Normal;
    }

    public static string Label(string? mode) => Normalize(mode) switch
    {
        Safe => Strings.T("Safe"),
        UltraSafe => Strings.T("Ultra safe"),
        _ => Strings.T("Normal")
    };

    public static string Hint(string? mode) => Normalize(mode) switch
    {
        Safe => Strings.T("No flagship models. Mid and light only, for the lead and the crew."),
        UltraSafe => Strings.T("Light models only, for the lead and the crew."),
        _ => Strings.T("Today's model picking. Flagship, mid and light models can work.")
    };

    public static string Beside(string? name, string? mode)
    {
        var label = Label(mode);
        return string.IsNullOrEmpty(name) ? label : name + " \u00b7 " + label;
    }
}
