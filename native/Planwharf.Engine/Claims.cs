// Port of engine/claims.js.
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public sealed class ClaimEngine
{
    private readonly AttemptEngine _engine;

    private ClaimEngine(AttemptEngine engine)
    {
        _engine = engine;
    }

    public static ClaimEngine Create()
    {
        var io = new MemoryAttemptIo();
        var engine = AttemptEngine.Create(io);
        return new ClaimEngine(engine);
    }

    public IReadOnlyList<JsonObject> GetAttempts() => _engine.GetAttempts();

    public JsonObject? GetActiveAttempt(JsonObject? query)
    {
        if (query == null) return null;
        var taskId = query["taskId"]?.GetValue<string>();
        var projectId = query["projectId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(projectId))
            return null;
        return _engine.GetActiveAttempt(query);
    }

    public JsonObject Claim(JsonObject? input)
    {
        if (input != null && input["taskId"]?.GetValue<string>() is string taskId && !string.IsNullOrWhiteSpace(taskId))
        {
            if (_engine.GetAttempts().Any(attempt => attempt["status"]?.GetValue<string>() == "active" && attempt["taskId"]?.GetValue<string>() == taskId))
            {
                return new JsonObject { ["ok"] = false, ["reason"] = "task already assigned" };
            }
        }
        return _engine.Claim(input);
    }

    private sealed class MemoryAttemptIo : IAttemptIo
    {
        private JsonObject _store = new()
        {
            ["nextId"] = 1,
            ["attempts"] = new JsonArray()
        };
        private readonly object _lock = new();

        public T WithLock<T>(Func<T> work)
        {
            lock (_lock)
            {
                return work();
            }
        }

        public JsonObject Read()
        {
            return (JsonObject)_store.DeepClone();
        }

        public void Write(JsonObject store)
        {
            _store = (JsonObject)store.DeepClone();
        }
    }
}
