// Port of engine/briefing.cjs.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

// The state given to the orchestrator, trimmed: long texts are clipped, finished work and user-only notes
// are left out. The JSON must be the one the JavaScript gives, field order included. JavaScript builds it
// with object spreads, where a field that is already there keeps its place and a field set to undefined
// disappears from the JSON; Value models "maybe undefined" so the same happens here.
public static class Briefing
{
    // A JavaScript value read from JSON: Missing is undefined, Present with a null Node is null.
    readonly record struct Value(bool Present, JsonNode? Node)
    {
        public static readonly Value Missing = new(false, null);
        public static Value Of(JsonNode? node) => new(true, node);
        public bool Truthy => Present && Node switch
        {
            null => false,
            JsonValue value => value.GetValueKind() switch
            {
                JsonValueKind.False => false,
                JsonValueKind.String => value.GetValue<string>().Length > 0,
                JsonValueKind.Number => double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture) is var n && n != 0 && !double.IsNaN(n),
                _ => true
            },
            _ => true
        };
        public bool IsNullish => !Present || Node is null;
        // value.key: undefined on undefined or null would be a TypeError; callers use Get only after ?. or a check.
        public Value Get(string key) => Node is JsonObject obj && obj.TryGetPropertyValue(key, out var child) ? Of(child) : Missing;
        public JsonArray? Array => Node as JsonArray;
    }

    static string? Text(Value value) => value.Node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    // clip: a string is cut to `limit` UTF-16 code units, anything else is kept as it is.
    static Value Clip(Value value, int limit = 1200) =>
        Text(value) is string text && text.Length > limit ? Value.Of(text.Substring(0, limit)) : value;

    // Sets a field as an object literal or a spread does: kept in place when the field is already there,
    // added at the end otherwise, and gone when the value is undefined.
    static void Put(JsonObject target, string key, Value value)
    {
        if (!value.Present) { target.Remove(key); return; }
        target[key] = value.Node?.DeepClone();
    }

    static JsonObject Pick(Value source, params string[] keys)
    {
        var result = new JsonObject();
        foreach (var key in keys) Put(result, key, source.Get(key));
        return result;
    }

    static JsonObject Spread(Value source)
    {
        var copy = new JsonObject();
        if (source.Node is JsonObject obj) foreach (var pair in obj) copy[pair.Key] = pair.Value?.DeepClone();
        return copy;
    }

    // a === b for JSON values: undefined and null differ, objects are equal only to themselves.
    static bool StrictEquals(Value a, Value b)
    {
        if (!a.Present || !b.Present) return a.Present == b.Present;
        if (a.Node is null || b.Node is null) return a.Node is null && b.Node is null;
        if (a.Node is JsonValue va && b.Node is JsonValue vb)
        {
            var kind = va.GetValueKind();
            if (kind != vb.GetValueKind()) return false;
            return kind switch
            {
                JsonValueKind.String => va.GetValue<string>() == vb.GetValue<string>(),
                JsonValueKind.Number => double.Parse(va.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture) == double.Parse(vb.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
                _ => true
            };
        }
        return ReferenceEquals(a.Node, b.Node);
    }

    // array?.method(...): undefined when the array is null or undefined. Anything else that is not an array
    // would fail in JavaScript with a TypeError.
    static IEnumerable<Value>? Items(Value value, string what)
    {
        if (value.IsNullish) return null;
        if (value.Node is not JsonArray array) throw new InvalidOperationException(what + " is not an array");
        return array.Select(Value.Of);
    }

    static Value List(IEnumerable<JsonNode?>? items) => items is null ? Value.Missing : Value.Of(new JsonArray(items.ToArray()));
    static IEnumerable<Value> Last(IEnumerable<Value> items, int count) { var list = items.ToList(); return list.Skip(Math.Max(0, list.Count - count)); }

    public static JsonObject CompactState(JsonObject state, JsonNode? previous = null)
    {
        var root = Value.Of(state);
        var handoff = root.Get("executionHandoff");
        var megaTasks = handoff.IsNullish ? null : Items(handoff.Get("megaTasks"), "megaTasks");
        var mega = megaTasks?.LastOrDefault() ?? Value.Missing;
        var megaId = mega.IsNullish ? Value.Missing : mega.Get("id");
        var checkpoints = handoff.IsNullish ? null : Items(handoff.Get("checkpoints"), "checkpoints");
        var checkpoint = checkpoints?.Where(cp => StrictEquals(cp.Get("megaTaskId"), megaId)).LastOrDefault() ?? Value.Missing;

        var focus = Value.Of(null);
        var stateFocus = root.Get("focus");
        if (stateFocus.Truthy)
        {
            var built = Spread(stateFocus);
            var repository = stateFocus.Get("repository");
            JsonObject? repo = null;
            if (repository.Truthy)
            {
                var remote = repository.Get("remote");
                var comparison = repository.Get("comparison");
                repo = new JsonObject { ["remote"] = Pick(remote.IsNullish ? Value.Missing : remote, "accessible", "private", "branch", "sha") };
                if (comparison.Truthy)
                {
                    var compared = Pick(comparison, "status", "ahead", "behind");
                    Put(compared, "error", Clip(comparison.Get("error")));
                    repo["comparison"] = compared;
                }
                else repo["comparison"] = null;
            }
            Put(built, "repository", Value.Of(repo));
            Put(built, "analysis", Clip(stateFocus.Get("analysis")));
            Put(built, "state", Clip(stateFocus.Get("state")));
            Put(built, "plan", List(Items(stateFocus.Get("plan"), "plan")?.Take(8).Select(text => Clip(text, 400).Node?.DeepClone())));
            Put(built, "lastLog", Value.Missing);
            Put(built, "workers", List(Items(stateFocus.Get("workers"), "workers")?.Select(worker =>
            {
                if (worker.IsNullish) throw new InvalidOperationException("Cannot destructure a worker that is null");
                var copy = Spread(worker);
                copy.Remove("log");
                Put(copy, "activity", Clip(worker.Get("activity"), 300));
                return (JsonNode?)copy;
            })));
            focus = Value.Of(built);
        }
        var current = Value.Of(focus.Node is null ? null : Rename(Pick(focus, "lastCommit", "dirty")));

        var result = Spread(root);
        Put(result, "focus", focus);
        Put(result, "others", List(Items(root.Get("others"), "others")?.Select(project =>
        {
            if (project.IsNullish) throw new InvalidOperationException("Cannot destructure a project that is null");
            var copy = Spread(project);
            copy.Remove("repository");
            return (JsonNode?)copy;
        })));

        JsonObject? brief = null;
        if (handoff.Truthy)
        {
            brief = new JsonObject();
            if (mega.Truthy)
            {
                var task = Pick(mega, "id", "title");
                Put(task, "goal", Clip(mega.Get("goal"), 2400));
                Put(task, "status", mega.Get("status"));
                Put(task, "nextAction", Clip(mega.Get("nextAction")));
                brief["megaTask"] = task;
            }
            else brief["megaTask"] = null;
            Put(brief, "subTasks", List(Items(handoff.Get("subTasks"), "subTasks")?
                .Where(sub => StrictEquals(sub.Get("megaTaskId"), megaId) && !StrictEquals(sub.Get("status"), Value.Of("COMPLETE")))
                .Select(sub => (JsonNode?)Pick(sub, "id", "title", "status", "assignedModelId", "dependencies"))));
            Put(brief, "results", List(Items(handoff.Get("results"), "results") is { } results
                ? Last(results.Where(item => StrictEquals(item.Get("megaTaskId"), megaId)), 3).Select(item =>
                {
                    var row = Pick(item, "subTaskId", "status");
                    Put(row, "summary", Clip(item.Get("summary")));
                    Put(row, "nextAction", Clip(item.Get("nextAction")));
                    Put(row, "evidencePath", item.Get("evidencePath"));
                    return (JsonNode?)row;
                })
                : null));
            if (checkpoint.Truthy)
            {
                var saved = Pick(checkpoint, "id", "completedSubTaskIds", "pendingSubTaskIds", "failedSubTaskIds");
                Put(saved, "nextAction", Clip(checkpoint.Get("nextAction")));
                brief["checkpoint"] = saved;
            }
            else brief["checkpoint"] = null;
            Put(brief, "notes", List(Items(handoff.Get("notes"), "notes") is { } notes
                ? Last(notes.Where(note => StrictEquals(note.Get("aiRelevant"), Value.Of(true))), 12).Select(note =>
                {
                    var row = Pick(note, "id");
                    Put(row, "text", Clip(note.Get("text")));
                    return (JsonNode?)row;
                })
                : null));
        }
        Put(result, "executionHandoff", Value.Of(brief));

        var focusId = focus.Node is null ? Value.Missing : focus.Get("id");
        Put(result, "researches", List(Items(root.Get("researches"), "researches") is { } researches
            ? Last(researches.Where(research => StrictEquals(research.Get("projectId"), focusId)), 5).Select(research => (JsonNode?)Pick(research, "id", "title", "status", "reportPath"))
            : null));

        var prior = previous is null ? Value.Of(null) : Value.Of(previous);
        JsonObject delta;
        if (prior.Truthy)
        {
            var head = current.Node is null ? Value.Missing : current.Get("head");
            var dirty = current.Node is null ? Value.Missing : current.Get("dirty");
            delta = new JsonObject
            {
                ["headChanged"] = !StrictEquals(prior.Get("head"), head),
                ["dirtyChanged"] = Stringify(prior.Get("dirty")) != Stringify(dirty)
            };
        }
        else delta = new JsonObject { ["initialInspection"] = true };
        delta["current"] = current.Node?.DeepClone();
        Put(result, "repositoryDelta", Value.Of(delta));
        return result;
    }

    // { head: focus.lastCommit, dirty: focus.dirty }
    static JsonObject Rename(JsonObject picked)
    {
        var repository = new JsonObject();
        if (picked.TryGetPropertyValue("lastCommit", out var head)) repository["head"] = head?.DeepClone();
        if (picked.TryGetPropertyValue("dirty", out var dirty)) repository["dirty"] = dirty?.DeepClone();
        return repository;
    }

    // JSON.stringify(value): undefined stays undefined (null here), anything else is its JSON text.
    static string? Stringify(Value value) => value.Present ? value.Node?.ToJsonString() ?? "null" : null;

    public static JsonArray RecentContext(JsonArray conversation)
    {
        var turns = conversation.Take(6).Reverse().Select(turn =>
        {
            var value = Value.Of(turn);
            if (value.IsNullish) throw new InvalidOperationException("Cannot read properties of " + (turn is null ? "null" : "undefined"));
            var row = new JsonObject();
            Put(row, "user", Clip(value.Get("question"), 1600));
            Put(row, "orchestrator", Clip(value.Get("answer"), 2400));
            return (JsonNode?)row;
        });
        return new JsonArray(turns.ToArray());
    }
}
