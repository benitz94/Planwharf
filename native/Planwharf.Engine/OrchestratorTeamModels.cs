// Port of the team-model evaluation and selection portions of engine/orchestrator.js.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public sealed record TeamRosterContext
{
    public required JsonNode? Models { get; init; }
    public required JsonObject Store { get; init; }
    public required JsonArray? ProviderDefinitions { get; init; }
    public required JsonArray? PlansLive { get; init; }
    public Func<string>? CurrentProjectId { get; init; }
    public Func<string>? LeadId { get; init; }
    public Func<string>? Now { get; init; }
    public Action? Persist { get; init; }
    public Func<JsonObject, bool>? IsLow { get; init; }
    public Func<Task<JsonObject>>? Tick { get; init; }
    public ExecutionBridge? ExecutionBridge { get; init; }
    // The composition root owns notebook loading and checkpoint confirmation.
    public Func<string, JsonObject, Task<JsonObject>>? Resume { get; init; }
}

public sealed record UsageStats(double Used, double Limit, double Remaining, double Pct, double LeftPct);
public sealed record DispatchPlanItem(string ModelId, string Label, string Slice, string Why, int? Pid, string? LogPath);
public sealed record FleetAssignment(string ProjectId, string Project, string TaskTitle, string? AttemptId,
    bool HistoricalAssignment, string Status, string Slice, int? Pid, bool Alive, string Activity);
public sealed record FleetItem(string Id, string Label, string Plan, string? ProviderId, string QuotaPoolId,
    bool OrchestratorEligible, bool WorkerEligible, bool ReviewerEligible, bool SupervisorEligible,
    JsonObject Roles, JsonObject RoleEligibility, bool ProviderEnabled, string Kind, bool Enabled,
    string State, IReadOnlyList<FleetAssignment> Assignments);
public sealed record CrewReassignRequest(string Type, string ProjectId, JsonObject Assignments, string? Why);
public sealed record RoleTransitionProposal(string ModelId, string Role, bool Enabled, bool Allowed, string? RejectionReason);

public static class OrchestratorTeamModels
{
    static JsonNode? P(JsonNode? node, string key) => (node as JsonObject)?[key];
    static string S(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    static bool T(JsonNode? node) => node is not null && node.GetValueKind() switch
    {
        JsonValueKind.False or JsonValueKind.Null => false,
        JsonValueKind.String => S(node).Length != 0,
        JsonValueKind.Number => N(node) != 0,
        _ => true
    };
    static bool False(JsonNode? node) => node?.GetValueKind() == JsonValueKind.False;
    static bool True(JsonNode? node) => node?.GetValueKind() == JsonValueKind.True;
    static double? Number(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number
        && double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
        && double.IsFinite(n) ? n : null;
    static double N(JsonNode? node, double fallback = 0) => Number(node) ?? fallback;
    static JsonObject Copy(JsonObject node) => (JsonObject)node.DeepClone();
    static IEnumerable<JsonObject> Rows(JsonNode? node) => (node as JsonArray)?.OfType<JsonObject>() ?? [];
    static JsonArray Array(IEnumerable<JsonObject> rows) => new(rows.Select(x => (JsonNode)Copy(x)).ToArray());
    static JsonNode? Or(JsonNode? a, JsonNode? b) => T(a) ? a : b;
    static string Provider(JsonObject item) => S(Or(item["providerId"], item["provider"]));
    static string Pool(JsonObject item) => S(Or(item["quotaPoolId"], item["plan"]));
    static bool Match(string value, string pattern) => Regex.IsMatch(value, pattern,
        RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    static string? RouteError(JsonObject item)
    {
        // The shared port represents an omitted provider as null; JavaScript's diagnostic says undefined.
        if (!False(item["available"]) && !T(item["adapter"]) && !item.ContainsKey("provider"))
            return "Provider/model mismatch: unsupported provider adapter undefined";
        return ModelRouting.ModelRouteError(item);
    }

    public static JsonArray NormalizeModels(JsonNode? models)
    {
        return Array(Rows(models).Where(x => x["id"]?.GetValueKind() == JsonValueKind.String).Select(source =>
        {
            var item = Copy(source);
            var error = item.ContainsKey("provider") || item.ContainsKey("cli") ? RouteError(item) : null;
            item["label"] = string.IsNullOrWhiteSpace(S(item["label"])) ? S(item["id"]) : S(item["label"]);
            item["enabled"] = True(item["enabled"]) && (T(item["providerCatalog"]) || !False(item["available"]) && error is null);
            item["available"] = !False(source["available"]) && error is null;
            item["unavailableReason"] = Or(source["unavailableReason"], JsonValue.Create(error))?.DeepClone();
            item["neverUsable"] = True(item["catalogExcluded"]) || !False(source["available"]) && error is not null;
            item["availabilityWarning"] = T(item["availabilityWarning"]) ? item["availabilityWarning"]!.DeepClone() : null;
            item["plan"] = string.IsNullOrWhiteSpace(S(item["plan"])) ? "Unnamed plan" : S(item["plan"]);
            item["kind"] = S(item["kind"]) is "premium" or "local" ? S(item["kind"]) : "default";
            item["cost"] = Number(item["cost"]) is double cost && cost >= 0 ? cost : 1;
            item["limit"] = Number(item["limit"]) is double limit && limit > 0 ? limit : 50;
            if (string.IsNullOrWhiteSpace(S(item["cli"]))) item.Remove("cli");
            return item;
        }));
    }

    public static UsageStats CalculateUsage(JsonObject? item)
    {
        var used = N(P(item, "used"));
        var limit = Math.Max(0, N(P(item, "limit")));
        var remaining = N(P(item, "remaining"), Math.Max(0, limit - used));
        var pct = limit > 0 ? Math.Min(100, Math.Floor(used * 100 / limit + 0.5)) : 0;
        return new(used, limit, remaining, pct, limit > 0 ? Math.Max(0, 100 - pct) : 100);
    }

    public static JsonObject UsageOf(JsonObject? item)
    {
        var u = CalculateUsage(item);
        return new() { ["used"] = u.Used, ["limit"] = u.Limit, ["remaining"] = u.Remaining, ["pct"] = u.Pct, ["leftPct"] = u.LeftPct };
    }

    static string Name(JsonObject? item) => S(Or(P(item, "label"), Or(P(item, "cli"), P(item, "id"))));
    static readonly string[] Families = ["fable", "opus", "sonnet", "haiku"];
    static int ClaudeOrder(JsonObject? item)
    {
        for (var i = 0; i < Families.Length; i++) if (Match(Name(item), Families[i])) return i;
        return 9;
    }
    public static int LeadTier(JsonObject? model) => ClaudeOrder(model) switch
    {
        0 or 1 => 0, 2 => 1, 3 => 2,
        _ => S(P(model, "kind")) switch { "premium" => 0, "local" => 2, _ => Match(Name(model), @"\b(?:flash|mini|lite|nano|luna)\b") ? 2 : 1 }
    };
    static string PlainName(JsonObject? model) => Regex.Replace(Name(model), @"(\d)-(\d)", "$1.$2", RegexOptions.ECMAScript);
    public static string FamilyOf(JsonObject? model)
    {
        var order = ClaudeOrder(model);
        return order < 4 ? "claude " + Families[order] : Regex.Replace(Regex.Replace(PlainName(model).ToLowerInvariant(), @"\d+(\.\d+)*", " ", RegexOptions.ECMAScript), @"[\s-]+", " ", RegexOptions.ECMAScript).Trim();
    }
    static int Newer(JsonObject a, JsonObject b)
    {
        static double[] Version(JsonObject item)
        {
            var match = Regex.Match(PlainName(item), @"\d+(?:\.\d+)*", RegexOptions.ECMAScript);
            return (match.Success ? match.Value : "0").Split('.').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }
        var x = Version(a); var y = Version(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var difference = (i < y.Length ? y[i] : 0).CompareTo(i < x.Length ? x[i] : 0);
            if (difference != 0) return difference;
        }
        return 0;
    }
    static JsonArray Rank(JsonArray? list, JsonArray? plans, bool quality)
    {
        var indexed = Rows(list).Select((model, index) => (model, index)).ToList();
        indexed.Sort((a, b) =>
        {
            var result = LeadTier(a.model).CompareTo(LeadTier(b.model));
            if (result == 0 && quality) result = ClaudeOrder(a.model).CompareTo(ClaudeOrder(b.model));
            if (result == 0 && FamilyOf(a.model) == FamilyOf(b.model)) result = Newer(a.model, b.model);
            if (result == 0 && !quality)
            {
                var x = ModelPolicy.QuotaLeft(a.model, plans); var y = ModelPolicy.QuotaLeft(b.model, plans);
                result = (x is null).CompareTo(y is null);
                if (result == 0) result = (y ?? 0).CompareTo(x ?? 0);
                if (result == 0) result = ClaudeOrder(a.model).CompareTo(ClaudeOrder(b.model));
            }
            return result == 0 ? a.index.CompareTo(b.index) : result;
        });
        return Array(indexed.Select(x => x.model));
    }
    public static JsonArray LeadRanking(JsonArray? list, JsonArray? plans = null) => Rank(list, plans, false);
    public static JsonArray LeadQuality(JsonArray? list) => Rank(list, null, true);
    public static bool CanOrchestrate(JsonObject? item) => item is not null && !False(P(item["roles"], "orchestrator"))
        && T(item["enabled"]) && !False(item["available"]) && !False(item["providerEnabled"])
        && !False(P(item["capabilities"], "canUseTools")) && !False(P(item["capabilities"], "canInspectFilesystem"))
        && (item["orchestratorEligible"] is { } eligible ? T(eligible) : S(item["kind"]) is "premium" or "default");
    public static bool AllowedBySavingMode(string? mode, JsonObject? item) => string.IsNullOrEmpty(mode)
        || ModelRouting.PersistedSavingMode(mode) == "normal" || ModelRouting.AllowsSavingMode(mode, item, Tier(item));
    static int Tier(JsonObject? item) => Number(P(item, "leadTier")) is double n && n == Math.Truncate(n) && n >= int.MinValue && n <= int.MaxValue ? (int)n : LeadTier(item);
    public static string JobKind(JsonObject? job)
    {
        var text = S(P(job, "title")) + " " + S(P(job, "detail"));
        if (Match(text, @"\b(hard|difficult|escalat\w*|premium|complex)\b")) return "hard";
        return Match(text, @"\b(implement|code|fix|bug|test|build|write)\b") ? "build" : "inspect";
    }
    public static int SliceCount(JsonObject? job, JsonObject? facts, int eligible)
    {
        var pool = eligible > 0 ? eligible : 1;
        return JobKind(job) is "hard" or "build" || (P(facts, "dirty") as JsonArray)?.Count >= 4 ? Math.Min(2, pool) : 1;
    }

    static double Used(JsonNode? usage, string id) => N(P(P(usage, id), "used"));
    static bool Exhausted(JsonArray? plans, string pool) => PlanUsage.PlanExhausted(plans, pool);
    static bool Fits(JsonObject item, JsonObject options)
    {
        var job = options["job"] as JsonObject;
        var plans = options["plans"] as JsonArray;
        var role = ModelRoles.AssignmentRole(job);
        var capabilities = item["capabilities"];
        return T(item["enabled"]) && !False(item["available"]) && ModelRoles.PermitsAssignment(item, job)
            && (role == "reviewer" || (S(P(job, "kind")) == "research" ? !False(P(capabilities, "canResearch"))
                : JobKind(job) != "build" || !False(P(capabilities, "canCode"))))
            && !False(item["providerEnabled"])
            && !(options["skip"] as JsonArray ?? []).Any(x => T(x) && JsonNode.DeepEquals(x, item["id"]))
            && !T(ModelPolicy.UsagePolicy(item, plans)["blocked"])
            && (T(item["providerCatalog"]) || Used(options["usage"], S(item["id"])) + N(item["cost"]) <= N(item["limit"]))
            && !Exhausted(plans, Pool(item)) && AllowedBySavingMode(S(options["savingMode"]), item);
    }

    public static JsonObject? PickWorker(JsonArray? models, JsonObject? options = null) => Pick(models, options ?? new(), null);

    // JSON options cannot carry functions. Context callers use this overload for the quota predicate.
    public static JsonObject? PickWorker(JsonArray? models, JsonObject? options, Func<JsonObject, bool>? low) => Pick(models, options ?? new(), low);
    static JsonObject? Pick(JsonArray? models, JsonObject options, Func<JsonObject, bool>? low)
    {
        var pool = Rows(NormalizeModels(models)).Select((model, index) =>
        {
            model["index"] = index;
            return model;
        }).Where(model => Fits(model, options) && (low is null || !low(model))).ToList();
        if (pool.Count == 0) return null;
        var chosen = ModelRoles.AssignmentRole(options["job"] as JsonObject) == "verifier"
            ? pool.FirstOrDefault(model => ModelRouting.ModelClass(model, Tier(model)) != "light") ?? pool[0] : pool[0];
        chosen["why"] = $"{JobKind(options["job"] as JsonObject)} job · {S(chosen["label"])} on {S(chosen["plan"])}; user catalog order";
        return Copy(chosen);
    }

    public static JsonArray PickWorkers(JsonArray? models, JsonObject? options = null) => PickWorkers(models, options, null);
    public static JsonArray PickWorkers(JsonArray? models, JsonObject? options, Func<JsonObject, bool>? low)
    {
        var opts = options is null ? new JsonObject() : Copy(options);
        var job = opts["job"] as JsonObject;
        var eligible = Rows(NormalizeModels(models)).Count(model => Fits(model, opts));
        var building = ModelRoles.AssignmentRole(job) == "worker" && S(P(job, "kind")) != "research";
        var verifyJob = job is null ? new JsonObject() : Copy(job);
        verifyJob["kind"] = "verify"; verifyJob["title"] = ModelRoles.VerifySlice;
        verifyJob["detail"] = "Run the tests and fix what fails.";
        var count = N(opts["count"]);
        if (opts["count"]?.GetValueKind() == JsonValueKind.String)
            double.TryParse(S(opts["count"]), NumberStyles.Float, CultureInfo.InvariantCulture, out count);
        var want = Math.Max(1, count != 0 ? count : building ? 2 : SliceCount(job, opts["facts"] as JsonObject, eligible));
        var workers = new JsonArray();
        var usage = opts["usage"] as JsonObject ?? new JsonObject();
        if (usage.Parent is null) opts["usage"] = usage;
        var pool = Rows(models).ToList();
        for (var i = 0; i < want; i++)
        {
            var verifying = building && i > 0;
            var provider = workers.Count > 0 ? Provider((JsonObject)workers[0]!) : "";
            var ordered = verifying ? pool.OrderBy(model => Provider(model) == provider).ToList() : pool;
            var nextOptions = Copy(opts);
            if (verifying) nextOptions["job"] = verifyJob.DeepClone();
            var chosen = Pick(Array(ordered), nextOptions, low)
                ?? (verifying && low is not null ? Pick(Array(ordered), nextOptions, null) : null);
            if (chosen is null) break;
            workers.Add((JsonNode)chosen);
            var id = S(chosen["id"]);
            pool = pool.Where(model => S(model["id"]) != id).ToList();
            usage[id] = new JsonObject { ["used"] = Used(usage, id) + N(chosen["cost"]) };
        }
        return workers;
    }

    public static JsonArray DispatchPlan(JsonArray? workers, JsonObject? job, JsonObject? facts = null)
    {
        var list = Rows(workers).ToList();
        var dirty = (P(facts, "dirty") as JsonArray)?.Count > 0;
        string[] slices = ModelRoles.AssignmentRole(job) == "reviewer"
            ? ["Review the approved scope and report findings.", "Review correctness and edge cases; report findings.", "Review tests and verification evidence; report findings."]
            : list.Count == 1 ? ["Whole job"]
            : [dirty ? "Implement the whole job in the project folder, starting from the uncommitted changes, with automated tests. Run the tests before you finish."
                : "Implement the whole job in the project folder, with automated tests. Run the tests before you finish.",
                ModelRoles.VerifySlice + ": read the requirements and run the tests, then go straight to what the tests do not cover (edge cases, error, recovery and concurrency paths), add tests for it and fix what fails. Do not rewrite code that works. If you find something you must not decide or fix yourself, write one line: \"Needs your decision: <plain words a non-programmer understands> | suggestion: <what you recommend> | breaks: yes\" only if the wrong choice could lose work or break the app, otherwise \"breaks: no\"."];
        return Array(list.Select((worker, index) => new JsonObject
        {
            ["modelId"] = worker["id"]?.DeepClone(), ["label"] = Or(worker["label"], worker["id"])?.DeepClone(),
            ["slice"] = index < slices.Length ? slices[index] : $"Parallel slice {index + 1}",
            ["why"] = T(worker["why"]) ? worker["why"]!.DeepClone() : JsonValue.Create(""),
            ["pid"] = null, ["logPath"] = null, ["resultPath"] = null, ["alive"] = false, ["activity"] = ""
        }));
    }

    static JsonObject Object(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject value) return value;
        var created = new JsonObject(); parent[key] = created; return created;
    }
    static JsonObject Merge(params JsonObject?[] objects)
    {
        var result = new JsonObject();
        foreach (var obj in objects) if (obj is not null) foreach (var pair in obj) result[pair.Key] = pair.Value?.DeepClone();
        return result;
    }
    static JsonObject EmptyRoles()
    {
        var result = new JsonObject();
        foreach (var role in ModelRoles.RoleIds) result[role] = false;
        return result;
    }
    static string TeamKey(JsonNode? scope, string id) => (T(scope) ? S(scope) : "-") + "|" + id;
    static JsonObject? Team(TeamRosterContext context) => P(context.Store["projectTeams"], context.CurrentProjectId?.Invoke() ?? "") as JsonObject;
    static string Lead(TeamRosterContext context) => context.LeadId?.Invoke() ?? S((Team(context) ?? context.Store)["orchestratorId"]);
    static bool LeadAllowed(TeamRosterContext context, string id) => !((Team(context) ?? context.Store)["notLead"] as JsonArray ?? []).Any(x => S(x) == id);
    static string Mode(TeamRosterContext context) => ModelRouting.PersistedSavingMode(S(P(context.Store["savingModes"], context.CurrentProjectId?.Invoke() ?? "")));
    static DateTimeOffset Now(TeamRosterContext context) => context.Now is null ? DateTimeOffset.UtcNow
        : DateTimeOffset.TryParse(context.Now(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.MinValue;
    static JsonObject AutoSwitch(TeamRosterContext context)
    {
        var saved = context.Store["autoSwitch"] as JsonObject;
        var legacy = Number(P(saved, "thresholdPercent"));
        if (legacy == 20 || Number(P(saved, "fiveHourPercent")) is not null) legacy = null;
        var five = N(P(saved, "fiveHourPercent"), legacy ?? 35);
        var week = N(P(saved, "weeklyPercent"), legacy ?? 35);
        return new() { ["enabled"] = !False(P(saved, "enabled")), ["fiveHourPercent"] = five, ["weeklyPercent"] = week, ["thresholdPercent"] = Math.Max(five, week) };
    }
    static JsonObject LimitFor(TeamRosterContext context, JsonObject model)
    {
        var own = P(context.Store["quotaLimits"], S(model["id"])) as JsonObject;
        var company = P(context.Store["poolLimits"], S(Or(model["companyPoolId"], JsonValue.Create(Pool(model))))) as JsonObject;
        if (True(P(own, "none")) || own is null && True(P(company, "none"))) return ModelPolicy.QuotaLimit(new JsonObject { ["none"] = true });
        var general = AutoSwitch(context);
        return ModelPolicy.QuotaLimit(new JsonObject
        {
            ["fiveHourPercent"] = Number(P(own, "fiveHourPercent")) ?? Number(P(company, "fiveHourPercent")) ?? N(general["fiveHourPercent"]),
            ["weeklyPercent"] = Number(P(own, "weeklyPercent")) ?? Number(P(company, "weeklyPercent")) ?? N(general["weeklyPercent"])
        });
    }
    static bool Low(TeamRosterContext context, JsonObject item) => context.IsLow?.Invoke(item)
        ?? (T(AutoSwitch(context)["enabled"]) && ModelPolicy.LowQuota(item, context.PlansLive, LimitFor(context, item)));
    static JsonNode? Refused(TeamRosterContext context, string id, DateTimeOffset now)
    {
        // Evaluation must not expire records in the caller's snapshot.
        var row = P(context.Store["refusals"], id);
        return DateTimeOffset.TryParse(S(P(row, "until")), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until) && until > now ? row : null;
    }
    static string RefusalSentence(JsonNode refused, DateTimeOffset today)
    {
        var raw = S(P(refused, "reason"));
        var match = Regex.Match(raw, "\"short_error\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.ECMAScript);
        var shortText = match.Success ? match.Groups[1].Value : Regex.Replace(raw, @"^[A-Z_]+_ERROR:\s*", "", RegexOptions.ECMAScript);
        var detail = Regex.Replace(shortText, @"\s+", " ", RegexOptions.ECMAScript).Trim();
        if (detail.Length > 100) detail = detail[..100];
        var when = "";
        if (DateTimeOffset.TryParse(S(P(refused, "until")), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until))
        {
            // Like JavaScript Date, display retry times in the owner's local timezone.
            today = today.ToLocalTime();
            until = until.ToLocalTime();
            var time = until.ToString("HH:mm", CultureInfo.InvariantCulture);
            when = until.Date == today.Date ? $" Tried again at {time}."
                : $" Tried again on {until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} at {time}.";
        }
        return "Quota used up" + (detail.Length > 0 ? " (" + detail + ")" : "") + "." + when;
    }

    public static JsonArray BuildRoster(TeamRosterContext context)
    {
        var store = context.Store;
        var team = Team(context);
        var now = Now(context);
        var accountRoles = Object(store, "accountModelRoles");
        var bindings = Object(store, "accountBindings");
        var normalized = NormalizeModels(context.Models);
        return Array(Rows(normalized).Select(item =>
        {
            var id = S(item["id"]);
            var legacyEnabled = P(store["enabled"], id) ?? item["enabled"];
            var provider = Rows(context.ProviderDefinitions).FirstOrDefault(p => S(p["id"]) == Provider(item));
            var account = P(provider, "account");
            var providerId = S(P(provider, "id"));
            var scope = T(P(provider, "settingsFolder")) ? providerId : T(P(account, "key")) ? providerId + ":" + S(P(account, "key")) : null;
            var legacy = P(store["modelRoles"], id) as JsonObject ?? item["roles"] as JsonObject ?? (T(legacyEnabled) ? new JsonObject() : EmptyRoles());
            if (scope is not null && accountRoles[scope] is not JsonObject)
            {
                var first = !T(bindings[providerId]);
                if (first) bindings[providerId] = scope;
                var initialized = new JsonObject();
                foreach (var model in Rows(context.Models).Where(model => S(model["providerId"]) == providerId || S(model["provider"]) == providerId))
                {
                    var modelId = S(model["id"]);
                    var preferences = P(store["modelRoles"], modelId) as JsonObject ?? model["roles"] as JsonObject
                        ?? (T(P(store["enabled"], modelId) ?? model["enabled"]) ? new JsonObject() : EmptyRoles());
                    initialized[modelId] = first ? ModelRoles.WithModelRoles(model, preferences)["roles"]!.DeepClone() : EmptyRoles();
                }
                accountRoles[scope] = initialized;
                context.Persist?.Invoke();
            }
            var permissions = team is not null ? P(team["roles"], TeamKey(JsonValue.Create(scope), id)) as JsonObject ?? EmptyRoles()
                : scope is not null ? P(accountRoles[scope], id) as JsonObject ?? EmptyRoles()
                : provider is not null && T(bindings[providerId]) ? EmptyRoles() : legacy;
            item["accountKey"] = T(P(account, "key")) ? P(account, "key")!.DeepClone() : null;
            item["accountType"] = Or(P(account, "type"), JsonValue.Create("unread"))?.DeepClone();
            item["accountScope"] = scope;
            item["settingsFolder"] = T(P(provider, "settingsFolder")) ? P(provider, "settingsFolder")!.DeepClone() : null;
            var plan = Rows(context.PlansLive).FirstOrDefault(plan => S(plan["id"]) == Pool(item));
            item["planStatus"] = Or(P(plan, "planStatus"), JsonValue.Create("unknown"))?.DeepClone();
            item["label"] = ProviderAccounts.AccountLabel(S(item["label"]), new ProviderAccounts.AccountMetadata(S(P(account, "key")), S(P(account, "type")), "", null));
            item["providerEnabled"] = provider is not null ? (P(store["providerEnabled"], providerId) ?? provider["enabled"])?.DeepClone() : JsonValue.Create(true);
            item["policy"] = ModelPolicy.ValidatePolicy(Merge(new JsonObject { ["alwaysApprove"] = item["alwaysApprove"]?.DeepClone() ?? JsonValue.Create(false) },
                ModelPolicy.MigratePolicy(item["policy"] ?? new JsonObject()), ModelPolicy.MigratePolicy(P(store["modelPolicies"], id) ?? new JsonObject())));
            if (provider is not null)
            {
                var shared = (JsonObject)ProviderPolicy.ProviderPolicyFor(provider, context.Models as JsonArray, store)["policy"]!;
                item["policy"] = new JsonObject { ["minRemainingPercent"] = shared["minRemainingPercent"]?.DeepClone(),
                    ["warningRemainingPercent"] = shared["warningRemainingPercent"]?.DeepClone(), ["fallbackModelId"] = null, ["alwaysApprove"] = shared["alwaysApprove"]?.DeepClone() };
                item["fallbackProviderId"] = shared["fallbackProviderId"]?.DeepClone(); item["policyScope"] = providerId;
            }
            item["alwaysApprove"] = item["policy"]!["alwaysApprove"]?.DeepClone();
            item["usagePolicy"] = ModelPolicy.UsagePolicy(item, context.PlansLive);
            item["quotaWindows"] = ModelPolicy.QuotaWindows(item, context.PlansLive);
            var own = P(store["quotaLimits"], id);
            var company = P(store["poolLimits"], S(Or(item["companyPoolId"], JsonValue.Create(Pool(item)))));
            item["quotaLimit"] = Merge(LimitFor(context, item), new JsonObject { ["custom"] = T(own), ["source"] = T(own) ? "model" : T(company) ? "company" : "general", ["pool"] = string.IsNullOrEmpty(Pool(item)) ? null : Pool(item) });
            item["lowQuota"] = Low(context, item);
            var tier = LeadTier(item);
            var weight = S(P(store["modelWeights"], id)).Trim().ToLowerInvariant();
            var overridden = weight is "premium" or "default" or "light";
            item["leadTier"] = tier; item["weight"] = overridden ? weight : new[] { "premium", "default", "light" }[tier];
            item["weightSource"] = overridden ? "team" : "lineup"; item["modelClass"] = ModelRouting.ModelClass(item, tier);
            var used = Used(store["usage"], id);
            item["used"] = used; item["remaining"] = Math.Max(0, N(item["limit"]) - used);
            var resolved = ModelRoles.WithModelRoles(item, permissions);
            resolved["enabled"] = ModelRoles.RoleIds.Any(role => T(P(resolved["roles"], role)));
            var refusal = Refused(context, id, now);
            if (refusal is not null) { resolved["available"] = false; resolved["unavailableReason"] = RefusalSentence(refusal, now); }
            resolved["leadAllowed"] = LeadAllowed(context, id);
            if (DateTimeOffset.TryParse(S(P(P(store["creditsNeeded"], id), "at")), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var creditsAt) && (now - creditsAt).TotalMilliseconds < 86_400_000)
            {
                resolved["available"] = false; resolved["neverUsable"] = true; resolved["creditsNeeded"] = true;
                resolved["unavailableReason"] = "Needs usage credits: your plan no longer includes this model. Buy credits, or leave it out; Look for models again checks it again.";
            }
            return resolved;
        }));
    }

    static bool LeadUsable(TeamRosterContext context, JsonObject model)
    {
        var candidate = Copy(model);
        candidate["roles"] = Merge(model["roles"] as JsonObject, new JsonObject { ["orchestrator"] = true });
        return LeadAllowed(context, S(model["id"])) && ModelRoles.RoleEligibility(model)["orchestrator"]
            && CanOrchestrate(candidate) && !T(ModelPolicy.UsagePolicy(model, context.PlansLive)["blocked"])
            && !Exhausted(context.PlansLive, Pool(model)) && Refused(context, S(model["id"]), Now(context)) is null;
    }
    static void SaveRoles(TeamRosterContext context, JsonObject model, JsonObject roles)
    {
        var id = S(model["id"]);
        var team = Team(context);
        if (team is not null) Object(team, "roles")[TeamKey(model["accountScope"], id)] = roles.DeepClone();
        else
        {
            Object(context.Store, "modelRoles")[id] = roles.DeepClone();
            if (T(model["accountScope"])) Object(Object(context.Store, "accountModelRoles"), S(model["accountScope"]))[id] = roles.DeepClone();
        }
    }
    static string LimitText(TeamRosterContext context, JsonObject model)
    {
        var limit = LimitFor(context, model);
        if (True(limit["none"])) return "no limit";
        var five = N(limit["fiveHourPercent"]); var week = N(limit["weeklyPercent"]);
        return five == week ? FormattableString.Invariant($"{five}%") : FormattableString.Invariant($"{five}% on 5 hours, {week}% on the week");
    }
    static void SwitchLead(TeamRosterContext context, JsonObject from, JsonObject to, string why)
    {
        SaveRoles(context, to, Merge(to["roles"] as JsonObject, new JsonObject { ["orchestrator"] = true }));
        (Team(context) ?? context.Store)["orchestratorId"] = to["id"]?.DeepClone();
        var projectId = context.CurrentProjectId?.Invoke();
        var projects = context.Store["projects"] as JsonObject;
        var targets = !string.IsNullOrEmpty(projectId) ? [projectId] : projects?.Select(x => x.Key)
            .Where(id => !(context.Store["projectTeams"] as JsonObject ?? new()).ContainsKey(id)).ToArray() ?? [];
        var text = $"Planwharf switched the lead: {S(Or(from["label"], from["id"]))} → {S(Or(to["label"], to["id"]))}. Why: {why}";
        foreach (var id in targets)
        {
            var row = Object(Object(context.Store, "projects"), id);
            var log = new JsonArray((JsonNode)new JsonObject { ["at"] = context.Now?.Invoke() ?? Now(context).ToString("O", CultureInfo.InvariantCulture), ["text"] = text });
            foreach (var line in (row["log"] as JsonArray ?? []).Take(11)) log.Add(line?.DeepClone());
            row["log"] = log;
        }
        context.Persist?.Invoke();
    }
    public static JsonObject? SelectLead(TeamRosterContext context)
    {
        var list = BuildRoster(context);
        var id = Lead(context);
        if (string.IsNullOrEmpty(id)) return null;
        var picked = Rows(list).FirstOrDefault(item => S(item["id"]) == id);
        if (picked is null) return null;
        var policy = ModelPolicy.UsagePolicy(picked, context.PlansLive);
        var refusal = Refused(context, id, Now(context));
        var outOfQuota = !False(picked["available"]) && (Low(context, picked) || Exhausted(context.PlansLive, Pool(picked)) || S(policy["state"]) == "limit" || refusal is not null);
        if (outOfQuota)
        {
            var next = Rows(LeadRanking(Array(Rows(list).Where(item => S(item["id"]) != id && LeadUsable(context, item) && !Low(context, item))), context.PlansLive)).FirstOrDefault();
            if (next is not null)
            {
                var left = ModelPolicy.QuotaLeft(picked, context.PlansLive);
                var label = S(Or(picked["label"], picked["id"]));
                var why = refusal is not null ? $"{label} refused the work (credits or quota)." : left is not null
                    ? FormattableString.Invariant($"quota of {label} is at {left}% (switch at {LimitText(context, picked)}).") : $"{label} cannot lead now.";
                SwitchLead(context, picked, next, why);
                return next;
            }
        }
        if (CanOrchestrate(picked) && (S(policy["state"]) is "expired" or "not signed in" || !T(policy["blocked"])) && !Exhausted(context.PlansLive, Pool(picked))) return picked;
        var fallback = ModelPolicy.ResolveUsageFallback(picked, list, context.PlansLive, CanOrchestrate);
        if (fallback is not null && S(fallback["id"]) != id) return Merge(fallback, new JsonObject { ["fallbackFrom"] = id });
        if (refusal is not null)
        {
            var other = Rows(list).FirstOrDefault(item => Provider(item) != Provider(picked) && CanOrchestrate(item)
                && !T(ModelPolicy.UsagePolicy(item, context.PlansLive)["blocked"]) && !Exhausted(context.PlansLive, Pool(item)));
            if (other is not null) return Merge(other, new JsonObject { ["fallbackFrom"] = id });
        }
        return null;
    }

    public static JsonObject ResearchModel(TeamRosterContext context, string modelId)
    {
        var worker = Rows(BuildRoster(context)).FirstOrDefault(model => S(model["id"]) == modelId)
            ?? throw new InvalidOperationException("Requested research model is not in the roster: " + modelId);
        var mode = Mode(context);
        if (!AllowedBySavingMode(mode, worker)) throw new InvalidOperationException(ModelRouting.SavingModeError(worker, mode, Tier(worker)));
        if (!ModelRoles.PermitsAssignment(worker, new JsonObject { ["kind"] = "research" })) throw new InvalidOperationException("The requested model is not enabled for the Worker role: " + modelId);
        if (!T(worker["enabled"]) || False(worker["available"])) throw new InvalidOperationException("Requested research model is disabled or unavailable: " + modelId);
        if (ProviderAdapters.AdapterFor(worker)?.SourceProtectedResearch != true) throw new InvalidOperationException("Source-protected research is not supported by the requested adapter: " + S(worker["provider"]));
        var error = RouteError(worker);
        if (error is not null) throw new InvalidOperationException(error);
        var policy = ModelPolicy.UsagePolicy(worker, context.PlansLive);
        if (T(policy["blocked"])) throw new InvalidOperationException(S(policy["reason"]));
        if (Exhausted(context.PlansLive, S(worker["plan"])) || N(worker["remaining"]) < N(worker["cost"])) throw new InvalidOperationException("Requested research model has no remaining usage: " + modelId);
        return worker;
    }

    static TeamRosterContext InProject(TeamRosterContext context, string? projectId)
    {
        if (string.IsNullOrEmpty(projectId)) return context;
        var teams = Object(context.Store, "projectTeams");
        if (teams[projectId] is not JsonObject)
        {
            var roles = new JsonObject();
            foreach (var item in Rows(BuildRoster(context with { CurrentProjectId = null, LeadId = null })))
                roles[TeamKey(item["accountScope"], S(item["id"]))] = item["roles"]!.DeepClone();
            teams[projectId] = new JsonObject { ["orchestratorId"] = context.Store["orchestratorId"]?.DeepClone(), ["roles"] = roles, ["notLead"] = context.Store["notLead"]?.DeepClone() ?? new JsonArray() };
        }
        return context with { CurrentProjectId = () => projectId, LeadId = null };
    }
    static JsonObject Ok() => new() { ["ok"] = true };
    static JsonObject Error(string reason) => new() { ["ok"] = false, ["reason"] = reason };

    public static JsonObject SetLeadAllowed(TeamRosterContext context, string id, bool allowed, string? projectId = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return Error("invalid data");
        context = InProject(context, projectId);
        if (!Rows(BuildRoster(context)).Any(item => S(item["id"]) == id)) return Error("model not present");
        if (!allowed && Lead(context) == id) return Error("This model is the lead now: choose another lead first.");
        var target = Team(context) ?? context.Store;
        var ids = (target["notLead"] as JsonArray ?? []).Select(S).Distinct(StringComparer.Ordinal).ToList();
        if (allowed) ids.Remove(id); else if (!ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
        target["notLead"] = new JsonArray(ids.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        context.Persist?.Invoke(); return Ok();
    }

    public static JsonObject SetOrchestrator(TeamRosterContext context, string? id, string? projectId = null)
    {
        context = InProject(context, projectId);
        var target = Team(context) ?? context.Store;
        if (string.IsNullOrWhiteSpace(id)) target["orchestratorId"] = null;
        else
        {
            var found = Rows(BuildRoster(context)).FirstOrDefault(item => S(item["id"]) == id);
            if (found is null) return Error("model not present");
            if (!CanOrchestrate(found)) return Error("model not eligible");
            target["orchestratorId"] = id;
        }
        context.Persist?.Invoke(); return Ok();
    }

    static bool ValidRoles(JsonObject roles) => roles.Count > 0 && roles.All(pair => ModelRoles.RoleIds.Contains(pair.Key, StringComparer.Ordinal) && pair.Value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False);
    public static JsonObject SetModelRoles(TeamRosterContext context, string id, JsonObject roles, string? projectId = null)
    {
        context = InProject(context, projectId);
        var model = Rows(BuildRoster(context)).FirstOrDefault(model => S(model["id"]) == id);
        if (model is null) return Error("Model not present.");
        if (roles is null || !ValidRoles(roles)) return Error(Team(context) is not null ? "Invalid model role settings." : "Roles must contain Orchestrator, Worker, Reviewer or Supervisor boolean flags.");
        var eligibility = ModelRoles.RoleEligibility(model);
        foreach (var pair in roles) if (True(pair.Value) && !eligibility[pair.Key]) return Error($"This model is not compatible with the {pair.Key} role.");
        var merged = Merge(model["roles"] as JsonObject, roles);
        SaveRoles(context, model, merged);
        var target = Team(context) ?? context.Store;
        if (S(target["orchestratorId"]) == id && !T(ModelRoles.WithModelRoles(model, merged)["roles"]!["orchestrator"])) target["orchestratorId"] = null;
        context.Persist?.Invoke(); return Ok();
    }

    public static JsonObject SetRoleForAll(TeamRosterContext context, string role, bool enabled, string? projectId = null)
    {
        if (!ModelRoles.RoleIds.Contains(role, StringComparer.Ordinal)) return Error("Invalid bulk role settings.");
        context = InProject(context, projectId);
        foreach (var model in Rows(BuildRoster(context)))
            SaveRoles(context, model, Merge(model["roles"] as JsonObject, new JsonObject { [role] = enabled && ModelRoles.RoleEligibility(model)[role] }));
        if (role == "orchestrator" && !enabled) (Team(context) ?? context.Store)["orchestratorId"] = null;
        context.Persist?.Invoke(); return Ok();
    }

    public static JsonObject SetRolesForMany(TeamRosterContext context, JsonArray updates, string? projectId = null)
    {
        if (updates is null || updates.Count == 0 || updates.Any(node => node is not JsonObject item || item["id"]?.GetValueKind() != JsonValueKind.String)) return Error("Choose at least one model.");
        if (Rows(updates).Any(item => item["roles"] is not JsonObject roles || !ValidRoles(roles))) return Error("Invalid role settings.");
        context = InProject(context, projectId);
        var changed = 0;
        foreach (var update in Rows(updates))
        {
            var id = S(update["id"]);
            var model = Rows(BuildRoster(context)).FirstOrDefault(item => S(item["id"]) == id);
            if (model is null) continue;
            var eligibility = ModelRoles.RoleEligibility(model);
            var fit = new JsonObject();
            foreach (var pair in (JsonObject)update["roles"]!) if (!True(pair.Value) || eligibility[pair.Key]) fit[pair.Key] = pair.Value?.DeepClone();
            if (fit.Count > 0 && True(SetModelRoles(context, id, fit)["ok"])) changed++;
        }
        return new JsonObject { ["ok"] = true, ["changed"] = changed };
    }

    public static JsonObject SetGoldModel(TeamRosterContext context, string? id)
    {
        var model = Rows(BuildRoster(context)).FirstOrDefault(model => S(model["id"]) == id);
        if (id is not null && (model is null || !ModelRoles.RoleEligibility(model)["worker"])) return Error("Choose a configured model compatible with the Worker role.");
        context.Store["goldModelId"] = id; context.Persist?.Invoke(); return Ok();
    }

    public static async Task<JsonObject> ReassignCrew(TeamRosterContext context, JsonObject action, JsonObject row, string by = "lead")
    {
        try
        {
            if (action is null || action.Any(pair => pair.Key is not ("type" or "projectId" or "assignments" or "why"))
                || S(action["type"]) != "reassign_crew" || action["projectId"]?.GetValueKind() != JsonValueKind.String
                || string.IsNullOrEmpty(S(action["projectId"])) || action["assignments"] is not JsonObject assignments)
                return Error("Invalid crew change.");
            if (row is null || !JsonNode.DeepEquals(action["projectId"], row["id"]))
                return Error("The crew change targets a different project from the selected one.");
            var projectId = S(row["id"]);
            var megaTaskId = S(Or(row["megaTaskId"], P(row["megaTask"], "id")));
            if (megaTaskId.Length == 0) return Error("This project has no proposed or stopped work.");
            if (context.ExecutionBridge is null) return Error("Execution bridge is unavailable.");
            var why = S(action["why"]);
            if (why.Length > 300) why = why[..300];
            var mega = context.ExecutionBridge.ReassignRemaining(projectId, megaTaskId, (JsonObject)assignments.DeepClone(), why, by);
            if (S(mega["status"]) == "READY_FOR_APPROVAL") return new() { ["ok"] = true, ["text"] = "Crew changed. Approve the work to start it." };
            if (S(mega["status"]) == "RUNNING")
            {
                if (context.Tick is not null) await context.Tick().ConfigureAwait(false);
                return new() { ["ok"] = true, ["text"] = "Crew changed; the remaining steps start with the new models." };
            }
            var resumed = context.Resume is null ? null : await context.Resume(projectId, new JsonObject { ["megaTaskId"] = megaTaskId }).ConfigureAwait(false);
            if (True(P(resumed, "ok"))) return new() { ["ok"] = true, ["text"] = "Crew changed and the work resumed." };
            return new() { ["ok"] = true, ["text"] = "Crew changed. Press Resume to continue" + (True(P(resumed, "requiresConfirmation"))
                ? " and confirm the changes made since the checkpoint." : T(P(resumed, "reason")) ? ": " + S(P(resumed, "reason")) : ".") };
        }
        catch (Exception error) { return Error(error.Message); }
    }

    public static Task<JsonObject> ChangeCrew(TeamRosterContext context, string projectId, JsonObject assignments)
    {
        var row = P(context.Store["projects"], projectId) as JsonObject;
        return ReassignCrew(context, new JsonObject { ["type"] = "reassign_crew", ["projectId"] = projectId, ["assignments"] = assignments?.DeepClone() },
            Merge(row, new JsonObject { ["id"] = projectId }), "user");
    }

    public static JsonArray FleetOf(JsonArray? models, JsonArray? projects, string? leadId, JsonArray? plans = null)
    {
        return Array(Rows(models).Select(item =>
        {
            var assignments = new JsonArray();
            foreach (var row in Rows(projects))
            {
                var hits = Rows(row["dispatch"]).Where(part => JsonNode.DeepEquals(part["modelId"], item["id"])).ToList();
                foreach (var part in hits)
                {
                    var historical = T(part["historicalAssignment"]);
                    var assignment = new JsonObject
                    {
                        ["projectId"] = row["id"]?.DeepClone(), ["project"] = row["name"]?.DeepClone(),
                        ["taskTitle"] = Or(part["assignmentJobTitle"], historical ? JsonValue.Create("Historical assignment title not retained") : Or(row["jobTitle"], JsonValue.Create("")))?.DeepClone(),
                        ["attemptId"] = T(part["attemptId"]) ? part["attemptId"]!.DeepClone() : null,
                        ["historicalAssignment"] = historical, ["status"] = Or(part["assignmentStatus"], row["status"])?.DeepClone(),
                        ["slice"] = Or(part["slice"], JsonValue.Create(""))?.DeepClone(),
                        ["pid"] = T(part["pid"]) ? part["pid"]!.DeepClone() : null, ["alive"] = T(part["alive"]),
                        ["activity"] = Or(part["activity"], historical ? JsonValue.Create("Original activity is not retained for this historical assignment.") : Or(row["workerActivity"], JsonValue.Create("")))?.DeepClone()
                    };
                    assignments.Add((JsonNode)assignment);
                }
                if (hits.Count == 0 && JsonNode.DeepEquals(row["modelId"], item["id"])) assignments.Add((JsonNode)new JsonObject
                {
                    ["projectId"] = row["id"]?.DeepClone(), ["project"] = row["name"]?.DeepClone(), ["status"] = row["status"]?.DeepClone(),
                    ["slice"] = "", ["pid"] = T(row["workerPid"]) ? row["workerPid"]!.DeepClone() : null,
                    ["alive"] = T(row["workerAlive"]), ["activity"] = Or(row["workerActivity"], JsonValue.Create(""))?.DeepClone()
                });
            }
            var working = Rows(assignments).Any(x => S(x["status"]) == "watching");
            var state = working ? "working" : False(item["available"]) ? "unavailable" : False(item["providerEnabled"]) ? "provider-disabled"
                : !T(item["enabled"]) ? "restricted" : assignments.Count > 0 ? "assigned" : "idle";
            var exhausted = Exhausted(plans, S(item["plan"]));
            var lead = !string.IsNullOrEmpty(leadId) && S(item["id"]) == leadId;
            var roles = item["roles"] as JsonObject;
            var eligibility = new JsonObject();
            foreach (var (key, value) in ModelRoles.RoleEligibility(item)) eligibility[key] = value;
            var output = new JsonObject();
            foreach (var key in new[] { "id", "label", "kind" }) if (item.ContainsKey(key)) output[key] = item[key]?.DeepClone();
            output["plan"] = Or(item["plan"], JsonValue.Create(""))?.DeepClone();
            if (item.ContainsKey("providerId") || item.ContainsKey("provider")) output["providerId"] = Or(item["providerId"], item["provider"])?.DeepClone();
            if (item.ContainsKey("quotaPoolId") || item.ContainsKey("plan")) output["quotaPoolId"] = Or(item["quotaPoolId"], item["plan"])?.DeepClone();
            output["orchestratorEligible"] = item["orchestratorEligible"]?.DeepClone() ?? JsonValue.Create(S(item["kind"]) is "premium" or "default");
            output["workerEligible"] = !False(item["workerEligible"]); output["reviewerEligible"] = !False(item["reviewerEligible"]);
            output["supervisorEligible"] = eligibility["supervisor"]!.DeepClone();
            output["roles"] = roles?.DeepClone() ?? ModelRoles.WithModelRoles(item)["roles"]!.DeepClone();
            output["roleEligibility"] = item["roleEligibility"]?.DeepClone() ?? eligibility;
            output["providerEnabled"] = !False(item["providerEnabled"]); output["enabled"] = T(item["enabled"]);
            output["available"] = !False(item["available"]);
            foreach (var key in new[] { "unavailableReason", "availabilityCheckedAt", "availabilityWarning", "accountKey", "provider", "cli" })
                output[key] = T(item[key]) ? item[key]!.DeepClone() : null;
            output["accountType"] = Or(item["accountType"], JsonValue.Create("unread"))?.DeepClone();
            output["catalogExcluded"] = True(item["catalogExcluded"]);
            output["neverUsable"] = True(item["neverUsable"]) || True(item["catalogExcluded"]);
            output["availabilityVerified"] = True(item["availabilityVerified"]);
            output["exhausted"] = exhausted;
            var roleNames = ModelRoles.RoleIds.Where(role => T(P(roles, role))).Select(role => char.ToUpperInvariant(role[0]) + role[1..]);
            output["role"] = lead ? "Lead" : roles is not null ? (roleNames.Any() ? string.Join(" / ", roleNames) : "No allowed roles") : CanOrchestrate(item) ? "Eligible" : "Worker";
            output["eligible"] = CanOrchestrate(item); output["lead"] = lead;
            output["state"] = exhausted && !working ? "exhausted" : state; output["busy"] = working; output["assignments"] = assignments;
            return output;
        }));
    }
}
