// Port of engine/phone-ollama.cjs.
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public sealed record OllamaSettings(bool Enabled, string BaseUrl, string Model);
public sealed record ChatItem(string Role, string Text);
public sealed record OllamaReply(bool Ok, string Model, string Text, string? Reason = null);
public sealed record OllamaModels(bool Ok, IReadOnlyList<string> Models);

public sealed class ChatMemory(int maxMessages = PhoneOllama.MaxMemoryMessages)
{
    private readonly int limit = maxMessages > 0 ? Math.Min(maxMessages, 20) : PhoneOllama.MaxMemoryMessages;
    private readonly Dictionary<string, List<ChatItem>> chats = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public void Remember(string? chatId, string? role, string? text)
    {
        var id = chatId ?? "";
        var clean = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        clean = clean[..Math.Min(clean.Length, 500)];
        if (id.Length == 0 || clean.Length == 0 || role is not ("user" or "assistant")) return;
        lock (gate)
        {
            if (!chats.TryGetValue(id, out var list)) chats[id] = list = [];
            list.Add(new ChatItem(role, clean));
            if (list.Count > limit) list.RemoveRange(0, list.Count - limit);
        }
    }

    public IReadOnlyList<ChatItem> History(string? chatId)
    {
        lock (gate) return chats.TryGetValue(chatId ?? "", out var items) ? [.. items] : [];
    }

    public void Clear(string? chatId) { lock (gate) chats.Remove(chatId ?? ""); }

    public JsonArray Snapshot()
    {
        lock (gate)
        {
            var result = new JsonArray();
            foreach (var (id, items) in chats)
            {
                var entries = new JsonArray();
                foreach (var item in items) entries.Add((JsonNode)new JsonObject { ["role"] = item.Role, ["text"] = item.Text });
                result.Add((JsonNode)new JsonObject { ["chatId"] = id, ["items"] = entries });
            }
            return result;
        }
    }
}

public static class PhoneOllama
{
    public const string DefaultUrl = "http://127.0.0.1:11434";
    public const string DefaultModel = "qwen2.5:3b";
    public const string MemoryFile = "phone-chat-memory.json";
    public const int MaxMemoryMessages = 8;
    private static readonly Regex ModelPattern = new(@"^[A-Za-z0-9_.:@+/-]{1,120}$", RegexOptions.CultureInvariant);
    private static readonly Regex CaptainPrefix = new(@"^(?:(?:⚓|🏴‍☠️)\s*)?(?:ahoy,?\s*)?(?:captain|capitano)[!:\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ClearPhrases = new(StringComparer.Ordinal)
    {
        "/clear", "/forget", "/cancella", "/dimentica", "/pulisci", "/svuota",
        "clear memory", "forget this chat", "cancella memoria", "pulisci memoria", "svuota memoria", "dimentica questa chat"
    };

    public static OllamaSettings DefaultSettings() => new(true, DefaultUrl, DefaultModel);

    public static OllamaSettings NormalizeSettings(JsonNode? raw)
    {
        if (raw is not JsonObject obj) return DefaultSettings();
        var enabled = !obj.ContainsKey("enabled") || Truthy(obj["enabled"]);
        var url = LoopbackUrl(Str(obj["baseUrl"])) ?? DefaultUrl;
        var model = obj["model"] is JsonValue value && value.TryGetValue<string>(out var supplied) ? supplied.Trim() : "";
        return new(enabled, url, ModelPattern.IsMatch(model) ? model : DefaultModel);
    }

    public static string? LoopbackUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != "http" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        if (uri.Host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1")) return null;
        var host = uri.Host == "::1" ? "[::1]" : uri.Host;
        return $"http://{host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}";
    }

    public static string EscapeHtml(string? text) => (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string WithCaptain(string? body, string? language = null)
    {
        var greeting = PhoneVoice.LanguageOrDefault(language) == "it" ? "🏴‍☠️ Ahoy, Capitano!" : "🏴‍☠️ Ahoy, Captain!";
        var trimmed = (body ?? "").Trim();
        if (trimmed.Length == 0) return greeting;
        var stripped = CaptainPrefix.Replace(trimmed, "").Trim();
        return stripped.Length == 0 ? greeting : $"{greeting}\n\n{stripped}";
    }

    public static bool IsClearMemory(string? text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        value = Regex.Replace(value, @"^(/(?:clear|forget|cancella|dimentica|pulisci|svuota))(?:@[a-z0-9_]+)$", "$1", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
        return ClearPhrases.Contains(value);
    }

    public static bool IsBareConfirmation(string? text)
    {
        var value = Regex.Replace((text ?? "").Trim().ToLowerInvariant(), @"[.!?]+$", "");
        return value is "yes" or "si" or "sì";
    }

    internal static string Str(JsonNode? value) => value is null ? "" : value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();
    internal static bool Truthy(JsonNode? value)
    {
        if (value is null) return false;
        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var s)) return s.Length > 0;
            if (scalar.TryGetValue<bool>(out var b)) return b;
            if (scalar.TryGetValue<double>(out var n)) return n != 0 && !double.IsNaN(n);
        }
        return true;
    }

    public static ChatMemory LoadChatMemory(string storeDir, int maxMessages = MaxMemoryMessages)
    {
        var memory = new ChatMemory(maxMessages);
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(storeDir, MemoryFile))) is JsonArray chats)
                foreach (var chat in chats)
                    if (chat?["items"] is JsonArray items)
                        foreach (var item in items)
                            memory.Remember(Str(chat["chatId"]), Str(item?["role"]), Str(item?["text"]));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            // A missing or broken memory file starts empty. Commands keep working.
        }
        return memory;
    }

    public static void SaveChatMemory(string storeDir, ChatMemory memory)
    {
        Directory.CreateDirectory(storeDir);
        var file = Path.Combine(storeDir, MemoryFile);
        var next = $"{file}.{Environment.ProcessId}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(next, memory.Snapshot().ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                IndentSize = 2,
                NewLine = "\n",
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }) + "\n");
            File.Move(next, file, overwrite: true);
        }
        catch
        {
            try { File.Delete(next); } catch { /* Preserve the original failure. */ }
            throw;
        }
    }

    private static string FirstLine(string value) => Regex.Split(value, "\\r?\\n", RegexOptions.ECMAScript)[0];
    private static string Cut(string value, int length) => value[..Math.Min(length, value.Length)];
    private static string Or(JsonNode? value, string fallback) => Truthy(value) ? Str(value) : fallback;
    private static double Number(JsonNode? value)
    {
        if (value is null) return 0;
        if (Str(value).Length == 0) return 0;
        if (value is JsonValue v && v.TryGetValue<bool>(out var boolean)) return boolean ? 1 : 0;
        return double.TryParse(Str(value), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
    }
    private static string JsNumber(double value) => value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);

    public static JsonArray ProjectFacts(JsonNode? projects = null, Func<string, string>? sanitize = null)
    {
        sanitize ??= value => value;
        var facts = new JsonArray();
        if (projects is not JsonArray array) return facts;
        foreach (var project in array.Take(12))
        {
            var goal = FirstLine(Or(project?["goal"], Or(project?["jobTitle"], "")));
            var timeLeft = project?["timeLeft"] as JsonObject;
            var minutes = timeLeft?.ContainsKey("minutes") == true ? Number(timeLeft["minutes"]) : double.NaN;
            var step = project?["step"];
            var name = Cut(sanitize(Or(project?["name"], Or(project?["id"], "Project"))), 80);
            var status = Cut(sanitize(Or(project?["status"], "idle")), 40);
            facts.Add((JsonNode)new JsonObject
            {
                ["name"] = name.Length == 0 ? "Project" : name,
                ["goal"] = Cut(sanitize(goal), 80),
                ["status"] = status.Length == 0 ? "idle" : status,
                ["step"] = step?["step"] is not null && step?["total"] is not null ? $"{JsNumber(Number(step["step"]))}/{JsNumber(Number(step["total"]))}" : "",
                ["minutes"] = double.IsFinite(minutes) && minutes >= 0 ? (JsonNode?)JsonValue.Create(Math.Floor(minutes + 0.5)) : null,
                ["waiting"] = Str(project?["status"]) == "waiting_on_you"
            });
        }
        return facts;
    }

    public static JsonArray BuildReplyMessages(string? question = null, JsonNode? projects = null, JsonNode? history = null,
        Func<string, string>? sanitize = null, string? model = null, string? language = null)
    {
        var lines = new List<string>();
        foreach (var fact in ProjectFacts(projects, sanitize))
        {
            var bits = new List<string> { $"{Str(fact?["name"])}: {Str(fact?["status"])}" };
            if (Truthy(fact?["goal"])) bits.Add($"goal {Str(fact?["goal"])}");
            if (Truthy(fact?["step"])) bits.Add($"step {Str(fact?["step"])}");
            if (fact?["minutes"] is not null) bits.Add($"about {Str(fact["minutes"])}m left");
            if (Truthy(fact?["waiting"])) bits.Add("waiting on the captain");
            lines.Add(string.Join("; ", bits));
        }
        var system = Cut(string.Join("\n", new[]
        {
            "You are Captain, Planwharf's harbor companion.",
            "Reply in the user's language, in at most 4 short sentences.",
            "Speak as a Captain-facing harbor companion.",
            "Use only the project facts below. Do not invent files, paths, commands, or actions.",
            "You cannot stop, resume, or change work. Tell the captain to use /status, /stop, or /resume for that.",
            "If the facts do not answer the question, say so.",
            PhoneVoice.Say(language ?? "en", "conversationExtra"),
            $"The local model answering this message is {(string.IsNullOrEmpty(model) ? DefaultModel : model)}. Do not claim another name.",
            lines.Count > 0 ? $"Facts:\n{string.Join("\n", lines)}" : "Facts: no projects."
        }), 2400);
        var messages = new JsonArray { (JsonNode)new JsonObject { ["role"] = "system", ["content"] = system } };
        if (history is JsonArray prior)
            foreach (var item in prior.Skip(Math.Max(0, prior.Count - MaxMemoryMessages)))
                messages.Add((JsonNode)new JsonObject { ["role"] = Str(item?["role"]) == "assistant" ? "assistant" : "user", ["content"] = Cut(Or(item?["text"], ""), 500) });
        messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = Cut(question ?? "", 500) });
        return messages;
    }

    public static string UnavailableReply(string? language = null, string? reason = null) => PhoneVoice.CompanionOffline(PhoneVoice.LanguageOrDefault(language), reason ?? "");

    public static async Task<OllamaReply> AskOllamaAsync(string? baseUrl, string? model, JsonNode? messages,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, int timeoutMs = 45000,
        string? language = null, CancellationToken cancellationToken = default)
    {
        var first = await AskOnceAsync(baseUrl, model, messages, send, timeoutMs, language, cancellationToken);
        if (first.Ok || cancellationToken.IsCancellationRequested) return first;
        return await AskOnceAsync(baseUrl, model, messages, send, timeoutMs, language, cancellationToken);
    }

    private static async Task<OllamaReply> AskOnceAsync(string? baseUrl, string? model, JsonNode? messages,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, int timeoutMs,
        string? language, CancellationToken cancellationToken)
    {
        var settings = NormalizeSettings(new JsonObject { ["baseUrl"] = baseUrl, ["model"] = model, ["enabled"] = true });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Math.Max(1, timeoutMs == 0 ? 45000 : timeoutMs));
        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["messages"] = messages is JsonArray array ? array.DeepClone() : new JsonArray(),
            ["stream"] = false,
            ["keep_alive"] = "5m",
            ["options"] = new JsonObject { ["temperature"] = 0.2, ["num_predict"] = 180 }
        };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.BaseUrl + "/api/chat")
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString()))
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await send(request, deadline.Token).WaitAsync(deadline.Token);
            JsonNode? data = null;
            try
            {
                var raw = await response.Content.ReadAsStringAsync(deadline.Token).WaitAsync(deadline.Token);
                data = JsonNode.Parse(raw);
            }
            catch (JsonException) { /* Malformed replies have no usable content. */ }
            var answer = Str((data as JsonObject)?["message"] is JsonObject message ? message["content"] : null);
            if (!response.IsSuccessStatusCode || answer.Trim().Length == 0)
            {
                var reason = response.IsSuccessStatusCode ? "empty" : $"HTTP {(int)response.StatusCode}";
                return new(false, settings.Model, UnavailableReply(language, reason), reason);
            }
            return new(true, settings.Model, WithCaptain(EscapeHtml(answer.Trim()), language));
        }
        catch (OperationCanceledException)
        {
            return new(false, settings.Model, UnavailableReply(language, "timeout"), "timeout");
        }
        catch (Exception)
        {
            return new(false, settings.Model, UnavailableReply(language, "unreachable"), "unreachable");
        }
    }

    public static async Task<OllamaModels> ListInstalledModelsAsync(string? baseUrl,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        int timeoutMs = 4000, CancellationToken cancellationToken = default)
    {
        var settings = NormalizeSettings(new JsonObject { ["baseUrl"] = baseUrl, ["enabled"] = true });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Math.Max(1, timeoutMs == 0 ? 4000 : timeoutMs));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.BaseUrl + "/api/tags");
            using var response = await send(request, deadline.Token).WaitAsync(deadline.Token);
            if (!response.IsSuccessStatusCode) return new(false, []);
            var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(deadline.Token).WaitAsync(deadline.Token));
            if ((data as JsonObject)?["models"] is not JsonArray models) return new(false, []);
            return new(true, models.Select(item => Str((item as JsonObject)?["name"])).Where(name => name.Length > 0).ToArray());
        }
        catch (Exception) { return new(false, []); }
    }
}
