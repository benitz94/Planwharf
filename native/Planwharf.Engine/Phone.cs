// Port of engine/phone.cjs.
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class Phone
{
    internal static string S(JsonNode? n) => n is null ? "" : n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();
    internal static bool B(JsonNode? n) => n is not null && S(n) is not ("false" or "0" or "");
    internal static double N(JsonNode? n) => n is null ? 0 : S(n) == "true" ? 1 : S(n) == "false" || S(n).Trim() == "" ? 0 : double.TryParse(S(n), CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    internal static JsonNode? Copy(JsonNode? n) => n?.DeepClone();
    internal static IEnumerable<JsonNode> Items(JsonNode? n) => n is JsonArray a ? a.OfType<JsonNode>() : [];
    static string R(string text, string pattern, string replacement, bool insensitive = false) => Regex.Replace(text, pattern, replacement, RegexOptions.ECMAScript | (insensitive ? RegexOptions.IgnoreCase : RegexOptions.None));
    public static string SanitizeText(string? text)
    {
        var clean = R(text ?? "", @"[\u0000-\u001f\u007f]+", " ");
        clean = R(clean, "[a-zA-Z]:\\\\[^\\s\"'<>]+", "[path]");
        clean = R(clean, "(?:^|\\s)(?:/|\\./)[^\\s\"'<>]+", " [path]");
        clean = R(clean, @"\b[\w-]+\.(?:cjs|mjs|js|ts|cs|json|md|py|sh|html|css|txt)\b", "[file]", true);
        return R(clean, @"(?:api[_-]?key|token|secret|password|bearer|ghp_[a-zA-Z0-9]+)\s*[:=]\s*[^\s,;]+", "[redacted]", true).Trim();
    }
    public static string SanitizeGoalTitle(string? title)
    {
        var text = SanitizeText((title ?? "").Split('\n')[0].Trim());
        return text.Length > 80 ? text[..77] + "..." : text.Length == 0 ? "Work" : text;
    }
    public static string? FormatDuration(double? minutes)
    {
        if (minutes is null || double.IsNaN(minutes.Value) || minutes < 0) return null;
        var m = Math.Floor(minutes.Value + .5);
        return m < 60 ? $"{m}m" : m % 60 > 0 ? $"{Math.Floor(m / 60)}h {m % 60}m" : $"{Math.Floor(m / 60)}h";
    }
    public static string EscapeHtml(string? text) => PhoneOllama.EscapeHtml(text);
    public static string? MaskToken(string? token) => string.IsNullOrEmpty(token) ? null : token.Trim().Length <= 8 ? "***" : token.Trim()[..4] + "..." + token.Trim()[^4..];
    public static string GenerateStartCode(Func<int, int, int>? random = null) => "pw-" + (random ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32)(100000, 999999);
    public static string? AlertForStatus(string? previous, string? current)
    {
        if (string.IsNullOrEmpty(previous) || previous == current) return null;
        return current switch
        {
            "complete" => "done", "failed" => "failed", "checkpointed" => "stopped",
            "watching" => previous is "checkpointed" or "failed" ? "resumed" : "started",
            "waiting_on_you" => "needs_owner", "blocked_no_worker" or "blocked_no_folder" => "blocked", _ => null
        };
    }
    public static JsonObject? RunningStep(JsonNode? project)
    {
        var part = Items(project?["dispatch"]).FirstOrDefault(p => S(p["recordedSubTaskStatus"]) == "RUNNING" || B(p["alive"]));
        if (part is null) return null;
        return new() { ["role"] = Regex.IsMatch(S(part["slice"] ?? part["title"]), "verify", RegexOptions.IgnoreCase) ? "Verify" : "Build", ["model"] = S(part["label"] ?? part["modelId"]), ["attempt"] = S(part["attemptId"]) };
    }
    public static string? AlertForStep(JsonNode? previous, JsonNode? current)
    {
        if (previous is null || current is null) return null;
        if (S(previous["role"]) != "Verify" && S(current["role"]) == "Verify") return "verify";
        if (B(previous["model"]) && B(current["model"]) && S(previous["model"]) != S(current["model"])) return "switched";
        return B(previous["attempt"]) && B(current["attempt"]) && S(previous["attempt"]) != S(current["attempt"]) ? "restarted" : null;
    }
    public static string FormatProgressBar(JsonNode? step, JsonNode? total = null)
    {
        double s, t;
        if (step is JsonObject) { s = N(step["step"]); t = N(step["total"]); }
        else if (S(step).Contains('/')) { var parts = S(step).Split('/'); s = N(JsonValue.Create(parts[0])); t = N(JsonValue.Create(parts[1])); }
        else { s = N(step); t = N(total); }
        if (!double.IsFinite(s) || !double.IsFinite(t) || t <= 0 || s < 0) return "";
        var width = (int)Math.Floor(Math.Min(t, 10));
        var filled = Math.Clamp((int)Math.Floor((t <= 10 ? Math.Min(s, t) : Math.Min(s, t) / t * 10) + .5), 0, width);
        return width == 0 ? "" : new string('■', filled) + new string('□', width - filled) + $" {s.ToString(CultureInfo.InvariantCulture)}/{t.ToString(CultureInfo.InvariantCulture)}";
    }
    public static JsonObject MainKeyboard() => JsonNode.Parse("""{"keyboard":[[{"text":"📊 Status"},{"text":"⏹ Stop"},{"text":"▶️ Resume"}]],"resize_keyboard":true,"is_persistent":true}""")!.AsObject();
    public static JsonObject StatusKeyboard(JsonNode? projects = null, string? language = null)
    {
        var it = PhoneVoice.LanguageOrDefault(language) == "it";
        var rows = new JsonArray();
        foreach (var p in Items(projects))
        {
            var id = S(p["id"]); if (id.Length is 0 or > 48) continue;
            var name = SanitizeText(S(p["name"] ?? p["id"])); name = name[..Math.Min(name.Length, 24)];
            var status = S(p["status"]);
            if (status == "watching") rows.Add((JsonNode)new JsonArray(InlineButton((it ? "⏹ Ferma " : "⏹ Stop ") + name, "stop:" + id)));
            else if (status is "checkpointed" or "failed") rows.Add((JsonNode)new JsonArray(InlineButton((it ? "▶️ Riprendi " : "▶️ Resume ") + name, "resume:" + id)));
        }
        rows.Add((JsonNode)new JsonArray(InlineButton(it ? "🔄 Aggiorna" : "🔄 Refresh", "status")));
        return new() { ["inline_keyboard"] = rows };
    }
    internal static JsonObject InlineButton(string text, string data) => new() { ["text"] = text, ["callback_data"] = data };
    static string Saving(JsonNode? mode, string lang)
    {
        var m = R(S(mode).Trim().ToLowerInvariant().Replace('_', '-'), @"\s+", " ");
        return m switch { "safe" or "sicura" or "sicuro" => lang == "it" ? "Sicura" : "Safe", "ultra-safe" or "ultra safe" or "ultrasafe" or "ultra sicura" or "ultra sicuro" => lang == "it" ? "Ultra sicura" : "Ultra safe", _ => lang == "it" ? "Normale" : "Normal" };
    }
    static readonly HashSet<string> AlertTypes = ["done", "failed", "needs_owner", "stopped", "started", "resumed", "blocked", "verify", "switched", "lead", "restarted", "updated"];
    public static string FormatAlertMessage(JsonObject input)
    {
        var lang = PhoneVoice.LanguageOrDefault(S(input["language"])); var it = lang == "it"; var type = S(input["type"]);
        var known = AlertTypes.Contains(type);
        var label = known ? PhoneVoice.TelegramAlertLabel(lang, type) : it ? "Aggiornato" : "Updated";
        var msg = $"Planwharf: {(SanitizeText(S(input["projectName"])) is { Length: > 0 } project ? project : "Project")}\n{(it ? "Obiettivo" : "Goal")}: {SanitizeGoalTitle(S(input["goalTitle"]))}\n{(it ? "Stato" : "Status")}: {label}";
        if (type == "done" && input["durationMinutes"] is not null && FormatDuration(N(input["durationMinutes"])) is { } duration) msg += $"\n{(it ? "Durata" : "Duration")}: {duration}";
        if (input["timeLeft"] is JsonObject time && time["minutes"] is not null)
        {
            var estimate = it ? $"Circa {S(time["minutes"])}m rimasti" : $"About {S(time["minutes"])}m left";
            if (B(time["longerThanUsual"])) estimate += it ? " (più del solito)" : " (longer than usual)";
            if (B(time["sure"])) estimate += " · " + S(time["sure"]);
            msg += $"\n{(it ? "Stima" : "Estimate")}: {estimate}";
        }
        if (B(input["detail"])) msg += "\n" + SanitizeText(S(input["detail"]));
        var plain = PhoneVoice.PlainAlert(lang, known ? type : "updated");
        if (plain != "") msg += "\n" + plain;
        return msg + $"\n{(it ? "Risparmio" : "Saving")}: {Saving(input["savingMode"], lang)}";
    }
    static readonly JsonObject Voice = JsonNode.Parse("""
{
  "en": {
    "ahoy": "AHOY, CAPTAIN!",
    "project": "Ship",
    "mission": "Mission",
    "status": "Status",
    "timeLeft": "Time left",
    "duration": "Voyage time",
    "lines": {
      "started": [
        "🚀 The crew has set sail on a new mission!",
        "🦜 The crew is hard at work. I'll keep you posted, Captain!"
      ],
      "resumed": [
        "⛵ Back at sea!",
        "🦜 The crew picked up where it left off."
      ],
      "done": [
        "🏆 Mission accomplished!",
        "🍻 The work is in the hold. Inspect the treasure and push it when you like it."
      ],
      "failed": [
        "🌊 Rough seas: the mission hit a snag.",
        "🛟 The crew has stopped. Have a look when you can, Captain."
      ],
      "needs_owner": [
        "📯 The crew awaits your orders!",
        "🗺️ Open Planwharf and tell them which way to go."
      ],
      "stopped": [
        "⚓ The crew has dropped anchor.",
        "🦜 Say /resume when you want to sail again."
      ],
      "blocked": [
        "🪨 Stuck on the rocks!",
        "🛠️ The crew needs a worker and a place to work."
      ],
      "verify": [
        "🔭 A second pair of eyes is checking the work.",
        "🦜 Another model is inspecting every plank."
      ],
      "switched": [
        "🔁 Change of watch!",
        "🦜 Another sailor took over this step."
      ],
      "lead": [
        "👑 A new navigator takes the helm!",
        "🦜 The new lead steers the crew from here."
      ],
      "restarted": [
        "🔁 Same step, fresh start.",
        "🦜 The crew is trying again."
      ],
      "updated": [
        "🧭 News from the deck.",
        "🦜 I'll keep you posted, Captain!"
      ]
    }
  },
  "it": {
    "ahoy": "AHOY, CAPITANO!",
    "project": "Nave",
    "mission": "Missione",
    "status": "Stato",
    "timeLeft": "Tempo rimasto",
    "duration": "Durata del viaggio",
    "lines": {
      "started": [
        "🚀 L'equipaggio ha preso il largo per una nuova missione!",
        "🦜 La ciurma è al lavoro. Ti tengo aggiornato, Capitano!"
      ],
      "resumed": [
        "⛵ Di nuovo in mare!",
        "🦜 La ciurma riparte da dove si era fermata."
      ],
      "done": [
        "🏆 Missione compiuta!",
        "🍻 Il bottino è nella stiva. Controllalo e fai il push quando ti piace."
      ],
      "failed": [
        "🌊 Mare grosso: la missione si è incagliata.",
        "🛟 La ciurma si è fermata. Dai un'occhiata quando puoi, Capitano."
      ],
      "needs_owner": [
        "📯 La ciurma aspetta i tuoi ordini!",
        "🗺️ Apri Planwharf e indica la rotta."
      ],
      "stopped": [
        "⚓ La ciurma ha gettato l'ancora.",
        "🦜 Scrivi /resume quando vuoi ripartire."
      ],
      "blocked": [
        "🪨 Incagliati sugli scogli!",
        "🛠️ Alla ciurma servono un marinaio e un posto dove lavorare."
      ],
      "verify": [
        "🔭 Un secondo paio d'occhi controlla il lavoro.",
        "🦜 Un altro modello ispeziona ogni asse."
      ],
      "switched": [
        "🔁 Cambio della guardia!",
        "🦜 Un altro marinaio ha preso questo passo."
      ],
      "lead": [
        "👑 Un nuovo navigatore prende il timone!",
        "🦜 Da qui guida la ciurma il nuovo lead."
      ],
      "restarted": [
        "🔁 Stesso passo, nuova partenza.",
        "🦜 La ciurma riprova."
      ],
      "updated": [
        "🧭 Notizie dal ponte.",
        "🦜 Ti tengo aggiornato, Capitano!"
      ]
    }
  }
}
""")!.AsObject();
    static string HtmlWithCode(string text) => R(EscapeHtml(text), "`([^`\\n]+)`", "<code>$1</code>");
    static string MissionTitle(string? title)
    {
        var first = Regex.Split(SanitizeGoalTitle(title), @"(?<=[.!?:;])\s", RegexOptions.ECMAScript)[0];
        first = R(first, @"[,;]?\s+(?:as|following|according to|seguendo|come)\b[^`]*`.*$", "", true);
        first = R(first, @"\s*(?:on|from|in|su|da)\s+(?:a\s+new\s+branch\s+)?`[^`]+`.*$", "", true);
        first = R(first, @"[,;:]\s*$", "").Trim();
        return first.Length == 0 ? "Work" : first;
    }
    public static JsonObject? CrewOfProject(JsonNode? project, JsonNode? models = null, JsonObject? previous = null)
    {
        if (project is null) return null;
        previous ??= new();
        var lead = S(project["leadLabel"]); if (lead == "") lead = S(Items(project["crew"]).FirstOrDefault(m => S(m["role"]) == "Orchestrator")?["name"]);
        var build = new JsonArray(); var verify = new JsonArray();
        foreach (var sub in Items(project["subTasks"]))
        {
            var name = S(Items(models).FirstOrDefault(m => JsonNode.DeepEquals(m["id"], sub["assignedModelId"]))?["label"] ?? sub["assignedModelId"]);
            var id = S(sub["id"]); var isVerify = Regex.IsMatch(S(sub["title"]), @"^Verify the work independently|^check\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
            var state = S(sub["status"]) switch { "COMPLETE" => "✅", "RUNNING" => "⚙️", "FAILED" => "❌", "BLOCKED" => "⚠️", _ => "" };
            (isVerify ? verify : build).Add((JsonNode)new JsonObject { ["id"] = Copy(sub["id"]), ["role"] = isVerify ? "verify" : "build", ["name"] = name, ["from"] = B(previous[id]) && S(previous[id]) != name ? S(previous[id]) : "", ["state"] = state });
        }
        return new() { ["lead"] = lead, ["leadFrom"] = B(previous["__lead"]) && S(previous["__lead"]) != lead ? S(previous["__lead"]) : "", ["build"] = build, ["verify"] = verify };
    }
    static IEnumerable<string> CrewLines(JsonNode? crew)
    {
        if (crew is null) yield break;
        string Safe(JsonNode? value) => EscapeHtml(SanitizeText(S(value)));
        if (B(crew["lead"])) yield return "👑 <b>Lead</b> " + (B(crew["leadFrom"]) ? Safe(crew["leadFrom"]) + " → " : "") + Safe(crew["lead"]);
        foreach (var kind in new[] { "build", "verify" }) foreach (var step in Items(crew[kind]))
            yield return (kind == "build" ? "🔨 <b>Build</b> " : "🔍 <b>Verify</b> ") + (B(step["from"]) ? Safe(step["from"]) + " → " : "") + Safe(step["name"]) + (B(step["state"]) ? " " + S(step["state"]) : "");
    }
    public static string FormatStatusMessage(JsonNode? projects = null, string? language = null)
    {
        var lang = PhoneVoice.LanguageOrDefault(language); var it = lang == "it"; var all = Items(projects).ToArray();
        if (all.Length == 0) return PhoneVoice.Say(lang, "statusEmpty");
        var sections = new List<string> { PhoneVoice.Say(lang, "statusHeader") };
        foreach (var p in all)
        {
            var status = S(p["status"]);
            var state = status switch { "watching" => "Working", "complete" => "Done", "failed" => "Failed", "checkpointed" => "Stopped", "waiting_on_you" => "Waiting on you", "blocked_no_worker" or "blocked_no_folder" => "Blocked (no usage)", "idle" or "" => "Idle", _ => SanitizeText(status) };
            var icon = state switch { "Working" => "🟢", "Done" => "✅", "Failed" => "❌", "Stopped" => "⏹", "Waiting on you" => "🔔", "Blocked (no usage)" => "⚠️", "Idle" => "⚪", _ => "•" };
            var details = status == "watching" && p["timeLeft"]?["minutes"] is { } minutes ? it ? $" · circa {EscapeHtml(S(minutes))}m rimasti" : $" · ~{EscapeHtml(S(minutes))}m left" : "";
            var mode = S(p["chatMode"]) == "talk" ? it ? "Chat" : "Talk" : it ? "Piano" : "Plan";
            var item = $"<b>{EscapeHtml(SanitizeText(S(p["name"] ?? p["id"] ?? JsonValue.Create("Project"))))}</b>\n{icon} <b>{(it ? "Stato" : "Status")}:</b> {EscapeHtml(PhoneVoice.StateLabel(lang, state))}{details}\n<b>{(it ? "Modalità" : "Mode")}:</b> {mode}\n<b>{(it ? "Risparmio" : "Saving")}:</b> {Saving(p["savingMode"], lang)}";
            var bar = FormatProgressBar(p["step"]);
            if (bar != "")
            {
                item += "\n" + bar;
                var fraction = Regex.Match(bar, @"(\d+(?:\.\d+)?)/(\d+(?:\.\d+)?)$", RegexOptions.ECMAScript);
                if (fraction.Success) item += "\n" + EscapeHtml(PhoneVoice.Say(lang, "progressStep", new Dictionary<string, string> { ["current"] = fraction.Groups[1].Value, ["total"] = fraction.Groups[2].Value }));
            }
            sections.Add(item);
        }
        return string.Join("\n\n", sections);
    }
    public static JsonNode? MatchProject(JsonNode? projects, string? query = null)
    {
        var all = Items(projects).ToArray(); var q = (query ?? "").Trim().ToLowerInvariant();
        if (q == "") return all.Length == 1 ? all[0] : null;
        var exact = all.Where(p => S(p["id"]).ToLowerInvariant() == q || S(p["name"]).ToLowerInvariant() == q).ToArray();
        if (exact.Length != 0) return exact.Length == 1 ? exact[0] : null;
        var partial = all.Where(p => S(p["id"]).Contains(q, StringComparison.OrdinalIgnoreCase) || S(p["name"]).Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray();
        return partial.Length == 1 ? partial[0] : null;
    }

    // Settings writes with unknown fields are deliberately gated: the reviewed sheet has not
    // authorized choosing between source serialization and the raw-byte preservation rule.
    public const string UnknownSettingsDecision = "Phone settings unknown-field persistence requires the reviewed owner decision.";
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> StoreGates = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    static object StoreGate(string directory) => StoreGates.GetOrAdd(Path.GetFullPath(directory), _ => new object());
    static JsonObject OllamaJson(OllamaSettings value) => new() { ["enabled"] = value.Enabled, ["baseUrl"] = value.BaseUrl, ["model"] = value.Model };
    public static JsonObject LoadPhoneSettings(string storeDir, Func<int, int, int>? random = null)
    {
        lock (StoreGate(storeDir))
        {
            var config = new JsonObject
            {
                ["ntfy"] = new JsonObject { ["enabled"] = false, ["topic"] = "", ["serverUrl"] = "https://ntfy.sh" },
                ["telegram"] = new JsonObject { ["enabled"] = false, ["startCode"] = GenerateStartCode(random), ["pairedChatId"] = null, ["pairedUsername"] = null, ["updateOffset"] = 0 },
                ["ollama"] = OllamaJson(PhoneOllama.DefaultSettings()), ["language"] = "en"
            };
            var corrupt = false;
            try
            {
                var path = Path.Combine(storeDir, "phone-settings.json");
                if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject raw)
                {
                    if (raw["ntfy"] is JsonObject ntfy)
                    {
                        config["ntfy"]!["enabled"] = B(ntfy["enabled"]);
                        config["ntfy"]!["topic"] = ntfy["topic"] is JsonValue v && v.TryGetValue<string>(out var topic) ? topic.Trim() : "";
                        config["ntfy"]!["serverUrl"] = ntfy["serverUrl"] is JsonValue u && u.TryGetValue<string>(out var url) && url.Trim() != "" ? url.Trim() : "https://ntfy.sh";
                    }
                    if (raw["telegram"] is JsonObject tg)
                    {
                        var target = config["telegram"]!;
                        target["enabled"] = B(tg["enabled"]);
                        if (tg["startCode"] is JsonValue code && code.TryGetValue<string>(out var start) && start.Trim() != "") target["startCode"] = start.Trim();
                        target["pairedChatId"] = tg["pairedChatId"] is null ? null : JsonValue.Create(S(tg["pairedChatId"]));
                        target["pairedUsername"] = tg["pairedUsername"] is null ? null : JsonValue.Create(S(tg["pairedUsername"]));
                        var offset = N(tg["updateOffset"]); target["updateOffset"] = double.IsFinite(offset) && offset == Math.Floor(offset) && offset >= 0 ? offset : 0;
                    }
                    if (raw["ollama"] is JsonObject) config["ollama"] = OllamaJson(PhoneOllama.NormalizeSettings(raw["ollama"]));
                    if (raw.ContainsKey("language")) config["language"] = PhoneVoice.LanguageOrDefault(S(raw["language"]));
                }
            }
            catch { corrupt = true; }
            string? token = null;
            try { var path = Path.Combine(storeDir, "telegram-bot.token"); if (File.Exists(path)) token = File.ReadAllText(path).Trim(); } catch { }
            config["telegramToken"] = string.IsNullOrEmpty(token) ? null : JsonValue.Create(token);
            config["corrupt"] = corrupt;
            return config;
        }
    }
    static void AssertKnownSettings(string storeDir)
    {
        var path = Path.Combine(storeDir, "phone-settings.json");
        JsonObject? raw;
        try { raw = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch { return; } // Source repairs corrupt input using defaults.
        if (raw is null) return;
        bool Unknown(JsonObject obj, params string[] names) => obj.Any(p => !names.Contains(p.Key, StringComparer.Ordinal));
        if (Unknown(raw, "ntfy", "telegram", "ollama", "language") ||
            raw["ntfy"] is JsonObject n && Unknown(n, "enabled", "topic", "serverUrl") ||
            raw["telegram"] is JsonObject t && Unknown(t, "enabled", "startCode", "pairedChatId", "pairedUsername", "updateOffset") ||
            raw["ollama"] is JsonObject o && Unknown(o, "enabled", "baseUrl", "model"))
            throw new InvalidOperationException(UnknownSettingsDecision);
    }
    static void WritePrivate(string path, string contents, Action<string>? restrict)
    {
        // Restrict before writing as well as afterwards, so an existing permissive file does
        // not expose a replacement token while ACLs are being updated.
        if (!File.Exists(path))
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var created = new FileStream(path, options);
        }
        void OwnerOnly()
        {
            if (!OperatingSystem.IsWindows()) { try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { } }
            else
            {
                try
                {
                    var info = new System.Diagnostics.ProcessStartInfo("icacls") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var arg in new[] { path, "/inheritance:r", "/grant:r", Environment.UserName + ":(R,W)" }) info.ArgumentList.Add(arg);
                    using var process = System.Diagnostics.Process.Start(info);
                    process?.WaitForExit();
                }
                catch { /* As in the source, missing ACL support leaves the user profile boundary. */ }
            }
        }
        (restrict ?? OwnerOnlyAdapter)(path);
        File.WriteAllText(path, contents, new System.Text.UTF8Encoding(false));
        (restrict ?? OwnerOnlyAdapter)(path);
        void OwnerOnlyAdapter(string _) => OwnerOnly();
    }
    public static JsonObject SavePhoneSettings(string storeDir, JsonObject settings, string? newToken = null,
        bool replaceToken = false, Func<int, int, int>? random = null, Action<string>? restrict = null)
    {
        lock (StoreGate(storeDir))
        {
            AssertKnownSettings(storeDir);
            Directory.CreateDirectory(storeDir);
            var current = LoadPhoneSettings(storeDir, random);
            var n = current["ntfy"]!.AsObject(); var t = current["telegram"]!.AsObject();
            var np = settings["ntfy"] as JsonObject; var tp = settings["telegram"] as JsonObject;
            if (np is not null)
            {
                if (np.ContainsKey("enabled")) n["enabled"] = B(np["enabled"]);
                if (np.ContainsKey("topic")) n["topic"] = (np["topic"] is null ? "null" : S(np["topic"])).Trim();
                if (np.ContainsKey("serverUrl") && (np["serverUrl"] is null ? "null" : S(np["serverUrl"])).Trim() is { Length: > 0 } url) n["serverUrl"] = url;
            }
            if (tp is not null)
            {
                if (tp.ContainsKey("enabled")) t["enabled"] = B(tp["enabled"]);
                if (B(tp["resetStartCode"])) t["startCode"] = GenerateStartCode(random);
                else if (B(tp["startCode"])) t["startCode"] = S(tp["startCode"]).Trim();
                foreach (var key in new[] { "pairedChatId", "pairedUsername" })
                    if (B(tp["unpair"])) t[key] = null;
                    else if (tp.ContainsKey(key)) t[key] = B(tp[key]) ? JsonValue.Create(S(tp[key])) : null;
                var offset = N(tp["updateOffset"]);
                if (tp.ContainsKey("updateOffset") && double.IsFinite(offset) && offset == Math.Floor(offset)) t["updateOffset"] = Math.Max(0, offset);
            }
            if (settings["ollama"] is JsonObject op)
            {
                var merged = current["ollama"]!.AsObject();
                foreach (var key in new[] { "enabled", "baseUrl", "model" }) if (op.ContainsKey(key)) merged[key] = Copy(op[key]);
                current["ollama"] = OllamaJson(PhoneOllama.NormalizeSettings(merged));
            }
            if (settings.ContainsKey("language")) current["language"] = PhoneVoice.LanguageOrDefault(S(settings["language"]));
            current.Remove("telegramToken"); current.Remove("corrupt");
            WritePrivate(Path.Combine(storeDir, "phone-settings.json"), current.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n", restrict);
            if (replaceToken || newToken is not null)
            {
                var path = Path.Combine(storeDir, "telegram-bot.token");
                if (newToken is null or "") { try { File.Delete(path); } catch { } }
                else WritePrivate(path, newToken.Trim() + "\n", restrict);
            }
            return LoadPhoneSettings(storeDir, random);
        }
    }

    public static string FormatTelegramAlertMessage(JsonObject input)
    {
        var lang = PhoneVoice.LanguageOrDefault(S(input["language"]));
        var it = lang == "it";
        var type = S(input["type"]);
        var known = AlertTypes.Contains(type);
        var kind = known ? type : "updated";
        var linesNode = Voice[it ? "it" : "en"]!["lines"]![kind] ?? Voice[it ? "it" : "en"]!["lines"]!["updated"];
        var headline = S(linesNode![0]);
        var signoff = S(linesNode![1]);
        var safeProject = EscapeHtml(SanitizeText(S(input["projectName"])) is { Length: > 0 } p ? p : "Project");
        var ahoy = S(Voice[it ? "it" : "en"]!["ahoy"]);

        var lines = new List<string>
        {
            $"🏴‍☠️ <b>{ahoy}</b>",
            headline,
            $"🚢 <b>{safeProject}</b>",
            $"🎯 {HtmlWithCode(MissionTitle(S(input["goalTitle"])))}",
            $"🧭 {PhoneVoice.TelegramAlertLabel(lang, kind)}"
        };
        var bar = FormatProgressBar(input["step"]);
        if (bar != "") lines.Add(bar);
        if (kind == "done" && input["durationMinutes"] is not null)
        {
            var dur = FormatDuration(N(input["durationMinutes"]));
            if (dur is not null) lines.Add("⏱️ " + dur);
        }
        if (input["timeLeft"] is JsonObject time && time["minutes"] is not null)
        {
            var minStr = S(time["minutes"]);
            var isOne = minStr == "1" || (double.TryParse(minStr, CultureInfo.InvariantCulture, out var mv) && mv == 1.0 && !minStr.Contains('.'));
            var timeText = it ? $"Circa {minStr} minut{(isOne ? "o" : "i")}" : $"About {minStr} minute{(isOne ? "" : "s")}";
            if (B(time["longerThanUsual"])) timeText += it ? " (più del solito)" : " (longer than usual)";
            lines.Add("⏳ " + EscapeHtml(timeText));
        }
        var savingWord = it ? "Risparmio" : "Saving";
        var savingName = Saving(input["savingMode"], lang);
        lines.Add($"<b>{savingWord}:</b> {EscapeHtml(savingName)}");
        lines.AddRange(CrewLines(input["crew"]));
        if (B(input["detail"])) lines.Add(HtmlWithCode(SanitizeText(S(input["detail"]))));
        lines.Add(signoff);
        return string.Join("\n", lines);
    }

    internal static string LanguageName(string language, string? nameOf = null)
    {
        var lang = PhoneVoice.LanguageOrDefault(language);
        var target = PhoneVoice.LanguageOrDefault(nameOf ?? lang);
        return target == "en" ? (lang == "it" ? "Inglese" : "English") : (lang == "it" ? "Italiano" : "Italian");
    }

    internal static (string action, string? language)? ParseLanguageCommand(string text)
    {
        var match = Regex.Match((text ?? "").Trim(), @"^/(?:language|lingua)(?:@[A-Za-z0-9_]+)?(?:\s+(\S+))?$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var arg = match.Groups[1].Value;
        if (string.IsNullOrEmpty(arg)) return ("show", null);
        var lang = PhoneVoice.NormalizeLanguage(arg);
        if (lang is null) return ("unknown", null);
        return ("set", lang);
    }

    internal static readonly HttpClient DefaultHttpClient = new();

    public static async Task<JsonObject> SendNtfyAlertAsync(
        string topic,
        string? serverUrl = null,
        string? message = null,
        string? title = "Planwharf",
        string? priority = "default",
        IEnumerable<string>? tags = null,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? fetch = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(topic)) return new JsonObject { ["ok"] = false, ["reason"] = "Topic is missing." };
        var baseUri = (serverUrl ?? "https://ntfy.sh").TrimEnd('/');
        var url = $"{baseUri}/{Uri.EscapeDataString(topic.Trim())}";
        var cleanTitle = (title ?? "").Replace("\r", " ").Replace("\n", " ");
        if (cleanTitle.Length > 200) cleanTitle = cleanTitle[..200];
        var cleanPriority = (priority ?? "default").Replace("\r", "").Replace("\n", "");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Title", cleanTitle);
            req.Headers.TryAddWithoutValidation("Priority", cleanPriority);
            if (tags != null)
            {
                var tagList = tags.Where(t => !string.IsNullOrEmpty(t)).ToArray();
                if (tagList.Length > 0) req.Headers.TryAddWithoutValidation("Tags", string.Join(",", tagList));
            }
            req.Content = new StringContent(message ?? "", System.Text.Encoding.UTF8, "text/plain");

            HttpResponseMessage res;
            if (fetch != null) res = await fetch(req, cancellationToken);
            else res = await DefaultHttpClient.SendAsync(req, cancellationToken);

            using (res)
            {
                if (!res.IsSuccessStatusCode)
                    return new JsonObject { ["ok"] = false, ["status"] = (int)res.StatusCode, ["reason"] = $"HTTP {(int)res.StatusCode}" };
                return new JsonObject { ["ok"] = true, ["status"] = (int)res.StatusCode };
            }
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["reason"] = ex.Message };
        }
    }

    public static async Task<JsonObject> SendTelegramMessageAsync(
        string? token,
        string? chatId,
        string? text,
        string parseMode = "HTML",
        JsonObject? replyMarkup = null,
        string apiBaseUrl = "https://api.telegram.org",
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? fetch = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token)) return new JsonObject { ["ok"] = false, ["reason"] = "Bot token is missing." };
        if (string.IsNullOrEmpty(chatId)) return new JsonObject { ["ok"] = false, ["reason"] = "Chat ID is missing." };

        var url = $"{apiBaseUrl.TrimEnd('/')}/bot{token}/sendMessage";
        var payload = new JsonObject
        {
            ["chat_id"] = chatId,
            ["text"] = PhoneVoice.FitTelegram(text ?? ""),
            ["reply_markup"] = replyMarkup ?? MainKeyboard()
        };
        if (!string.IsNullOrEmpty(parseMode)) payload["parse_mode"] = parseMode;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

            HttpResponseMessage res;
            if (fetch != null) res = await fetch(req, cancellationToken);
            else res = await DefaultHttpClient.SendAsync(req, cancellationToken);

            using (res)
            {
                var body = await res.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? data = null;
                try { data = JsonNode.Parse(body); } catch { }
                var ok = res.IsSuccessStatusCode && (data?["ok"]?.GetValue<bool>() ?? false);
                if (!ok)
                {
                    var reason = data?["description"]?.GetValue<string>() ?? $"HTTP {(int)res.StatusCode}";
                    return new JsonObject { ["ok"] = false, ["status"] = (int)res.StatusCode, ["reason"] = reason };
                }
                return new JsonObject { ["ok"] = true, ["data"] = data };
            }
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["reason"] = ex.Message };
        }
    }

    public static async Task<JsonObject> PollTelegramUpdatesAsync(
        string? token,
        long offset = 0,
        int timeout = 0,
        string apiBaseUrl = "https://api.telegram.org",
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? fetch = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token)) return new JsonObject { ["ok"] = false, ["updates"] = new JsonArray(), ["reason"] = "Bot token is missing." };

        var url = $"{apiBaseUrl.TrimEnd('/')}/bot{token}/getUpdates?offset={offset}&timeout={timeout}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            HttpResponseMessage res;
            if (fetch != null) res = await fetch(req, cancellationToken);
            else res = await DefaultHttpClient.SendAsync(req, cancellationToken);

            using (res)
            {
                var body = await res.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? data = null;
                try { data = JsonNode.Parse(body); } catch { }
                var ok = res.IsSuccessStatusCode && (data?["ok"]?.GetValue<bool>() ?? false);
                if (!ok)
                {
                    var reason = data?["description"]?.GetValue<string>() ?? $"HTTP {(int)res.StatusCode}";
                    return new JsonObject { ["ok"] = false, ["updates"] = new JsonArray(), ["reason"] = reason };
                }
                var updates = Copy(data?["result"]) as JsonArray ?? new JsonArray();
                return new JsonObject { ["ok"] = true, ["updates"] = updates };
            }
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["updates"] = new JsonArray(), ["reason"] = ex.Message };
        }
    }

    internal static async Task AnswerCallbackAsync(string? token, string? id, string? text,
        string apiBaseUrl = "https://api.telegram.org",
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? fetch = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(id)) return;
        var url = $"{apiBaseUrl.TrimEnd('/')}/bot{token}/answerCallbackQuery";
        var payload = new JsonObject { ["callback_query_id"] = id, ["text"] = text ?? "" };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            HttpResponseMessage res;
            if (fetch != null) res = await fetch(req, cancellationToken);
            else res = await DefaultHttpClient.SendAsync(req, cancellationToken);
            using (res) { }
        }
        catch { }
    }

    public static PhoneNotifier CreatePhoneNotifier(PhoneNotifierOptions options) => new(options);
}

public sealed class PhoneNotifierOptions
{
    public required string StoreDir { get; set; }
    public Func<Task<JsonObject?>>? GetSnapshot { get; set; }
    public Func<string, Task>? OnStopProject { get; set; }
    public Func<string, Task>? OnResumeProject { get; set; }
    public Func<string, string, string, Task<JsonObject?>>? OnAnswerDecision { get; set; }
    public Func<string, string, Task<JsonObject?>>? OnDelegateDecision { get; set; }
    public Func<Task<JsonObject?>>? GetDecisions { get; set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Fetch { get; set; }
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";
    public string NtfyServerUrl { get; set; } = "https://ntfy.sh";
    public double ConfirmWindowMs { get; set; } = 60000;
    public Func<long>? Clock { get; set; }
    public Func<int, int, int>? Random { get; set; }
}

public sealed class PhoneNotifier
{
    private record PendingConfirmation(string Action, string ProjectId, string ProjectName, long ExpiresAt);
    private record TechnicalInfo(string Code, string Note);

    private readonly string _storeDir;
    private readonly Func<Task<JsonObject?>>? _getSnapshot;
    private readonly Func<string, Task>? _onStopProject;
    private readonly Func<string, Task>? _onResumeProject;
    private readonly Func<string, string, string, Task<JsonObject?>>? _onAnswerDecision;
    private readonly Func<string, string, Task<JsonObject?>>? _onDelegateDecision;
    private readonly Func<Task<JsonObject?>>? _getDecisions;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _fetch;
    private readonly string _apiBaseUrl;
    private readonly string _ntfyServerUrl;
    private readonly double _confirmWindowMs;
    private readonly Func<long> _clock;
    private readonly Func<int, int, int>? _random;

    private JsonObject _settings;
    private long _telegramOffset;
    private PendingConfirmation? _pendingConfirmation;
    private TechnicalInfo? _lastTechnical;

    private readonly Dictionary<string, string> _projectStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject?> _stepStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _crewBefore = new(StringComparer.Ordinal);
    private readonly ChatMemory _chatMemory;

    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    private static Task<HttpResponseMessage> SendDefaultHttpAsync(HttpRequestMessage req, CancellationToken ct) => Phone.DefaultHttpClient.SendAsync(req, ct);

    public PhoneNotifier(PhoneNotifierOptions options)
    {
        _storeDir = options.StoreDir;
        _getSnapshot = options.GetSnapshot;
        _onStopProject = options.OnStopProject;
        _onResumeProject = options.OnResumeProject;
        _onAnswerDecision = options.OnAnswerDecision;
        _onDelegateDecision = options.OnDelegateDecision;
        _getDecisions = options.GetDecisions;
        _fetch = options.Fetch;
        _apiBaseUrl = options.ApiBaseUrl;
        _ntfyServerUrl = options.NtfyServerUrl;
        _confirmWindowMs = options.ConfirmWindowMs > 0 ? options.ConfirmWindowMs : 60000;
        _clock = options.Clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _random = options.Random;

        _settings = Phone.LoadPhoneSettings(_storeDir, _random);
        var offset = Phone.N(_settings["telegram"]?["updateOffset"]);
        _telegramOffset = double.IsFinite(offset) ? (long)offset : 0;
        _chatMemory = PhoneOllama.LoadChatMemory(_storeDir);
    }

    private void RememberTechnical(string code, object? error)
    {
        var raw = error is Exception ex ? ex.Message : error is JsonObject o ? Phone.S(o["message"]) : Phone.S(JsonValue.Create(error?.ToString()));
        var note = Phone.SanitizeText(raw);
        if (string.IsNullOrEmpty(note)) note = "No further note was given.";
        if (note.Length > 300) note = note[..300];
        _lastTechnical = new TechnicalInfo(code, note);
    }

    private string TechnicalReply(string language)
    {
        if (_lastTechnical == null) return PhoneVoice.Say(language, "detailsNone");
        return PhoneVoice.Say(language, "details", new Dictionary<string, string>
        {
            ["code"] = Phone.EscapeHtml(_lastTechnical.Code),
            ["note"] = Phone.EscapeHtml(_lastTechnical.Note)
        });
    }

    private void RememberOffset(long next)
    {
        _telegramOffset = next;
        Phone.SavePhoneSettings(_storeDir, new JsonObject
        {
            ["telegram"] = new JsonObject { ["updateOffset"] = next }
        }, random: _random);
    }

    public JsonObject GetSettings()
    {
        _settings = Phone.LoadPhoneSettings(_storeDir, _random);
        if (Phone.B(_settings["corrupt"])) _settings = Phone.SavePhoneSettings(_storeDir, new JsonObject(), random: _random);
        var tg = _settings["telegram"]!.AsObject();
        var ntfy = _settings["ntfy"]!.AsObject();
        var ollama = _settings["ollama"]!.AsObject();
        var token = Phone.S(_settings["telegramToken"]);
        return new JsonObject
        {
            ["ntfy"] = new JsonObject
            {
                ["enabled"] = Phone.B(ntfy["enabled"]),
                ["topic"] = Phone.S(ntfy["topic"]),
                ["serverUrl"] = Phone.S(ntfy["serverUrl"])
            },
            ["telegram"] = new JsonObject
            {
                ["enabled"] = Phone.B(tg["enabled"]),
                ["startCode"] = Phone.S(tg["startCode"]),
                ["paired"] = Phone.B(tg["pairedChatId"]),
                ["pairedChatId"] = Phone.Copy(tg["pairedChatId"]),
                ["pairedUsername"] = Phone.Copy(tg["pairedUsername"]),
                ["tokenConfigured"] = !string.IsNullOrEmpty(token),
                ["tokenPreview"] = Phone.MaskToken(token)
            },
            ["ollama"] = new JsonObject
            {
                ["enabled"] = Phone.B(ollama["enabled"]),
                ["baseUrl"] = Phone.S(ollama["baseUrl"]),
                ["model"] = Phone.S(ollama["model"])
            },
            ["language"] = PhoneVoice.LanguageOrDefault(Phone.S(_settings["language"]))
        };
    }

    public JsonObject UpdateSettings(JsonObject? patch = null, string? newToken = null)
    {
        patch ??= new JsonObject();
        var telegram = new JsonObject();
        if (patch["telegram"] is JsonObject tp)
        {
            if (tp.ContainsKey("enabled")) telegram["enabled"] = Phone.B(tp["enabled"]);
            if (Phone.B(tp["unpair"])) telegram["unpair"] = true;
            if (Phone.B(tp["resetStartCode"])) telegram["resetStartCode"] = true;
        }
        JsonObject? ollama = null;
        if (patch["ollama"] is JsonObject op)
        {
            ollama = new JsonObject
            {
                ["enabled"] = op.ContainsKey("enabled") ? Phone.B(op["enabled"]) : null,
                ["baseUrl"] = op.ContainsKey("baseUrl") ? Phone.Copy(op["baseUrl"]) : null,
                ["model"] = op.ContainsKey("model") ? Phone.Copy(op["model"]) : null
            };
        }
        var savePayload = new JsonObject
        {
            ["ntfy"] = Phone.Copy(patch["ntfy"]),
            ["telegram"] = telegram,
            ["ollama"] = ollama,
            ["language"] = Phone.Copy(patch["language"])
        };
        _settings = Phone.SavePhoneSettings(_storeDir, savePayload, newToken, replaceToken: newToken is not null, random: _random);
        if (Phone.B(telegram["unpair"])) _pendingConfirmation = null;
        return GetSettings();
    }

    public async Task<JsonObject> SendAlertAsync(JsonObject input)
    {
        var fresh = Phone.LoadPhoneSettings(_storeDir, _random);
        var isIt = PhoneVoice.LanguageOrDefault(Phone.S(fresh["language"])) == "it";
        var detail = Phone.S(input["detail"]);
        var breaks = Phone.B(input["breaks"]);
        if (breaks && !detail.Contains("break") && !detail.Contains("rompere"))
        {
            var warning = isIt ? "⚠️ Potrebbe rompere qualcosa" : "⚠️ Could break something";
            detail = detail.Length > 0 ? $"{detail}\n{warning}" : warning;
        }

        var type = Phone.S(input["type"]);
        var projectName = Phone.S(input["projectName"]);
        var goalTitle = Phone.S(input["goalTitle"]);
        var durationMinutes = input["durationMinutes"] is not null ? Phone.N(input["durationMinutes"]) : (double?)null;
        var timeLeft = input["timeLeft"] as JsonObject;
        var step = input["step"];
        var crew = input["crew"];
        var savingMode = input["savingMode"];
        var decisionId = Phone.S(input["decisionId"]);
        var suggestion = Phone.S(input["suggestion"]);
        var projectId = Phone.S(input["projectId"]);

        var ntfyTask = Task.Run(async () =>
        {
            var ntfy = fresh["ntfy"]!.AsObject();
            if (Phone.B(ntfy["enabled"]) && Phone.B(ntfy["topic"]))
            {
                var safeTitle = $"Planwharf: {Phone.SanitizeText(projectName)}";
                var priority = type == "failed" ? "high" : type == "needs_owner" ? "urgent" : "default";
                var tags = type == "failed" ? new[] { "warning" } : type == "needs_owner" ? new[] { "bell" } : new[] { "white_check_mark" };
                var alertObj = new JsonObject
                {
                    ["type"] = type,
                    ["projectName"] = projectName,
                    ["goalTitle"] = goalTitle,
                    ["durationMinutes"] = durationMinutes,
                    ["timeLeft"] = Phone.Copy(timeLeft),
                    ["detail"] = detail,
                    ["savingMode"] = Phone.Copy(savingMode),
                    ["language"] = fresh["language"]?.DeepClone()
                };
                var plainMsg = Phone.FormatAlertMessage(alertObj);
                return await Phone.SendNtfyAlertAsync(
                    topic: Phone.S(ntfy["topic"]),
                    serverUrl: Phone.S(ntfy["serverUrl"]) is { Length: > 0 } u ? u : _ntfyServerUrl,
                    message: plainMsg,
                    title: safeTitle,
                    priority: priority,
                    tags: tags,
                    fetch: _fetch);
            }
            return (JsonObject?)null;
        });

        var tgTask = Task.Run(async () =>
        {
            var tg = fresh["telegram"]!.AsObject();
            var token = Phone.S(fresh["telegramToken"]);
            var chatId = Phone.S(tg["pairedChatId"]);
            if (Phone.B(tg["enabled"]) && !string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(chatId))
            {
                var tgAlertObj = new JsonObject
                {
                    ["type"] = type,
                    ["projectName"] = projectName,
                    ["goalTitle"] = goalTitle,
                    ["durationMinutes"] = durationMinutes,
                    ["timeLeft"] = Phone.Copy(timeLeft),
                    ["detail"] = detail,
                    ["step"] = Phone.Copy(step),
                    ["crew"] = Phone.Copy(crew),
                    ["savingMode"] = Phone.Copy(savingMode),
                    ["language"] = fresh["language"]?.DeepClone()
                };
                var tgMsg = Phone.FormatTelegramAlertMessage(tgAlertObj);
                JsonObject? replyMarkup = null;
                if (!string.IsNullOrEmpty(decisionId) && !string.IsNullOrEmpty(projectId))
                {
                    var acceptLabel = !string.IsNullOrEmpty(suggestion)
                        ? (isIt ? $"Accetta: {suggestion}" : $"Accept: {suggestion}")
                        : (isIt ? "Accetta" : "Accept");
                    replyMarkup = new JsonObject
                    {
                        ["inline_keyboard"] = new JsonArray(
                            (JsonNode)new JsonArray(
                                (JsonNode)Phone.InlineButton($"✅ {acceptLabel}", $"decide_suggest:{projectId}:{decisionId}"),
                                (JsonNode)Phone.InlineButton(isIt ? "🤖 Lascia decidere al lead" : "🤖 Let the lead decide", $"decide_lead:{projectId}:{decisionId}")
                            )
                        )
                    };
                }
                return await Phone.SendTelegramMessageAsync(
                    token: token,
                    chatId: chatId,
                    text: tgMsg,
                    parseMode: "HTML",
                    replyMarkup: replyMarkup,
                    apiBaseUrl: _apiBaseUrl,
                    fetch: _fetch);
            }
            return (JsonObject?)null;
        });

        await Task.WhenAll(ntfyTask, tgTask);
        return new JsonObject
        {
            ["ntfy"] = await ntfyTask,
            ["telegram"] = await tgTask
        };
    }

    public async Task CheckTransitionsAsync()
    {
        await _transitionGate.WaitAsync();
        try
        {
            await CheckTransitionsNowAsync();
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task CheckTransitionsNowAsync()
    {
        if (_getSnapshot == null) return;
        JsonObject? snapshot;
        try { snapshot = await _getSnapshot(); }
        catch { return; }
        if (snapshot == null || snapshot["projects"] is not JsonArray projects) return;

        foreach (var p in Phone.Items(projects))
        {
            var id = Phone.S(p["id"]);
            if (string.IsNullOrEmpty(id)) continue;
            var curr = Phone.S(p["status"]);
            _projectStates.TryGetValue(id, out var prev);
            _projectStates[id] = curr;

            if (prev == null) continue; // First observation establishes baseline

            var step = Phone.RunningStep(p);
            _stepStates.TryGetValue(id, out var prevStep);
            _stepStates[id] = step;

            _crewBefore.TryGetValue(id, out var before);
            before ??= [];
            var crew = Phone.CrewOfProject(p, snapshot["models"], new JsonObject(before.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value)))));
            var leadChanged = crew != null && Phone.B(crew["leadFrom"]);
            var alertType = Phone.AlertForStatus(prev, curr)
                ?? (curr == "watching" ? Phone.AlertForStep(prevStep, step) : null)
                ?? (leadChanged ? "lead" : null);

            if (alertType == null)
            {
                var kept = new Dictionary<string, string>(before);
                if (crew != null)
                {
                    foreach (var item in Phone.Items(crew["build"]).Concat(Phone.Items(crew["verify"])))
                    {
                        var itemId = Phone.S(item["id"]);
                        if (!kept.ContainsKey(itemId)) kept[itemId] = Phone.S(item["name"]);
                    }
                    if (!kept.ContainsKey("__lead") && Phone.B(crew["lead"])) kept["__lead"] = Phone.S(crew["lead"]);
                }
                _crewBefore[id] = kept;
                continue;
            }

            var now = new Dictionary<string, string> { ["__lead"] = crew != null ? Phone.S(crew["lead"]) : "" };
            if (crew != null)
            {
                foreach (var item in Phone.Items(crew["build"]).Concat(Phone.Items(crew["verify"])))
                {
                    now[Phone.S(item["id"])] = Phone.S(item["name"]);
                }
            }
            _crewBefore[id] = now;

            JsonNode? firstDec = null;
            if (p["openDecisions"] is JsonArray openDecs && openDecs.Count > 0)
                firstDec = openDecs[0];
            else if (snapshot["decisionsInbox"]?["open"] is JsonArray inboxDecs)
                firstDec = inboxDecs.FirstOrDefault(d => Phone.S(d?["projectId"]) == id);

            var detail = alertType == "restarted" ? "The same step started again."
                : (alertType == "needs_owner" && Phone.B(firstDec?["text"]) ? Phone.S(firstDec?["text"]) : "");

            var alertObj = new JsonObject
            {
                ["type"] = alertType,
                ["projectId"] = id,
                ["projectName"] = Phone.Copy(p["name"]),
                ["goalTitle"] = Phone.Copy(p["goal"] ?? p["jobTitle"]),
                ["timeLeft"] = Phone.Copy(p["timeLeft"]),
                ["detail"] = detail,
                ["step"] = Phone.Copy(p["step"]),
                ["crew"] = crew,
                ["savingMode"] = Phone.Copy(p["savingMode"])
            };
            if (alertType == "needs_owner" && firstDec != null)
            {
                alertObj["decisionId"] = Phone.Copy(firstDec["id"]);
                alertObj["suggestion"] = Phone.Copy(firstDec["suggestion"]);
                alertObj["breaks"] = Phone.B(firstDec["breaks"]);
            }
            await SendAlertAsync(alertObj);
        }
    }

    public async Task ProcessTelegramMessageAsync(JsonObject msg)
    {
        await _commandGate.WaitAsync();
        try
        {
            if (msg["callback_query"] is JsonObject query) await HandleCallbackAsync(query);
            else await HandleTelegramMessageAsync(msg);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task PollUpdatesAsync()
    {
        await _commandGate.WaitAsync();
        try
        {
            await PollUpdatesNowAsync();
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task PollUpdatesNowAsync()
    {
        var fresh = Phone.LoadPhoneSettings(_storeDir, _random);
        var tg = fresh["telegram"]!.AsObject();
        var token = Phone.S(fresh["telegramToken"]);
        if (!Phone.B(tg["enabled"]) || string.IsNullOrEmpty(token)) return;

        var offsetVal = Phone.N(tg["updateOffset"]);
        if (double.IsFinite(offsetVal)) _telegramOffset = (long)offsetVal;

        var res = await Phone.PollTelegramUpdatesAsync(token, _telegramOffset, 0, _apiBaseUrl, _fetch);
        if (Phone.B(res["ok"]) && res["updates"] is JsonArray updates && updates.Count > 0)
        {
            foreach (var u in updates.OfType<JsonObject>())
            {
                var id = Phone.N(u["update_id"]);
                if (!double.IsFinite(id) || id < _telegramOffset) continue;
                RememberOffset((long)id + 1);
                if (u["callback_query"] is JsonObject cb) await HandleCallbackAsync(cb);
                else if (u["message"] is JsonObject msg) await HandleTelegramMessageAsync(msg);
            }
        }
    }

    public async Task<JsonObject> ProbeOllamaAsync()
    {
        var fresh = Phone.LoadPhoneSettings(_storeDir, _random);
        var ollama = fresh["ollama"]!.AsObject();
        var baseUrl = Phone.S(ollama["baseUrl"]);
        var model = Phone.S(ollama["model"]);
        var listed = await PhoneOllama.ListInstalledModelsAsync(baseUrl, _fetch ?? SendDefaultHttpAsync, 4000);
        var modelsArr = new JsonArray();
        foreach (var m in listed.Models) modelsArr.Add((JsonNode)JsonValue.Create(m)!);
        return new JsonObject
        {
            ["ok"] = listed.Ok,
            ["enabled"] = Phone.B(ollama["enabled"]),
            ["baseUrl"] = baseUrl,
            ["model"] = model,
            ["models"] = modelsArr,
            ["installed"] = listed.Models.Contains(model)
        };
    }

    private async Task HandleTelegramMessageAsync(JsonObject msg)
    {
        if (msg["chat"] is not JsonObject chat || chat["id"] is null) return;
        var chatId = Phone.S(chat["id"]);
        var rawText = Phone.S(msg["text"]).Trim();
        var text = (rawText is "📊 Status" or "📊 Stato") ? "/status"
            : (rawText is "⏹ Stop" or "⏹ Ferma") ? "/stop"
            : (rawText is "▶️ Resume" or "▶️ Riprendi") ? "/resume"
            : (rawText is "❓ Decisions" or "❓ Decisioni") ? "/decisions"
            : rawText;

        if (string.IsNullOrEmpty(text))
        {
            if (msg["document"] is not null || msg["photo"] is not null || msg["video"] is not null ||
                msg["voice"] is not null || msg["audio"] is not null || msg["sticker"] is not null)
            {
                var freshSettings = Phone.LoadPhoneSettings(_storeDir, _random);
                var botToken = Phone.S(freshSettings["telegramToken"]);
                if (!string.IsNullOrEmpty(botToken) && Phone.S(freshSettings["telegram"]?["pairedChatId"]) == chatId)
                {
                    await Phone.SendTelegramMessageAsync(botToken, chatId,
                        PhoneVoice.Say(PhoneVoice.LanguageOrDefault(Phone.S(freshSettings["language"])), "attachments"),
                        apiBaseUrl: _apiBaseUrl, fetch: _fetch);
                }
            }
            return;
        }

        var fresh = Phone.LoadPhoneSettings(_storeDir, _random);
        var token = Phone.S(fresh["telegramToken"]);
        if (string.IsNullOrEmpty(token)) return;

        var pairedChatId = Phone.S(fresh["telegram"]?["pairedChatId"]);
        var startCode = Phone.S(fresh["telegram"]?["startCode"]);

        if (string.IsNullOrEmpty(pairedChatId))
        {
            var match = Regex.Match(text, @"^/?start\s*([a-zA-Z0-9_-]*)$", RegexOptions.IgnoreCase);
            var enteredCode = match.Success ? match.Groups[1].Value : text;
            if (!string.IsNullOrEmpty(enteredCode) && enteredCode.Equals(startCode, StringComparison.OrdinalIgnoreCase))
            {
                fresh["telegram"]!["pairedChatId"] = JsonValue.Create(chatId);
                fresh["telegram"]!["pairedUsername"] = chat["username"] is not null ? JsonValue.Create(Phone.S(chat["username"])) : null;
                Phone.SavePhoneSettings(_storeDir, new JsonObject
                {
                    ["telegram"] = new JsonObject
                    {
                        ["pairedChatId"] = JsonValue.Create(chatId),
                        ["pairedUsername"] = chat["username"] is not null ? JsonValue.Create(Phone.S(chat["username"])) : null
                    }
                }, random: _random);
                await Phone.SendTelegramMessageAsync(token, chatId,
                    PhoneVoice.Say(PhoneVoice.LanguageOrDefault(Phone.S(fresh["language"])), "pairing"),
                    replyMarkup: Phone.MainKeyboard(),
                    apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            }
            return;
        }

        if (chatId != pairedChatId) return;

        var nowMs = _clock();
        if (_pendingConfirmation != null && nowMs < _pendingConfirmation.ExpiresAt)
        {
            if (PhoneOllama.IsBareConfirmation(text))
            {
                var target = _pendingConfirmation;
                _pendingConfirmation = null;
                try
                {
                    if (_onStopProject != null) await _onStopProject(target.ProjectId);
                    if (_lastTechnical?.Code == "stop-failed") _lastTechnical = null;
                    await Phone.SendTelegramMessageAsync(token, chatId,
                        PhoneVoice.Say(PhoneVoice.LanguageOrDefault(Phone.S(fresh["language"])), "crewStopped",
                            new Dictionary<string, string> { ["name"] = Phone.EscapeHtml(Phone.SanitizeText(target.ProjectName)) }),
                        apiBaseUrl: _apiBaseUrl, fetch: _fetch);
                }
                catch (Exception err)
                {
                    RememberTechnical("stop-failed", err);
                    await Phone.SendTelegramMessageAsync(token, chatId,
                        PhoneVoice.Say(PhoneVoice.LanguageOrDefault(Phone.S(fresh["language"])), "couldNotCompleteStop"),
                        apiBaseUrl: _apiBaseUrl, fetch: _fetch);
                }
                return;
            }
            else
            {
                _pendingConfirmation = null;
                await Phone.SendTelegramMessageAsync(token, chatId,
                    PhoneVoice.Say(PhoneVoice.LanguageOrDefault(Phone.S(fresh["language"])), "stopCancelled"),
                    apiBaseUrl: _apiBaseUrl, fetch: _fetch);
                if (!text.StartsWith('/')) return;
            }
        }
        else
        {
            _pendingConfirmation = null;
        }

        var lang = PhoneVoice.LanguageOrDefault(Phone.S(fresh["language"]));
        var command = Regex.Replace(text, @"^/([a-z0-9]+)(?:@[A-Za-z0-9_]+)?", m => "/" + m.Groups[1].Value.ToLowerInvariant(), RegexOptions.IgnoreCase);

        if (PhoneVoice.IsDetailsRequest(text) || PhoneVoice.IsDetailsRequest(command))
        {
            await Phone.SendTelegramMessageAsync(token, chatId, TechnicalReply(lang), replyMarkup: Phone.MainKeyboard(), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            return;
        }

        var languageCmd = Phone.ParseLanguageCommand(text);
        if (languageCmd != null)
        {
            var lc = languageCmd.Value;
            if (lc.action == "set" && lc.language != null)
            {
                Phone.SavePhoneSettings(_storeDir, new JsonObject { ["language"] = lc.language }, random: _random);
                await Phone.SendTelegramMessageAsync(token, chatId,
                    PhoneVoice.Say(lc.language, "languageSet", new Dictionary<string, string>
                    {
                        ["languageName"] = Phone.LanguageName(lc.language, lc.language)
                    }),
                    replyMarkup: Phone.MainKeyboard(), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            }
            else if (lc.action == "unknown")
            {
                await Phone.SendTelegramMessageAsync(token, chatId,
                    PhoneVoice.Say(lang, "languageUnknown"),
                    replyMarkup: Phone.MainKeyboard(), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            }
            else
            {
                await Phone.SendTelegramMessageAsync(token, chatId,
                    PhoneVoice.Say(lang, "languageShow", new Dictionary<string, string>
                    {
                        ["languageName"] = Phone.LanguageName(lang, lang)
                    }),
                    replyMarkup: Phone.MainKeyboard(), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            }
            return;
        }

        JsonObject? snapshot = null;
        var snapshotFailed = false;
        Exception? snapshotError = null;
        if (_getSnapshot != null)
        {
            try { snapshot = await _getSnapshot(); }
            catch (Exception ex) { snapshotFailed = true; snapshotError = ex; }
        }
        if (snapshotFailed)
        {
            RememberTechnical("projects-unreadable", snapshotError);
            await Phone.SendTelegramMessageAsync(token, chatId,
                PhoneVoice.Say(lang, "couldNotReadProjects"),
                apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            return;
        }
        if (_lastTechnical?.Code == "projects-unreadable") _lastTechnical = null;
        var projects = snapshot?["projects"] as JsonArray ?? [];

        if (command is "/status" or "/stato" || command.StartsWith("/status ") || command.StartsWith("/stato "))
        {
            var statusText = Phone.FormatStatusMessage(projects, lang);
            await Phone.SendTelegramMessageAsync(token, chatId, statusText, replyMarkup: Phone.StatusKeyboard(projects, lang), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            return;
        }

        if (command is "/decisions" or "/decisioni" || command.StartsWith("/decisions ") || command.StartsWith("/decisioni "))
        {
            var openDecisions = new List<JsonObject>();
            if (_getDecisions != null)
            {
                try
                {
                    var res = await _getDecisions();
                    if (res?["open"] is JsonArray arr)
                        openDecisions.AddRange(arr.OfType<JsonObject>());
                }
                catch { }
            }
            else if (snapshot?["decisionsInbox"]?["open"] is JsonArray inboxOpen)
            {
                openDecisions.AddRange(inboxOpen.OfType<JsonObject>());
            }
            else
            {
                foreach (var p in Phone.Items(projects))
                {
                    if (p["openDecisions"] is JsonArray pDecs)
                    {
                        foreach (var d in pDecs.OfType<JsonObject>())
                        {
                            var clone = d.DeepClone()!.AsObject();
                            clone["projectId"] = Phone.Copy(p["id"]);
                            clone["projectName"] = Phone.Copy(p["name"] ?? p["id"]);
                            openDecisions.Add(clone);
                        }
                    }
                }
            }

            if (openDecisions.Count == 0)
            {
                var noneText = lang == "it" ? "Nessuna decisione in attesa." : "No decisions waiting.";
                await Phone.SendTelegramMessageAsync(token, chatId, noneText, replyMarkup: Phone.MainKeyboard(), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
                return;
            }

            var isIt = lang == "it";
            foreach (var d in openDecisions)
            {
                var proj = Phone.EscapeHtml(Phone.SanitizeText(Phone.S(d["projectName"] ?? d["projectId"] ?? JsonValue.Create("Project"))));
                var work = Phone.EscapeHtml(Phone.S(d["workTitle"] ?? d["workId"]));
                var decText = Phone.EscapeHtml(Phone.S(d["text"]));
                var sug = Phone.EscapeHtml(Phone.S(d["suggestion"]));
                var breakWarn = Phone.B(d["breaks"]) ? (isIt ? " ⚠️ <b>Potrebbe rompere qualcosa</b>" : " ⚠️ <b>Could break something</b>") : "";
                var block = $"🚢 <b>{proj}</b>{(work != "" ? $" · {work}" : "")}\n❓ {decText}{breakWarn}";
                if (sug != "") block += $"\n💡 <i>{(isIt ? "Suggerimento" : "Suggestion")}: {sug}</i>";

                var sugVal = Phone.S(d["suggestion"]);
                var acceptLabel = !string.IsNullOrEmpty(sugVal) ? (isIt ? $"Accetta: {sugVal}" : $"Accept: {sugVal}") : (isIt ? "Accetta" : "Accept");
                var buttons = new JsonArray(
                    (JsonNode)new JsonArray(
                        (JsonNode)Phone.InlineButton($"✅ {acceptLabel}", $"decide_suggest:{Phone.S(d["projectId"])}:{Phone.S(d["id"])}"),
                        (JsonNode)Phone.InlineButton(isIt ? "🤖 Lascia decidere al lead" : "🤖 Let the lead decide", $"decide_lead:{Phone.S(d["projectId"])}:{Phone.S(d["id"])}")
                    )
                );
                await Phone.SendTelegramMessageAsync(token, chatId, block, replyMarkup: new JsonObject { ["inline_keyboard"] = buttons }, parseMode: "HTML", apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            }
            return;
        }

        if (command.StartsWith("/stop") || command.StartsWith("/ferma"))
        {
            var arg = Regex.Replace(command, @"^/(?:stop|ferma)\s*", "", RegexOptions.IgnoreCase).Trim();
            var matched = Phone.MatchProject(projects, arg);
            if (matched == null)
            {
                if (projects.Count == 0)
                {
                    await Phone.SendTelegramMessageAsync(token, chatId, PhoneVoice.Say(lang, "stopNone"), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
                }
                else
                {
                    var names = string.Join(", ", Phone.Items(projects).Select(p => Phone.EscapeHtml(Phone.SanitizeText(Phone.S(p["name"] ?? p["id"])))));
                    await Phone.SendTelegramMessageAsync(token, chatId, PhoneVoice.Say(lang, "stopSpecify", new Dictionary<string, string> { ["names"] = names }), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
                }
                return;
            }
            var mId = Phone.S(matched["id"]);
            var mName = Phone.S(matched["name"] ?? matched["id"]);
            _pendingConfirmation = new PendingConfirmation("stop", mId, mName, _clock() + (long)_confirmWindowMs);
            var confirmBtns = new JsonArray(
                (JsonNode)new JsonArray(
                    (JsonNode)Phone.InlineButton(lang == "it" ? "✅ Sì, ferma" : "✅ Yes, stop", $"yes:{mId}"),
                    (JsonNode)Phone.InlineButton(lang == "it" ? "❌ Annulla" : "❌ Cancel", "cancel")
                )
            );
            await Phone.SendTelegramMessageAsync(token, chatId,
                PhoneVoice.Say(lang, "stopConfirm", new Dictionary<string, string> { ["name"] = Phone.EscapeHtml(Phone.SanitizeText(mName)) }),
                replyMarkup: new JsonObject { ["inline_keyboard"] = confirmBtns },
                apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            return;
        }

        if (command.StartsWith("/resume") || command.StartsWith("/riprendi"))
        {
            var arg = Regex.Replace(command, @"^/(?:resume|riprendi)\s*", "", RegexOptions.IgnoreCase).Trim();
            var matched = Phone.MatchProject(projects, arg);
            if (matched == null)
            {
                if (projects.Count == 0)
                {
                    await Phone.SendTelegramMessageAsync(token, chatId, PhoneVoice.Say(lang, "resumeNone"), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
                }
                else
                {
                    var names = string.Join(", ", Phone.Items(projects).Select(p => Phone.EscapeHtml(Phone.SanitizeText(Phone.S(p["name"] ?? p["id"])))));
                    await Phone.SendTelegramMessageAsync(token, chatId, PhoneVoice.Say(lang, "resumeSpecify", new Dictionary<string, string> { ["names"] = names }), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
                }
                return;
            }
            var mId = Phone.S(matched["id"]);
            var mName = Phone.S(matched["name"] ?? matched["id"]);
            try
            {
                if (_onResumeProject != null) await _onResumeProject(mId);
                if (_lastTechnical?.Code == "resume-failed") _lastTechnical = null;
                await Phone.SendTelegramMessageAsync(token, chatId,
                    PhoneVoice.Say(lang, "crewResumed", new Dictionary<string, string> { ["name"] = Phone.EscapeHtml(Phone.SanitizeText(mName)) }),
                    apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            }
            catch (Exception err)
            {
                RememberTechnical("resume-failed", err);
                await Phone.SendTelegramMessageAsync(token, chatId,
                    PhoneVoice.Say(lang, "couldNotCompleteResume"),
                    apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            }
            return;
        }

        if (command is "/help" or "/aiuto" or "/guida" || command.StartsWith("/start"))
        {
            await Phone.SendTelegramMessageAsync(token, chatId, PhoneVoice.Say(lang, "help"), replyMarkup: Phone.MainKeyboard(), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            return;
        }

        if (PhoneOllama.IsClearMemory(text) || PhoneOllama.IsClearMemory(command))
        {
            _chatMemory.Clear(chatId);
            try { PhoneOllama.SaveChatMemory(_storeDir, _chatMemory); } catch { }
            await Phone.SendTelegramMessageAsync(token, chatId, PhoneVoice.Say(lang, "memoryCleared"), replyMarkup: Phone.MainKeyboard(), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            return;
        }

        var ollama = PhoneOllama.NormalizeSettings(fresh["ollama"]);
        if (!ollama.Enabled || PhoneOllama.IsBareConfirmation(text))
        {
            await Phone.SendTelegramMessageAsync(token, chatId, PhoneVoice.Say(lang, "unrecognized"), replyMarkup: Phone.MainKeyboard(), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            return;
        }

        var histArr = new JsonArray();
        foreach (var item in _chatMemory.History(chatId))
            histArr.Add((JsonNode)new JsonObject { ["role"] = item.Role, ["text"] = item.Text });

        var messages = PhoneOllama.BuildReplyMessages(
            question: text,
            projects: projects,
            history: histArr,
            sanitize: Phone.SanitizeText,
            model: ollama.Model,
            language: lang);

        _chatMemory.Remember(chatId, "user", Phone.SanitizeText(text));

        var reply = await PhoneOllama.AskOllamaAsync(
            baseUrl: ollama.BaseUrl,
            model: ollama.Model,
            messages: messages,
            send: _fetch ?? SendDefaultHttpAsync,
            timeoutMs: 45000,
            language: lang);

        if (!reply.Ok) RememberTechnical("companion-offline", reply.Reason);
        if (reply.Ok) _chatMemory.Remember(chatId, "assistant", Phone.SanitizeText(reply.Text));
        try { PhoneOllama.SaveChatMemory(_storeDir, _chatMemory); } catch { }

        await Phone.SendTelegramMessageAsync(token, chatId, reply.Text, replyMarkup: Phone.MainKeyboard(), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
    }

    private async Task HandleCallbackAsync(JsonObject query)
    {
        if (query["message"] is not JsonObject qMsg || qMsg["chat"] is not JsonObject qChat) return;
        var chatId = Phone.S(qChat["id"]);
        var fresh = Phone.LoadPhoneSettings(_storeDir, _random);
        var token = Phone.S(fresh["telegramToken"]);
        var pairedChatId = Phone.S(fresh["telegram"]?["pairedChatId"]);
        var qId = Phone.S(query["id"]);

        if (string.IsNullOrEmpty(token) || chatId != pairedChatId)
        {
            if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(qId))
                await Phone.AnswerCallbackAsync(token, qId, "", _apiBaseUrl, _fetch);
            return;
        }

        var data = Phone.S(query["data"]);
        if (!string.IsNullOrEmpty(qId)) await Phone.AnswerCallbackAsync(token, qId, "", _apiBaseUrl, _fetch);

        if (data is "status" or "cancel")
        {
            if (data == "cancel") _pendingConfirmation = null;
            await HandleTelegramMessageAsync(new JsonObject
            {
                ["chat"] = new JsonObject { ["id"] = chatId },
                ["text"] = "/status"
            });
            return;
        }

        var parts = data.Split(':');
        var verb = parts[0];
        var id = parts.Length > 1 ? parts[1] : "";
        if (string.IsNullOrEmpty(id)) return;

        if (verb == "stop")
        {
            var lang = PhoneVoice.LanguageOrDefault(Phone.S(fresh["language"]));
            var projName = id;
            if (_getSnapshot != null)
            {
                try
                {
                    var snap = await _getSnapshot();
                    if (snap?["projects"] is JsonArray snapProjects)
                    {
                        var found = snapProjects.OfType<JsonObject>().FirstOrDefault(p => Phone.S(p["id"]) == id);
                        if (found != null && Phone.B(found["name"])) projName = Phone.S(found["name"]);
                    }
                }
                catch { }
            }
            _pendingConfirmation = new PendingConfirmation("stop", id, projName, _clock() + (long)_confirmWindowMs);
            var confirmBtns = new JsonArray(
                (JsonNode)new JsonArray(
                    (JsonNode)Phone.InlineButton(lang == "it" ? "✅ Sì, ferma" : "✅ Yes, stop", $"yes:{id}"),
                    (JsonNode)Phone.InlineButton(lang == "it" ? "❌ Annulla" : "❌ Cancel", "cancel")
                )
            );
            await Phone.SendTelegramMessageAsync(token, chatId, PhoneVoice.Say(lang, "stopConfirmTap"),
                replyMarkup: new JsonObject { ["inline_keyboard"] = confirmBtns },
                apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            return;
        }

        if (verb == "yes" && _pendingConfirmation != null && _pendingConfirmation.ProjectId == id)
        {
            await HandleTelegramMessageAsync(new JsonObject
            {
                ["chat"] = new JsonObject { ["id"] = chatId },
                ["text"] = "yes"
            });
            return;
        }

        if (verb == "resume")
        {
            await HandleTelegramMessageAsync(new JsonObject
            {
                ["chat"] = new JsonObject { ["id"] = chatId },
                ["text"] = $"/resume {id}"
            });
            return;
        }

        if (verb == "decide_suggest")
        {
            var projId = parts[1];
            var decId = string.Join(":", parts.Skip(2));
            if (string.IsNullOrEmpty(projId) || string.IsNullOrEmpty(decId)) return;
            var lang = PhoneVoice.LanguageOrDefault(Phone.S(Phone.LoadPhoneSettings(_storeDir, _random)["language"]));
            var isIt = lang == "it";
            string? err = null;
            if (_onAnswerDecision != null)
            {
                try
                {
                    var res = await _onAnswerDecision(projId, decId, "accepted");
                    if (res != null && res["ok"]?.GetValue<bool>() == false) err = Phone.S(res["reason"]);
                }
                catch (Exception e)
                {
                    err = e.Message;
                }
            }
            if (err != null)
            {
                var errMsg = isIt && err.Contains("already started")
                    ? "⚠️ Il passo che richiede questa decisione è già iniziato."
                    : $"⚠️ {err}";
                await Phone.SendTelegramMessageAsync(token, chatId, errMsg, replyMarkup: Phone.MainKeyboard(), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
                return;
            }
            await Phone.SendTelegramMessageAsync(token, chatId,
                isIt ? "✅ Decisione accettata con il suggerimento." : "✅ Decision accepted with suggestion.",
                replyMarkup: Phone.MainKeyboard(), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            return;
        }

        if (verb == "decide_lead")
        {
            var projId = parts[1];
            var decId = string.Join(":", parts.Skip(2));
            if (string.IsNullOrEmpty(projId) || string.IsNullOrEmpty(decId)) return;
            var lang = PhoneVoice.LanguageOrDefault(Phone.S(Phone.LoadPhoneSettings(_storeDir, _random)["language"]));
            var isIt = lang == "it";
            string? err = null;
            if (_onDelegateDecision != null)
            {
                try
                {
                    var res = await _onDelegateDecision(projId, decId);
                    if (res != null && res["ok"]?.GetValue<bool>() == false) err = Phone.S(res["reason"]);
                }
                catch (Exception e)
                {
                    err = e.Message;
                }
            }
            if (err != null)
            {
                var errMsg = isIt && err.Contains("already started")
                    ? "⚠️ Il passo che richiede questa decisione è già iniziato."
                    : $"⚠️ {err}";
                await Phone.SendTelegramMessageAsync(token, chatId, errMsg, replyMarkup: Phone.MainKeyboard(), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
                return;
            }
            await Phone.SendTelegramMessageAsync(token, chatId,
                isIt ? "🤖 Decisione affidata al lead." : "🤖 Let the lead decide.",
                replyMarkup: Phone.MainKeyboard(), apiBaseUrl: _apiBaseUrl, fetch: _fetch);
            return;
        }
    }
}
