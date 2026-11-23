// Port of engine/execution-next.cjs.
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public static class ExecutionNext
{
    private static string Text(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    private static string Label(JsonObject sub)
    {
        var id = sub["id"]?.GetValue<string>() ?? "";
        var title = sub["title"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(title) ? $"{title} ({id})" : id;
    }

    public static string GetExecutionNext(JsonObject? mega, JsonObject? durable = null)
    {
        if (mega == null) return "";

        var durableObj = durable ?? new JsonObject();
        var megaId = mega["id"]?.GetValue<string>() ?? "";
        var projectId = mega["projectId"]?.GetValue<string>() ?? "";
        var currentCheckpointId = mega["currentCheckpointId"]?.GetValue<string>();
        var status = mega["status"]?.GetValue<string>() ?? "";

        var subs = (durableObj["subTasks"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(s => s["megaTaskId"]?.GetValue<string>() == megaId)
            .ToList() ?? new List<JsonObject>();

        var results = (durableObj["results"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(r => r["megaTaskId"]?.GetValue<string>() == megaId)
            .ToList() ?? new List<JsonObject>();

        var cp = (durableObj["checkpoints"] as JsonArray)?
            .OfType<JsonObject>()
            .FirstOrDefault(c => c["id"]?.GetValue<string>() == currentCheckpointId &&
                                 c["megaTaskId"]?.GetValue<string>() == megaId &&
                                 c["projectId"]?.GetValue<string>() == projectId);

        string Recovery()
        {
            var failed = subs.Where(s => s["status"]?.GetValue<string>() is "FAILED" or "BLOCKED").ToList();
            var currentResults = failed.Select(sub =>
            {
                var subId = sub["id"]?.GetValue<string>();
                var resId = sub["resultId"]?.GetValue<string>();
                return results.FirstOrDefault(r => r["resultId"]?.GetValue<string>() == resId && r["subTaskId"]?.GetValue<string>() == subId);
            }).Where(r => r != null).Cast<JsonObject>().ToList();

            var action = currentResults.Select(r => Text(r["nextAction"])).FirstOrDefault(a => !string.IsNullOrEmpty(a));
            if (!string.IsNullOrEmpty(action)) return action;

            var blockers = new List<string>();
            if (mega["blockers"] is JsonArray mbArr)
            {
                foreach (var b in mbArr)
                {
                    var t = Text(b);
                    if (!string.IsNullOrEmpty(t)) blockers.Add(t);
                }
            }
            foreach (var r in currentResults)
            {
                if (r["blockers"] is JsonArray rbArr)
                {
                    foreach (var b in rbArr)
                    {
                        var t = Text(b);
                        if (!string.IsNullOrEmpty(t)) blockers.Add(t);
                    }
                }
            }

            if (blockers.Count > 0)
                return $"Resolve blockers: {string.Join("; ", blockers)}";

            if (failed.Count > 0)
                return $"Review {Label(failed[0])} and resume from an explicit checkpoint.";

            return "";
        }

        string Remaining()
        {
            var active = subs.FirstOrDefault(s => s["status"]?.GetValue<string>() == "RUNNING");
            if (active != null)
                return $"Work in progress: {Label(active)}.";

            var pending = subs.Where(s => s["status"]?.GetValue<string>() == "PENDING").ToList();

            List<string> Prerequisites(JsonObject sub)
            {
                var deps = (sub["dependencies"] as JsonArray)?.Select(d => Text(d)).Where(d => !string.IsNullOrEmpty(d)).ToList() ?? new List<string>();
                return deps.Where(id =>
                {
                    var parent = subs.FirstOrDefault(p => p["id"]?.GetValue<string>() == id);
                    return parent?["status"]?.GetValue<string>() != "COMPLETE";
                }).ToList();
            }

            var runnable = pending.FirstOrDefault(s => Prerequisites(s).Count == 0);
            if (runnable != null)
                return $"Next SubTask: {Label(runnable)}.";

            if (pending.Count > 0)
            {
                var sub = pending[0];
                var waiting = Prerequisites(sub).Select(id =>
                {
                    var parent = subs.FirstOrDefault(p => p["id"]?.GetValue<string>() == id);
                    return parent != null ? Label(parent) : id;
                }).ToList();
                return $"Waiting for prerequisite {string.Join(", ", waiting)} before {Label(sub)}.";
            }

            return Recovery();
        }

        if (status == "READY_FOR_APPROVAL")
        {
            var na = Text(mega["nextAction"]);
            return !string.IsNullOrEmpty(na) ? na : "Review the proposed slices and approve this MegaTask.";
        }

        if (status == "COMPLETE")
        {
            var completeResults = results.Where(r => r["status"]?.GetValue<string>() == "COMPLETE").ToList();
            var na = completeResults.Count > 0 ? Text(completeResults[^1]["nextAction"]) : "";
            return !string.IsNullOrEmpty(na) ? na : "Review completed evidence.";
        }

        if (status == "PAUSED")
        {
            var cpNa = Text(cp?["nextAction"]);
            if (!string.IsNullOrEmpty(cpNa)) return cpNa;
            var rem = Remaining();
            if (!string.IsNullOrEmpty(rem)) return rem;
            return "Review remaining work and resume this checkpoint.";
        }

        if (status is "FAILED" or "BLOCKED")
        {
            var cpNa = Text(cp?["nextAction"]);
            if (!string.IsNullOrEmpty(cpNa)) return cpNa;
            var rec = Recovery();
            if (!string.IsNullOrEmpty(rec)) return rec;
            return "Review failed work and resume from an explicit checkpoint.";
        }

        if (status == "RUNNING")
        {
            var hasDeadlineError = mega["deadlineError"] != null && (mega["deadlineError"] is not JsonValue dv || dv.TryGetValue<bool>(out var db) && db || dv.TryGetValue<string>(out var ds) && !string.IsNullOrEmpty(ds));
            if (hasDeadlineError)
                return Text(mega["nextAction"]);
            var rem = Remaining();
            return !string.IsNullOrEmpty(rem) ? rem : "Reconciling completed work.";
        }

        return "Review execution state.";
    }

    public static string GetHomeNext(JsonObject item, JsonObject? row = null, JsonObject? durable = null)
    {
        var durableObj = durable ?? new JsonObject();
        var folder = item["project"]?["folder"]?.GetValue<string>();
        if (string.IsNullOrEmpty(ProjectWorkspace.LinkedWorkspace(folder)))
            return "Relink the project folder on this PC.";

        var projectId = item["project"]?["id"]?.GetValue<string>() ?? "";
        var tasks = (durableObj["megaTasks"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(t => t["projectId"]?.GetValue<string>() == projectId)
            .ToList() ?? new List<JsonObject>();

        var rowMegaId = row?["megaTaskId"]?.GetValue<string>();
        var mega = tasks.FirstOrDefault(t => t["id"]?.GetValue<string>() == rowMegaId) ?? (tasks.Count > 0 ? tasks[^1] : null);

        if (mega?["status"]?.GetValue<string>() == "READY_FOR_APPROVAL")
            return "Approve the prepared MegaTask.";

        if (mega != null)
        {
            var mStatus = mega["status"]?.GetValue<string>();
            var mId = mega["id"]?.GetValue<string>();
            if (mStatus is "PAUSED" or "FAILED")
            {
                var checkpoints = (durableObj["checkpoints"] as JsonArray)?
                    .OfType<JsonObject>()
                    .Any(c => c["projectId"]?.GetValue<string>() == projectId && c["megaTaskId"]?.GetValue<string>() == mId);
                if (checkpoints == true)
                    return "Resume from checkpoint.";
            }
            return GetExecutionNext(mega, durableObj);
        }

        if (row?["status"]?.GetValue<string>() == "needs_approval")
            return "Approve the prepared job.";

        if (row?["canResume"]?.GetValue<bool>() == true)
            return "Resume from checkpoint.";

        if (item["revisions"] is JsonArray revs && revs.Count > 0 && revs[^1] is JsonObject lastRev &&
            lastRev["open"] is JsonArray openArr && openArr.Count > 0)
        {
            return "Tell the lead what to build.";
        }

        return "Review the Living Plan.";
    }
}
