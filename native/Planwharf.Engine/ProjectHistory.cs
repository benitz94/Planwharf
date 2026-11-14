// Port of engine/project-history.cjs.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

// Project history: milestone aggregation for the technical timeline, and checkpoint differences
// to verify safety before an autonomous or user-guided resume.
public static class ProjectHistory
{
    // A JavaScript value used as a map key. Strings and numbers stay distinct: "1" !== 1.
    readonly record struct IdKey(int Kind, string Text)
    {
        public static bool TryRead(JsonObject owner, string key, out IdKey id)
        {
            id = default;
            if (!owner.TryGetPropertyValue(key, out var node) || node is not JsonValue value) return false;
            switch (value.GetValueKind())
            {
                case JsonValueKind.String:
                    var text = value.GetValue<string>();
                    if (text.Length == 0) return false;
                    id = new(1, text);
                    return true;
                case JsonValueKind.True:
                    id = new(2, "true");
                    return true;
                case JsonValueKind.Number:
                    var number = Number(value);
                    if (number == 0 || double.IsNaN(number)) return false;
                    id = new(3, number.ToString("R", CultureInfo.InvariantCulture));
                    return true;
                default:
                    return false;
            }
        }

        public string Suffix => Kind == 3
            ? double.Parse(Text, NumberStyles.Float, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : Text;
    }

    public static JsonArray SummarizeTimeline(JsonArray events, JsonObject execution)
    {
        var groups = new Dictionary<IdKey, List<JsonObject>>();
        var groupOrder = new List<IdKey>();
        var milestones = new List<JsonObject>();

        foreach (var item in events)
        {
            if (item is not JsonObject ev) throw new InvalidOperationException("Cannot read properties of null (reading 'reference')");
            IdKey megaTaskId = default;
            var hasId = ev["reference"] is JsonObject reference && IdKey.TryRead(reference, "megaTaskId", out megaTaskId);
            if (!hasId)
            {
                milestones.Add((JsonObject)ev.DeepClone());
                continue;
            }

            if (!groups.TryGetValue(megaTaskId, out var list))
            {
                list = [];
                groups[megaTaskId] = list;
                groupOrder.Add(megaTaskId);
            }
            list.Add((JsonObject)ev.DeepClone());
        }

        JsonArray? megaTasks = null;
        if (groupOrder.Count > 0)
        {
            if (!execution.TryGetPropertyValue("megaTasks", out var megaNode))
                throw new InvalidOperationException("Cannot read properties of undefined (reading 'find')");
            if (megaNode is null) throw new InvalidOperationException("Cannot read properties of null (reading 'find')");
            if (megaNode is not JsonArray foundMegas) throw new InvalidOperationException("execution.megaTasks.find is not a function");
            megaTasks = foundMegas;
        }

        foreach (var id in groupOrder)
        {
            var evidence = groups[id];
            JsonObject? mega = null;
            foreach (var m in megaTasks!)
            {
                if (m is null) throw new InvalidOperationException("Cannot read properties of null (reading 'id')");
                if (m is JsonObject mObj && IdKey.TryRead(mObj, "id", out var megaId) && megaId.Equals(id))
                {
                    mega = mObj;
                    break;
                }
            }

            if (mega is null)
            {
                milestones.AddRange(evidence);
                continue;
            }

            if (!execution.TryGetPropertyValue("results", out var resultsNode))
                throw new InvalidOperationException("Cannot read properties of undefined (reading 'filter')");
            if (resultsNode is null) throw new InvalidOperationException("Cannot read properties of null (reading 'filter')");
            if (resultsNode is not JsonArray resultsArray) throw new InvalidOperationException("execution.results.filter is not a function");

            var joined = new List<string>();
            foreach (var r in resultsArray)
            {
                if (r is null) throw new InvalidOperationException("Cannot read properties of null (reading 'megaTaskId')");
                if (r is not JsonObject rObj || !IdKey.TryRead(rObj, "megaTaskId", out var resultId) || !resultId.Equals(id)) continue;
                if (!rObj.TryGetPropertyValue("status", out var statusNode) || statusNode is not JsonValue statusValue || statusValue.GetValueKind() != JsonValueKind.String || statusValue.GetValue<string>() != "COMPLETE") continue;
                rObj.TryGetPropertyValue("summary", out var summaryNode);
                joined.Add(summaryNode is null ? "" : JsText(summaryNode, true));
            }

            var last = evidence[^1];
            var milestone = (JsonObject)last.DeepClone();
            milestone["id"] = "milestone:" + id.Suffix;
            milestone["type"] = "MEGATASK_MILESTONE";

            var statusIsComplete = mega.TryGetPropertyValue("status", out var megaStatus) && megaStatus is JsonValue complete && complete.GetValueKind() == JsonValueKind.String && complete.GetValue<string>() == "COMPLETE";
            var resultsText = joined.Count == 0 ? "" : string.Join(" ", joined);
            if (resultsText.Length > 600) resultsText = resultsText[..600];
            var titlePresent = mega.TryGetPropertyValue("title", out var titleNode);
            var statusPresent = mega.TryGetPropertyValue("status", out megaStatus);
            milestone["summary"] = $"{(statusIsComplete ? "Completed " : "")}{JsText(titleNode, titlePresent)}: {JsText(megaStatus, statusPresent)}. {resultsText}".Trim();
            milestone["evidenceCount"] = evidence.Count;

            var evidenceArr = new JsonArray();
            foreach (var ev in evidence) evidenceArr.Add((JsonNode)ev.DeepClone());
            milestone["evidence"] = evidenceArr;
            milestones.Add(milestone);
        }

        milestones = milestones
            .Select((milestone, index) => (milestone, index))
            .OrderBy(pair => CreatedAt(pair.milestone), StringComparer.Ordinal)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.milestone)
            .ToList();

        var result = new JsonArray();
        foreach (var m in milestones) result.Add((JsonNode)m);
        return result;
    }

    public static JsonObject CheckpointDiff(JsonObject checkpoint, JsonObject current, int planRevisionId, JsonArray? completed = null)
    {
        if (!checkpoint.TryGetPropertyValue("repoState", out var beforeNode) || beforeNode is not JsonObject before)
            throw new InvalidOperationException("Cannot read properties of " + (beforeNode is null ? "undefined" : "null") + " (reading 'repoHead')");
        var changes = new JsonArray();

        if (!StrictSame(before, "repoHead", current, "repoHead"))
        {
            var from = Shown(before, "repoHead");
            var to = Shown(current, "repoHead");
            changes.Add((JsonNode)JsonValue.Create($"HEAD changed from {from} to {to}"));
        }

        if (!StrictSame(before, "fingerprint", current, "fingerprint"))
        {
            if (!current.TryGetPropertyValue("dirtyFiles", out var dirty) || dirty is null)
                throw new InvalidOperationException("Cannot read properties of undefined (reading 'length')");
            changes.Add((JsonNode)JsonValue.Create($"External repository changes detected; {JsLength(dirty)} files currently changed locally"));
        }

        var hasRevision = checkpoint.TryGetPropertyValue("planRevisionId", out var revisionNode);
        var sameRevision = hasRevision && revisionNode is JsonValue revisionValue && revisionValue.GetValueKind() == JsonValueKind.Number && Number(revisionValue) == planRevisionId;
        if (!sameRevision)
            changes.Add((JsonNode)JsonValue.Create($"Living Plan changed from R{JsText(revisionNode, hasRevision)} to R{planRevisionId.ToString(CultureInfo.InvariantCulture)}"));

        if (!Truthy(before, "comparable") || !Truthy(current, "comparable"))
            changes.Add((JsonNode)JsonValue.Create("Repository comparison is incomplete; inspect technical evidence before Resume"));

        var hasSavedIds = checkpoint.TryGetPropertyValue("completedSubTaskIds", out var savedIds);
        var savedIsEmpty = !hasSavedIds || Falsy(savedIds, hasSavedIds);
        var newlyCompleteCount = 0;
        if (completed is not null)
        {
            foreach (var node in completed)
            {
                if (savedIsEmpty || !JsIncludes(savedIds, node)) newlyCompleteCount++;
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

    static string CreatedAt(JsonObject ev)
    {
        if (!ev.TryGetPropertyValue("createdAt", out var node) || node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            throw new InvalidOperationException("createdAt.localeCompare is not a function");
        return value.GetValue<string>();
    }

    // `${value}` and `value || 'unavailable'`. Missing and JSON null are different.
    static string JsText(JsonNode? node, bool present)
    {
        if (!present) return "undefined";
        if (node is null) return "null";
        if (node is JsonValue value)
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>(),
                JsonValueKind.Number => JsNumber(Number(value)),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => value.ToJsonString()
            };
        }
        if (node is JsonArray array) return string.Join(",", array.Select(item => item is null ? "" : JsText(item, true)));
        return "[object Object]";
    }

    static string Shown(JsonObject owner, string key)
    {
        var present = owner.TryGetPropertyValue(key, out var node);
        return Falsy(node, present) ? "unavailable" : JsText(node, present);
    }

    static bool StrictSame(JsonObject left, string leftKey, JsonObject right, string rightKey)
    {
        var leftHas = left.TryGetPropertyValue(leftKey, out var leftNode);
        var rightHas = right.TryGetPropertyValue(rightKey, out var rightNode);
        if (leftHas != rightHas) return false;
        if (!leftHas || ReferenceEquals(leftNode, rightNode)) return true;
        if (leftNode is null || rightNode is null) return leftNode is null && rightNode is null;
        if (leftNode is JsonValue a && rightNode is JsonValue b)
        {
            if (a.GetValueKind() != b.GetValueKind()) return false;
            return a.GetValueKind() switch
            {
                JsonValueKind.String => a.GetValue<string>() == b.GetValue<string>(),
                JsonValueKind.Number => Number(a) == Number(b),
                _ => true
            };
        }
        return false;
    }

    static bool Truthy(JsonObject owner, string key)
    {
        var present = owner.TryGetPropertyValue(key, out var node);
        return !Falsy(node, present);
    }

    static bool Falsy(JsonNode? node, bool present)
    {
        if (!present || node is null) return true;
        if (node is not JsonValue value) return false;
        return value.GetValueKind() switch
        {
            JsonValueKind.False => true,
            JsonValueKind.String => value.GetValue<string>().Length == 0,
            JsonValueKind.Number => Number(value) is var number && (number == 0 || double.IsNaN(number)),
            _ => false
        };
    }

    static int JsLength(JsonNode node)
    {
        if (node is JsonArray array) return array.Count;
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String) return value.GetValue<string>().Length;
        throw new InvalidOperationException("Cannot read properties of " + JsText(node, true) + " (reading 'length')");
    }

    static bool JsIncludes(JsonNode? collection, JsonNode? item)
    {
        if (collection is JsonArray array) return array.Any(entry => SameNode(entry, item));
        if (collection is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            return value.GetValue<string>().Contains(item is null ? "null" : JsText(item, true), StringComparison.Ordinal);
        throw new InvalidOperationException(JsText(collection, true) + ".includes is not a function");
    }

    static bool SameNode(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is not JsonValue a || right is not JsonValue b || a.GetValueKind() != b.GetValueKind()) return false;
        return a.GetValueKind() switch
        {
            JsonValueKind.String => a.GetValue<string>() == b.GetValue<string>(),
            JsonValueKind.Number => Number(a) == Number(b) || (double.IsNaN(Number(a)) && double.IsNaN(Number(b))),
            _ => true
        };
    }

    static double Number(JsonValue value) => double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    static string JsNumber(double number)
    {
        if (double.IsNaN(number)) return "NaN";
        if (double.IsPositiveInfinity(number)) return "Infinity";
        if (double.IsNegativeInfinity(number)) return "-Infinity";
        if (number == 0) return "0";
        return number == Math.Truncate(number) && Math.Abs(number) < 1e21
            ? number.ToString("0", CultureInfo.InvariantCulture)
            : number.ToString("R", CultureInfo.InvariantCulture);
    }
}
