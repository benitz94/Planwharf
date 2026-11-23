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
            if (string.Equals(Path.GetFileName(file), "required.json", StringComparison.OrdinalIgnoreCase)) continue;
            var node = JsonNode.Parse(File.ReadAllText(file));
            if (node is not JsonArray arr) continue;
            var count = arr.Count;
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
        ["provider-refusal/refusalReason"] = a => ProviderRefusal.RefusalReason(Text(a, 0)),
        ["provider-refusal/activeRefusal"] = a => ProviderRefusal.ActiveRefusal(Obj(a, 0), Text(a, 1)!, a[2]!.GetValue<double>()),
        ["provider-refusal/recordRefusal"] = a => a.Count > 4
            ? ProviderRefusal.RecordRefusal(Obj(a, 0)!, Text(a, 1)!, Text(a, 2)!, Text(a, 3)!, a[4]!.GetValue<double>())
            : ProviderRefusal.RecordRefusal(Obj(a, 0)!, Text(a, 1)!, Text(a, 2)!, Text(a, 3)!),
        ["provider-diagnostics/diagnoseProvider"] = a => ProviderDiagnostics.DiagnoseProvider(Obj(a, 0)!, Text(a, 1), Node(a, 2)),
        ["briefing/compactState"] = a => Briefing.CompactState(Obj(a, 0)!, Node(a, 1)),
        ["briefing/recentContext"] = a => Briefing.RecentContext(Arr(a, 0)!),
        ["project-history/summarizeTimeline"] = a => ProjectHistory.SummarizeTimeline(Arr(a, 0)!, Obj(a, 1)!),
        ["project-history/checkpointDiff"] = a => a.Count > 3
            ? ProjectHistory.CheckpointDiff(Obj(a, 0)!, Obj(a, 1)!, a[2]!.GetValue<int>(), Arr(a, 3))
            : ProjectHistory.CheckpointDiff(Obj(a, 0)!, Obj(a, 1)!, a[2]!.GetValue<int>()),
        ["activity-evidence/redact"] = a => ActivityEvidence.Redact(Text(a, 0)),
        ["activity-evidence/technicalEvidence"] = a => ActivityEvidence.TechnicalEvidence(Text(a, 0), Obj(a, 1)),
        ["autonomy/createSettings"] = a => Autonomy.CreateSettings(Text(a, 0)!, Text(a, 1)!),
        ["autonomy/pushGuardEnvironment"] = a => Autonomy.PushGuardEnvironment(Obj(a, 0)),
        ["autonomy/nextStep"] = a => Autonomy.NextStep(Obj(a, 0)!),
        ["autonomy/strictDecisionPrompt"] = a => Autonomy.StrictDecisionPrompt(Text(a, 0)!),
        ["autonomy/cleanFinish"] = a => Autonomy.CleanFinish(Arr(a, 0), Obj(a, 1)),
        ["autonomy/evaluationPrompt"] = a => Autonomy.EvaluationPrompt(Obj(a, 0)!),
        ["autonomy/nudgePrompt"] = a => Autonomy.NudgePrompt(Obj(a, 0)!),
        ["autonomy/assignmentPrompt"] = a => Autonomy.AssignmentPrompt(Obj(a, 0)!),
        ["autonomy/parseDecision"] = a => Autonomy.ParseDecision(Text(a, 0)),
        ["autonomy/parseAssignment"] = a => Autonomy.ParseAssignment(Text(a, 0), a[1]!.GetValue<int>(), Node(a, 2)),
        ["autonomy/splitPrompt"] = a => Autonomy.SplitPrompt(Obj(a, 0)!),
        ["autonomy/fitWeights"] = a => Autonomy.FitWeights(Arr(a, 0), Arr(a, 1)),
        ["autonomy/parseSplit"] = a => Autonomy.ParseSplit(Text(a, 0), Obj(a, 1)!),
        ["plan/example"] = _ => Plan.Example(),
        ["plan/isFolderPath"] = a => (JsonNode)JsonValue.Create(Plan.IsFolderPath(Text(a, 0)))!,
        ["plan/isGithubUrl"] = a => (JsonNode)JsonValue.Create(Plan.IsGithubUrl(Text(a, 0)))!,
        ["plan/taskTiming"] = a => Plan.TaskTiming(Obj(a, 0)),
        ["plan/validate"] = a => (JsonNode)JsonValue.Create(Plan.Validate(Node(a, 0)))!,
        ["plan/validateProject"] = a => (JsonNode)JsonValue.Create(Plan.ValidateProject(Node(a, 0)))!,
        ["plan/createProject"] = a => Plan.CreateProject(Node(a, 0), Obj(a, 1), Text(a, 2)!),
        ["plan/restoreProject"] = a => Plan.RestoreProject(Node(a, 0), Obj(a, 1), Obj(a, 2), Text(a, 3)!),
        ["plan/setPlace"] = a => Plan.SetPlace(Node(a, 0), Obj(a, 1)),
        ["plan/resetProject"] = a => Plan.ResetProject(Node(a, 0), Obj(a, 1), Text(a, 2)!),
        ["plan/removeProject"] = a => Plan.RemoveProject(Node(a, 0), Obj(a, 1)),
        ["plan/appendRevision"] = a => Plan.AppendRevision(Obj(a, 0)!, Obj(a, 1), Text(a, 2)!),
        ["plan/startGrill"] = a => a.Count > 2 ? Plan.StartGrill(Obj(a, 0)!, Text(a, 1)!, Obj(a, 2)) : Plan.StartGrill(Obj(a, 0)!, Text(a, 1)!),
        ["plan/startGrillDocs"] = a => a.Count > 3 ? Plan.StartGrillDocs(Obj(a, 0)!, Arr(a, 1), Text(a, 2)!, Obj(a, 3)) : Plan.StartGrillDocs(Obj(a, 0)!, Arr(a, 1), Text(a, 2)!),
        ["plan/grill"] = a => Plan.Grill(Obj(a, 0)!, Obj(a, 1), Text(a, 2)!),
        ["plan/importRevision"] = a => Plan.ImportRevision(Obj(a, 0)!, Text(a, 1)!, Text(a, 2)!),
        ["plan/createTask"] = a => Plan.CreateTask(Obj(a, 0)!, Obj(a, 1)),
        ["plan/removeTask"] = a => Plan.RemoveTask(Obj(a, 0)!, Obj(a, 1)),
        ["plan/diff"] = a => Plan.Diff(Obj(a, 0), Obj(a, 1)!),
        ["quota/describe"] = a => Quota.Describe(Node(a, 0)),
        ["quota/html"] = a => (JsonNode)JsonValue.Create(Quota.Html(Node(a, 0)))!,
        ["models/getEnabledModels"] = a => Models.GetEnabledModels(Node(a, 0)),
        ["models/setModelEnabled"] = a => Models.SetModelEnabled(Node(a, 0), Text(a, 1), a[2]!.GetValue<bool>()),
        ["models/validateWorkerClaim"] = a => Models.ValidateWorkerClaim(Node(a, 0), Text(a, 1)),
        ["project-export/sanitize"] = a => ProjectExport.Sanitize(Node(a, 0)),
        ["project-export/continueFrom"] = a => ProjectExport.ContinueFrom(Obj(a, 0)),
        ["execution-next/executionNext"] = a => (JsonNode)JsonValue.Create(ExecutionNext.GetExecutionNext(Obj(a, 0), Obj(a, 1)))!,
        ["execution-next/homeNext"] = a => (JsonNode)JsonValue.Create(ExecutionNext.GetHomeNext(Obj(a, 0)!, Obj(a, 1), Obj(a, 2)))!,
        ["provider-catalog/notInstalledReason"] = a => (JsonNode)JsonValue.Create(ProviderCatalog.NotInstalledReason(Obj(a, 0)!))!,
        ["provider-catalog/providerConfiguration"] = a => ProviderCatalog.ProviderConfiguration(Obj(a, 0)!),
        ["provider-catalog/loadCatalog"] = a =>
        {
            var cat = ProviderCatalog.LoadCatalog(Node(a, 0));
            return new JsonObject
            {
                ["providers"] = new JsonArray(cat.Providers.Select(p => (JsonNode)p.DeepClone()).ToArray()),
                ["models"] = new JsonArray(cat.Models.Select(m => (JsonNode)m.DeepClone()).ToArray())
            };
        },
        ["provider-catalog/providerViews"] = a => new JsonArray(ProviderCatalog.ProviderViews(
            Arr(a, 0)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>(),
            Arr(a, 1)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>(),
            Arr(a, 2)?.OfType<JsonObject>().ToList(),
            Obj(a, 3)).Select(v => (JsonNode)v.DeepClone()).ToArray()),
        ["provider-policy/validateProviderPolicy"] = a => ProviderPolicy.ValidateProviderPolicy(Node(a, 0)),
        ["provider-policy/providerPolicyFor"] = a => ProviderPolicy.ProviderPolicyFor(Obj(a, 0)!, Arr(a, 1), Obj(a, 2)),
        ["model-routing/modelRouteError"] = a => (JsonNode?)JsonValue.Create(ModelRouting.ModelRouteError(Obj(a, 0))),
        ["model-routing/resolveCliModel"] = a => (JsonNode?)JsonValue.Create(ModelRouting.ResolveCliModel(Obj(a, 0))),
        ["model-routing/savingModeError"] = a => (JsonNode?)JsonValue.Create(ModelRouting.SavingModeError(Obj(a, 0), Text(a, 1), a.Count > 2 ? a[2]?.GetValue<int>() : null)),
        ["claude/claudeToolArgs"] = a => new JsonArray(Claude.ClaudeToolArgs(Obj(a, 0)).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        ["claude/parseModels"] = a =>
        {
            var (ok, reason, models) = Claude.ParseModels(a[0] as JsonArray);
            var obj = new JsonObject { ["ok"] = ok };
            if (ok) obj["models"] = new JsonArray(models!.Select(m => (JsonNode)m.DeepClone()).ToArray());
            else obj["reason"] = reason;
            return obj;
        },
        ["claude-usage/parseClaudeUsage"] = a => ClaudeUsage.ParseClaudeUsage(Obj(a, 0)),
        ["repository/githubRepo"] = a => (JsonNode?)JsonValue.Create(Repository.GithubRepo(Text(a, 0))),
        ["repository/repositoryAddress"] = a =>
        {
            var addr = Repository.GetRepositoryAddress(Text(a, 0));
            return addr == null ? null : new JsonObject
            {
                ["provider"] = addr.Provider,
                ["repository"] = addr.Repository,
                ["origin"] = addr.Origin,
                ["baseUrl"] = addr.BaseUrl,
                ["url"] = addr.Url
            };
        },
        ["repository/cleanError"] = a => (JsonNode?)JsonValue.Create(Repository.CleanError(Text(a, 0))),
        ["repository/relation"] = a => (JsonNode?)JsonValue.Create(Repository.Relation(a[0]?.GetValue<int>() ?? 0, a[1]?.GetValue<int>() ?? 0)),
        ["push-email/privateEmailRefused"] = a => (JsonNode?)JsonValue.Create(PushEmail.PrivateEmailRefused(Text(a, 0))),
        ["plan-status/statusFrom"] = a => (JsonNode?)JsonValue.Create(PlanStatus.StatusFrom(Text(a, 0), Obj(a, 1))),
        ["plan-status/withStatus"] = a => PlanStatus.WithStatus(Text(a, 0), Obj(a, 1) ?? new JsonObject()),
        ["plan-status/blockedStatus"] = a => (JsonNode?)JsonValue.Create(PlanStatus.BlockedStatus(Obj(a, 0))),
        ["plan-status/providerError"] = a => PlanStatus.ProviderError(Text(a, 0), a.Count > 1 ? (a[1] is JsonValue ? Text(a, 1) : a[1]?.ToJsonString()) : null, Text(a, 2) ?? ""),
        ["plan-status/notePlanRefusal"] = a =>
        {
            var (plans, status) = PlanStatus.NotePlanRefusal(a[0] as JsonArray, Obj(a, 1), Text(a, 2));
            return new JsonObject { ["plans"] = plans, ["status"] = status };
        },
        ["plan-status/mergePlans"] = a => PlanStatus.MergePlans(a[0] as JsonArray, a.Count > 1 ? a[1] as JsonArray : null),
        ["supervisor/label"] = a => (JsonNode?)JsonValue.Create(Supervisor.Label(Text(a, 0))),
        ["supervisor/advisoryError"] = a => (JsonNode?)JsonValue.Create(Supervisor.AdvisoryError(Obj(a, 0), a.Count > 1 && a[1] is JsonArray ar ? ar.OfType<JsonObject>().ToList() : null)),
        ["supervisor/projectFacts"] = a => Supervisor.ProjectFacts(Obj(a, 0)!, Text(a, 1)!, a.Count > 2 ? a[2]?.GetValue<long>() : null),
        ["activity/activityCapabilities"] = a => Activity.ActivityCapabilities(Obj(a, 0)),
        ["activity/activityMessages"] = a => new JsonArray(Activity.ActivityMessages(Text(a, 0)).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        ["activity/interpretActivity"] = a => (JsonNode?)JsonValue.Create(Activity.InterpretActivity(Text(a, 0), Obj(a, 1), a.Count > 2 ? a[2]?.GetValue<bool>() : null)),
        ["activity/workerReport"] = a => (JsonNode?)JsonValue.Create(Activity.WorkerReport(Text(a, 0))),
        ["activity/activityEvents"] = a => new JsonArray(Activity.ActivityEventsList(Text(a, 0), Obj(a, 1)).Select(e => (JsonNode)e.DeepClone()).ToArray()),
        ["activity-sessions/assignmentSession"] = a => ActivitySessions.AssignmentSession(Obj(a, 0)!),
        ["provider-runtime/providerEnvironment"] = a =>
        {
            var dict = a.Count > 1 && a[1] is JsonObject o ? o.ToDictionary(k => k.Key, v => v.Value?.GetValue<string>() ?? "") : new Dictionary<string, string>();
            var res = ProviderRuntime.ProviderEnvironment(Text(a, 0)!, dict, Text(a, 2));
            var outObj = new JsonObject();
            foreach (var kvp in res) outObj[kvp.Key] = kvp.Value;
            return outObj;
        },
        ["provider-runtime/accountEnvironment"] = a =>
        {
            var dict = a.Count > 2 && a[2] is JsonObject o ? o.ToDictionary(k => k.Key, v => v.Value?.GetValue<string>() ?? "") : new Dictionary<string, string>();
            var res = ProviderRuntime.AccountEnvironment(Text(a, 0)!, Obj(a, 1), dict, Text(a, 3));
            var outObj = new JsonObject();
            foreach (var kvp in res) outObj[kvp.Key] = kvp.Value;
            return outObj;
        },
        ["provider-runtime/launchSpec"] = a =>
        {
            var spec = ProviderRuntime.LaunchSpec(Obj(a, 0)!, Text(a, 1)!, Text(a, 2)!, a.Count > 3 && a[3]?.GetValue<bool>() == true, a.Count > 4 && a[4]?.GetValue<bool>() == true);
            var obj = new JsonObject
            {
                ["command"] = spec.Command,
                ["args"] = new JsonArray(spec.Args.Select(arg => (JsonNode)JsonValue.Create(arg)!).ToArray()),
                ["cwd"] = spec.Cwd,
                ["provider"] = spec.Provider,
                ["adapter"] = spec.Adapter,
                ["model"] = spec.Model
            };
            if (spec.Stdin != null) obj["stdin"] = spec.Stdin;
            if (spec.PromptFile != null) obj["promptFile"] = spec.PromptFile;
            return obj;
        },
        ["time-left/roleOf"] = a => (JsonNode?)JsonValue.Create(TimeLeft.RoleOf(Obj(a, 0))),
        ["time-left/stepRecord"] = a => TimeLeft.StepRecord(Obj(a, 0)),
        ["time-left/parseHistory"] = a => new JsonArray(TimeLeft.ParseHistory(Text(a, 0)).Select(r => (JsonNode)r.DeepClone()).ToArray()),
        ["time-left/historyText"] = a => (JsonNode?)JsonValue.Create(TimeLeft.HistoryText(a.Count > 0 && a[0] is JsonArray ar ? ar.OfType<JsonObject>().ToList() : null)),
        ["time-left/estimate"] = a =>
        {
            var obj = a.Count > 0 && a[0] is JsonObject o ? o : new JsonObject();
            var history = obj["history"] as JsonArray;
            var steps = obj["steps"] as JsonArray;
            var now = obj["now"]?.GetValue<string>();
            var histList = history?.OfType<JsonObject>().ToList();
            var stepList = steps?.OfType<JsonObject>().ToList();
            var est = TimeLeft.Estimate(histList, stepList, now);
            if (est == null) return null;
            return new JsonObject
            {
                ["minutes"] = est.Minutes,
                ["endsAt"] = est.EndsAt,
                ["sure"] = est.Sure,
                ["longerThanUsual"] = est.LongerThanUsual
            };
        },
        ["phone-voice/normalizeLanguage"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.NormalizeLanguage(Text(a, 0))),
        ["phone-voice/languageOrDefault"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.LanguageOrDefault(Text(a, 0))),
        ["phone-voice/say"] = a =>
        {
            var vars = a.Count > 2 && a[2] is JsonObject o ? o.ToDictionary(k => k.Key, v => v.Value?.GetValue<string>() ?? "") : null;
            return (JsonNode?)JsonValue.Create(PhoneVoice.Say(Text(a, 0) ?? "", Text(a, 1) ?? "", vars));
        },
        ["phone-voice/telegramAlertLabel"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.TelegramAlertLabel(Text(a, 0) ?? "", Text(a, 1) ?? "")),
        ["phone-voice/plainAlert"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.PlainAlert(Text(a, 0) ?? "", Text(a, 1) ?? "")),
        ["phone-voice/stateLabel"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.StateLabel(Text(a, 0) ?? "", Text(a, 1) ?? "")),
        ["phone-voice/isDetailsRequest"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.IsDetailsRequest(Text(a, 0) ?? "")),
        ["phone-voice/fitTelegram"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.FitTelegram(Text(a, 0) ?? "")),
        ["phone-voice/reasonLine"] = a => (JsonNode?)JsonValue.Create(PhoneVoice.ReasonLine(Text(a, 0) ?? "", Text(a, 1) ?? "")),
        ["step-outcomes/canonicalRole"] = a => (JsonNode?)JsonValue.Create(StepOutcomes.CanonicalRole(Text(a, 0))),
        ["step-outcomes/canonicalClass"] = a => (JsonNode?)JsonValue.Create(StepOutcomes.CanonicalClass(Text(a, 0))),
        ["step-outcomes/resolveModelMeta"] = a =>
        {
            var m = StepOutcomes.ResolveModelMeta(a.Count > 0 ? a[0] : null);
            return new JsonObject { ["company"] = m.Company, ["class"] = m.Class };
        },
        ["step-outcomes/stepOutcomeRecord"] = a => StepOutcomes.StepOutcomeRecord(Obj(a, 0)),
        ["step-outcomes/outcomesSummary"] = a => new JsonArray(StepOutcomes.OutcomesSummary(a.Count > 0 && a[0] is JsonArray ar ? ar.OfType<JsonObject>().ToList() : null).Select(r => (JsonNode)r.DeepClone()).ToArray()),
        ["step-outcomes/summaryText"] = a => (JsonNode?)JsonValue.Create(StepOutcomes.SummaryText(Obj(a, 0))),
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
