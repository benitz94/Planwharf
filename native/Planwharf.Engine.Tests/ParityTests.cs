using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

// The module-by-module comparison (native/porting/PLAN.md, phase 1b): every case recorded from the
// JavaScript in native/porting/parity/<module>.json is given to the C# port, which must return the same
// result, or fail with the same message.
public class ParityTests
{
    static readonly string Folder = FindFolder();

    static string FindFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "porting", "parity");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("native/porting/parity not found above " + AppContext.BaseDirectory);
    }

    public static TheoryData<string, int> Cases()
    {
        var data = new TheoryData<string, int>();
        foreach (var file in Directory.GetFiles(Folder, "*.json").OrderBy(file => file, StringComparer.Ordinal))
        {
            var count = JsonNode.Parse(File.ReadAllText(file))!.AsArray().Count;
            for (var i = 0; i < count; i++) data.Add(Path.GetFileNameWithoutExtension(file), i);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_CSharp_port_gives_the_JavaScript_result(string module, int index)
    {
        var recorded = JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, module + ".json")))!.AsArray()[index]!.AsObject();
        var fn = recorded["fn"]!.GetValue<string>();
        var args = recorded["args"]!.AsArray();
        if (!Ports.TryGetValue($"{module}/{fn}", out var port)) throw new Xunit.Sdk.XunitException($"No C# port is wired for {module}/{fn}: add it to ParityTests.Ports.");
        JsonNode? result = null; string? error = null;
        try { result = port(args); }
        catch (InvalidOperationException failure) { error = failure.Message; }
        var where = $"{module}/{fn} case {index}: {args.ToJsonString()}";
        if (recorded["error"] is JsonNode expectedError)
        {
            Assert.True(error != null, $"{where}\nThe JavaScript fails with \"{expectedError}\", the C# returned {result?.ToJsonString() ?? "null"}.");
            Assert.Equal(expectedError.GetValue<string>(), error);
            return;
        }
        Assert.True(error == null, $"{where}\nThe C# fails with \"{error}\", the JavaScript returned {recorded["result"]?.ToJsonString() ?? "null"}.");
        Assert.True(Same(recorded["result"], result), $"{where}\nJavaScript: {recorded["result"]?.ToJsonString() ?? "null"}\nC#:         {result?.ToJsonString() ?? "null"}");
    }

    static JsonObject? Obj(JsonArray args, int i) => i < args.Count ? args[i]?.DeepClone() as JsonObject : null;
    static JsonArray? Arr(JsonArray args, int i) => i < args.Count ? args[i]?.DeepClone() as JsonArray : null;
    static JsonNode? Node(JsonArray args, int i) => i < args.Count ? args[i]?.DeepClone() : null;
    static string? Text(JsonArray args, int i) => i < args.Count && args[i] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    static JsonNode? Num(double? value) => value is null ? null : JsonValue.Create(value.Value);

    // One entry per ported function: how to call the C# with the recorded JavaScript arguments.
    static readonly Dictionary<string, Func<JsonArray, JsonNode?>> Ports = new()
    {
        ["model-policy/migratePolicy"] = a => ModelPolicy.MigratePolicy(Node(a, 0)),
        ["model-policy/validatePolicy"] = a => ModelPolicy.ValidatePolicy(Node(a, 0)),
        ["model-policy/usagePolicy"] = a => ModelPolicy.UsagePolicy(Obj(a, 0)!, Arr(a, 1)),
        ["model-policy/quotaLeft"] = a => Num(ModelPolicy.QuotaLeft(Obj(a, 0)!, Arr(a, 1))),
        ["model-policy/quotaLimit"] = a => ModelPolicy.QuotaLimit(Node(a, 0)),
        ["model-policy/quotaWindows"] = a => ModelPolicy.QuotaWindows(Obj(a, 0)!, Arr(a, 1)),
        ["model-policy/windowKind"] = a => ModelPolicy.WindowKind(Node(a, 0)),
        ["model-policy/lowQuota"] = a => ModelPolicy.LowQuota(Obj(a, 0)!, Arr(a, 1), a.Count > 2 ? Node(a, 2) : null),
        ["model-policy/quotaSwitches"] = a =>
        {
            var o = Obj(a, 0)!;
            var limit = o["thresholdPercent"]?.DeepClone();
            var enabled = o["enabled"] is not JsonValue flag || flag.GetValue<bool>();
            return ModelPolicy.QuotaSwitches(o["running"]?.DeepClone() as JsonArray, o["models"]?.DeepClone() as JsonArray, o["plans"]?.DeepClone() as JsonArray, limit, enabled);
        },
        ["model-policy/availableAgain"] = a => ModelPolicy.AvailableAgain(Obj(a, 0), a.Count > 1 ? (long)a[1]!.GetValue<double>() : null, a.Count > 2 ? (long)a[2]!.GetValue<double>() : null),
        ["model-policy/resolveUsageFallback"] = a => ModelPolicy.ResolveUsageFallback(Obj(a, 0)!, Arr(a, 1)!, Arr(a, 2), _ => true),
        ["model-roles/isVerifySlice"] = a => ModelRoles.IsVerifySlice(Node(a, 0)),
        ["model-roles/supportsSupervisor"] = a => ModelRoles.SupportsSupervisor(Obj(a, 0)!),
        ["model-roles/roleEligibility"] = a => new JsonObject(ModelRoles.RoleEligibility(Obj(a, 0)!).Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
        ["model-roles/withModelRoles"] = a => ModelRoles.WithModelRoles(Obj(a, 0)!, Obj(a, 1)),
        ["model-roles/assignmentRole"] = a => ModelRoles.AssignmentRole(Obj(a, 0)),
        ["model-roles/permitsAssignment"] = a => ModelRoles.PermitsAssignment(Obj(a, 0)!, Obj(a, 1)),
        ["structured-final/supportsFinalResult"] = a => StructuredFinal.SupportsFinalResult(Node(a, 0)),
        ["structured-final/resultSchema"] = a => StructuredFinal.ResultSchema(Obj(a, 0)!),
        ["structured-final/readFinalResult"] = a => StructuredFinal.ReadFinalResult(Text(a, 0), Text(a, 1)),
        ["activity-events/sanitize"] = a => a.Count > 1 ? ActivityEvents.Sanitize(Text(a, 0), (int)a[1]!.GetValue<double>()) : ActivityEvents.Sanitize(Text(a, 0)),
        ["provider-accounts/accountType"] = a => ProviderAccounts.AccountType(Text(a, 0)),
        ["provider-accounts/accountLabel"] = a => ProviderAccounts.AccountLabel(Text(a, 0)!,
            Obj(a, 1) is { } account ? new ProviderAccounts.AccountMetadata(null, account["type"]?.GetValue<string>() ?? "unread", "", null) : null),
    };

    // Same JSON: numbers by value (35 and 35.0 are the same), objects by keys, arrays in order.
    static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonObject oa && b is JsonObject ob)
            return oa.Count == ob.Count && oa.All(pair => ob.TryGetPropertyValue(pair.Key, out var other) && Same(pair.Value, other));
        if (a is JsonArray aa && b is JsonArray ab) return aa.Count == ab.Count && aa.Zip(ab).All(pair => Same(pair.First, pair.Second));
        if (a is JsonValue va && b is JsonValue vb)
        {
            if (va.GetValueKind() == JsonValueKind.Number && vb.GetValueKind() == JsonValueKind.Number)
                return double.Parse(va.ToJsonString(), CultureInfo.InvariantCulture) == double.Parse(vb.ToJsonString(), CultureInfo.InvariantCulture);
            return JsonNode.DeepEquals(va, vb);
        }
        return false;
    }
}
