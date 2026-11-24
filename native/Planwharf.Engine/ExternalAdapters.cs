// Port of engine/external-adapters.cjs.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class ExternalAdapters
{
    public sealed record ApiHostLaunch(string Command, IReadOnlyList<string> Args);
    public sealed record CatalogRequest(string Url, JsonObject Headers, int TimeoutMs, string Redirect);
    public sealed record CatalogResponse(int Status, JsonNode? Body);
    public sealed record Dependencies(IReadOnlyDictionary<string, string> Environment,
        Func<JsonObject, string?> Executable, Func<CatalogRequest, CancellationToken, Task<CatalogResponse>> Transport,
        Func<ApiHostLaunch> ApiHost, Action<int> Terminate);

    public sealed class Adapter(string name, Dependencies dependencies)
    {
        public string NativeModel => @"\S+";
        public bool SourceProtectedResearch => false;
        public JsonObject Capabilities => new() { ["canCode"] = true, ["canResearch"] = false,
            ["canUseTools"] = true, ["canInspectFilesystem"] = true, ["supportsStreaming"] = false };
        public JsonObject Environment(JsonObject inherited) => (JsonObject)inherited.DeepClone();
        public void Terminate(int pid) => dependencies.Terminate(pid);
        public JsonObject ResultIdentity(JsonObject model) => new() {
            ["provider"] = ProviderIdentity(model)?.DeepClone(), ["model"] = model["cli"]?.DeepClone() };
        public Task<JsonObject> QuotaAsync() => Task.FromResult(new JsonObject { ["readable"] = false, ["exhausted"] = false,
            ["leftPct"] = null, ["reason"] = "This adapter has no verified provider balance/quota endpoint. Observed API tokens are recorded separately." });
        public string? Executable(JsonObject provider) => name == "api" ? dependencies.ApiHost().Command : dependencies.Executable(provider);
        public JsonObject Availability(JsonObject provider)
        {
            if (name != "api") return new JsonObject { ["available"] = dependencies.Executable(provider) != null };
            try { ApiHeaders(ApiConfig(provider), dependencies.Environment); return new JsonObject { ["available"] = true }; }
            catch (InvalidOperationException error) { return new JsonObject { ["available"] = false, ["reason"] = error.Message }; }
        }
        public JsonObject Execute(JsonObject model, string folder, string prompt, bool lead = false, bool research = false)
        {
            if (name != "api") return CliSpec(model, folder, prompt, lead, research, dependencies.Executable);
            if (research) throw Error("Source-protected research is not supported by the API adapter.");
            if (!lead && !Truth(model["apiResultOutputPath"])) throw Error("API workers require the structured MegaTask result contract.");
            var config = ApiConfig(model);
            ApiHeaders(config, dependencies.Environment);
            var host = dependencies.ApiHost();
            var args = new JsonArray();
            foreach (var arg in host.Args) args.Add((JsonNode?)JsonValue.Create(arg));
            var envelope = new JsonObject { ["config"] = config, ["model"] = model["cli"]?.DeepClone(), ["prompt"] = prompt,
                ["lead"] = lead, ["advisory"] = model["advisory"] is JsonValue advisory && advisory.TryGetValue<bool>(out var b) && b,
                ["alwaysApprove"] = Permission(model)?.DeepClone() ?? JsonValue.Create(false) };
            if (model.ContainsKey("apiResultOutputPath")) envelope["resultOutputPath"] = model["apiResultOutputPath"]?.DeepClone();
            if (model.ContainsKey("apiResultIdentity")) envelope["resultIdentity"] = model["apiResultIdentity"]?.DeepClone();
            return new JsonObject { ["command"] = host.Command, ["args"] = args, ["cwd"] = folder,
                ["provider"] = ProviderIdentity(model)?.DeepClone(), ["adapter"] = "api", ["model"] = model["cli"]?.DeepClone(),
                ["stdin"] = Compact(envelope) };
        }
        public async Task<JsonObject> DiscoverAsync(JsonObject provider, CancellationToken cancellationToken = default)
        {
            if (name != "api") return new JsonObject { ["ok"] = false,
                ["reason"] = "Configured CLI uses explicit model IDs; no enumeration command has been verified." };
            var config = ApiConfig(provider);
            var response = await dependencies.Transport(new CatalogRequest(Text(config["baseUrl"]) + "/models",
                ApiHeaders(config, dependencies.Environment), 10000, "error"), cancellationToken).ConfigureAwait(false);
            if (response.Status is < 200 or > 299) return new JsonObject { ["ok"] = false,
                ["reason"] = "API model catalog HTTP " + response.Status + "; configured model IDs are preserved." };
            if (response.Body == null) throw Error("Cannot read properties of null (reading 'data')");
            if (response.Body is not JsonObject body || body["data"] is not JsonArray data) return new JsonObject { ["ok"] = false,
                ["reason"] = "API model catalog format changed; expected data array." };
            var models = new JsonArray();
            foreach (var row in data)
            {
                if (row == null) throw Error("Cannot read properties of null (reading 'id')");
                if (row is not JsonObject obj || Text(obj["id"]) is not string id) continue;
                models.Add((JsonNode)new JsonObject { ["cli"] = id,
                    ["label"] = Truth(obj["display_name"]) ? obj["display_name"]!.DeepClone() : JsonValue.Create(id),
                    ["capabilities"] = new JsonObject { ["canCode"] = false, ["canUseTools"] = false, ["canOrchestrate"] = false },
                    ["orchestratorEligible"] = false, ["workerEligible"] = false });
            }
            return new JsonObject { ["ok"] = true, ["models"] = models };
        }
    }

    public static void RegisterExternalAdapters(Action<string, Adapter> register, Dependencies dependencies)
    {
        register("api", new Adapter("api", dependencies));
        register("configured-cli", new Adapter("configured-cli", dependencies));
    }
    internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    internal static bool Truth(JsonNode? node) => node != null && node.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.Null => false,
        System.Text.Json.JsonValueKind.String => Text(node)!.Length != 0,
        System.Text.Json.JsonValueKind.Number => double.Parse(node.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) != 0,
        _ => true
    };
    internal static bool Match(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.ECMAScript);
    internal static string JsString(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray array => string.Join(",", array.Select(value => value == null ? "" : JsString(value))),
        JsonObject => "[object Object]",
        _ => Text(node) ?? node.ToJsonString()
    };
    internal static JsonNode? ProviderIdentity(JsonObject model) => Truth(model["providerId"]) ? model["providerId"] : model["provider"];
    private static JsonNode? Permission(JsonObject model) => (model["policy"] as JsonObject)?["alwaysApprove"] ?? model["alwaysApprove"];
    internal static JsonObject Config(JsonObject provider) => Truth(provider["providerConfig"]) ? provider["providerConfig"] as JsonObject ?? new JsonObject() : provider;
    internal static InvalidOperationException Error(string message) => new(message);
    public static JsonObject ApiConfig(JsonObject? provider)
    {
        var config = Config(provider ?? new JsonObject());
        var baseUrl = JsString(config["baseUrl"]).Trim().Replace("\t", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        // WHATWG special URLs allow a missing authority delimiter (https:example.com).
        baseUrl = Regex.Replace(baseUrl, "^(https?):[/\\\\]*", "$1://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var url))
            throw Error("API_CONFIGURATION_INVALID: baseUrl is required.");
        if (url.UserInfo.Length != 0 || url.Query.Length > 1 || url.Fragment.Length > 1 ||
            (url.Scheme != "https" && !(url.Scheme == "http" && url.Host is "localhost" or "127.0.0.1" or "[::1]")))
            throw Error("API_CONFIGURATION_INVALID: use HTTPS, or HTTP on loopback, without credentials or query parameters.");
        if (config.ContainsKey("authEnv") && !Match(JsString(config["authEnv"]), "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw Error("API_CONFIGURATION_INVALID: authEnv must name an environment variable.");
        var protocol = Truth(config["apiProtocol"]) ? Text(config["apiProtocol"]) : "openai";
        if (protocol is not ("openai" or "anthropic")) throw Error("API_CONFIGURATION_INVALID: apiProtocol must be openai or anthropic.");
        foreach (var (key, max) in new[] { ("maxOutputTokens", 65536), ("maxTurns", 100), ("requestTimeoutMs", 300000) })
            if (config.ContainsKey(key) && !ValidLimit(config[key], max))
                throw Error("API_CONFIGURATION_INVALID: invalid " + key);
        var result = new JsonObject { ["baseUrl"] = url.AbsoluteUri.TrimEnd('/') };
        if (config.ContainsKey("authEnv")) result["authEnv"] = config["authEnv"]?.DeepClone();
        result["apiProtocol"] = protocol;
        result["maxOutputTokens"] = config["maxOutputTokens"]?.DeepClone() ?? JsonValue.Create(4096);
        result["maxTurns"] = config["maxTurns"]?.DeepClone() ?? JsonValue.Create(30);
        result["requestTimeoutMs"] = config["requestTimeoutMs"]?.DeepClone() ?? JsonValue.Create(90000);
        return result;
    }

    public static JsonObject ApiHeaders(JsonObject config, IReadOnlyDictionary<string, string> env)
    {
        var auth = Truth(config["authEnv"]) ? JsString(config["authEnv"]) : null;
        var key = auth != null && env.TryGetValue(auth, out var found) ? found : null;
        if (!string.IsNullOrEmpty(auth) && string.IsNullOrEmpty(key))
            throw Error("PROVIDER_AUTHENTICATION_FAILED: API key environment variable is not set: " + auth);
        var headers = new JsonObject { ["Content-Type"] = "application/json" };
        if (Text(config["apiProtocol"]) == "anthropic")
        {
            headers["anthropic-version"] = "2023-06-01";
            if (!string.IsNullOrEmpty(key)) headers["x-api-key"] = key;
        }
        else if (!string.IsNullOrEmpty(key)) headers["Authorization"] = "Bearer " + key;
        return headers;
    }

    private static bool ValidLimit(JsonNode? value, int max)
    {
        if (value?.GetValueKind() != System.Text.Json.JsonValueKind.Number) return false;
        var number = double.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
        return number >= 1 && number <= max && Math.Truncate(number) == number;
    }

    public static string? ConfiguredCommand(JsonObject provider, IReadOnlyDictionary<string, string> env,
        Func<string, bool> isFile, bool windows)
    {
        var command = Text(Config(provider)["cliCommand"]);
        if (string.IsNullOrEmpty(command) || Match(command, "[\\r\\n\\0]")) return null;
        var absolute = windows ? Match(command, "^(?:[A-Za-z]:[\\\\/]|[\\\\/]{2}|[\\\\/])") : command.StartsWith('/');
        if (absolute) return isFile(command) ? command : null;
        // Bare executable names use PATH; shell wrappers and shell command strings are not executed.
        if (!Match(command, "^[\\w.-]+$")) return null;
        foreach (var dir in (env.TryGetValue("PATH", out var path) ? path : "").Split(windows ? ';' : ':'))
        {
            var extension = windows && !command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ".exe" : "";
            var candidate = JoinExecutable(dir, command + extension, windows);
            if (isFile(candidate)) return candidate;
        }
        return null;
    }

    private static string JoinExecutable(string directory, string command, bool windows)
    {
        var path = directory + (directory.Length == 0 ? "" : "/") + command;
        if (windows) path = path.Replace('\\', '/');
        var absolute = path.StartsWith('/');
        var unc = windows && path.StartsWith("//", StringComparison.Ordinal);
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == ".." && parts.Count != 0 && parts[^1] != "..") parts.RemoveAt(parts.Count - 1);
            else if (part != ".." || !absolute) parts.Add(part);
        }
        var normalized = (unc ? "//" : absolute ? "/" : "") + string.Join("/", parts);
        if (normalized.Length == 0) normalized = ".";
        return windows ? normalized.Replace('/', '\\') : normalized;
    }

    public static JsonObject CliSpec(JsonObject model, string folder, string prompt, bool lead, bool research,
        Func<JsonObject, string?> executable)
    {
        if (research) throw Error("Source-protected research is not supported by configured-cli.");
        var config = model["providerConfig"] as JsonObject ?? new JsonObject();
        var command = executable(model) ?? throw Error("Configured CLI executable not found.");
        var baseArgs = config["cliArgs"] as JsonArray;
        var yolo = Permission(model);
        var permissions = config[Truth(yolo) ? "yoloArgs" : "restrictedArgs"] as JsonArray;
        if (baseArgs == null || !baseArgs.Any(arg => Text(arg)?.Contains("{model}", StringComparison.Ordinal) == true))
            throw Error("CLI_CONFIGURATION_INVALID: cliArgs must explicitly contain {model}.");
        if (permissions == null) throw Error("PERMISSION_MODE_UNSUPPORTED: configure the CLI arguments for the selected YOLO mode explicitly.");
        var args = new JsonArray();
        foreach (var arg in baseArgs.Concat(permissions))
        {
            var text = Text(arg);
            if (text == null || Match(text, "[\\0\\r\\n]")) throw Error("CLI_CONFIGURATION_INVALID: arguments must be single-line strings.");
            args.Add((JsonNode?)JsonValue.Create(text.Replace("{model}", Text(model["cli"]) ?? "undefined", StringComparison.Ordinal).Replace("{cwd}", folder, StringComparison.Ordinal)));
        }
        var protocol = Text(config["outputProtocol"]);
        if (!SupportedProtocol(protocol)) throw Error("CLI_CONFIGURATION_INVALID: declare a supported structured outputProtocol.");
        return new JsonObject { ["command"] = command, ["args"] = args, ["cwd"] = folder,
            ["provider"] = ProviderIdentity(model)?.DeepClone(), ["adapter"] = "configured-cli", ["model"] = model["cli"]?.DeepClone(),
            ["stdin"] = protocol == "antigravity-jsonl" ? Compact(new JsonObject { ["event"] = "user", ["message"] = new JsonObject { ["content"] = prompt } }) + "\n" : prompt,
            ["outputProtocol"] = protocol };
    }
    internal static bool SupportedProtocol(string? protocol) => protocol is "codex-jsonl" or "messages-jsonl" or "antigravity-jsonl";
    internal static string Compact(JsonNode node) => node.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
