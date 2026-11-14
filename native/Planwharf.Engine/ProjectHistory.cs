// Port of engine/project-history.cjs.
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

// Project history: milestone aggregation for the technical timeline, and checkpoint differences
// to verify safety before an autonomous or user-guided resume.
public static class ProjectHistory
{
    public static JsonArray SummarizeTimeline(JsonArray events, JsonObject execution)
    {
        var groups = new Dictionary<string, List<JsonObject>>();
        var groupOrder = new List<string>();
        var milestones = new List<JsonObject>();

        foreach (var item in events)
        {
            if (item is not JsonObject ev) continue;
            var megaTaskId = ev["reference"] is JsonObject reference && reference.TryGetPropertyValue("megaTaskId", out var idNode) && idNode is JsonValue val && val.TryGetValue<string>(out var idStr) && !string.IsNullOrEmpty(idStr)
                ? idStr
                : null;

            if (megaTaskId is null)
            {
                milestones.Add((JsonObject)ev.DeepClone());
                continue;
            }

            if (!groups.TryGetValue(megaTaskId, out var list))
            {
                list = new List<JsonObject>();
                groups[megaTaskId] = list;
                groupOrder.Add(megaTaskId);
            }
            list.Add((JsonObject)ev.DeepClone());
        }

        var megaTasks = execution["megaTasks"] as JsonArray;
        var resultsArray = execution["results"] as JsonArray;

        foreach (var id in groupOrder)
        {
            var evidence = groups[id];
            JsonObject? mega = null;
            if (megaTasks is not null)
            {
                foreach (var m in megaTasks)
                {
                    if (m is JsonObject mObj && mObj["id"]?.GetValue<string>() == id)
                    {
                        mega = mObj;
                        break;
                    }
                }
            }

            if (mega is null)
            {
                milestones.AddRange(evidence);
                continue;
            }

            var matchingResults = new List<string>();
            if (resultsArray is not null)
            {
                foreach (var r in resultsArray)
                {
                    if (r is JsonObject rObj &&
                        rObj["megaTaskId"]?.GetValue<string>() == id &&
                        rObj["status"]?.GetValue<string>() == "COMPLETE")
                    {
                        matchingResults.Add(rObj["summary"]?.GetValue<string>() ?? "");
                    }
                }
            }

            var last = evidence[^1];
            var milestone = (JsonObject)last.DeepClone();
            milestone["id"] = "milestone:" + id;
            milestone["type"] = "MEGATASK_MILESTONE";

            var megaStatus = mega["status"]?.GetValue<string>() ?? "";
            var megaTitle = mega["title"]?.GetValue<string>() ?? "";
            var prefix = megaStatus == "COMPLETE" ? "Completed " : "";
            var resultsText = "";
            if (matchingResults.Count > 0)
            {
                var joined = string.Join(" ", matchingResults);
                resultsText = joined.Length > 600 ? joined.Substring(0, 600) : joined;
            }

            var summary = $"{prefix}{megaTitle}: {megaStatus}. {resultsText}".Trim();
            milestone["summary"] = summary;
            milestone["evidenceCount"] = evidence.Count;

            var evidenceArr = new JsonArray();
            foreach (var ev in evidence) evidenceArr.Add((JsonNode)ev.DeepClone());
            milestone["evidence"] = evidenceArr;

            milestones.Add(milestone);
        }

        milestones.Sort((a, b) =>
        {
            var ca = a["createdAt"]?.GetValue<string>() ?? "";
            var cb = b["createdAt"]?.GetValue<string>() ?? "";
            return string.CompareOrdinal(ca, cb);
        });

        var result = new JsonArray();
        foreach (var m in milestones) result.Add((JsonNode)m);
        return result;
    }

    public static JsonObject CheckpointDiff(JsonObject checkpoint, JsonObject current, int planRevisionId, JsonArray? completed = null)
    {
        var before = checkpoint["repoState"] as JsonObject ?? new JsonObject();
        var changes = new JsonArray();

        var beforeHead = before["repoHead"]?.GetValue<string>();
        var currentHead = current["repoHead"]?.GetValue<string>();
        if (beforeHead != currentHead)
        {
            var from = string.IsNullOrEmpty(beforeHead) ? "unavailable" : beforeHead;
            var to = string.IsNullOrEmpty(currentHead) ? "unavailable" : currentHead;
            changes.Add((JsonNode)JsonValue.Create($"HEAD changed from {from} to {to}"));
        }

        var beforeFp = before["fingerprint"]?.GetValue<string>();
        var currentFp = current["fingerprint"]?.GetValue<string>();
        if (beforeFp != currentFp)
        {
            var dirtyCount = current["dirtyFiles"] is JsonArray da ? da.Count : 0;
            changes.Add((JsonNode)JsonValue.Create($"External repository changes detected; {dirtyCount} files currently changed locally"));
        }

        var cpPlanRev = checkpoint["planRevisionId"]?.GetValue<int>() ?? 0;
        if (cpPlanRev != planRevisionId)
        {
            changes.Add((JsonNode)JsonValue.Create($"Living Plan changed from R{cpPlanRev} to R{planRevisionId}"));
        }

        var beforeComp = before["comparable"]?.GetValue<bool>() ?? false;
        var currentComp = current["comparable"]?.GetValue<bool>() ?? false;
        if (!beforeComp || !currentComp)
        {
            changes.Add((JsonNode)JsonValue.Create("Repository comparison is incomplete; inspect technical evidence before Resume"));
        }

        var completedIds = new HashSet<string>();
        if (checkpoint["completedSubTaskIds"] is JsonArray cpComp)
        {
            foreach (var node in cpComp)
            {
                if (node?.GetValue<string>() is string s) completedIds.Add(s);
            }
        }

        var newlyCompleteCount = 0;
        if (completed is not null)
        {
            foreach (var node in completed)
            {
                if (node?.GetValue<string>() is string s && !completedIds.Contains(s))
                {
                    newlyCompleteCount++;
                }
            }
        }

        if (newlyCompleteCount > 0)
        {
            changes.Add((JsonNode)JsonValue.Create($"{newlyCompleteCount} previously pending SubTask(s) already completed; these will not be replayed"));
        }

        var requiresConfirmation = false;
        foreach (var change in changes)
        {
            if (change?.GetValue<string>() is string text && !text.Contains("will not be replayed"))
            {
                requiresConfirmation = true;
                break;
            }
        }

        return new JsonObject
        {
            ["changes"] = changes,
            ["requiresConfirmation"] = requiresConfirmation,
            ["before"] = before.DeepClone(),
            ["current"] = current.DeepClone()
        };
    }
}
