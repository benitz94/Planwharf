using System.Reflection;
using Planwharf.Engine;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Planwharf.Engine.Tests;

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollection;

[Collection("Localization")]
public sealed class StringsTests : IDisposable
{
    readonly string previous = Strings.CurrentLanguage;
    public void Dispose() => Strings.CurrentLanguage = previous;
    static Dictionary<string, string> Dictionary(string name) =>
        (Dictionary<string, string>)typeof(Strings).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    [Fact]
    public void Catalogs_have_identical_keys_and_nonempty_values()
    {
        var english = Dictionary("English");
        var italian = Dictionary("Italian");
        Assert.NotEmpty(english);
        Assert.Equal(english.Keys.Order(), italian.Keys.Order());
        Assert.All(english.Values.Concat(italian.Values), value => Assert.False(string.IsNullOrWhiteSpace(value)));
    }

    [Fact]
    public void Language_switch_and_unknown_language_are_deterministic()
    {
        Strings.CurrentLanguage = "en";
        Assert.Equal("Language", Strings.T("Language"));
        Strings.CurrentLanguage = "it";
        Assert.Equal("Lingua", Strings.T("Language"));
        Strings.CurrentLanguage = "fr";
        Assert.Equal("en", Strings.CurrentLanguage);
        Assert.Equal("Language", Strings.T("Language"));
        Assert.Equal("unknown.key", Strings.T("unknown.key"));
    }

    [Fact]
    public void Missing_Italian_entry_falls_back_to_English()
    {
        var italian = Dictionary("Italian");
        var value = italian["Language"];
        try
        {
            italian.Remove("Language");
            Strings.CurrentLanguage = "it";
            Assert.Equal("Language", Strings.T("Language"));
        }
        finally { italian["Language"] = value; }
    }

    [Fact]
    public void Dates_follow_the_selected_language_independently_of_the_OS()
    {
        var date = new DateTime(2026, 11, 18, 13, 45, 0);
        Strings.CurrentLanguage = "it";
        Assert.Equal("mercoledì novembre", date.ToString("dddd MMMM", Strings.Culture));
        Strings.CurrentLanguage = "en";
        Assert.Equal("Wednesday November", date.ToString("dddd MMMM", Strings.Culture));
    }

    [Fact]
    public void Translations_preserve_format_arguments()
    {
        foreach (var (key, value) in Dictionary("Italian"))
        {
            Assert.Equal(System.Text.CompositeFormat.Parse(key).MinimumArgumentCount,
                System.Text.CompositeFormat.Parse(value).MinimumArgumentCount);
        }
    }

    [Fact]
    public void Blank_unknown_and_region_tags_resolve_without_changing_catalogs()
    {
        var before = Dictionary("English").Count;
        Strings.CurrentLanguage = null!;
        Assert.Equal("en", Strings.CurrentLanguage);
        Assert.Equal("", Strings.T(""));
        Assert.Equal("not-a-key", Strings.T("not-a-key"));
        Assert.Throws<ArgumentNullException>(() => Strings.T(null!));
        Assert.Throws<ArgumentNullException>(() => Strings.Format(null!));
        foreach (var tag in new[] { "IT", " it ", "it-IT", "it_IT" })
        {
            Strings.CurrentLanguage = tag;
            Assert.Equal("it", Strings.CurrentLanguage);
            Assert.Equal("Lingua", Strings.T("Language"));
        }
        foreach (var tag in new[] { "en-US", "EN", "item", "" })
        {
            Strings.CurrentLanguage = tag;
            Assert.Equal("en", Strings.CurrentLanguage);
            Assert.Equal("Language", Strings.T("Language"));
        }
        Assert.Equal(before, Dictionary("English").Count);
    }

    [Fact]
    public void Format_uses_one_language_for_words_and_numbers()
    {
        var date = new DateTime(2026, 11, 18, 13, 45, 0);
        Strings.CurrentLanguage = "en";
        Assert.Equal("12.5% left", Strings.Format("{0}% left", 12.5, "ignored"));
        Assert.Equal("Stops at 13:45 (in 10 min)", Strings.Format("Stops at {0:HH:mm} (in {1} min)", date, 10));
        Assert.Equal("Wed, Nov 18 · 13:45", date.ToString("ddd, MMM d · HH:mm", Strings.Culture));
        Assert.Equal("plain", Strings.Format("plain", 1));
        Assert.Equal("7", Strings.Format("{0}", 7));
        Assert.Throws<FormatException>(() => Strings.Format("{0}% left"));
        Strings.CurrentLanguage = "it";
        Assert.Equal("12,5% residua", Strings.Format("{0}% left", 12.5));
        Assert.Equal("Si ferma alle 13:45 (tra 10 min)", Strings.Format("Stops at {0:HH:mm} (in {1} min)", date, 10));
        Assert.Equal("Planwharf non si \u00e8 avviato entro 3 secondi. Consulta log.txt.",
            Strings.Format("Planwharf did not start within {0:0} seconds. See {1}.", 3.2, "log.txt"));
        Assert.Equal("mer, nov 18 · 13:45", date.ToString("ddd, MMM d · HH:mm", Strings.Culture));
        Assert.Equal("1,5", 1.5.ToString("0.0", Strings.Culture));
    }

    [Fact]
    public void Every_catalog_entry_formats_in_both_languages()
    {
        var date = new DateTime(2026, 11, 18, 13, 45, 0);
        foreach (var language in new[] { "en", "it" })
        {
            Strings.CurrentLanguage = language;
            foreach (var key in Dictionary("English").Keys)
            {
                var count = System.Text.CompositeFormat.Parse(Strings.T(key)).MinimumArgumentCount;
                var numbers = Enumerable.Range(0, count).Select(i => (object)(i + 1)).ToArray();
                string formatted;
                try { formatted = count == 0 ? Strings.Format(key) : Strings.Format(key, numbers); }
                catch (FormatException)
                {
                    var mixed = Enumerable.Range(0, count).Select(i => i == 0 ? (object)date : i).ToArray();
                    formatted = Strings.Format(key, mixed);
                }
                Assert.False(string.IsNullOrWhiteSpace(formatted), language + " " + key);
            }
        }
    }

    [Fact]
    public async Task Readers_keep_a_valid_language_while_it_switches()
    {
        var failures = 0;
        var gate = new Barrier(6);
        var workers = Enumerable.Range(0, 6).Select(i => Task.Run(() =>
        {
            gate.SignalAndWait();
            for (var n = 0; n < 300; n++)
            {
                try
                {
                    if (i == 0) Strings.CurrentLanguage = n % 2 == 0 ? "it" : "en-US";
                    else
                    {
                        var label = Strings.T("Language");
                        if (label is not ("Language" or "Lingua")) Interlocked.Increment(ref failures);
                        var percent = Strings.Format("{0}% left", 12.5);
                        if (percent is not ("12.5% left" or "12,5% residua")) Interlocked.Increment(ref failures);
                        if (Strings.Culture.Name is not ("en-US" or "it-IT")) Interlocked.Increment(ref failures);
                    }
                }
                catch { Interlocked.Increment(ref failures); }
            }
        })).ToArray();
        await Task.WhenAll(workers);
        Assert.Equal(0, failures);
        Assert.Contains(Strings.CurrentLanguage, new[] { "en", "it" });
    }
}
