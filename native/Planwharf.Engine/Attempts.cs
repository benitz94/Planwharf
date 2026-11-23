// Port of engine/attempts.js.
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public interface IAttemptIo
{
    T WithLock<T>(Func<T> work);
    JsonObject Read();
    void Write(JsonObject store);
}

public sealed class AttemptEngine
{
    private readonly IAttemptIo _io;

    private AttemptEngine(IAttemptIo io)
    {
        _io = io ?? throw new ArgumentNullException(nameof(io));
    }

    public static AttemptEngine Create(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("attempt store directory required");
        return new AttemptEngine(new NodeFileAttemptIo(directory));
    }

    public static AttemptEngine Create(IAttemptIo io)
    {
        if (io == null) throw new ArgumentNullException(nameof(io));
        return new AttemptEngine(io);
    }

    private static bool IsId(string? value) => !string.IsNullOrWhiteSpace(value);

    private static JsonObject EmptyStore() => new()
    {
        ["nextId"] = 1,
        ["attempts"] = new JsonArray()
    };

    private T Transact<T>(Func<JsonObject, (bool write, T value)> mutator)
    {
        return _io.WithLock(() =>
        {
            var store = _io.Read();
            var (write, value) = mutator(store);
            if (write) _io.Write(store);
            return value;
        });
    }

    public IReadOnlyList<JsonObject> GetAttempts()
    {
        return Transact(store =>
        {
            var list = new List<JsonObject>();
            if (store["attempts"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject obj)
                        list.Add((JsonObject)obj.DeepClone());
                }
            }
            return (false, (IReadOnlyList<JsonObject>)list);
        });
    }

    public JsonObject? GetActiveAttempt(JsonObject? query)
    {
        if (query == null) return null;
        var taskId = query["taskId"]?.GetValue<string>();
        var projectId = query["projectId"]?.GetValue<string>();
        return Transact(store =>
        {
            if (store["attempts"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not JsonObject obj) continue;
                    if (obj["status"]?.GetValue<string>() != "active") continue;
                    if (IsId(taskId) && obj["taskId"]?.GetValue<string>() != taskId) continue;
                    if (IsId(projectId) && obj["projectId"]?.GetValue<string>() != projectId) continue;
                    if (IsId(taskId) || IsId(projectId))
                        return (false, (JsonObject)obj.DeepClone());
                }
            }
            return (false, (JsonObject?)null);
        });
    }

    public JsonObject Claim(JsonObject? input)
    {
        if (input == null)
            return new JsonObject { ["ok"] = false, ["reason"] = "invalid data" };

        var taskId = input["taskId"]?.GetValue<string>();
        var projectId = input["projectId"]?.GetValue<string>();
        var modelId = input["modelId"]?.GetValue<string>();
        var candidates = input["candidates"] as JsonArray;

        if (!IsId(taskId) || !IsId(projectId) || !IsId(modelId) || candidates == null)
            return new JsonObject { ["ok"] = false, ["reason"] = "invalid data" };

        JsonObject? matched = null;
        int enabledCount = 0;
        foreach (var c in candidates)
        {
            if (c is not JsonObject cand) continue;
            var enabled = cand["enabled"]?.GetValue<bool>() == true;
            if (enabled) enabledCount++;
            if (cand["id"]?.GetValue<string>() == modelId && matched == null)
                matched = cand;
        }

        if (enabledCount == 0)
            return new JsonObject { ["ok"] = false, ["reason"] = "no model enabled" };
        if (matched == null)
            return new JsonObject { ["ok"] = false, ["reason"] = "model not present" };
        if (matched["enabled"]?.GetValue<bool>() != true)
            return new JsonObject { ["ok"] = false, ["reason"] = "model not eligible" };

        var megaTaskId = input["megaTaskId"]?.GetValue<string>();
        var subTaskId = input["subTaskId"]?.GetValue<string>();
        var research = input["research"]?.GetValue<bool>() == true;

        return Transact(store =>
        {
            var attemptsArr = store["attempts"] as JsonArray;
            if (attemptsArr == null)
            {
                attemptsArr = new JsonArray();
                store["attempts"] = attemptsArr;
            }

            foreach (var item in attemptsArr)
            {
                if (item is not JsonObject existing || existing["status"]?.GetValue<string>() != "active") continue;
                if (existing["taskId"]?.GetValue<string>() == taskId && existing["projectId"]?.GetValue<string>() == projectId)
                    return (false, new JsonObject { ["ok"] = false, ["reason"] = "task already assigned" });
            }

            foreach (var item in attemptsArr)
            {
                if (item is not JsonObject existing || existing["status"]?.GetValue<string>() != "active") continue;
                if (existing["projectId"]?.GetValue<string>() == projectId)
                {
                    bool isSiblingSubTask = IsId(megaTaskId) &&
                                            existing["megaTaskId"]?.GetValue<string>() == megaTaskId &&
                                            IsId(subTaskId) &&
                                            existing["subTaskId"]?.GetValue<string>() != subTaskId;
                    if (!isSiblingSubTask)
                        return (false, new JsonObject { ["ok"] = false, ["reason"] = "project already assigned" });
                }
            }

            int nextId = store["nextId"]?.GetValue<int>() ?? 1;
            if (nextId <= 0) nextId = 1;

            var newAttempt = new JsonObject
            {
                ["id"] = "attempt-" + nextId,
                ["taskId"] = taskId,
                ["projectId"] = projectId,
                ["modelId"] = modelId,
                ["status"] = "active"
            };

            if (IsId(megaTaskId) && IsId(subTaskId))
            {
                newAttempt["megaTaskId"] = megaTaskId;
                newAttempt["subTaskId"] = subTaskId;
                newAttempt["research"] = research;
            }

            store["nextId"] = nextId + 1;
            attemptsArr.Add((JsonNode)newAttempt.DeepClone());

            return (true, new JsonObject
            {
                ["ok"] = true,
                ["attempt"] = (JsonObject)newAttempt.DeepClone()
            });
        });
    }

    public JsonObject CloseAttempt(JsonObject? input)
    {
        var attemptId = input?["attemptId"]?.GetValue<string>();
        if (!IsId(attemptId))
            return new JsonObject { ["ok"] = false, ["reason"] = "invalid data" };

        return Transact(store =>
        {
            if (store["attempts"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not JsonObject existing || existing["id"]?.GetValue<string>() != attemptId) continue;
                    if (existing["status"]?.GetValue<string>() != "active")
                    {
                        return (false, new JsonObject
                        {
                            ["ok"] = false,
                            ["reason"] = "attempt not active",
                            ["attempt"] = (JsonObject)existing.DeepClone()
                        });
                    }
                    existing["status"] = "closed";
                    var reason = input?["reason"]?.GetValue<string>();
                    existing["closedReason"] = !string.IsNullOrWhiteSpace(reason) ? reason.Trim() : "closed";
                    return (true, new JsonObject
                    {
                        ["ok"] = true,
                        ["attempt"] = (JsonObject)existing.DeepClone()
                    });
                }
            }
            return (false, new JsonObject { ["ok"] = false, ["reason"] = "attempt not found" });
        });
    }

    public JsonObject AttachResult(JsonObject? input)
    {
        var attemptId = input?["attemptId"]?.GetValue<string>();
        var resultPath = input?["resultPath"]?.GetValue<string>();
        if (!IsId(attemptId) || string.IsNullOrWhiteSpace(resultPath))
            return new JsonObject { ["ok"] = false, ["reason"] = "invalid data" };

        return Transact(store =>
        {
            if (store["attempts"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not JsonObject existing || existing["id"]?.GetValue<string>() != attemptId) continue;
                    existing["resultPath"] = resultPath;
                    if (input?["status"]?.GetValue<string>() is string status)
                        existing["resultStatus"] = status;
                    if (input?["exitCode"] is JsonNode exitCodeNode)
                        existing["exitCode"] = exitCodeNode.GetValue<int>();
                    return (true, new JsonObject
                    {
                        ["ok"] = true,
                        ["attempt"] = (JsonObject)existing.DeepClone()
                    });
                }
            }
            return (false, new JsonObject { ["ok"] = false, ["reason"] = "attempt not found" });
        });
    }

    public JsonObject? GetResult(string attemptId)
    {
        if (!IsId(attemptId)) return null;
        return Transact(store =>
        {
            if (store["attempts"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is JsonObject existing && existing["id"]?.GetValue<string>() == attemptId)
                        return (false, (JsonObject)existing.DeepClone());
                }
            }
            return (false, (JsonObject?)null);
        });
    }

    private sealed class NodeFileAttemptIo : IAttemptIo
    {
        private readonly string _dir;
        private readonly string _file;
        private readonly string _lock;

        public NodeFileAttemptIo(string dir)
        {
            _dir = dir;
            Directory.CreateDirectory(_dir);
            _file = Path.Combine(_dir, "attempts.json");
            _lock = Path.Combine(_dir, "attempts.lock");
        }

        public T WithLock<T>(Func<T> work)
        {
            var started = Environment.TickCount64;
            FileStream? lockStream = null;
            while (lockStream == null)
            {
                try
                {
                    lockStream = new FileStream(_lock, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    if (Environment.TickCount64 - started > 5000)
                        throw new InvalidOperationException("attempt store locked");
                    Thread.Sleep(20);
                }
            }

            try
            {
                return work();
            }
            finally
            {
                try { lockStream.Dispose(); } catch { }
                try { File.Delete(_lock); } catch { }
            }
        }

        public JsonObject Read()
        {
            try
            {
                if (!File.Exists(_file)) return EmptyStore();
                var text = File.ReadAllText(_file);
                var parsed = JsonNode.Parse(text);
                if (parsed is not JsonObject obj || obj["attempts"] is not JsonArray)
                    return EmptyStore();
                int nextId = obj["nextId"]?.GetValue<int>() ?? 1;
                if (nextId <= 0) nextId = 1;
                return new JsonObject
                {
                    ["nextId"] = nextId,
                    ["attempts"] = (JsonArray)obj["attempts"]!.DeepClone()
                };
            }
            catch
            {
                return EmptyStore();
            }
        }

        public void Write(JsonObject store)
        {
            var temp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, store.ToJsonString() + "\n");
            AtomicRename.RenameWithRetry(temp, _file);
        }
    }
}
