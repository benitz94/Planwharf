// Fake MCP worker used to prove the launch and server boundary (job 25).
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Planwharf.Engine;

namespace Planwharf.FakeCli;

static class McpWorker
{
    public static int Run(string launchPath, string transcriptPath, TextWriter error)
    {
        JsonObject launch;
        try { launch = JsonNode.Parse(File.ReadAllText(launchPath))!.AsObject(); }
        catch (Exception)
        {
            error.WriteLine("planwharf-fake\tlaunch\tmalformed");
            return 2;
        }
        if (Text(launch, "transport") == "json-file")
        {
            File.WriteAllText(Text(launch, "resultFile"), launch["result"]!.ToJsonString());
            Write(transcriptPath, new JsonObject { ["transport"] = "json-file", ["wrote"] = true });
            return 0;
        }
        var server = launch["server"] as JsonObject;
        if (server is null) return Fail(launch, transcriptPath, error, "malformed");
        var start = new ProcessStartInfo(Text(server, "command"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        if (server["args"] is JsonArray serverArgs)
            foreach (var arg in serverArgs) start.ArgumentList.Add(arg!.GetValue<string>());
        Process process;
        try { process = Process.Start(start)!; }
        catch (Exception) { return Fail(launch, transcriptPath, error, "mcp-setup-failed"); }
        using (process)
        {
            var stderr = process.StandardError.ReadToEndAsync();
            var results = new JsonArray();
            var names = new JsonArray();
            try
            {
                Send(process, Request(1, "initialize", new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "planwharf-fake", ["version"] = "1" }
                }));
                if (Read(process) is null) return Fail(launch, transcriptPath, error, "mcp-setup-failed");
                process.StandardInput.Write("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n");
                process.StandardInput.Flush();
                Send(process, Request(2, "tools/list", new JsonObject()));
                var listed = Read(process);
                if (listed?["result"]?["tools"] is JsonArray tools)
                    foreach (var tool in tools) names.Add((JsonNode)JsonValue.Create(Text(tool!.AsObject(), "name"))!);
                var id = 3;
                if (launch["calls"] is JsonArray calls)
                    foreach (var item in calls)
                    {
                        if (item is not JsonObject call) continue;
                        Send(process, Request(id++, "tools/call", new JsonObject { ["name"] = Text(call, "name"), ["arguments"] = call["arguments"]?.DeepClone() }));
                        var response = Read(process);
                        var text = response?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "no-response";
                        results.Add((JsonNode)JsonValue.Create(text)!);
                    }
            }
            catch (Exception)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                return Fail(launch, transcriptPath, error, "mcp-setup-failed");
            }
            process.StandardInput.Close();
            process.WaitForExit(5000);
            _ = stderr;
            Write(transcriptPath, new JsonObject { ["transport"] = "mcp", ["tools"] = names, ["results"] = results, ["serverExit"] = process.ExitCode });
            return 0;
        }
    }

    static int Fail(JsonObject launch, string transcriptPath, TextWriter error, string code)
    {
        var root = Text(launch, "root");
        var token = Text(launch, "token");
        if (root.Length > 0 && token.Length > 0) WorkerMcp.RecordSetupFailure(root, token);
        error.WriteLine("planwharf-fake\tsetup\t" + code);
        Write(transcriptPath, new JsonObject { ["transport"] = "mcp", ["recovery"] = code });
        return 3;
    }

    static void Send(Process process, JsonObject message)
    {
        process.StandardInput.Write(message.ToJsonString());
        process.StandardInput.Write('\n');
        process.StandardInput.Flush();
    }

    static JsonObject? Read(Process process)
    {
        var line = process.StandardOutput.ReadLine();
        return line is null ? null : JsonNode.Parse(line) as JsonObject;
    }

    static JsonObject Request(int id, string method, JsonObject parameters) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters
    };

    static void Write(string path, JsonObject body)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, body.ToJsonString(), new UTF8Encoding(false));
    }

    static string Text(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
