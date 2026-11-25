using System.Globalization;
using System.Reflection;
using Planwharf.Engine;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Planwharf.Engine.Tests;

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollection;

// The app is English only (owner, 25 November 2026): the catalog lists every text on screen.
[Collection("Localization")]
public sealed class StringsTests
{
    static Dictionary<string, string> English() =>
        (Dictionary<string, string>)typeof(Strings).GetField("English", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    [Fact]
    public void The_catalog_is_English_and_complete()
    {
        var english = English();
        Assert.NotEmpty(english);
        Assert.All(english, pair => Assert.Equal(pair.Key, pair.Value));
        Assert.Equal("en", Strings.CurrentLanguage);
        Assert.Equal("Language", Strings.T("Language"));
        Assert.Equal("unknown.key", Strings.T("unknown.key"));
        Assert.Equal("", Strings.T(""));
        Assert.Throws<ArgumentNullException>(() => Strings.T(null!));
        Assert.Throws<ArgumentNullException>(() => Strings.Format(null!));
    }

    [Fact]
    public void Numbers_and_dates_are_English_whatever_the_computer_says()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
            Assert.Equal("1.5 s", Strings.Format("{0} s", 1.5));
            Assert.Equal("Wed, Nov 18", new DateTime(2026, 11, 18).ToString("ddd, MMM d", Strings.Culture));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Every_catalog_entry_formats()
    {
        foreach (var key in English().Keys)
        {
            var count = System.Text.RegularExpressions.Regex.Matches(key, @"(?<!\{)\{(\d+)").Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(-1).Max() + 1;
            var formatted = Strings.Format(key, Enumerable.Range(0, count).Select(i => (object?)("x" + i)).ToArray());
            Assert.False(string.IsNullOrEmpty(formatted) && key.Length > 0, key);
        }
    }
}
