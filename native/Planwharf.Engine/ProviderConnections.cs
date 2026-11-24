// Port of engine/provider-connections.cjs.
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public static class ProviderConnections
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    { "id", "label", "adapter", "baseUrl", "authEnv", "apiProtocol", "maxOutputTokens", "maxTurns", "requestTimeoutMs", "cliCommand", "cliArgs", "yoloArgs", "restrictedArgs", "outputProtocol", "modelIds", "toolsEnabled" };

    public static JsonObject Connection(JsonNode? input)
    {
        if (input is JsonArray { Count: 0 }) throw ExternalAdapters.Error("Use a unique lowercase provider ID and a label.");
        if (input is not JsonObject obj || obj.Any(pair => !Allowed.Contains(pair.Key)))
            throw ExternalAdapters.Error("Invalid connection fields. Store an environment variable name, never an API key.");
        var id = ExternalAdapters.Truth(obj["id"]) ? ExternalAdapters.JsString(obj["id"]) : "";
        var label = ExternalAdapters.Text(obj["label"]);
        if (!ExternalAdapters.Match(id ?? "", "^[a-z][a-z0-9-]{0,63}$") || label == null || Trim(label).Length == 0 || label.Length > 100)
            throw ExternalAdapters.Error("Use a unique lowercase provider ID and a label.");
        var adapter = ExternalAdapters.Text(obj["adapter"]);
        if (adapter is not ("api" or "configured-cli")) throw ExternalAdapters.Error("Unsupported connection adapter.");
        if (obj["modelIds"] is not JsonArray models || models.Count is < 1 or > 100 ||
            models.Any(node => ExternalAdapters.Text(node) is not string text || text.Length is < 1 or > 200 ||
                ExternalAdapters.Match(text, "[\\s\\x00-\\x1f\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff]")) ||
            models.Select(node => ExternalAdapters.Text(node)).Distinct(StringComparer.Ordinal).Count() != models.Count)
            throw ExternalAdapters.Error("Provide unique exact model IDs without whitespace.");
        if (obj["toolsEnabled"] is not JsonValue value || !value.TryGetValue<bool>(out var tools))
            throw ExternalAdapters.Error("Declare whether these models support tools.");
        var provider = new JsonObject { ["id"] = obj["id"]?.DeepClone(), ["label"] = Trim(label), ["adapter"] = adapter,
            ["quotaPoolId"] = obj["id"]?.DeepClone(), ["enabled"] = true, ["policy"] = new JsonObject { ["alwaysApprove"] = false } };
        if (adapter == "api")
            foreach (var pair in ExternalAdapters.ApiConfig(obj)) provider[pair.Key] = pair.Value?.DeepClone();
        else
        {
            var command = ExternalAdapters.Text(obj["cliCommand"]);
            if (string.IsNullOrEmpty(command) || ExternalAdapters.Match(command, "[\\r\\n\\0]")) throw ExternalAdapters.Error("Provide a native CLI executable.");
            foreach (var key in new[] { "cliArgs", "yoloArgs", "restrictedArgs" })
            {
                if (obj[key] is not JsonArray args || args.Any(arg => ExternalAdapters.Text(arg) is not string text || ExternalAdapters.Match(text, "[\\r\\n\\0]")))
                    throw ExternalAdapters.Error(key + " must be an explicit JSON array of arguments.");
                provider[key] = args.DeepClone();
            }
            if (!((JsonArray)obj["cliArgs"]!).Any(arg => ExternalAdapters.Text(arg)!.Contains("{model}", StringComparison.Ordinal)))
                throw ExternalAdapters.Error("CLI arguments must include {model}.");
            if (!ExternalAdapters.SupportedProtocol(ExternalAdapters.Text(obj["outputProtocol"])))
                throw ExternalAdapters.Error("Select a supported structured output protocol.");
            provider["cliCommand"] = command;
            provider["outputProtocol"] = obj["outputProtocol"]!.DeepClone();
        }
        var resultModels = new JsonArray();
        foreach (var model in models)
        {
            var cli = ExternalAdapters.Text(model)!;
            resultModels.Add((JsonNode)new JsonObject { ["id"] = id + ":" + cli, ["cli"] = cli, ["label"] = cli, ["enabled"] = true,
                ["orchestratorEligible"] = tools, ["workerEligible"] = tools, ["reviewerEligible"] = tools,
                ["capabilities"] = new JsonObject { ["canCode"] = tools, ["canUseTools"] = tools, ["canResearch"] = false },
                ["availabilityVerified"] = false, ["availabilityWarning"] = "Configured route; account access and model inference have not been verified." });
        }
        provider["models"] = resultModels;
        return provider;
    }

    // ECMAScript trim includes BOM and excludes the .NET-only NEL whitespace character.
    private static string Trim(string text) => text.Trim(' ', '\t', '\n', '\r', '\v', '\f', '\u00a0', '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200a', '\u2028', '\u2029', '\u202f', '\u205f', '\u3000', '\ufeff');
}
