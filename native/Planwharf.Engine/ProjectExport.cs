// Port of engine/project-export.cjs.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class ProjectExport
{
    private static readonly Regex ForbiddenKeys = new(@"^(?:credentials?|auth|authEnv|access.?token|refresh.?token|tokens?|api.?key|password|secret|environment|env|workerTail|thinking|log)$", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
    private static readonly Regex BearerRegex = new(@"\bBearer\s+[A-Za-z0-9._~+\/-]+", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
    private static readonly Regex TokenRegex = new(@"\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,})\b", RegexOptions.ECMAScript);

    public static JsonNode? Sanitize(JsonNode? node)
    {
        if (node is JsonValue val && val.TryGetValue<string>(out var str))
        {
            var replaced = BearerRegex.Replace(str, "Bearer [REDACTED]");
            replaced = TokenRegex.Replace(replaced, "[REDACTED]");
            return JsonValue.Create(replaced);
        }

        if (node is JsonArray arr)
        {
            var result = new JsonArray();
            foreach (var item in arr)
            {
                result.Add(Sanitize(item?.DeepClone()));
            }
            return result;
        }

        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var pair in obj)
            {
                if (ForbiddenKeys.IsMatch(pair.Key)) continue;
                result[pair.Key] = Sanitize(pair.Value?.DeepClone());
            }
            return result;
        }

        return node?.DeepClone();
    }

    public static JsonObject ExportProject(JsonObject project, JsonObject execution, JsonObject? settings = null)
    {
        var proj = project["project"] as JsonObject ?? new JsonObject();
        var id = proj["id"]?.GetValue<string>() ?? "";

        var megaTasks = (execution["megaTasks"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(m => m["projectId"]?.GetValue<string>() == id)
            .Select(m => (JsonObject)m.DeepClone())
            .ToList() ?? new List<JsonObject>();

        var megaIds = megaTasks.Select(m => m["id"]?.GetValue<string>() ?? "").ToHashSet(StringComparer.Ordinal);

        var subTasks = (execution["subTasks"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(s => megaIds.Contains(s["megaTaskId"]?.GetValue<string>() ?? ""))
            .Select(s => (JsonObject)s.DeepClone())
            .ToList() ?? new List<JsonObject>();

        var results = (execution["results"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(r => megaIds.Contains(r["megaTaskId"]?.GetValue<string>() ?? ""))
            .Select(r => (JsonObject)r.DeepClone())
            .ToList() ?? new List<JsonObject>();

        var checkpoints = (execution["checkpoints"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(c => c["projectId"]?.GetValue<string>() == id)
            .Select(c => (JsonObject)c.DeepClone())
            .ToList() ?? new List<JsonObject>();

        var timeline = (execution["timeline"] as JsonArray)?
            .OfType<JsonObject>()
            .Where(t => t["projectId"]?.GetValue<string>() == id)
            .Select(t => (JsonObject)t.DeepClone())
            .ToList() ?? new List<JsonObject>();

        var dataMegaTasks = new JsonArray();
        foreach (var m in megaTasks) dataMegaTasks.Add((JsonNode)m);
        var dataSubTasks = new JsonArray();
        foreach (var s in subTasks) dataSubTasks.Add((JsonNode)s);
        var dataResults = new JsonArray();
        foreach (var r in results) dataResults.Add((JsonNode)r);
        var dataCheckpoints = new JsonArray();
        foreach (var c in checkpoints) dataCheckpoints.Add((JsonNode)c);
        var dataTimeline = new JsonArray();
        foreach (var t in timeline) dataTimeline.Add((JsonNode)t);

        var data = new JsonObject
        {
            ["megaTasks"] = dataMegaTasks,
            ["subTasks"] = dataSubTasks,
            ["results"] = dataResults,
            ["checkpoints"] = dataCheckpoints,
            ["timeline"] = dataTimeline
        };

        var summary = ProjectHistory.SummarizeTimeline((JsonArray)dataTimeline.DeepClone(), (JsonObject)data.DeepClone());

        var sett = settings ?? new JsonObject();
        var exportSettings = new JsonObject
        {
            ["providerPolicies"] = sett["providerPolicies"]?.DeepClone(),
            ["modelPolicies"] = sett["modelPolicies"]?.DeepClone(),
            ["modelRoles"] = sett["modelRoles"]?.DeepClone(),
            ["accountModelRoles"] = sett["accountModelRoles"]?.DeepClone(),
            ["mode"] = sett["mode"]?.DeepClone(),
            ["orchestratorId"] = sett["orchestratorId"]?.DeepClone(),
            ["enabled"] = sett["enabled"]?.DeepClone(),
            ["providerEnabled"] = sett["providerEnabled"]?.DeepClone()
        };

        var revisions = project["revisions"] as JsonArray ?? new JsonArray();
        var lastDirection = "";
        if (revisions.Count > 0 && revisions[^1] is JsonObject lastRev)
            lastDirection = lastRev["direction"]?.GetValue<string>() ?? "";

        var projName = proj["name"]?.GetValue<string>() ?? "";
        var markdownLines = summary.OfType<JsonObject>().Select(ev => $"- {ev["createdAt"]}: {ev["summary"]}");
        var markdown = $"# {projName}\n\n{lastDirection}\n\n" + string.Join("\n", markdownLines);

        var homeObj = project["home"] as JsonObject;
        var notes = homeObj?["notes"]?.DeepClone() ?? new JsonArray();

        var bundle = new JsonObject
        {
            ["format"] = "planwharf-project-export",
            ["schema"] = 1,
            ["exportedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["restoreSupported"] = true,
            ["project"] = proj.DeepClone(),
            ["revisions"] = revisions.DeepClone(),
            ["tasks"] = project["tasks"]?.DeepClone() ?? new JsonArray(),
            ["notes"] = notes,
            ["execution"] = data,
            ["timeline"] = summary,
            ["settings"] = exportSettings,
            ["markdown"] = markdown
        };

        return (JsonObject)Sanitize(bundle)!;
    }

    public static JsonObject RemapExecution(JsonObject execution, string projectId)
    {
        var megaMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var checkpointMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var megaTasks = new List<JsonObject>();

        if (execution["megaTasks"] is JsonArray megaArr)
        {
            foreach (var item in megaArr)
            {
                if (item is not JsonObject mega) continue;
                var oldId = mega["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(oldId)) continue;

                var newId = "MT-" + Guid.NewGuid().ToString();
                megaMap[oldId] = newId;

                var st = mega["status"]?.GetValue<string>();
                var status = st is "COMPLETE" or "FAILED" or "CANCELLED" or "READY_FOR_APPROVAL" or "PAUSED" ? st : "PAUSED";

                var cloned = (JsonObject)mega.DeepClone();
                cloned["id"] = newId;
                cloned["projectId"] = projectId;
                cloned["status"] = status;

                if (cloned["repoBefore"] is not JsonObject)
                {
                    cloned["repoBefore"] = new JsonObject
                    {
                        ["repoHead"] = null,
                        ["dirtyFiles"] = new JsonArray(),
                        ["comparable"] = false,
                        ["gitReadable"] = false
                    };
                }
                megaTasks.Add(cloned);
            }
        }

        if (execution["checkpoints"] is JsonArray cpArr)
        {
            foreach (var item in cpArr)
            {
                if (item is not JsonObject cp) continue;
                var cpId = cp["id"]?.GetValue<string>();
                var cpMegaId = cp["megaTaskId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(cpId) || string.IsNullOrEmpty(cpMegaId) || !megaMap.ContainsKey(cpMegaId)) continue;
                checkpointMap[cpId] = "CP-" + Guid.NewGuid().ToString();
            }
        }

        foreach (var mega in megaTasks)
        {
            var oldCpId = mega["currentCheckpointId"]?.GetValue<string>();
            string? mapped = null;
            if (!string.IsNullOrEmpty(oldCpId) && checkpointMap.TryGetValue(oldCpId, out var m))
            {
                mapped = m;
            }
            else if (execution["checkpoints"] is JsonArray allCps)
            {
                var fallback = checkpointMap.Reverse().FirstOrDefault(pair =>
                {
                    var orig = allCps.OfType<JsonObject>().FirstOrDefault(c => c["id"]?.GetValue<string>() == pair.Key);
                    return orig != null && megaMap.TryGetValue(orig["megaTaskId"]?.GetValue<string>() ?? "", out var newMId) && newMId == mega["id"]?.GetValue<string>();
                });
                mapped = fallback.Value;
            }
            mega["currentCheckpointId"] = mapped;
        }

        var subMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var subTasks = new List<JsonObject>();
        if (execution["subTasks"] is JsonArray subArr)
        {
            foreach (var item in subArr)
            {
                if (item is not JsonObject sub) continue;
                var subMegaId = sub["megaTaskId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(subMegaId) || !megaMap.TryGetValue(subMegaId, out var newMegaId)) continue;

                var oldSubId = sub["id"]?.GetValue<string>();
                var newSubId = "ST-" + Guid.NewGuid().ToString();
                if (!string.IsNullOrEmpty(oldSubId)) subMap[oldSubId] = newSubId;

                var cloned = (JsonObject)sub.DeepClone();
                cloned["id"] = newSubId;
                cloned["megaTaskId"] = newMegaId;
                if (cloned["assignedModelId"] == null) cloned["assignedModelId"] = "unassigned";
                subTasks.Add(cloned);
            }
        }

        foreach (var sub in subTasks)
        {
            if (sub["dependencies"] is JsonArray deps)
            {
                var newDeps = new JsonArray();
                foreach (var d in deps)
                {
                    var depId = d?.GetValue<string>() ?? "";
                    newDeps.Add(subMap.TryGetValue(depId, out var mapped) ? (JsonNode)mapped : (JsonNode)depId);
                }
                sub["dependencies"] = newDeps;
            }
            else
            {
                sub["dependencies"] = new JsonArray();
            }
        }

        var results = new List<JsonObject>();
        if (execution["results"] is JsonArray resArr)
        {
            foreach (var item in resArr)
            {
                if (item is not JsonObject res) continue;
                var resMegaId = res["megaTaskId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(resMegaId) || !megaMap.TryGetValue(resMegaId, out var newMegaId)) continue;

                var cloned = (JsonObject)res.DeepClone();
                cloned["megaTaskId"] = newMegaId;
                results.Add(cloned);
            }
        }

        var checkpoints = new List<JsonObject>();
        if (execution["checkpoints"] is JsonArray cpArrFinal)
        {
            foreach (var item in cpArrFinal)
            {
                if (item is not JsonObject cp) continue;
                var cpId = cp["id"]?.GetValue<string>();
                var cpMegaId = cp["megaTaskId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(cpId) || string.IsNullOrEmpty(cpMegaId) || !checkpointMap.TryGetValue(cpId, out var newCpId) || !megaMap.TryGetValue(cpMegaId, out var newMegaId)) continue;

                var cloned = (JsonObject)cp.DeepClone();
                cloned["id"] = newCpId;
                cloned["projectId"] = projectId;
                cloned["megaTaskId"] = newMegaId;
                checkpoints.Add(cloned);
            }
        }

        var timeline = new List<JsonObject>();
        if (execution["timeline"] is JsonArray timeArr)
        {
            foreach (var item in timeArr)
            {
                if (item is not JsonObject ev) continue;
                var cloned = (JsonObject)ev.DeepClone();
                cloned["id"] = "EV-" + Guid.NewGuid().ToString();
                cloned["projectId"] = projectId;

                if (cloned["reference"] is JsonObject refObj &&
                    refObj["megaTaskId"]?.GetValue<string>() is string refMegaId &&
                    megaMap.TryGetValue(refMegaId, out var newRefMegaId))
                {
                    var updatedRef = (JsonObject)refObj.DeepClone();
                    updatedRef["megaTaskId"] = newRefMegaId;
                    if (refObj["checkpointId"]?.GetValue<string>() is string refCpId &&
                        checkpointMap.TryGetValue(refCpId, out var newRefCpId))
                    {
                        updatedRef["checkpointId"] = newRefCpId;
                    }
                    cloned["reference"] = updatedRef;
                }
                timeline.Add(cloned);
            }
        }

        var resObj = new JsonObject
        {
            ["megaTasks"] = new JsonArray(megaTasks.Select(m => (JsonNode)m).ToArray()),
            ["subTasks"] = new JsonArray(subTasks.Select(s => (JsonNode)s).ToArray()),
            ["results"] = new JsonArray(results.Select(r => (JsonNode)r).ToArray()),
            ["checkpoints"] = new JsonArray(checkpoints.Select(c => (JsonNode)c).ToArray()),
            ["timeline"] = new JsonArray(timeline.Select(t => (JsonNode)t).ToArray())
        };

        return (JsonObject)Sanitize(resObj)!;
    }

    public static JsonObject ContinueFrom(JsonObject? execution)
    {
        if (execution?["megaTasks"] is JsonArray megaArr)
        {
            foreach (var item in megaArr)
            {
                if (item is JsonObject mega && mega["status"]?.GetValue<string>() == "PAUSED" &&
                    mega["currentCheckpointId"]?.GetValue<string>() is string cpId && !string.IsNullOrEmpty(cpId))
                {
                    return new JsonObject
                    {
                        ["action"] = "resume",
                        ["megaTaskId"] = mega["id"]?.GetValue<string>(),
                        ["checkpointId"] = cpId
                    };
                }
            }

            foreach (var item in megaArr)
            {
                if (item is JsonObject mega && mega["status"]?.GetValue<string>() == "READY_FOR_APPROVAL")
                {
                    return new JsonObject
                    {
                        ["action"] = "approve",
                        ["megaTaskId"] = mega["id"]?.GetValue<string>()
                    };
                }
            }
        }

        return new JsonObject { ["action"] = null };
    }

    public static JsonObject ImportProject(JsonObject notebook, JsonObject bundle, JsonObject? place = null)
    {
        var clean = Sanitize(bundle) as JsonObject;
        if (clean == null || clean["format"]?.GetValue<string>() != "planwharf-project-export" || clean["schema"]?.GetValue<int>() != 1)
            throw new InvalidOperationException("Import a Planwharf project export (schema 1).");

        var proj = clean["project"] as JsonObject;
        var restoreSource = new JsonObject
        {
            ["name"] = proj?["name"]?.GetValue<string>(),
            ["revisions"] = clean["revisions"]?.DeepClone(),
            ["tasks"] = clean["tasks"]?.DeepClone(),
            ["notes"] = clean["notes"]?.DeepClone()
        };

        var nextNotebook = Plan.RestoreProject(notebook, restoreSource, place, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        var projectsArr = nextNotebook["projects"] as JsonArray ?? new JsonArray();
        var importedProj = projectsArr.Count > 0 ? projectsArr[^1] as JsonObject : new JsonObject();
        var importedProjId = importedProj?["project"]?["id"]?.GetValue<string>() ?? "";

        var cleanExec = clean["execution"] as JsonObject ?? new JsonObject();
        var execution = RemapExecution(cleanExec, importedProjId);

        return new JsonObject
        {
            ["notebook"] = nextNotebook,
            ["project"] = importedProj?.DeepClone(),
            ["execution"] = execution,
            ["continue"] = ContinueFrom(execution)
        };
    }
}
