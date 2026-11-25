// Port of order parsing, command interpretation, intent classification, and order routing from engine/orchestrator.js.
// Strictly stateless and side-effect-free per sheet 46e.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public sealed record CheckpointTalkInfo(
    string Activity,
    string Note,
    string Orch,
    string DoingTemplate)
{
    public string FormatDoing(string name, string title) =>
        DoingTemplate.Replace("{name}", name).Replace("{title}", title);
}

public sealed record TurnHistory(
    string Question,
    string Answer,
    string? ProjectId = null,
    string? ChatSessionId = null,
    string? GrillKind = null);

public abstract record ParsedOrder(string Action, string? ProjectId)
{
    public string? GoldModel { get; init; }

    public virtual JsonObject ToJsonObject()
    {
        var obj = new JsonObject { ["action"] = Action };
        if (ProjectId != null) obj["projectId"] = ProjectId;
        if (GoldModel != null) obj["goldModel"] = GoldModel;
        return obj;
    }
}

public sealed record ModelToggleOrder(string Id, bool Enabled, string Label)
    : ParsedOrder("model", null)
{
    public override JsonObject ToJsonObject()
    {
        var obj = base.ToJsonObject();
        obj["id"] = Id;
        obj["enabled"] = Enabled;
        obj["label"] = Label;
        return obj;
    }
}

public sealed record LeadSelectOrder(string Id, string Label)
    : ParsedOrder("lead", null)
{
    public override JsonObject ToJsonObject()
    {
        var obj = base.ToJsonObject();
        obj["id"] = Id;
        obj["label"] = Label;
        return obj;
    }
}

public sealed record LifecycleOrder(string Action, string? ProjectId, string? Name)
    : ParsedOrder(Action, ProjectId) // "start", "stop", "resume", "approve"
{
    public override JsonObject ToJsonObject()
    {
        var obj = base.ToJsonObject();
        if (Name != null) obj["name"] = Name;
        return obj;
    }
}

public sealed record SavingModeOrder(string SavingMode, string? ProjectId)
    : ParsedOrder("saving-mode", ProjectId)
{
    public override JsonObject ToJsonObject()
    {
        var obj = base.ToJsonObject();
        obj["savingMode"] = SavingMode;
        return obj;
    }
}

public sealed record ChatModeOrder(string Mode, string? ChatMode, string? ProjectId)
    : ParsedOrder("mode", ProjectId)
{
    public override JsonObject ToJsonObject()
    {
        var obj = base.ToJsonObject();
        obj["mode"] = Mode;
        if (ChatMode != null) obj["chatMode"] = ChatMode;
        return obj;
    }
}

public sealed record SystemQueryOrder(string Action, string? ProjectId)
    : ParsedOrder(Action, ProjectId); // "quota", "state", "endGrill"

public sealed record RejectOrder(string Mode, string? ProjectId)
    : ParsedOrder("reject", ProjectId)
{
    public override JsonObject ToJsonObject()
    {
        var obj = base.ToJsonObject();
        obj["mode"] = Mode;
        return obj;
    }
}

public sealed record ProjectSummary(
    string Id,
    string Name,
    string? Status = null,
    string? ChatMode = null,
    string? SavingMode = null,
    string? MegaTaskId = null)
{
    public static ProjectSummary? FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var id = obj["id"]?.GetValue<string>() ?? string.Empty;
        var name = obj["name"]?.GetValue<string>() ?? id;
        var status = obj["status"]?.GetValue<string>();
        var chatMode = obj["chatMode"]?.GetValue<string>();
        var savingMode = obj["savingMode"]?.GetValue<string>();
        var megaTaskId = obj["megaTaskId"]?.GetValue<string>();
        return new ProjectSummary(id, name, status, chatMode, savingMode, megaTaskId);
    }
}

public sealed record ModelCatalogEntry(
    string Id,
    string Label)
{
    public static ModelCatalogEntry? FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var id = obj["id"]?.GetValue<string>() ?? string.Empty;
        var label = obj["label"]?.GetValue<string>() ?? id;
        return new ModelCatalogEntry(id, label);
    }
}

public sealed record OrchestratorSnapshot(
    IReadOnlyList<ProjectSummary>? Projects = null,
    IReadOnlyList<ModelCatalogEntry>? Models = null,
    string? Mode = null,
    string? OrchestratorLabel = null,
    string? OrchestratorId = null)
{
    public static OrchestratorSnapshot FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj) return new OrchestratorSnapshot();

        List<ProjectSummary>? projects = null;
        if (obj["projects"] is JsonArray projArr)
        {
            projects = new List<ProjectSummary>();
            foreach (var item in projArr)
            {
                var summary = ProjectSummary.FromJson(item);
                if (summary != null) projects.Add(summary);
            }
        }

        List<ModelCatalogEntry>? models = null;
        if (obj["models"] is JsonArray modArr)
        {
            models = new List<ModelCatalogEntry>();
            foreach (var item in modArr)
            {
                var entry = ModelCatalogEntry.FromJson(item);
                if (entry != null) models.Add(entry);
            }
        }

        var mode = obj["mode"]?.GetValue<string>();
        var orchLabel = obj["orchestratorLabel"]?.GetValue<string>();
        var orchId = obj["orchestratorId"]?.GetValue<string>();

        return new OrchestratorSnapshot(projects, models, mode, orchLabel, orchId);
    }
}

public sealed record AskRoutingOptions(
    IReadOnlyList<TurnHistory>? Conversation = null,
    string? RequestedGrillKind = null,
    string? GoldModel = null,
    bool Phase2 = false);

public sealed record OrderRouteResult(
    bool Ok,
    string? Reason = null,
    ParsedOrder? Order = null,
    string? GrillKind = null,
    bool ResearchRequested = false,
    bool FreshTask = false,
    ProjectSummary? TargetProject = null);

public static partial class OrchestratorOrderParsing
{
    [GeneratedRegex(@"^(how |what |why |who |where |when |which |quanto |quale |come sta|come mai|che (ne |sta |fa )|chi |perch|status\b|quota\b|how far)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuestionPrefixRegex();

    [GeneratedRegex(@"^(do it|fallo|procedi|smista|go)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExactWorkRegex();

    [GeneratedRegex(@"\b(implement(?:a|are)?|smista|dispatch|assign(?:a)? |metti i worker|dai il lavoro|do the (?:job|task|work)|fai (?:il |la )(?:job|task|lavoro)|fallo(?: fare)?|lavora(?:ci)?(?: su)?|procedi|esegui|costruisci|continua(?:re)?|continue\b|go ahead|vai avanti|fix (?:the |this )|sistema )\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoundaryWorkRegex();

    [GeneratedRegex(@"^(?:please\s+|per favore\s+)?(?:(?:vorrei|voglio|puoi|can you)\s+(?:fare\s+|fai\s+|an?\s+)?)?(?:implement(?:a|are)?|build|fix|sistema|esegui|fai|audit|review|analizza|controlla|verifica)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FreshTaskRegex();

    [GeneratedRegex(@"^(research|investigate|cerca|ricerca|indaga)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResearchLeadingRegex();

    [GeneratedRegex(@"\b(fai|fa|manda|assegna|chiedi|vorrei|voglio|puoi|can you|please|have|ask|assign|delegate|send|avvia)\b[\s\S]*\b(research|researching|ricerca|ricerche|cercare|cerca|investiga|indaga|investigate)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResearchDelegationRegex();

    [GeneratedRegex(@"^(fallo|procedi|vai|do it|go ahead|yes|s[iì])(?:[.!\s]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResearchAffirmativeRegex();

    [GeneratedRegex(@"\b(research|ricerca|ricerche|cercare|investigat\w*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResearchHistoryRegex();

    [GeneratedRegex(@"\b(off|disable|spegni|disattiva|turn off)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModelOffRegex();

    [GeneratedRegex(@"\b(on|enable|accendi|attiva|turn on)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModelOnRegex();

    [GeneratedRegex(@"\b(orchestrator|as lead|use as|usa come)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModelLeadRegex();

    [GeneratedRegex(@"[.!?]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingPunctuationRegex();

    [GeneratedRegex(@"[.!]+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingExclDotRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleWhitespaceRegex();

    [GeneratedRegex(@"^(?:mode normal|modalit[aà] normale)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SavingNormalRegex();

    [GeneratedRegex(@"^(?:mode safe|modalit[aà] sicura)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SavingSafeRegex();

    [GeneratedRegex(@"^(?:mode ultra safe|mode ultra-safe|modalit[aà] ultra sicura)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SavingUltraSafeRegex();

    [GeneratedRegex(@"^(?:mode talk|mode chat|modalit[aà] chat)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatTalkRegex();

    [GeneratedRegex(@"^(?:mode plan|modalit[aà] (?:plan|piano))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatPlanRegex();

    [GeneratedRegex(@"^(?:mode pipeline|modalit[aà] pipeline)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatPipelineRegex();

    [GeneratedRegex(@"\b(mode|modalit[aà]|switch to|set to|passa a|passa ad)(?:\b|(?<=[à]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatWantsModeRegex();

    [GeneratedRegex(@"\bpipeline\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatWordPipelineRegex();

    [GeneratedRegex(@"\b(automatic|auto)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatWordAutoRegex();

    [GeneratedRegex(@"\bauto start\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatWordAutoStartRegex();

    [GeneratedRegex(@"\b(manual|manuale)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatWordManualRegex();

    [GeneratedRegex(@"\b(talk|chat)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatWordTalkRegex();

    [GeneratedRegex(@"\b(?:plan|piano)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatWordPlanRegex();

    [GeneratedRegex(@"^(?:stop|ferma|kill|arresta)(?:\s+(.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StopCommandRegex();

    [GeneratedRegex(@"^(?:resume|riprendi)(?:\s+(.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResumeCommandRegex();

    [GeneratedRegex(@"^(?:approve run|approva|approve)(?:\s+(.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApproveCommandRegex();

    [GeneratedRegex(@"^(?:auto start|avvia|start)(?:\s+(.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartCommandRegex();

    [GeneratedRegex(@"^grill me[.!?]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GrillMeRegex();

    [GeneratedRegex(@"^grill with (?:the )?docs[.!?]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GrillDocsRegex();

    [GeneratedRegex(@"^(?:stop|finish|end) grill(?:ing)?[.!?]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndGrillRegex();

    [GeneratedRegex(@"^(?:quota(?: remaining)?|remaining quota|how much quota is left\??|provider status|model status)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotaQueryRegex();

    [GeneratedRegex(@"^(?:status|current (?:process )?state|current checkpoint(?: identity)?|selected project)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StateQueryRegex();

    // 2.1 looksLikeQuestion
    public static bool LooksLikeQuestion(string? question)
    {
        var t = (question ?? string.Empty).Trim();
        if (t.EndsWith('?')) return true;
        return QuestionPrefixRegex().IsMatch(t);
    }

    // 2.2 looksLikeWork
    public static bool LooksLikeWork(string? question)
    {
        var t = (question ?? string.Empty).Trim();
        if (ExactWorkRegex().IsMatch(t)) return true;
        return BoundaryWorkRegex().IsMatch(t);
    }

    // 2.3 freshTaskIntent
    public static bool FreshTaskIntent(string? question)
    {
        var t = (question ?? string.Empty).Trim();
        return FreshTaskRegex().IsMatch(t);
    }

    // 2.4 checkpointTalk
    public static CheckpointTalkInfo CheckpointTalk(string? reason)
    {
        var why = string.IsNullOrEmpty(reason) ? "deadline" : reason;
        if (why == "application update")
        {
            return new CheckpointTalkInfo(
                Activity: "Paused for an application update. Checkpoint saved.",
                Note: "Paused for an application update. Checkpoint saved. Press Resume after restart.",
                Orch: "Saved unfinished work before updating.",
                DoingTemplate: "Paused {name}: {title}. Update checkpoint saved.");
        }
        if (why is "stop" or "shutdown")
        {
            return new CheckpointTalkInfo(
                Activity: "Stopped. Checkpoint saved.",
                Note: "Stopped. Checkpoint saved. Press Resume to continue.",
                Orch: "Stopped. Checkpoint saved.",
                DoingTemplate: "Stopped {name} · {title}. Checkpoint saved.");
        }
        if (why == "deadline")
        {
            return new CheckpointTalkInfo(
                Activity: "Deadline reached. Checkpoint saved.",
                Note: "Deadline reached. Checkpoint saved. Resume next session.",
                Orch: "Paused at deadline. Checkpoint saved.",
                DoingTemplate: "Deadline reached on {name} · {title}. Checkpoint saved.");
        }
        return new CheckpointTalkInfo(
            Activity: "Job ended without completing. Checkpoint saved.",
            Note: $"Job ended ({why}). Checkpoint saved. Press Resume to continue.",
            Orch: "Job ended without completing. Checkpoint saved.",
            DoingTemplate: $"{{name}} · {{title}} ended without completing ({why}). Checkpoint saved.");
    }

    // 2.5 researchIntent
    public static bool ResearchIntent(string? question, IReadOnlyList<TurnHistory>? history = null)
    {
        var q = (question ?? string.Empty).Trim();
        if (ResearchLeadingRegex().IsMatch(q)) return true;
        if (ResearchDelegationRegex().IsMatch(q)) return true;
        if (ResearchAffirmativeRegex().IsMatch(q) && history != null && history.Count > 0)
        {
            var first = history[0];
            var combined = (first.Question ?? string.Empty) + " " + (first.Answer ?? string.Empty);
            if (ResearchHistoryRegex().IsMatch(combined)) return true;
        }
        return false;
    }

    // 2.6 pickAskedProject
    public static ProjectSummary? PickAskedProject(
        OrchestratorSnapshot? snap,
        string? projectId,
        string? question)
    {
        var rows = snap?.Projects ?? (IReadOnlyList<ProjectSummary>)Array.Empty<ProjectSummary>();
        if (rows.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var trimmed = projectId.Trim();
            return rows.FirstOrDefault(r => string.Equals(r.Id, trimmed, StringComparison.Ordinal));
        }
        var q = (question ?? string.Empty).ToLowerInvariant();
        var named = rows.FirstOrDefault(row =>
            (!string.IsNullOrEmpty(row.Name) && q.Contains(row.Name.ToLowerInvariant())) ||
            (!string.IsNullOrEmpty(row.Id) && q.Contains(row.Id.ToLowerInvariant())));
        if (named != null) return named;
        return rows.Count == 1 ? rows[0] : null;
    }

    // 2.7 parseOrder
    public static ParsedOrder? ParseOrder(
        string? question,
        OrchestratorSnapshot? snap,
        string? projectId)
    {
        var q = (question ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(q)) return null;

        var ql = q.ToLowerInvariant();
        var row = PickAskedProject(snap, projectId, q);
        var commandText = TrailingExclDotRegex().Replace(ql, string.Empty).Trim();

        bool MatchesProjectCommand(Regex regex, out string? target)
        {
            target = null;
            var match = regex.Match(commandText);
            if (!match.Success) return false;
            var rawTarget = (match.Groups[1].Value ?? string.Empty).Trim();
            if (rawTarget.StartsWith("on ", StringComparison.OrdinalIgnoreCase))
                rawTarget = rawTarget[3..].Trim();
            target = rawTarget;
            if (string.IsNullOrEmpty(target)) return true;
            if (row != null)
            {
                if (!string.IsNullOrEmpty(row.Id) && string.Equals(row.Id, target, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrEmpty(row.Name) && string.Equals(row.Name, target, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        var models = snap?.Models ?? (IReadOnlyList<ModelCatalogEntry>)Array.Empty<ModelCatalogEntry>();
        ModelCatalogEntry? namedModel = null;
        foreach (var item in models)
        {
            var label = (item.Label ?? string.Empty).ToLowerInvariant();
            var id = (item.Id ?? string.Empty).ToLowerInvariant();
            if ((label.Length > 2 && ql.Contains(label)) || (!string.IsNullOrEmpty(id) && ql.Contains(id)))
            {
                namedModel = item;
                break;
            }
        }

        var off = ModelOffRegex().IsMatch(q);
        var on = ModelOnRegex().IsMatch(q);
        if (namedModel != null && off && !on)
            return new ModelToggleOrder(namedModel.Id ?? string.Empty, false, namedModel.Label ?? string.Empty);
        if (namedModel != null && on && !off)
            return new ModelToggleOrder(namedModel.Id ?? string.Empty, true, namedModel.Label ?? string.Empty);
        if (namedModel != null && ModelLeadRegex().IsMatch(ql))
            return new LeadSelectOrder(namedModel.Id ?? string.Empty, namedModel.Label ?? string.Empty);

        if (MatchesProjectCommand(StopCommandRegex(), out _))
            return new LifecycleOrder("stop", row?.Id, row?.Name);
        if (MatchesProjectCommand(ResumeCommandRegex(), out _))
            return new LifecycleOrder("resume", row?.Id, row?.Name);
        if (MatchesProjectCommand(ApproveCommandRegex(), out _))
            return new LifecycleOrder("approve", row?.Id, row?.Name);
        if (MatchesProjectCommand(StartCommandRegex(), out _))
            return new LifecycleOrder("start", row?.Id, row?.Name);

        // Saving mode parsing
        var effectiveProjectId = row?.Id ?? projectId;
        var savingCmd = MultipleWhitespaceRegex().Replace(TrailingPunctuationRegex().Replace(ql, string.Empty).Trim(), " ");
        if (!string.IsNullOrEmpty(savingCmd))
        {
            if (SavingNormalRegex().IsMatch(savingCmd))
                return new SavingModeOrder("normal", effectiveProjectId);
            if (SavingSafeRegex().IsMatch(savingCmd))
                return new SavingModeOrder("safe", effectiveProjectId);
            if (SavingUltraSafeRegex().IsMatch(savingCmd))
                return new SavingModeOrder("ultra-safe", effectiveProjectId);
        }

        // Chat mode parsing
        var chatCmd = TrailingPunctuationRegex().Replace(ql, string.Empty).Trim();
        if (!string.IsNullOrEmpty(chatCmd))
        {
            if (ChatTalkRegex().IsMatch(chatCmd))
                return new ChatModeOrder("talk", "talk", effectiveProjectId);
            if (ChatPlanRegex().IsMatch(chatCmd))
                return new ChatModeOrder("plan", "plan", effectiveProjectId);
            if (ChatPipelineRegex().IsMatch(chatCmd))
                return new RejectOrder("pipeline", effectiveProjectId);

            if (ChatWantsModeRegex().IsMatch(chatCmd))
            {
                if (ChatWordPipelineRegex().IsMatch(chatCmd))
                    return new RejectOrder("pipeline", effectiveProjectId);
                if (ChatWordAutoRegex().IsMatch(chatCmd) && !ChatWordAutoStartRegex().IsMatch(chatCmd))
                    return new ChatModeOrder("auto", null, effectiveProjectId);
                if (ChatWordManualRegex().IsMatch(chatCmd))
                    return new ChatModeOrder("manual", "plan", effectiveProjectId);
                if (ChatWordTalkRegex().IsMatch(chatCmd))
                    return new ChatModeOrder("talk", "talk", effectiveProjectId);
                if (ChatWordPlanRegex().IsMatch(chatCmd))
                    return new ChatModeOrder("plan", "plan", effectiveProjectId);
            }
        }

        return null;
    }

    // 2.8 RouteAsk
    public static OrderRouteResult RouteAsk(
        string? question,
        OrchestratorSnapshot? snap,
        string? projectId,
        AskRoutingOptions? options = null)
    {
        var conversation = options?.Conversation;
        var requestedGrillKind = options?.RequestedGrillKind;
        var goldModel = options?.GoldModel;
        var phase2 = options?.Phase2 ?? false;
        return RouteAskInternal(question, snap, projectId, conversation, requestedGrillKind, goldModel, phase2);
    }

    public static OrderRouteResult RouteAsk(
        string? question,
        OrchestratorSnapshot? snap,
        string? projectId,
        IReadOnlyList<TurnHistory>? conversation,
        string? requestedGrillKind = null,
        string? goldModel = null)
    {
        return RouteAskInternal(question, snap, projectId, conversation, requestedGrillKind, goldModel, phase2: false);
    }

    public static OrderRouteResult RouteAsk(
        string? question,
        JsonNode? snapNode,
        string? projectId,
        AskRoutingOptions? options = null)
    {
        return RouteAsk(question, OrchestratorSnapshot.FromJson(snapNode), projectId, options);
    }

    public static ParsedOrder? ParseOrder(
        string? question,
        JsonNode? snapNode,
        string? projectId)
    {
        return ParseOrder(question, OrchestratorSnapshot.FromJson(snapNode), projectId);
    }

    public static ProjectSummary? PickAskedProject(
        JsonNode? snapNode,
        string? projectId,
        string? question)
    {
        return PickAskedProject(OrchestratorSnapshot.FromJson(snapNode), projectId, question);
    }

    private static OrderRouteResult RouteAskInternal(
        string? question,
        OrchestratorSnapshot? snap,
        string? projectId,
        IReadOnlyList<TurnHistory>? conversation,
        string? requestedGrillKind,
        string? goldModel,
        bool phase2)
    {
        var q = (question ?? string.Empty).Trim();
        var projects = snap?.Projects ?? (IReadOnlyList<ProjectSummary>)Array.Empty<ProjectSummary>();

        if (!string.IsNullOrEmpty(projectId) && projects.Count > 0 &&
            !projects.Any(p => string.Equals(p.Id, projectId, StringComparison.Ordinal)))
        {
            return new OrderRouteResult(false, Reason: "Selected project no longer exists. Select a project before sending.");
        }

        var ql = q.ToLowerInvariant();
        var mentionedProjects = projects.Where(row =>
            (!string.IsNullOrEmpty(row.Id) && ql.Contains(row.Id.ToLowerInvariant())) ||
            (!string.IsNullOrEmpty(row.Name) && ql.Contains(row.Name.ToLowerInvariant()))).ToList();

        ProjectSummary? explicitlyNamedCommand = null;
        foreach (var row in mentionedProjects)
        {
            var candidates = new[] { row.Id, row.Name }.Where(n => !string.IsNullOrEmpty(n));
            foreach (var name in candidates)
            {
                var escaped = Regex.Escape(name);
                var pattern = @"^(?:start|stop|resume|approve(?: run)?|avvia|ferma|riprendi|approva)\s+" + escaped + @"[.!?]*$";
                if (Regex.IsMatch(q, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    explicitlyNamedCommand = row;
                    break;
                }
            }
            if (explicitlyNamedCommand != null) break;
        }

        var effectiveProjectId = projectId;
        if (phase2 && explicitlyNamedCommand != null && string.IsNullOrEmpty(effectiveProjectId))
            effectiveProjectId = explicitlyNamedCommand.Id;

        if (!string.IsNullOrEmpty(effectiveProjectId) &&
            mentionedProjects.Any(row => !string.Equals(row.Id, effectiveProjectId, StringComparison.Ordinal)))
        {
            return new OrderRouteResult(false, Reason: "Message names a different project from the selected recipient. Select the intended project before sending.");
        }

        if (string.IsNullOrEmpty(effectiveProjectId) && mentionedProjects.Count > 1)
        {
            return new OrderRouteResult(false, Reason: "Message names multiple projects. Select the intended recipient.");
        }

        var contextProjectId = effectiveProjectId;
        if (string.IsNullOrEmpty(contextProjectId) && mentionedProjects.Count == 1)
            contextProjectId = mentionedProjects[0].Id;

        string? typedGrill = GrillMeRegex().IsMatch(q) ? "me" : (GrillDocsRegex().IsMatch(q) ? "docs" : null);
        string? previousGrill = null;
        if (conversation != null && !string.IsNullOrEmpty(effectiveProjectId))
        {
            previousGrill = conversation.FirstOrDefault(turn =>
                string.Equals(turn.ProjectId, effectiveProjectId, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(turn.GrillKind))?.GrillKind;
        }

        var requestedGrill = requestedGrillKind ?? typedGrill;
        var isEndGrill = EndGrillRegex().IsMatch(q);

        ParsedOrder? preliminaryOrder = null;
        if (requestedGrill == null)
        {
            if (isEndGrill)
                preliminaryOrder = new SystemQueryOrder("endGrill", effectiveProjectId);
            else
                preliminaryOrder = ParseOrder(q, snap, effectiveProjectId);
        }

        var grillKind = requestedGrill ?? (preliminaryOrder == null ? previousGrill : null);
        if (!string.IsNullOrEmpty(grillKind) && PickAskedProject(snap, effectiveProjectId, q) == null)
        {
            return new OrderRouteResult(false, Reason: "Select the project before starting Grill.");
        }

        var researchRequested = string.IsNullOrEmpty(grillKind) && ResearchIntent(q, conversation);
        if (researchRequested && PickAskedProject(snap, effectiveProjectId, q) == null)
        {
            return new OrderRouteResult(false, Reason: "Select a project or name it explicitly before dispatching research.");
        }

        string? statusKind = QuotaQueryRegex().IsMatch(q) ? "quota" : (StateQueryRegex().IsMatch(q) ? "state" : null);

        ParsedOrder? order;
        if (!string.IsNullOrEmpty(grillKind) || researchRequested)
            order = null;
        else if (!string.IsNullOrEmpty(statusKind))
            order = new SystemQueryOrder(statusKind, effectiveProjectId);
        else
            order = preliminaryOrder;

        if (order != null)
        {
            var actionsRequiringProject = new[] { "start", "stop", "resume", "approve", "skip", "watching" };
            if (actionsRequiringProject.Contains(order.Action) && string.IsNullOrEmpty(order.ProjectId))
                return new OrderRouteResult(false, Reason: "Select a project or name it explicitly before issuing a command.");

            if (!string.IsNullOrEmpty(goldModel) && order.Action != "start")
                return new OrderRouteResult(false, Reason: "Gold applies to new work requests or Start. Use Default to approve or resume the existing task; its saved model constraint stays active.");

            if (!string.IsNullOrEmpty(goldModel))
                order = order with { GoldModel = goldModel };
        }

        var targetForMode = projects.FirstOrDefault(p => string.Equals(p.Id, contextProjectId, StringComparison.Ordinal));
        var isTalking = targetForMode != null && string.Equals(targetForMode.ChatMode, "talk", StringComparison.OrdinalIgnoreCase);

        var freshTask = false;
        ProjectSummary? target = null;

        if (!isTalking && order == null && string.IsNullOrEmpty(grillKind) && !researchRequested && FreshTaskIntent(q))
        {
            target = PickAskedProject(snap, effectiveProjectId, q);
            if (target == null)
                return new OrderRouteResult(false, Reason: "Select the project for this new task.");
            freshTask = true;
        }

        if (target == null)
        {
            target = PickAskedProject(snap, order?.ProjectId ?? effectiveProjectId, q);
        }

        return new OrderRouteResult(
            Ok: true,
            Reason: null,
            Order: order,
            GrillKind: grillKind,
            ResearchRequested: researchRequested,
            FreshTask: freshTask,
            TargetProject: target);
    }
}
