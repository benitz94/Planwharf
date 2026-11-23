// Port of engine/phone-voice.cjs.
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class PhoneVoice
{
    public static readonly string[] SupportedLanguages = ["en", "it"];

    private static readonly Dictionary<string, Dictionary<string, string>> AlertLabels = new(StringComparer.Ordinal)
    {
        ["en"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["done"] = "Done \u2705",
            ["failed"] = "Failed \u274c",
            ["needs_owner"] = "Needs your attention \ud83d\udd14",
            ["stopped"] = "Stopped \u23f9",
            ["started"] = "Started \u25b6\ufe0f",
            ["resumed"] = "Resumed \u25b6\ufe0f",
            ["blocked"] = "Blocked \u26a0\ufe0f",
            ["verify"] = "Verify started \ud83d\udd0e",
            ["switched"] = "Switched model \ud83d\udd01",
            ["lead"] = "New lead \ud83d\udc51",
            ["restarted"] = "Step restarted \ud83d\udd01",
            ["updated"] = "Updated \u2022"
        },
        ["it"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["done"] = "Fatto \u2705",
            ["failed"] = "Non riuscito \u274c",
            ["needs_owner"] = "Serve il tuo intervento \ud83d\udd14",
            ["stopped"] = "Fermo \u23f9",
            ["started"] = "Partito \u25b6\ufe0f",
            ["resumed"] = "Ripreso \u25b6\ufe0f",
            ["blocked"] = "Bloccato \u26a0\ufe0f",
            ["verify"] = "Controllo avviato \ud83d\udd0e",
            ["switched"] = "Modello cambiato \ud83d\udd01",
            ["lead"] = "Nuovo lead \ud83d\udc51",
            ["restarted"] = "Passo riavviato \ud83d\udd01",
            ["updated"] = "Aggiornato \u2022"
        }
    };

    private static readonly Dictionary<string, Dictionary<string, string>> PlainAlerts = new(StringComparer.Ordinal)
    {
        ["en"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["done"] = "The work is finished.",
            ["failed"] = "The work did not succeed. The crew has stopped.",
            ["needs_owner"] = "The crew is waiting for you.",
            ["stopped"] = "The crew is paused.",
            ["started"] = "The crew has started.",
            ["resumed"] = "The crew is working again.",
            ["blocked"] = "The crew cannot move until Planwharf has a worker and a place to work.",
            ["verify"] = "A different model is checking the work.",
            ["switched"] = "The crew changed who is doing this step.",
            ["lead"] = "Another model now leads the crew.",
            ["restarted"] = "The same step started again.",
            ["updated"] = "The project changed."
        },
        ["it"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["done"] = "Il lavoro \u00e8 finito.",
            ["failed"] = "Il lavoro non \u00e8 riuscito. L'equipaggio si \u00e8 fermato.",
            ["needs_owner"] = "L'equipaggio aspetta te.",
            ["stopped"] = "L'equipaggio \u00e8 in pausa.",
            ["started"] = "L'equipaggio \u00e8 partito.",
            ["resumed"] = "L'equipaggio lavora di nuovo.",
            ["blocked"] = "L'equipaggio non pu\u00f2 muoversi finch\u00e9 Planwharf non ha un lavoratore e un posto dove lavorare.",
            ["verify"] = "Un altro modello sta controllando il lavoro.",
            ["switched"] = "L'equipaggio ha cambiato chi esegue questo passo.",
            ["lead"] = "Un altro modello ora guida l'equipaggio.",
            ["restarted"] = "Lo stesso passo \u00e8 ricominciato.",
            ["updated"] = "Il progetto \u00e8 cambiato."
        }
    };

    private static readonly Dictionary<string, Dictionary<string, string>> StateLabels = new(StringComparer.Ordinal)
    {
        ["en"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Working"] = "Working",
            ["Done"] = "Done",
            ["Failed"] = "Failed",
            ["Stopped"] = "Stopped",
            ["Waiting on you"] = "Waiting on you",
            ["Blocked (no usage)"] = "Blocked (no usage)",
            ["Idle"] = "Idle"
        },
        ["it"] = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Working"] = "Al lavoro",
            ["Done"] = "Fatto",
            ["Failed"] = "Non riuscito",
            ["Stopped"] = "Fermo",
            ["Waiting on you"] = "In attesa di te",
            ["Blocked (no usage)"] = "Bloccato (senza utilizzo)",
            ["Idle"] = "A riposo"
        }
    };

    public static string? NormalizeLanguage(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        var basePart = text.Split(new[] { '-', '_' }, 2)[0];
        if (basePart == "en" || text is "english" or "inglese") return "en";
        if (basePart == "it" || text is "italian" or "italiano") return "it";
        return null;
    }

    public static string LanguageOrDefault(string? value) => NormalizeLanguage(value) ?? "en";

    public static string TelegramAlertLabel(string language, string type)
    {
        var lang = LanguageOrDefault(language);
        if (AlertLabels[lang].TryGetValue(type, out var label)) return label;
        return AlertLabels[lang]["updated"];
    }

    public static string PlainAlert(string language, string type)
    {
        var lang = LanguageOrDefault(language);
        if (PlainAlerts[lang].TryGetValue(type, out var label)) return label;
        return PlainAlerts[lang]["updated"];
    }

    public static string StateLabel(string language, string state)
    {
        var lang = LanguageOrDefault(language);
        if (StateLabels[lang].TryGetValue(state, out var label)) return label;
        return state;
    }

    private static readonly Dictionary<string, (string en, string it)> Catalog = new(StringComparer.Ordinal)
    {
        ["pairing"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Harbor Connected</b>\nPlanwharf connected and standing by.\nYou can now use the deck buttons or commands:\n• /status — projects and progress\n• /stop &lt;project&gt; — stop the crew (requires confirmation)\n• /resume &lt;project&gt; — resume work\n• /details — the short technical note, only when you ask\n• /language — English or Italiano",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Porto collegato</b>\nPlanwharf è collegato e in attesa.\nPuoi usare i pulsanti di coperta o i comandi:\n• /status — progetti e avanzamento\n• /stop &lt;progetto&gt; — ferma l'equipaggio (serve conferma)\n• /resume &lt;progetto&gt; — riprendi il lavoro\n• /details — la nota tecnica breve, solo se la chiedi\n• /language — English o Italiano"
        ),
        ["help"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Harbor Guide</b>\nUse the deck buttons, or:\n• /status — projects and progress\n• /stop &lt;project&gt; — stop the crew (requires confirmation)\n• /resume &lt;project&gt; — resume work\n• /details — the short technical note from the last problem\n• /language — English or Italiano\nYou can also ask in plain words. The buttons do the same work as the commands.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Guida del porto</b>\nUsa i pulsanti di coperta, oppure:\n• /status — progetti e avanzamento\n• /stop &lt;progetto&gt; — ferma l'equipaggio (serve conferma)\n• /resume &lt;progetto&gt; — riprendi il lavoro\n• /details — la nota tecnica breve dell'ultimo problema\n• /language — English o Italiano\nPuoi anche chiedere con parole semplici. I pulsanti fanno lo stesso lavoro dei comandi."
        ),
        ["stopConfirm"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Confirm Stop Order</b>\n⏹ Stop the crew for \"{name}\"? Reply \"yes\" to confirm.\nThe crew pauses only after you answer yes.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Conferma la fermata</b>\n⏹ Fermo l'equipaggio di \"{name}\"? Rispondi \"yes\" o \"sì\" per confermare.\nL'equipaggio si ferma solo dopo la tua risposta."
        ),
        ["stopConfirmTap"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Confirm Stop Order</b>\n⏹ Stop this crew? Reply \"yes\" to confirm or tap below.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Conferma la fermata</b>\n⏹ Fermo questo equipaggio? Rispondi \"yes\" o \"sì\", oppure tocca sotto."
        ),
        ["crewStopped"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Crew Stopped</b>\n⏹ The crew for \"{name}\" has been stopped.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Equipaggio fermo</b>\n⏹ L'equipaggio di \"{name}\" è stato fermato."
        ),
        ["stopCancelled"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Stop Cancelled</b>\nStop cancelled. Crew will continue work.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Fermata annullata</b>\nFermata annullata. L'equipaggio continua il lavoro."
        ),
        ["couldNotCompleteStop"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Harbor Notice</b>\n⚠️ Planwharf could not complete that.\nThe crew is still working. Nothing was changed.\nAsk for technical details with /details.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Avviso dal porto</b>\n⚠️ Planwharf non è riuscito a fermare l'equipaggio.\nL'equipaggio sta ancora lavorando. Non è cambiato nulla.\nChiedi i dettagli tecnici con /details."
        ),
        ["couldNotCompleteResume"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Harbor Notice</b>\n⚠️ Planwharf could not complete that.\nThe crew is still paused. Nothing was changed.\nAsk for technical details with /details.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Avviso dal porto</b>\n⚠️ Planwharf non è riuscito a far ripartire l'equipaggio.\nL'equipaggio è ancora in pausa. Non è cambiato nulla.\nChiedi i dettagli tecnici con /details."
        ),
        ["couldNotReadProjects"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Harbor Notice</b>\n⚠️ Planwharf could not read the projects. Try again.\nAsk for technical details with /details.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Avviso dal porto</b>\n⚠️ Planwharf non riesce a leggere i progetti. Riprova.\nChiedi i dettagli tecnici con /details."
        ),
        ["stopNone"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Stop Order</b>\nNo projects available to stop.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Ordine di fermata</b>\nNon ci sono progetti da fermare."
        ),
        ["stopSpecify"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Stop Order</b>\nPlease specify a project: /stop &lt;name&gt;\n<b>Projects in port:</b> {names}",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Ordine di fermata</b>\nIndica un progetto: /stop &lt;nome&gt;\n<b>Progetti in porto:</b> {names}"
        ),
        ["resumeNone"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Resume Order</b>\nNo projects available to resume.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Ordine di ripresa</b>\nNon ci sono progetti da riprendere."
        ),
        ["resumeSpecify"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Resume Order</b>\nPlease specify a project: /resume &lt;name&gt;\n<b>Projects in port:</b> {names}",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Ordine di ripresa</b>\nIndica un progetto: /resume &lt;nome&gt;\n<b>Progetti in porto:</b> {names}"
        ),
        ["crewResumed"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Crew Resumed</b>\n▶️ The crew for \"{name}\" has resumed.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Equipaggio ripartito</b>\n▶️ L'equipaggio di \"{name}\" è ripartito."
        ),
        ["memoryCleared"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Logbook Cleared</b>\nThis chat memory is clear. Commands are unchanged.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Giornale di bordo pulito</b>\nLa memoria di questa chat è pulita. I comandi restano gli stessi."
        ),
        ["unrecognized"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Unrecognized Command</b>\nUnrecognized command. Use /status, /stop &lt;project&gt;, or /resume &lt;project&gt;.\nOr ask in plain words. /details shows the last technical note.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Comando non riconosciuto</b>\nComando non riconosciuto. Usa /status, /stop &lt;progetto&gt; o /resume &lt;progetto&gt;.\nOppure chiedi con parole semplici. /details mostra l'ultima nota tecnica."
        ),
        ["attachments"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Harbor Notice</b>\n📎 Attachments are not accepted. Use the deck buttons.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Avviso dal porto</b>\n📎 Gli allegati non sono accettati. Usa i pulsanti di coperta."
        ),
        ["statusEmpty"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Harbor Status</b>\nNo projects in port.\nWhen a project is running, its step appears here.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Stato del porto</b>\nNessun progetto in porto.\nQuando un progetto è in corso, il suo passo compare qui."
        ),
        ["statusHeader"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Harbor Status</b>\nHere is where every project stands.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Stato del porto</b>\nEcco a che punto è ogni progetto."
        ),
        ["progressStep"] = (
            "The crew is on step {current} of {total}.",
            "L'equipaggio è al passo {current} di {total}."
        ),
        ["detailsNone"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Technical details</b>\nNothing technical is waiting. The last command finished without an error.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Dettagli tecnici</b>\nNon c'è nulla di tecnico in attesa. L'ultimo comando è finito senza errori."
        ),
        ["details"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Technical details</b>\nCode: {code}\nNote: {note}\nFile names, paths and secrets are left out.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Dettagli tecnici</b>\nCodice: {code}\nNota: {note}\nNomi di file, percorsi e segreti sono esclusi."
        ),
        ["languageShow"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Language</b>\nMessages are in {languageName}.\nSend /language en or /language it.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Lingua</b>\nI messaggi sono in {languageName}.\nInvia /language en o /language it."
        ),
        ["languageSet"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Language</b>\nMessages are now in {languageName}. Commands stay the same.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Lingua</b>\nI messaggi ora sono in {languageName}. I comandi restano gli stessi."
        ),
        ["languageUnknown"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Language</b>\nPlanwharf knows English and Italian.\nSend /language en or /language it.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Lingua</b>\nPlanwharf conosce inglese e italiano.\nInvia /language en o /language it."
        ),
        ["companionOffline"] = (
            "🏴‍☠️ Ahoy, Captain!\n\n<b>Harbor Companion Offline</b>\n⚠️ The local model is not answering right now. {reasonLine}\nDeck commands still work:\n• /status\n• /stop &lt;project&gt;\n• /resume &lt;project&gt;\nAsk for technical details with /details.",
            "🏴‍☠️ Ahoy, Capitano!\n\n<b>Compagno di porto assente</b>\n⚠️ Il modello locale non risponde in questo momento. {reasonLine}\nI comandi di coperta funzionano ancora:\n• /status\n• /stop &lt;progetto&gt;\n• /resume &lt;progetto&gt;\nChiedi i dettagli tecnici con /details."
        ),
        ["conversationExtra"] = (
            "Speak warmly, like a first mate helping a captain who does not write code. Say what the progress means in everyday words. When something goes wrong, say what the captain can do next. Technical details stay out unless the captain asks.",
            "The captain selected Italian. Answer in Italian. Parla con parole semplici, come un nostromo che aiuta un capitano che non scrive codice. Spiega l'avanzamento come passi, non come codice. Se qualcosa non va, di' cosa può fare il capitano. I dettagli tecnici restano fuori finché il capitano non li chiede."
        )
    };

    private static string Fill(string template, IDictionary<string, string>? vars)
    {
        if (vars == null || string.IsNullOrEmpty(template)) return template;
        return Regex.Replace(template, @"\{([a-zA-Z0-9]+)\}", match =>
        {
            var key = match.Groups[1].Value;
            return vars.TryGetValue(key, out var val) ? val : "";
        });
    }

    public static string Say(string language, string key, IDictionary<string, string>? vars = null)
    {
        var lang = LanguageOrDefault(language);
        if (!Catalog.TryGetValue(key, out var row)) return "";
        var template = lang == "it" ? row.it : row.en;
        return Fill(template, vars);
    }

    public static string ReasonLine(string language, string reason)
    {
        var lang = LanguageOrDefault(language);
        var code = reason ?? "";
        if (lang == "it")
        {
            if (code == "timeout") return "Ha impiegato troppo tempo a rispondere.";
            if (code == "unreachable") return "L'aiutante su questo computer non ha risposto.";
            if (code == "empty") return "Ha risposto senza testo.";
            if (code.StartsWith("HTTP")) return "Ha rifiutato la domanda.";
            return "L'aiutante su questo computer non ha risposto.";
        }
        else
        {
            if (code == "timeout") return "It took too long to answer.";
            if (code == "unreachable") return "The helper on this computer did not answer.";
            if (code == "empty") return "It answered with nothing.";
            if (code.StartsWith("HTTP")) return "It refused the question.";
            return "The helper on this computer did not answer.";
        }
    }

    public static string CompanionOffline(string language, string reason)
    {
        return Say(language, "companionOffline", new Dictionary<string, string>
        {
            ["reasonLine"] = ReasonLine(language, reason)
        });
    }

    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "b", "i", "u", "s", "code", "pre", "a"
    };

    private static List<string> OpenTagsIn(string str)
    {
        var stack = new List<string>();
        var re = new Regex(@"</?([a-zA-Z0-9-]+)(?:\s[^<>]*)?>", RegexOptions.ECMAScript);
        foreach (Match match in re.Matches(str))
        {
            var name = match.Groups[1].Value.ToLowerInvariant();
            if (!AllowedTags.Contains(name)) continue;
            var closing = match.Value.StartsWith("</");
            var selfClosing = match.Value.EndsWith("/>");
            if (closing)
            {
                var idx = stack.LastIndexOf(name);
                if (idx != -1) stack.RemoveAt(idx);
            }
            else if (!selfClosing)
            {
                stack.Add(name);
            }
        }
        return stack;
    }

    public static string FitTelegram(string text)
    {
        var value = text ?? "";
        if (value.Length <= 4096) return value;
        const string note = "\n…";
        int targetLen = 4096 - note.Length;

        for (int iter = 0; iter < 10; iter++)
        {
            if (targetLen <= 0) break;
            var cut = value.Length > targetLen ? value[..targetLen] : value;
            if (cut.Length > 0 && char.IsHighSurrogate(cut[^1]))
                cut = cut[..^1];

            var lastOpenTag = cut.LastIndexOf('<');
            var lastCloseTag = cut.LastIndexOf('>');
            if (lastOpenTag > lastCloseTag) cut = cut[..lastOpenTag];

            var lastAmp = cut.LastIndexOf('&');
            var lastSemi = cut.LastIndexOf(';');
            if (lastAmp > lastSemi && cut.Length - lastAmp < 10) cut = cut[..lastAmp];

            var line = cut.LastIndexOf('\n');
            var kept = (line > targetLen - 600 && line > 0) ? cut[..line] : cut;
            if (kept.Length > 0 && char.IsHighSurrogate(kept[^1]))
                kept = kept[..^1];

            var open = OpenTagsIn(kept);
            open.Reverse();
            var closing = string.Concat(open.Select(t => "</" + t + ">"));
            if (kept.Length + closing.Length + note.Length <= 4096)
                return kept + closing + note;

            targetLen = 4096 - note.Length - closing.Length - 10;
        }

        var fallback = value[..(4096 - note.Length)];
        if (fallback.Length > 0 && char.IsHighSurrogate(fallback[^1]))
            fallback = fallback[..^1];
        return fallback + note;
    }

    public static bool IsDetailsRequest(string text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        var command = Regex.Replace(value, @"^/([a-z0-9]+)(?:@[A-Za-z0-9_]+)?", m => "/" + m.Groups[1].Value.ToLowerInvariant());
        if (command is "/details" or "/dettagli" || command.StartsWith("/details ") || command.StartsWith("/dettagli "))
            return true;

        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            "details", "show details", "technical details", "show technical details",
            "technical note", "show technical note", "dettagli", "mostra dettagli",
            "mostra i dettagli", "dettagli tecnici", "mostra dettagli tecnici", "nota tecnica"
        };
        return set.Contains(value);
    }
}
