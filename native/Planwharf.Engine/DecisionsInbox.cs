using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public sealed record DecisionItem(
    string Id,
    string ProjectId,
    string ProjectName,
    string WorkId,
    string WorkTitle,
    bool IsPipeline,
    string Text,
    string Suggestion,
    bool Breaks,
    string At,
    string Answer,
    string? AnsweredAt = null,
    string? AnsweredBy = null,
    string? AutoReason = null,
    bool StepStarted = false);

public sealed record DecisionsInboxView(
    IReadOnlyList<DecisionItem> Open,
    IReadOnlyList<DecisionItem> Answered,
    int OpenCount)
{
    public static DecisionsInboxView FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return new DecisionsInboxView([], [], 0);
        }

        var openList = new List<DecisionItem>();
        var answeredList = new List<DecisionItem>();

        static bool BoolVal(JsonNode? node)
        {
            if (node is JsonValue val)
            {
                if (val.TryGetValue<bool>(out var b)) return b;
                if (val.TryGetValue<string>(out var s) && bool.TryParse(s, out var sb)) return sb;
                if (val.TryGetValue<int>(out var i)) return i != 0;
            }
            return false;
        }

        static int IntVal(JsonNode? node, int fallback)
        {
            if (node is JsonValue val)
            {
                if (val.TryGetValue<int>(out var i)) return i;
                if (val.TryGetValue<string>(out var s) && int.TryParse(s, out var si)) return si;
            }
            return fallback;
        }

        void ParseList(JsonArray? arr, List<DecisionItem> target)
        {
            if (arr == null) return;
            foreach (var itemNode in arr)
            {
                if (itemNode is not JsonObject itemObj) continue;
                var id = itemObj["id"]?.ToString() ?? "";
                var projectId = itemObj["projectId"]?.ToString() ?? "";
                var projectName = itemObj["projectName"]?.ToString() ?? projectId;
                var workId = itemObj["workId"]?.ToString() ?? "";
                var workTitle = itemObj["workTitle"]?.ToString() ?? workId;
                var isPipeline = BoolVal(itemObj["isPipeline"]);
                var text = itemObj["text"]?.ToString() ?? "";
                var suggestion = itemObj["suggestion"]?.ToString() ?? "";
                var breaks = BoolVal(itemObj["breaks"]);
                var at = itemObj["at"]?.ToString() ?? "";
                var answer = itemObj["answer"]?.ToString() ?? "";
                var answeredAt = itemObj["answeredAt"]?.ToString();
                var answeredBy = itemObj["answeredBy"]?.ToString();
                var autoReason = itemObj["autoReason"]?.ToString();
                var stepStarted = BoolVal(itemObj["stepStarted"]);

                target.Add(new DecisionItem(
                    id, projectId, projectName, workId, workTitle, isPipeline,
                    text, suggestion, breaks, at, answer, answeredAt, answeredBy, autoReason, stepStarted));
            }
        }

        ParseList(obj["open"] as JsonArray, openList);
        ParseList(obj["answered"] as JsonArray, answeredList);

        var openCount = IntVal(obj["openCount"], openList.Count);
        return new DecisionsInboxView(openList, answeredList, openCount);
    }
}
