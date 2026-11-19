// Port of engine/worker-result.cjs.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public static class WorkerResult
{
    static string? Text(JsonNode? node, string key) => StructuredFinal.String(StructuredFinal.Field(node, key));
    static bool Strings(JsonNode? node) => node is JsonArray a && a.All(x => StructuredFinal.String(x) is not null);

    public static JsonNode? ReadJSON(string? file)
    {
        try
        {
            if (file is null || new FileInfo(file).Length > 1024 * 1024) return null;
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            return JsonNode.Parse(text.StartsWith('\uFEFF') ? text[1..] : text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    public static bool ValidateResult(JsonNode? result, JsonObject identity)
    {
        if (result is not JsonObject r) return false;
        foreach (var key in StructuredFinal.IdentityKeys)
            if (Text(r, key) is not { Length: > 0 } value || value != Text(identity, key)) return false;
        if (Text(r, "status") is not ("COMPLETE" or "FAILED" or "BLOCKED") ||
            Text(r, "summary") is not { } summary || StructuredFinal.Trim(summary).Length == 0 || Text(r, "nextAction") is null) return false;
        if (r.ContainsKey("createdAt") && (Text(r, "createdAt") is not { } date ||
            !DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _))) return false;
        if (!Strings(r["filesChanged"]) || !Strings(r["blockers"]) || r["tests"] is not JsonArray tests) return false;
        if (!tests.All(t => Text(t, "command") is not null && Text(t, "status") is "PASSED" or "FAILED" or "NOT_RUN" && Text(t, "evidence") is not null)) return false;
        return !r.ContainsKey("sources") || Strings(r["sources"]);
    }

    public static JsonObject? ReadValidatedResult(string file, JsonObject identity, string exitFile)
    {
        var result = ReadJSON(file);
        var exit = ReadJSON(exitFile);
        if (!ValidateResult(result, identity) || exit is not JsonObject record || Text(exit, "attemptId") != Text(identity, "attemptId") ||
            record["exitCode"] is not JsonValue code || !code.TryGetValue<double>(out var number) || !double.IsFinite(number) || Math.Truncate(number) != number) return null;
        if (StructuredFinal.Truthy(identity["provider"]) && !JsonNode.DeepEquals(identity["provider"], record["provider"]) ||
            StructuredFinal.Truthy(identity["cli"]) && !JsonNode.DeepEquals(identity["cli"], record["model"])) return null;
        if (Text(result, "status") == "COMPLETE" && number != 0) return null;
        var copy = result!.DeepClone().AsObject();
        // The runner stamps the exit record, so a worker need not spend a call on the clock.
        if (!StructuredFinal.Truthy(copy["createdAt"]) && record.ContainsKey("createdAt")) copy["createdAt"] = record["createdAt"]?.DeepClone();
        copy["exitCode"] = record["exitCode"]!.DeepClone();
        copy["evidencePath"] = file;
        return copy;
    }

    public static string ResultContract(JsonObject opts)
    {
        if (!StructuredFinal.Truthy(opts["structuredResultPath"])) return "";
        var template = new JsonObject();
        foreach (var key in StructuredFinal.IdentityKeys)
        {
            var source = key == "attemptId" ? "attempt" : key;
            if (opts.ContainsKey(source)) template[key] = opts[source]?.DeepClone();
        }
        template["status"] = "COMPLETE | FAILED | BLOCKED";
        template["summary"] = "What actually happened";
        template["filesChanged"] = new JsonArray();
        template["tests"] = JsonNode.Parse("""[{"command":"Exact command or check","status":"PASSED | FAILED | NOT_RUN","evidence":"Observed outcome; do not invent execution"}]""");
        template["blockers"] = new JsonArray();
        template["nextAction"] = "Remaining work or recommended next step";
        var json = template.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        const string tail = "Use one status from the listed choices. Record only observed work and tests. No tests means an empty array. Exit 0 alone is not completion. For research, include reportPath (the absolute report.md path), sources (URLs), and recommendedNextAction.";
        if (StructuredFinal.SupportsFinalResult(opts["model"]))
            return "Planwharf structured result contract:\nFinish by returning this object as your structured final output, with these exact identity fields. Do not write a result file; Planwharf saves it and records the time:\n" + json + "\n" + tail;
        return "Planwharf structured result contract:\nWrite one UTF-8 JSON object to " + Text(opts, "structuredResultPath") + ". If a write_result tool is provided, submit the JSON through that tool instead of writing the result with write_file. Use these exact identity fields:\n" + json + "\n" + tail + " Keep the text execution evidence separate.";
    }

    // A COMPLETE result naming a nonexistent file is a false claim. A missing path
    // is accepted only when Git tracks it, which is what a deletion looks like.
    public static string[] MissingClaims(JsonNode? result, string folder)
    {
        if (Text(result, "status") != "COMPLETE" || StructuredFinal.Field(result, "filesChanged") is not JsonArray files) return [];
        return files.Select(StructuredFinal.String).Where(f => f is not null && StructuredFinal.Trim(f).Length > 0)
            .Select(f => f!).Where(f => !Path.Exists(Path.GetFullPath(f, Path.GetFullPath(folder))) && !Tracked(f, folder)).ToArray();
    }

    static bool Tracked(string file, string folder)
    {
        try
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = folder, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "ls-files", "--error-unmatch", "--", file }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(stdout, stderr);
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { return false; }
    }

    public static JsonObject RejectMissingClaims(JsonObject result, string folder)
    {
        var missing = MissingClaims(result, folder);
        if (missing.Length == 0) return result;
        var copy = result.DeepClone().AsObject();
        copy["status"] = "FAILED";
        copy["blockers"]!.AsArray().Add((JsonNode?)JsonValue.Create("Planwharf found no such file: " + string.Join(", ", missing)));
        return copy;
    }

    public static bool ReportExists(JsonObject result, string folder)
    {
        if (Text(result, "reportPath") is not { Length: > 0 } report) return false;
        try
        {
            if (Path.GetFullPath(report) != Path.Combine(Path.GetFullPath(folder), "report.md")) return false;
            var info = new FileInfo(report);
            return info.Exists && (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0 && info.Length > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
}
