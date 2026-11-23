// Tests for PhoneVoice.cs.
using Xunit;

namespace Planwharf.Engine.Tests;

public class PhoneVoiceTests
{
    [Fact]
    public void NormalizeLanguage_EnAndIt_NormalizesVariants()
    {
        Assert.Equal("en", PhoneVoice.NormalizeLanguage("en"));
        Assert.Equal("en", PhoneVoice.NormalizeLanguage("en-US"));
        Assert.Equal("en", PhoneVoice.NormalizeLanguage("English"));
        Assert.Equal("it", PhoneVoice.NormalizeLanguage("it"));
        Assert.Equal("it", PhoneVoice.NormalizeLanguage("it_IT"));
        Assert.Equal("it", PhoneVoice.NormalizeLanguage("Italiano"));
        Assert.Null(PhoneVoice.NormalizeLanguage("es"));
    }

    [Fact]
    public void Say_SubstitutesVariablesAcrossEnAndItCatalogs()
    {
        var en = PhoneVoice.Say("en", "stopConfirm", new Dictionary<string, string> { ["name"] = "Planwharf" });
        Assert.Contains("Stop the crew for \"Planwharf\"?", en);

        var it = PhoneVoice.Say("it", "stopConfirm", new Dictionary<string, string> { ["name"] = "Planwharf" });
        Assert.Contains("Fermo l'equipaggio di \"Planwharf\"?", it);
    }

    [Fact]
    public void TelegramAlertLabel_MapsStateIconsAcrossLanguages()
    {
        Assert.Equal("Done \u2705", PhoneVoice.TelegramAlertLabel("en", "done"));
        Assert.Equal("Fatto \u2705", PhoneVoice.TelegramAlertLabel("it", "done"));
        Assert.Equal("Failed \u274c", PhoneVoice.TelegramAlertLabel("en", "failed"));
        Assert.Equal("Non riuscito \u274c", PhoneVoice.TelegramAlertLabel("it", "failed"));
    }

    [Fact]
    public void FitTelegram_ClosesUnclosedTagsAndTruncatesTo4096Characters()
    {
        var shortText = "<b>Hello world</b>";
        Assert.Equal(shortText, PhoneVoice.FitTelegram(shortText));

        var longHtml = "<b>" + new string('a', 4100) + "</b>";
        var fitted = PhoneVoice.FitTelegram(longHtml);
        Assert.True(fitted.Length <= 4096);
        Assert.EndsWith("</b>\n…", fitted);
    }
}
