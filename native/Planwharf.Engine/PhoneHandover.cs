// Port of engine/phone-handover.cjs.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public record HandoverPlanResult(
    bool Allowed,
    string Blocker,
    JsonObject? Pause,
    string Build,
    string Restore)
{
    public JsonObject ToJsonObject()
    {
        return new JsonObject
        {
            ["allowed"] = Allowed,
            ["blocker"] = Blocker,
            ["pause"] = Pause?.DeepClone(),
            ["build"] = Build,
            ["restore"] = Restore
        };
    }
}

public class HandoverCallArgs
{
    public JsonObject? Installed { get; set; }
    public string? UpdatedBuild { get; set; }
    public bool BackupOk { get; set; }
    public string? Token { get; set; }
    public Func<JsonObject?, Task<JsonObject?>>? PausePolling { get; set; }
    public Func<Task<JsonObject?>>? PollOnce { get; set; }
    public Func<Task<JsonObject?>>? StartBuild { get; set; }
    public Func<JsonObject?, Task<JsonObject?>>? SendAlert { get; set; }
    public Func<JsonObject?, Task<JsonObject?>>? ReplyTo { get; set; }
    public Func<Task<JsonObject?>>? AskWhenIdle { get; set; }
    public Func<JsonObject?, Task>? StopBuild { get; set; }
    public Func<JsonObject?, Task<JsonObject?>>? ResumePolling { get; set; }
}

public static class PhoneHandover
{
    private static readonly object _gate = new();
    private static bool _handoverBusy;

    public static bool IsHandoverBusy
    {
        get { lock (_gate) return _handoverBusy; }
        internal set { lock (_gate) _handoverBusy = value; }
    }

    public static HandoverPlanResult HandoverPlan(
        JsonObject? installed = null,
        string? updatedBuild = null,
        bool backupOk = false,
        bool tokenPresent = false)
    {
        installed ??= new JsonObject();
        var reasons = new List<string>();
        if (!backupOk)
        {
            reasons.Add("A recoverable backup of the plan, phone settings and bot token is required before polling pauses.");
        }
        if (string.IsNullOrEmpty(updatedBuild))
        {
            reasons.Add("The updated native build is missing.");
        }

        var installedPath = installed["path"]?.ToString();
        if (!string.IsNullOrEmpty(installedPath) && PhoneActivation.SamePath(installedPath, updatedBuild))
        {
            reasons.Add("The updated build is the process that is already polling.");
        }
        if (!tokenPresent)
        {
            reasons.Add("No bot token is available, so the paired bot cannot be verified.");
        }

        var addressNode = installed["address"];
        var addressStr = addressNode?.ToString();
        if (string.IsNullOrEmpty(addressStr))
        {
            reasons.Add("The installed listener address was not recorded, so polling cannot be restored.");
        }

        JsonObject? pause = null;
        if (!string.IsNullOrEmpty(addressStr))
        {
            int? pid = null;
            if (installed.TryGetPropertyValue("pid", out var pidNode) && pidNode is JsonValue val && val.TryGetValue<int>(out var p))
            {
                pid = p;
            }

            pause = new JsonObject
            {
                ["address"] = addressStr,
                ["pid"] = pid,
                ["path"] = installedPath ?? string.Empty
            };
        }

        return new HandoverPlanResult(
            Allowed: reasons.Count == 0,
            Blocker: string.Join(" ", reasons),
            Pause: pause,
            Build: updatedBuild ?? string.Empty,
            Restore: "re-enable telegram polling on the installed listener"
        );
    }

    public static HandoverPlanResult HandoverPlan(JsonObject? options)
    {
        options ??= new JsonObject();
        var installed = options["installed"] as JsonObject;
        var updatedBuild = options["updatedBuild"]?.ToString();
        var backupOk = options["backupOk"]?.GetValue<bool>() == true;
        var tokenPresent = options["tokenPresent"]?.GetValue<bool>() == true;
        return HandoverPlan(installed, updatedBuild, backupOk, tokenPresent);
    }

    public static string ClipEvidence(string? value)
    {
        var raw = value ?? string.Empty;
        var redacted = Regex.Replace(raw, @"\b\d{5,}:[A-Za-z0-9_-]{6,}\b", "[redacted]");
        return redacted.Length > 600 ? redacted.Substring(0, 600) : redacted;
    }

    public static JsonObject? EvidenceRow(JsonObject? item)
    {
        if (item == null) return null;
        var hasText = item.TryGetPropertyValue("text", out var textNode) && textNode != null && !string.IsNullOrEmpty(textNode.ToString());
        var hasMsgId = item.TryGetPropertyValue("messageId", out var msgNode) && msgNode != null;
        var hasUpdId = item.TryGetPropertyValue("updateId", out var updNode) && updNode != null;

        if (!hasText && !hasMsgId && !hasUpdId) return null;

        var text = item["text"]?.ToString() ?? string.Empty;

        return new JsonObject
        {
            ["messageId"] = hasMsgId ? msgNode?.DeepClone() : null,
            ["updateId"] = hasUpdId ? updNode?.DeepClone() : null,
            ["text"] = ClipEvidence(text)
        };
    }

    public static JsonObject ExchangeEvidence(JsonObject? outbound, JsonObject? inbound, JsonObject? reply)
    {
        return new JsonObject
        {
            ["outbound"] = EvidenceRow(outbound),
            ["inbound"] = EvidenceRow(inbound),
            ["reply"] = EvidenceRow(reply)
        };
    }

    public static JsonObject ExchangeEvidence(JsonObject? options = null)
    {
        options ??= new JsonObject();
        return ExchangeEvidence(
            options["outbound"] as JsonObject,
            options["inbound"] as JsonObject,
            options["reply"] as JsonObject
        );
    }

    public static string PollBlockerText(JsonNode? value)
    {
        if (value is null) return string.Empty;
        if (value is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            if (jv.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
            {
                if (jv.TryGetValue<long>(out var l)) return l.ToString();
                return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return jv.ToString();
        }
        if (value is JsonObject obj)
        {
            if (obj["message"]?.ToString() is string msg && !string.IsNullOrEmpty(msg)) return msg;
            if (obj["description"]?.ToString() is string desc && !string.IsNullOrEmpty(desc)) return desc;
        }
        return string.Empty;
    }

    public static JsonObject Refused(HandoverPlanResult plan, JsonObject? extra = null)
    {
        var res = new JsonObject
        {
            ["ok"] = false,
            ["restored"] = false,
            ["plan"] = plan.ToJsonObject(),
            ["evidence"] = ExchangeEvidence()
        };
        if (extra != null)
        {
            foreach (var (k, v) in extra)
            {
                res[k] = v?.DeepClone();
            }
        }
        return res;
    }

    public static async Task<JsonObject> RunHandoverAsync(HandoverCallArgs args)
    {
        lock (_gate)
        {
            if (_handoverBusy)
            {
                var planBusy = HandoverPlan(args.Installed, args.UpdatedBuild, args.BackupOk, !string.IsNullOrEmpty(args.Token));
                var res = Refused(planBusy);
                res["blocker"] = "A handover is already running.";
                return res;
            }
            _handoverBusy = true;
        }

        try
        {
            var plan = HandoverPlan(args.Installed, args.UpdatedBuild, args.BackupOk, !string.IsNullOrEmpty(args.Token));
            if (!plan.Allowed)
            {
                return Refused(plan);
            }

            JsonObject? paused = null;
            try
            {
                if (args.PausePolling != null)
                {
                    paused = await args.PausePolling(args.Installed);
                }
            }
            catch (Exception error)
            {
                var blockerText = !string.IsNullOrEmpty(error.Message) ? error.Message : "Polling could not be paused.";
                return Refused(plan, new JsonObject { ["blocker"] = blockerText });
            }

            if (paused == null || paused["ok"]?.GetValue<bool>() == false)
            {
                return Refused(plan, new JsonObject { ["paused"] = paused?.DeepClone() });
            }

            JsonObject? inbound = null;
            JsonObject? reply = null;
            JsonObject? started = null;
            JsonObject? outbound = null;
            var blocker = string.Empty;
            var failed = false;

            try
            {
                var polled = args.PollOnce != null ? await args.PollOnce() : null;
                if (polled?["blocked"]?.GetValue<bool>() == true)
                {
                    failed = true;
                    blocker = "The installed listener is still polling.";
                }
                else if (polled != null && polled.ContainsKey("error") && polled["error"] != null)
                {
                    failed = true;
                    var err = PollBlockerText(polled["error"]);
                    blocker = !string.IsNullOrEmpty(err) ? err : "Handover poll failed.";
                }
                else if (polled != null && polled["ok"]?.GetValue<bool>() == false)
                {
                    failed = true;
                    var rsn = PollBlockerText(polled["reason"]);
                    blocker = !string.IsNullOrEmpty(rsn) ? rsn : "Handover poll failed.";
                }
                else
                {
                    JsonObject? update = null;
                    if (polled?["updates"] is JsonArray arr)
                    {
                        foreach (var item in arr)
                        {
                            if (item is JsonObject obj && obj["message"] is JsonObject msg && !string.IsNullOrEmpty(msg["text"]?.ToString()))
                            {
                                update = obj;
                                break;
                            }
                        }
                    }

                    if (update != null)
                    {
                        var msg = (JsonObject)update["message"]!;
                        inbound = new JsonObject
                        {
                            ["updateId"] = update["update_id"]?.DeepClone(),
                            ["text"] = msg["text"]?.ToString() ?? string.Empty
                        };
                        if (args.ReplyTo != null)
                        {
                            var answered = await args.ReplyTo(msg);
                            reply = new JsonObject
                            {
                                ["messageId"] = answered?["messageId"]?.DeepClone(),
                                ["text"] = answered?["text"]?.ToString() ?? string.Empty
                            };
                        }
                    }
                    else if (args.AskWhenIdle != null)
                    {
                        var answered = await args.AskWhenIdle();
                        reply = new JsonObject
                        {
                            ["messageId"] = answered?["messageId"]?.DeepClone(),
                            ["text"] = answered?["text"]?.ToString() ?? string.Empty
                        };
                    }

                    var built = args.StartBuild != null ? await args.StartBuild() : null;
                    if (built == null || string.IsNullOrEmpty(built["address"]?.ToString()))
                    {
                        failed = true;
                    }
                    else
                    {
                        started = built;
                        if (args.SendAlert != null)
                        {
                            var alert = await args.SendAlert(started);
                            outbound = new JsonObject
                            {
                                ["messageId"] = alert?["messageId"]?.DeepClone(),
                                ["text"] = alert?["text"]?.ToString() ?? string.Empty
                            };
                        }
                    }
                }
            }
            catch (Exception error)
            {
                failed = true;
                blocker = !string.IsNullOrEmpty(error.Message) ? error.Message : "Handover failed.";
            }

            if (!string.IsNullOrEmpty(started?["address"]?.ToString()) && args.StopBuild != null)
            {
                try
                {
                    await args.StopBuild(started);
                }
                catch
                {
                    // The listener still has to poll again.
                }
            }

            JsonObject? restored = null;
            if (args.ResumePolling != null)
            {
                try
                {
                    restored = await args.ResumePolling(args.Installed);
                }
                catch
                {
                    restored = new JsonObject { ["ok"] = false };
                }
            }
            restored ??= new JsonObject { ["ok"] = false };

            var restoredOk = restored["ok"]?.GetValue<bool>() == true;
            var hasOutboundText = !string.IsNullOrEmpty(outbound?["text"]?.ToString());
            var ok = restoredOk && !failed && hasOutboundText;

            var result = new JsonObject
            {
                ["ok"] = ok,
                ["restored"] = restoredOk,
                ["plan"] = plan.ToJsonObject(),
                ["paused"] = paused?.DeepClone()
            };

            if (!string.IsNullOrEmpty(blocker))
            {
                result["blocker"] = blocker;
            }

            if (started != null)
            {
                result["started"] = new JsonObject
                {
                    ["address"] = started["address"]?.ToString() ?? string.Empty,
                    ["model"] = started["model"]?.ToString() ?? string.Empty,
                    ["paired"] = started["paired"]?.GetValue<bool>() == true
                };
            }

            result["evidence"] = ExchangeEvidence(outbound, inbound, reply);
            return result;
        }
        finally
        {
            lock (_gate)
            {
                _handoverBusy = false;
            }
        }
    }
}
