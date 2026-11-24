// Tests for Planwharf.Engine.PhoneHandover.

using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class PhoneHandoverTests
{
    private static readonly JsonObject Installed = new()
    {
        ["address"] = "http://127.0.0.1:4173",
        ["pid"] = 13540,
        ["path"] = @"C:\Users\benit\Downloads\Planwharf-native-win-x64-v0.2.85\Planwharf.exe"
    };

    private const string Build = @"D:\benit\Desktop\Projects\Planwharf-engine\validation\phone-activation-native\Planwharf.exe";

    private static (List<string> calls, HandoverCallArgs args) ReadyHandover(Action<HandoverCallArgs>? customize = null)
    {
        return ReadyHandover((_, a) => customize?.Invoke(a));
    }

    private static (List<string> calls, HandoverCallArgs args) ReadyHandover(Action<List<string>, HandoverCallArgs> customize)
    {
        var calls = new List<string>();
        var args = new HandoverCallArgs
        {
            Installed = Installed.DeepClone() as JsonObject,
            UpdatedBuild = Build,
            BackupOk = true,
            Token = "123456:secret",
            PausePolling = _ => { calls.Add("pause"); return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); },
            PollOnce = () => { calls.Add("poll"); return Task.FromResult<JsonObject?>(new JsonObject { ["updates"] = new JsonArray() }); },
            ReplyTo = _ => throw new InvalidOperationException("must not reply"),
            StartBuild = () =>
            {
                calls.Add("start");
                return Task.FromResult<JsonObject?>(new JsonObject
                {
                    ["address"] = "http://127.0.0.1:4174",
                    ["model"] = "qwen2.5:3b",
                    ["paired"] = true
                });
            },
            SendAlert = _ =>
            {
                calls.Add("alert");
                return Task.FromResult<JsonObject?>(new JsonObject
                {
                    ["messageId"] = 9,
                    ["text"] = "🏴‍☠️ Ahoy, Captain!\nStatus: Done ✅"
                });
            },
            StopBuild = _ => { calls.Add("stop"); return Task.CompletedTask; },
            ResumePolling = _ => { calls.Add("resume"); return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); }
        };
        customize(calls, args);
        return (calls, args);
    }

    [Fact]
    public void Handover_stays_blocked_until_backup_token_and_different_build_are_present()
    {
        var missing = PhoneHandover.HandoverPlan(Installed, Build, backupOk: false, tokenPresent: true);
        Assert.False(missing.Allowed);
        Assert.Contains("backup", missing.Blocker);

        var sameInstalled = Installed.DeepClone() as JsonObject;
        sameInstalled!["path"] = Build;
        var same = PhoneHandover.HandoverPlan(sameInstalled, Build, backupOk: true, tokenPresent: true);
        Assert.False(same.Allowed);
        Assert.Contains("already polling", same.Blocker);

        var ready = PhoneHandover.HandoverPlan(Installed, Build, backupOk: true, tokenPresent: true);
        Assert.True(ready.Allowed);
        Assert.Equal("http://127.0.0.1:4173", ready.Pause?["address"]?.ToString());
        Assert.Equal("re-enable telegram polling on the installed listener", ready.Restore);
    }

    [Fact]
    public void Exchange_evidence_keeps_message_text_and_drops_bot_token()
    {
        var evidence = PhoneHandover.ExchangeEvidence(
            outbound: new JsonObject { ["messageId"] = 9, ["text"] = "🏴‍☠️ Ahoy, Captain!\nStatus: Done ✅ token 123456:ABCDEF_secret" },
            inbound: new JsonObject { ["updateId"] = 4, ["text"] = "how is Harbor?" },
            reply: new JsonObject { ["messageId"] = 10, ["text"] = "🏴‍☠️ Ahoy, Captain!\nHarbor is on step 2 of 4." }
        );

        Assert.Equal(9, evidence["outbound"]?["messageId"]?.GetValue<int>());
        Assert.Contains("Done", evidence["outbound"]?["text"]?.ToString());
        Assert.DoesNotContain("ABCDEF_secret", evidence["outbound"]?["text"]?.ToString());
        Assert.Equal("how is Harbor?", evidence["inbound"]?["text"]?.ToString());
        Assert.Contains("step 2 of 4", evidence["reply"]?["text"]?.ToString());
    }

    [Fact]
    public async Task Paused_listener_is_restored_when_updated_build_sends_alert_and_natural_reply()
    {
        var calls = new List<string>();
        var result = await PhoneHandover.RunHandoverAsync(new HandoverCallArgs
        {
            Installed = Installed.DeepClone() as JsonObject,
            UpdatedBuild = Build,
            BackupOk = true,
            Token = "123456:secret",
            PausePolling = _ => { calls.Add("pause"); return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); },
            PollOnce = () => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["updates"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["update_id"] = 4,
                        ["message"] = new JsonObject { ["text"] = "how is Harbor?" }
                    }
                }
            }),
            ReplyTo = msg =>
            {
                calls.Add(msg?["text"]?.ToString() ?? "");
                return Task.FromResult<JsonObject?>(new JsonObject
                {
                    ["messageId"] = 10,
                    ["text"] = "🏴‍☠️ Ahoy, Captain!\nHarbor is on step 2 of 4."
                });
            },
            StartBuild = () =>
            {
                calls.Add("start");
                return Task.FromResult<JsonObject?>(new JsonObject
                {
                    ["address"] = "http://127.0.0.1:4174",
                    ["model"] = "qwen2.5:3b",
                    ["paired"] = true
                });
            },
            SendAlert = _ => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["messageId"] = 9,
                ["text"] = "🏴‍☠️ Ahoy, Captain!\nPlanwharf: Planwharf Test\nStatus: Done ✅"
            }),
            StopBuild = _ => { calls.Add("stop"); return Task.CompletedTask; },
            ResumePolling = _ => { calls.Add("resume"); return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); }
        });

        Assert.True(result["ok"]?.GetValue<bool>());
        Assert.True(result["restored"]?.GetValue<bool>());
        Assert.Equal(new[] { "pause", "how is Harbor?", "start", "stop", "resume" }, calls);
        Assert.Equal("qwen2.5:3b", result["started"]?["model"]?.ToString());
        Assert.Contains("Harbor", result["evidence"]?["reply"]?["text"]?.ToString());
        Assert.DoesNotContain("123456:secret", result.ToJsonString());
    }

    [Fact]
    public async Task Failed_pause_does_not_start_updated_build()
    {
        var started = false;
        var result = await PhoneHandover.RunHandoverAsync(new HandoverCallArgs
        {
            Installed = Installed.DeepClone() as JsonObject,
            UpdatedBuild = Build,
            BackupOk = true,
            Token = "123456:secret",
            PausePolling = _ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = false }),
            PollOnce = () => throw new InvalidOperationException("must not poll"),
            ReplyTo = _ => throw new InvalidOperationException("must not reply"),
            StartBuild = () => { started = true; return Task.FromResult<JsonObject?>(new JsonObject { ["address"] = "http://127.0.0.1:4174" }); },
            SendAlert = _ => Task.FromResult<JsonObject?>(new JsonObject { ["text"] = "nope" }),
            StopBuild = _ => Task.CompletedTask,
            ResumePolling = _ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true })
        });

        Assert.False(result["ok"]?.GetValue<bool>());
        Assert.False(started);
    }

    [Fact]
    public async Task Poll_that_is_still_blocked_restores_installed_listener()
    {
        var started = false;
        var resumed = false;
        var result = await PhoneHandover.RunHandoverAsync(new HandoverCallArgs
        {
            Installed = Installed.DeepClone() as JsonObject,
            UpdatedBuild = Build,
            BackupOk = true,
            Token = "123456:secret",
            PausePolling = _ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }),
            PollOnce = () => Task.FromResult<JsonObject?>(new JsonObject { ["blocked"] = true, ["updates"] = new JsonArray() }),
            ReplyTo = _ => throw new InvalidOperationException("must not reply"),
            StartBuild = () => { started = true; return Task.FromResult<JsonObject?>(new JsonObject { ["address"] = "http://127.0.0.1:4174" }); },
            SendAlert = _ => Task.FromResult<JsonObject?>(new JsonObject { ["text"] = "nope" }),
            StopBuild = _ => Task.CompletedTask,
            ResumePolling = _ => { resumed = true; return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); }
        });

        Assert.False(result["ok"]?.GetValue<bool>());
        Assert.True(result["restored"]?.GetValue<bool>());
        Assert.False(started);
        Assert.True(resumed);
        Assert.Contains("still polling", result["blocker"]?.ToString());
    }

    [Fact]
    public async Task Handover_poll_error_stops_handover_skips_build_and_idle_reply_and_restores_polling()
    {
        var started = false;
        var idleAsked = false;
        var replied = false;
        var resumed = false;
        var result = await PhoneHandover.RunHandoverAsync(new HandoverCallArgs
        {
            Installed = Installed.DeepClone() as JsonObject,
            UpdatedBuild = Build,
            BackupOk = true,
            Token = "123456:secret",
            PausePolling = _ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }),
            PollOnce = () => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["updates"] = new JsonArray(),
                ["error"] = "Telegram poll error: 401 Unauthorized"
            }),
            AskWhenIdle = () => { idleAsked = true; return Task.FromResult<JsonObject?>(new JsonObject { ["messageId"] = 10, ["text"] = "idle" }); },
            ReplyTo = _ => { replied = true; return Task.FromResult<JsonObject?>(new JsonObject { ["messageId"] = 10, ["text"] = "reply" }); },
            StartBuild = () => { started = true; return Task.FromResult<JsonObject?>(new JsonObject { ["address"] = "http://127.0.0.1:4174" }); },
            SendAlert = _ => Task.FromResult<JsonObject?>(new JsonObject { ["text"] = "alert" }),
            StopBuild = _ => Task.CompletedTask,
            ResumePolling = _ => { resumed = true; return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); }
        });

        Assert.False(result["ok"]?.GetValue<bool>());
        Assert.True(result["restored"]?.GetValue<bool>());
        Assert.False(started);
        Assert.False(idleAsked);
        Assert.False(replied);
        Assert.True(resumed);
        Assert.Equal("Telegram poll error: 401 Unauthorized", result["blocker"]?.ToString());
        Assert.Null(result["evidence"]?["reply"]);
    }

    [Fact]
    public async Task Installed_listener_is_polling_again_when_updated_build_does_not_start()
    {
        var resumed = false;
        var result = await PhoneHandover.RunHandoverAsync(new HandoverCallArgs
        {
            Installed = Installed.DeepClone() as JsonObject,
            UpdatedBuild = Build,
            BackupOk = true,
            Token = "123456:secret",
            PausePolling = _ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }),
            PollOnce = () => Task.FromResult<JsonObject?>(new JsonObject { ["updates"] = new JsonArray() }),
            ReplyTo = _ => throw new InvalidOperationException("no inbound"),
            StartBuild = () => Task.FromResult<JsonObject?>(null),
            SendAlert = _ => throw new InvalidOperationException("no build"),
            StopBuild = _ => Task.CompletedTask,
            ResumePolling = _ => { resumed = true; return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); }
        });

        Assert.False(result["ok"]?.GetValue<bool>());
        Assert.True(result["restored"]?.GetValue<bool>());
        Assert.True(resumed);
    }

    [Fact]
    public async Task Thrown_step_after_pause_still_restores_polling_and_stops_build_that_started()
    {
        var (pollCalls, pollArgs) = ReadyHandover(a =>
        {
            a.PollOnce = () => throw new InvalidOperationException("poll dropped");
            a.StartBuild = () => throw new InvalidOperationException("must not start");
        });
        var pollResult = await PhoneHandover.RunHandoverAsync(pollArgs);
        Assert.False(pollResult["ok"]?.GetValue<bool>());
        Assert.True(pollResult["restored"]?.GetValue<bool>());
        Assert.Contains("poll dropped", pollResult["blocker"]?.ToString());
        Assert.Equal(new[] { "pause", "resume" }, pollCalls);

        var (replyCalls, replyArgs) = ReadyHandover(a =>
        {
            a.PollOnce = () => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["updates"] = new JsonArray
                {
                    new JsonObject { ["update_id"] = 8, ["message"] = new JsonObject { ["text"] = "status?" } }
                }
            });
            a.ReplyTo = _ => throw new InvalidOperationException("reply dropped");
        });
        var replyResult = await PhoneHandover.RunHandoverAsync(replyArgs);
        Assert.True(replyResult["restored"]?.GetValue<bool>());
        Assert.Equal("status?", replyResult["evidence"]?["inbound"]?["text"]?.ToString());
        Assert.DoesNotContain("start", replyCalls);
        Assert.Equal("resume", replyCalls.Last());

        var stopped = false;
        var (_, alertArgs) = ReadyHandover(a =>
        {
            a.SendAlert = _ => throw new InvalidOperationException("alert dropped");
            a.StopBuild = _ => { stopped = true; return Task.CompletedTask; };
        });
        var alertResult = await PhoneHandover.RunHandoverAsync(alertArgs);
        Assert.False(alertResult["ok"]?.GetValue<bool>());
        Assert.True(alertResult["restored"]?.GetValue<bool>());
        Assert.True(stopped);
        Assert.Contains("alert dropped", alertResult["blocker"]?.ToString());

        var stoppedAfterStart = false;
        var (startCalls, startArgs) = ReadyHandover(a =>
        {
            a.StartBuild = () => throw new InvalidOperationException("build missing");
            a.StopBuild = _ => { stoppedAfterStart = true; return Task.CompletedTask; };
        });
        var startResult = await PhoneHandover.RunHandoverAsync(startArgs);
        Assert.True(startResult["restored"]?.GetValue<bool>());
        Assert.False(stoppedAfterStart);
        Assert.Contains("build missing", startResult["blocker"]?.ToString());
        Assert.Equal("resume", startCalls.Last());
    }

    [Fact]
    public async Task Stopping_updated_build_cannot_skip_restoring_installed_listener()
    {
        var (calls, args) = ReadyHandover((c, a) =>
        {
            a.StopBuild = _ => { c.Add("stop"); throw new InvalidOperationException("kill failed"); };
        });
        var result = await PhoneHandover.RunHandoverAsync(args);
        Assert.True(result["restored"]?.GetValue<bool>());
        Assert.True(result["ok"]?.GetValue<bool>());
        Assert.Equal(new[] { "pause", "poll", "start", "alert", "stop", "resume" }, calls);
    }

    [Fact]
    public async Task Empty_alert_is_kept_as_failed_exchange_and_polling_still_returns()
    {
        var resumed = false;
        var (_, args) = ReadyHandover(a =>
        {
            a.SendAlert = _ => Task.FromResult<JsonObject?>(new JsonObject { ["messageId"] = 11, ["text"] = "" });
            a.ResumePolling = _ => { resumed = true; return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); };
        });
        var result = await PhoneHandover.RunHandoverAsync(args);
        Assert.False(result["ok"]?.GetValue<bool>());
        Assert.True(result["restored"]?.GetValue<bool>());
        Assert.True(resumed);
        Assert.Equal("", result["evidence"]?["outbound"]?["text"]?.ToString());
    }

    [Fact]
    public async Task No_pending_message_uses_idle_question_and_records_that_reply()
    {
        var calls = new List<string>();
        var (_, args) = ReadyHandover(a =>
        {
            a.PollOnce = () => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["updates"] = new JsonArray
                {
                    new JsonObject { ["update_id"] = 1, ["message"] = new JsonObject { ["text"] = "" } },
                    new JsonObject { ["update_id"] = 2 }
                }
            });
            a.AskWhenIdle = () =>
            {
                calls.Add("idle");
                return Task.FromResult<JsonObject?>(new JsonObject { ["messageId"] = 12, ["text"] = "🏴‍☠️ Ahoy, Captain!\nAll systems are idle." });
            };
            a.ReplyTo = _ => throw new InvalidOperationException("blank text is not a question");
        });
        var result = await PhoneHandover.RunHandoverAsync(args);
        Assert.True(result["ok"]?.GetValue<bool>());
        Assert.Equal(new[] { "idle" }, calls);
        Assert.Contains("All systems are idle", result["evidence"]?["reply"]?["text"]?.ToString());
        Assert.Null(result["evidence"]?["inbound"]);
    }

    [Fact]
    public async Task Pause_that_throws_does_not_resume_or_start_and_next_handover_can_run()
    {
        var started = false;
        var resumed = false;
        var (_, args) = ReadyHandover(a =>
        {
            a.PausePolling = _ => throw new InvalidOperationException("settings down");
            a.StartBuild = () => { started = true; return Task.FromResult<JsonObject?>(new JsonObject { ["address"] = "http://127.0.0.1:4174" }); };
            a.ResumePolling = _ => { resumed = true; return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); };
        });
        var failed = await PhoneHandover.RunHandoverAsync(args);
        Assert.False(failed["ok"]?.GetValue<bool>());
        Assert.False(failed["restored"]?.GetValue<bool>());
        Assert.Contains("settings down", failed["blocker"]?.ToString());
        Assert.False(started);
        Assert.False(resumed);

        var (_, nextArgs) = ReadyHandover();
        var next = await PhoneHandover.RunHandoverAsync(nextArgs);
        Assert.True(next["ok"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Second_handover_waits_out_first_instead_of_pausing_listener_twice()
    {
        var tcs = new TaskCompletionSource<bool>();
        var secondPaused = false;
        var (_, firstArgs) = ReadyHandover(a =>
        {
            a.PollOnce = async () => { await tcs.Task; return new JsonObject { ["updates"] = new JsonArray() }; };
        });

        var firstTask = PhoneHandover.RunHandoverAsync(firstArgs);
        await Task.Yield();

        var (_, secondArgs) = ReadyHandover(a =>
        {
            a.PausePolling = _ => { secondPaused = true; return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); };
        });
        var second = await PhoneHandover.RunHandoverAsync(secondArgs);

        Assert.False(second["ok"]?.GetValue<bool>());
        Assert.False(second["restored"]?.GetValue<bool>());
        Assert.False(secondPaused);
        Assert.Contains("already running", second["blocker"]?.ToString());

        tcs.SetResult(true);
        var finished = await firstTask;
        Assert.True(finished["ok"]?.GetValue<bool>());
        Assert.True(finished["restored"]?.GetValue<bool>());

        var (_, afterArgs) = ReadyHandover();
        var after = await PhoneHandover.RunHandoverAsync(afterArgs);
        Assert.True(after["ok"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Resume_failure_is_reported_after_alert_and_build_has_stopped()
    {
        var stopped = false;
        var (_, args) = ReadyHandover(a =>
        {
            a.StopBuild = _ => { stopped = true; return Task.CompletedTask; };
            a.ResumePolling = _ => Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = false });
        });
        var result = await PhoneHandover.RunHandoverAsync(args);
        Assert.True(stopped);
        Assert.False(result["ok"]?.GetValue<bool>());
        Assert.False(result["restored"]?.GetValue<bool>());
        Assert.Contains("Done", result["evidence"]?["outbound"]?["text"]?.ToString());
    }

    [Fact]
    public async Task Thrown_resume_is_failed_restore_after_build_stops()
    {
        var stopped = false;
        var (_, args) = ReadyHandover(a =>
        {
            a.StopBuild = _ => { stopped = true; return Task.CompletedTask; };
            a.ResumePolling = _ => throw new InvalidOperationException("listener down");
        });
        var result = await PhoneHandover.RunHandoverAsync(args);
        Assert.True(stopped);
        Assert.False(result["ok"]?.GetValue<bool>());
        Assert.False(result["restored"]?.GetValue<bool>());
        Assert.Contains("Done", result["evidence"]?["outbound"]?["text"]?.ToString());
    }

    [Fact]
    public async Task Missing_token_never_pauses_installed_listener()
    {
        var paused = false;
        var (_, args) = ReadyHandover(a =>
        {
            a.Token = "";
            a.PausePolling = _ => { paused = true; return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); };
        });
        var result = await PhoneHandover.RunHandoverAsync(args);
        Assert.False(result["ok"]?.GetValue<bool>());
        Assert.False(result["plan"]?["allowed"]?.GetValue<bool>());
        Assert.False(paused);
    }

    [Fact]
    public async Task Error_object_failed_poll_and_waiting_message_still_stop_without_build_or_idle_reply()
    {
        var shapes = new (JsonObject shape, string expectedBlocker)[]
        {
            (new JsonObject
            {
                ["updates"] = new JsonArray { new JsonObject { ["update_id"] = 4, ["message"] = new JsonObject { ["text"] = "status?" } } },
                ["error"] = new JsonObject { ["message"] = "socket down" }
            }, "socket down"),
            (new JsonObject
            {
                ["ok"] = false,
                ["updates"] = new JsonArray(),
                ["reason"] = new JsonObject { ["message"] = "disk full" }
            }, "disk full"),
            (new JsonObject
            {
                ["ok"] = false,
                ["updates"] = new JsonArray(),
                ["reason"] = ""
            }, "Handover poll failed."),
            (new JsonObject
            {
                ["updates"] = new JsonArray(),
                ["error"] = new JsonObject { ["message"] = "poll exploded" }
            }, "poll exploded"),
            (new JsonObject
            {
                ["updates"] = new JsonArray(),
                ["error"] = 503
            }, "503"),
            (new JsonObject
            {
                ["updates"] = new JsonArray(),
                ["error"] = new JsonObject()
            }, "Handover poll failed."),
            (new JsonObject
            {
                ["updates"] = new JsonArray(),
                ["error"] = " "
            }, " ")
        };

        foreach (var (shape, expectedBlocker) in shapes)
        {
            var started = false;
            var idleAsked = false;
            var replied = false;
            var alerted = false;
            JsonObject? resumed = null;

            var (_, args) = ReadyHandover(a =>
            {
                a.PollOnce = () => Task.FromResult<JsonObject?>(shape.DeepClone() as JsonObject);
                a.AskWhenIdle = () => { idleAsked = true; return Task.FromResult<JsonObject?>(new JsonObject { ["messageId"] = 1, ["text"] = "idle" }); };
                a.ReplyTo = _ => { replied = true; return Task.FromResult<JsonObject?>(new JsonObject { ["messageId"] = 2, ["text"] = "reply" }); };
                a.StartBuild = () => { started = true; return Task.FromResult<JsonObject?>(new JsonObject { ["address"] = "http://127.0.0.1:4174" }); };
                a.SendAlert = _ => { alerted = true; return Task.FromResult<JsonObject?>(new JsonObject { ["text"] = "alert" }); };
                a.ResumePolling = listener => { resumed = listener; return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); };
            });

            var result = await PhoneHandover.RunHandoverAsync(args);
            Assert.False(result["ok"]?.GetValue<bool>());
            Assert.True(result["restored"]?.GetValue<bool>());
            Assert.Equal(expectedBlocker, result["blocker"]?.ToString());
            Assert.False(started);
            Assert.False(idleAsked);
            Assert.False(replied);
            Assert.False(alerted);
            Assert.NotNull(resumed);
            Assert.Null(result["evidence"]?["reply"]);
            Assert.Null(result["evidence"]?["outbound"]);
        }
    }
}
