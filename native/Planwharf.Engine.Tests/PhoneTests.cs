using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class PhoneTests
{
    [Fact]
    public void SanitizeText_strips_paths_files_secrets_and_controls()
    {
        Assert.Equal("Fix [path] [path] [file] [redacted]", Phone.SanitizeText("Fix C:\\private\\file /home/owner app.cs token=synthetic\n"));
        Assert.Equal("Work", Phone.SanitizeGoalTitle("\nsecret"));
        Assert.Equal(80, Phone.SanitizeGoalTitle(new string('a', 90)).Length);
        Assert.Equal("&lt;&amp;&gt;", Phone.EscapeHtml("<&>"));
    }
    [Fact]
    public void Progress_zero_rounding_and_bounds_match_source()
    {
        Assert.Equal("□□□ 0/3", Phone.FormatProgressBar(JsonValue.Create("0/3")));
        Assert.Equal("■■■■■■■■■■ 20/12", Phone.FormatProgressBar(JsonValue.Create("20/12")));
        Assert.Equal("", Phone.FormatProgressBar(JsonValue.Create("1/0")));
        Assert.Equal("1h 1m", Phone.FormatDuration(60.5));
        Assert.Null(Phone.FormatDuration(-1));
        Assert.Equal("pw-100000", Phone.GenerateStartCode((min, max) => { Assert.Equal(999999, max); return min; }));
        Assert.Equal("synt...oken", Phone.MaskToken("synthetic-token"));
    }
    [Fact]
    public void Ambiguous_or_duplicate_project_names_do_not_select_a_crew()
    {
        var projects = JsonNode.Parse("""[{"id":"a","name":"Harbor"},{"id":"b","name":"Harbor"}]""");
        Assert.Null(Phone.MatchProject(projects));
        Assert.Null(Phone.MatchProject(projects, "Harbor"));
        Assert.Equal("a", Phone.MatchProject(projects, "a")!["id"]!.GetValue<string>());
        Assert.Null(Phone.AlertForStatus(null, "complete"));
        Assert.Equal("resumed", Phone.AlertForStatus("failed", "watching"));
        Assert.Null(Phone.AlertForStatus("watching", "watching"));
    }

    private sealed class TempStore : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pw-phone-test-" + Guid.NewGuid().ToString("N"));
        public TempStore() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }

    [Fact]
    public void Settings_lifecycle_private_token_and_unknown_fields_gated()
    {
        using var store = new TempStore();
        var defaults = Phone.LoadPhoneSettings(store.Path, (min, max) => 123456);
        Assert.Equal("pw-123456", defaults["telegram"]!["startCode"]!.GetValue<string>());
        Assert.Equal(0, defaults["telegram"]!["updateOffset"]!.GetValue<int>());
        Assert.False(defaults["telegram"]!["enabled"]!.GetValue<bool>());
        Assert.Null(defaults["telegramToken"]);
        Assert.False(defaults["corrupt"]!.GetValue<bool>());

        // Save new settings and separate token
        var saved = Phone.SavePhoneSettings(store.Path, new JsonObject
        {
            ["ntfy"] = new JsonObject { ["enabled"] = true, ["topic"] = "my-topic" },
            ["telegram"] = new JsonObject { ["enabled"] = true }
        }, newToken: "synthetic-bot-token-1234");

        Assert.True(saved["ntfy"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("my-topic", saved["ntfy"]!["topic"]!.GetValue<string>());
        Assert.Equal("synthetic-bot-token-1234", saved["telegramToken"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(store.Path, "telegram-bot.token")));
        Assert.Equal("synthetic-bot-token-1234", File.ReadAllText(Path.Combine(store.Path, "telegram-bot.token")).Trim());

        // Replace token with empty deletes the token file
        var cleared = Phone.SavePhoneSettings(store.Path, new JsonObject(), newToken: "", replaceToken: true);
        Assert.Null(cleared["telegramToken"]);
        Assert.False(File.Exists(Path.Combine(store.Path, "telegram-bot.token")));

        // Corrupt file recovery
        File.WriteAllText(Path.Combine(store.Path, "phone-settings.json"), "{ broken json");
        var corrupt = Phone.LoadPhoneSettings(store.Path);
        Assert.True(corrupt["corrupt"]!.GetValue<bool>());
        var repaired = Phone.SavePhoneSettings(store.Path, new JsonObject());
        Assert.False(repaired["corrupt"]!.GetValue<bool>());

        // Unknown fields decision: throwing InvalidOperationException with UnknownSettingsDecision
        File.WriteAllText(Path.Combine(store.Path, "phone-settings.json"), """{"customField":"extra","telegram":{"enabled":true}}""");
        var ex = Assert.Throws<InvalidOperationException>(() => Phone.SavePhoneSettings(store.Path, new JsonObject()));
        Assert.Equal(Phone.UnknownSettingsDecision, ex.Message);
    }

    [Fact]
    public void Alert_formatting_and_voice_in_both_languages()
    {
        // Plain alert message
        var plainEn = Phone.FormatAlertMessage(new JsonObject
        {
            ["type"] = "done",
            ["projectName"] = "Dock file.cs & Two",
            ["goalTitle"] = "Ship cargo\nsecond line",
            ["durationMinutes"] = 65,
            ["savingMode"] = "safe"
        });
        Assert.Contains("Planwharf: Dock [file] & Two", plainEn);
        Assert.Contains("Duration: 1h 5m", plainEn);
        Assert.Contains("Saving: Safe", plainEn);

        var plainIt = Phone.FormatAlertMessage(new JsonObject
        {
            ["type"] = "needs_owner",
            ["projectName"] = "Porto",
            ["goalTitle"] = "Fissare i cavi",
            ["timeLeft"] = new JsonObject { ["minutes"] = 12, ["longerThanUsual"] = true },
            ["detail"] = "Controllare D:\\secret\\file.txt",
            ["language"] = "it"
        });
        Assert.Contains("Obiettivo: Fissare i cavi", plainIt);
        Assert.Contains("Stato: Serve il tuo intervento 🔔", plainIt);
        Assert.Contains("Stima: Circa 12m rimasti (più del solito)", plainIt);
        Assert.Contains("Controllare [path]", plainIt);
        Assert.DoesNotContain("secret", plainIt);

        // Telegram HTML alert message with companion voice
        var tgEn = Phone.FormatTelegramAlertMessage(new JsonObject
        {
            ["type"] = "started",
            ["projectName"] = "Ship <Great>",
            ["goalTitle"] = "Fix `main` branch",
            ["step"] = new JsonObject { ["step"] = 2, ["total"] = 5 },
            ["savingMode"] = "ultra-safe",
            ["crew"] = new JsonObject
            {
                ["lead"] = "Grok 4.7",
                ["leadFrom"] = "GPT-5",
                ["build"] = new JsonArray
                {
                    new JsonObject { ["name"] = "Claude 3.7", ["from"] = "Gemini", ["state"] = "⚙️" }
                },
                ["verify"] = new JsonArray()
            }
        });
        Assert.StartsWith("🏴‍☠️ <b>AHOY, CAPTAIN!</b>", tgEn);
        Assert.Contains("🚢 <b>Ship &lt;Great&gt;</b>", tgEn);
        Assert.Contains("🎯 Fix <code>main</code> branch", tgEn);
        Assert.Contains("■■□□□ 2/5", tgEn);
        Assert.Contains("<b>Saving:</b> Ultra safe", tgEn);
        Assert.Contains("👑 <b>Lead</b> GPT-5 → Grok 4.7", tgEn);
        Assert.Contains("🔨 <b>Build</b> Gemini → Claude 3.7 ⚙️", tgEn);

        // Italian voice with duration
        var tgIt = Phone.FormatTelegramAlertMessage(new JsonObject
        {
            ["type"] = "done",
            ["projectName"] = "Nave",
            ["goalTitle"] = "Lavoro finito",
            ["durationMinutes"] = 30,
            ["language"] = "it"
        });
        Assert.StartsWith("🏴‍☠️ <b>AHOY, CAPITANO!</b>", tgIt);
        Assert.Contains("🏆 Missione compiuta!", tgIt);
        Assert.Contains("🧭 Fatto ✅", tgIt);
        Assert.Contains("⏱️ 30m", tgIt);
    }

    [Fact]
    public async Task Http_isolation_ntfy_and_telegram_transport()
    {
        var sentRequests = new List<HttpRequestMessage>();
        HttpResponseMessage FakeHandler(HttpRequestMessage req)
        {
            sentRequests.Add(req);
            if (req.RequestUri!.ToString().Contains("fail"))
                return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"ok":false,"description":"Bad Request"}""", System.Text.Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":[{"update_id":42,"message":{"text":"hi"}}]}""", System.Text.Encoding.UTF8, "application/json")
            };
        }

        // SendNtfyAlertAsync
        var ntfyRes = await Phone.SendNtfyAlertAsync(
            topic: "alerts-topic",
            serverUrl: "https://ntfy.test",
            message: "Hello Captain",
            title: "Planwharf Title",
            priority: "high",
            tags: ["warning", "bell"],
            fetch: (req, ct) => Task.FromResult(FakeHandler(req)));

        Assert.True(ntfyRes["ok"]!.GetValue<bool>());
        var ntfyReq = sentRequests.First(r => r.RequestUri!.Host == "ntfy.test");
        Assert.Equal("/alerts-topic", ntfyReq.RequestUri!.AbsolutePath);
        Assert.Equal("Planwharf Title", ntfyReq.Headers.GetValues("Title").First());
        Assert.Equal("high", ntfyReq.Headers.GetValues("Priority").First());
        Assert.Equal("warning,bell", ntfyReq.Headers.GetValues("Tags").First());

        // SendTelegramMessageAsync
        var tgRes = await Phone.SendTelegramMessageAsync(
            token: "tok-123",
            chatId: "chat-456",
            text: "<b>Test</b>",
            apiBaseUrl: "https://tg.test",
            fetch: (req, ct) => Task.FromResult(FakeHandler(req)));

        Assert.True(tgRes["ok"]!.GetValue<bool>());
        var tgReq = sentRequests.First(r => r.RequestUri!.Host == "tg.test" && r.RequestUri.AbsolutePath.Contains("sendMessage"));
        Assert.Equal("/bottok-123/sendMessage", tgReq.RequestUri!.AbsolutePath);

        // PollTelegramUpdatesAsync
        var pollRes = await Phone.PollTelegramUpdatesAsync(
            token: "tok-123",
            offset: 10,
            apiBaseUrl: "https://tg.test",
            fetch: (req, ct) => Task.FromResult(FakeHandler(req)));

        Assert.True(pollRes["ok"]!.GetValue<bool>());
        Assert.Single(pollRes["updates"]!.AsArray());
        Assert.Equal(42, pollRes["updates"]![0]!["update_id"]!.GetValue<double>());
    }

    [Fact]
    public async Task Telegram_pairing_lifecycle_and_ignoring_foreign_chats()
    {
        using var store = new TempStore();
        Phone.SavePhoneSettings(store.Path, new JsonObject
        {
            ["telegram"] = new JsonObject { ["enabled"] = true, ["startCode"] = "pw-999888" }
        }, newToken: "fake-bot-token");

        var sentMessages = new List<(string ChatId, string Text)>();
        Task<HttpResponseMessage> FakeHttp(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath.Contains("sendMessage"))
            {
                var body = req.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                var node = JsonNode.Parse(body)!;
                sentMessages.Add((node["chat_id"]!.GetValue<string>(), node["text"]!.GetValue<string>()));
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":{}}""", System.Text.Encoding.UTF8, "application/json")
            });
        }

        var notifier = Phone.CreatePhoneNotifier(new PhoneNotifierOptions
        {
            StoreDir = store.Path,
            Fetch = FakeHttp
        });

        // 1. Message with invalid start code is ignored
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "chat-1" },
            ["text"] = "wrong-code"
        });
        Assert.Empty(sentMessages);
        Assert.False(notifier.GetSettings()["telegram"]!["paired"]!.GetValue<bool>());

        // 2. Message with valid start code pairs successfully
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "-1001234567890", ["username"] = "captain" },
            ["text"] = "/start pw-999888"
        });
        Assert.Single(sentMessages);
        Assert.Equal("-1001234567890", sentMessages[0].ChatId);
        Assert.Contains("Planwharf connected and standing by", sentMessages[0].Text);
        Assert.True(notifier.GetSettings()["telegram"]!["paired"]!.GetValue<bool>());
        Assert.Equal("-1001234567890", notifier.GetSettings()["telegram"]!["pairedChatId"]!.GetValue<string>());

        // 3. Message from another chat is ignored
        sentMessages.Clear();
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "foreign-chat" },
            ["text"] = "/status"
        });
        Assert.Empty(sentMessages);

        // 4. Message from paired chat is answered
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "-1001234567890" },
            ["text"] = "/status"
        });
        Assert.Single(sentMessages);
        Assert.Contains("No projects in port.", sentMessages[0].Text);
    }

    [Fact]
    public async Task Stop_confirmation_expiry_and_cancellation()
    {
        using var store = new TempStore();
        Phone.SavePhoneSettings(store.Path, new JsonObject
        {
            ["telegram"] = new JsonObject
            {
                ["enabled"] = true,
                ["pairedChatId"] = "owner-chat"
            }
        }, newToken: "fake-token");

        var sentMessages = new List<string>();
        Task<HttpResponseMessage> FakeHttp(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath.Contains("sendMessage"))
            {
                var body = req.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                var node = JsonNode.Parse(body)!;
                sentMessages.Add(node["text"]!.GetValue<string>());
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":{}}""", System.Text.Encoding.UTF8, "application/json")
            });
        }

        var stoppedProjects = new List<string>();
        long simulatedTime = 10000;
        var projects = new JsonArray
        {
            new JsonObject { ["id"] = "proj-1", ["name"] = "Harbor Project", ["status"] = "watching" }
        };

        var notifier = Phone.CreatePhoneNotifier(new PhoneNotifierOptions
        {
            StoreDir = store.Path,
            Fetch = FakeHttp,
            Clock = () => simulatedTime,
            ConfirmWindowMs = 5000,
            GetSnapshot = () => Task.FromResult<JsonObject?>(new JsonObject { ["projects"] = projects.DeepClone() }),
            OnStopProject = id => { stoppedProjects.Add(id); return Task.CompletedTask; }
        });

        // Request stop -> asks confirmation
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "owner-chat" },
            ["text"] = "/stop Harbor"
        });
        Assert.Single(sentMessages);
        Assert.Contains("Stop the crew for \"Harbor Project\"?", sentMessages[0]);

        // Non-confirmation message cancels confirmation
        sentMessages.Clear();
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "owner-chat" },
            ["text"] = "nevermind"
        });
        Assert.Single(sentMessages);
        Assert.Contains("Stop cancelled. Crew will continue work.", sentMessages[0]);
        Assert.Empty(stoppedProjects);

        // Request stop again
        sentMessages.Clear();
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "owner-chat" },
            ["text"] = "/stop Harbor"
        });
        Assert.Single(sentMessages);

        // Confirmation expires after 5000ms
        simulatedTime += 6000;
        sentMessages.Clear();
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "owner-chat" },
            ["text"] = "yes"
        });
        Assert.Empty(stoppedProjects);
        Assert.Contains("Unrecognized command", sentMessages[0]);

        // Request stop third time and confirm within window
        sentMessages.Clear();
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "owner-chat" },
            ["text"] = "/stop Harbor"
        });
        sentMessages.Clear();
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "owner-chat" },
            ["text"] = "yes"
        });
        Assert.Single(stoppedProjects);
        Assert.Equal("proj-1", stoppedProjects[0]);
        Assert.Contains("The crew for \"Harbor Project\" has been stopped.", sentMessages[0]);
    }

    [Fact]
    public async Task Transitions_deduplication_and_lead_change_with_arrow()
    {
        using var store = new TempStore();
        Phone.SavePhoneSettings(store.Path, new JsonObject
        {
            ["telegram"] = new JsonObject { ["enabled"] = true, ["pairedChatId"] = "owner-chat" },
            ["ntfy"] = new JsonObject { ["enabled"] = true, ["topic"] = "alerts" }
        }, newToken: "fake-token");

        var sentNtfy = new List<string>();
        var sentTelegram = new List<string>();
        Task<HttpResponseMessage> FakeHttp(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult() ?? "";
            if (req.RequestUri!.AbsolutePath.Contains("sendMessage"))
            {
                var node = JsonNode.Parse(body)!;
                sentTelegram.Add(node["text"]!.GetValue<string>());
            }
            else if (req.RequestUri.Host == "ntfy.sh")
            {
                sentNtfy.Add(body);
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":{}}""", System.Text.Encoding.UTF8, "application/json")
            });
        }

        var project = new JsonObject
        {
            ["id"] = "proj-alpha",
            ["name"] = "Alpha",
            ["status"] = "watching",
            ["goal"] = "Build engine",
            ["leadLabel"] = "Lead A",
            ["subTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = "s1", ["title"] = "Build core", ["assignedModelId"] = "m1", ["status"] = "RUNNING" }
            }
        };

        var models = new JsonArray
        {
            new JsonObject { ["id"] = "m1", ["label"] = "Model 1" },
            new JsonObject { ["id"] = "m2", ["label"] = "Model 2" }
        };

        JsonObject? snapshot = new JsonObject { ["projects"] = new JsonArray { project }, ["models"] = models };

        var notifier = Phone.CreatePhoneNotifier(new PhoneNotifierOptions
        {
            StoreDir = store.Path,
            Fetch = FakeHttp,
            GetSnapshot = () => Task.FromResult(snapshot?.DeepClone()!.AsObject())
        });

        // 1. Initial check establishes baseline; second check records crew; no alert sent
        await notifier.CheckTransitionsAsync();
        await notifier.CheckTransitionsAsync();
        Assert.Empty(sentTelegram);
        Assert.Empty(sentNtfy);

        // 2. New lead change sends lead alert with arrow
        project["leadLabel"] = "Lead B";
        await notifier.CheckTransitionsAsync();
        Assert.Single(sentTelegram);
        Assert.Contains("👑 <b>Lead</b> Lead A → Lead B", sentTelegram[0]);
        Assert.Contains("New lead 👑", sentTelegram[0]);

        // 3. Status changes to complete -> sends done alert
        sentTelegram.Clear();
        sentNtfy.Clear();
        project["status"] = "complete";
        await notifier.CheckTransitionsAsync();
        Assert.Single(sentTelegram);
        Assert.Contains("Done ✅", sentTelegram[0]);
        Assert.Single(sentNtfy);
        Assert.Contains("Status: Done ✅", sentNtfy[0]);
    }

    [Fact]
    public async Task Decisions_command_and_callback_queries()
    {
        using var store = new TempStore();
        Phone.SavePhoneSettings(store.Path, new JsonObject
        {
            ["telegram"] = new JsonObject { ["enabled"] = true, ["pairedChatId"] = "owner-chat" }
        }, newToken: "fake-token");

        var sentMessages = new List<(string Text, JsonNode? ReplyMarkup)>();
        Task<HttpResponseMessage> FakeHttp(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult() ?? "";
            if (req.RequestUri!.AbsolutePath.Contains("sendMessage"))
            {
                var node = JsonNode.Parse(body)!;
                sentMessages.Add((node["text"]!.GetValue<string>(), node["reply_markup"]?.DeepClone()));
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":{}}""", System.Text.Encoding.UTF8, "application/json")
            });
        }

        var openDecisions = new JsonArray();
        string? answeredDecision = null;
        string? delegatedDecision = null;

        var notifier = Phone.CreatePhoneNotifier(new PhoneNotifierOptions
        {
            StoreDir = store.Path,
            Fetch = FakeHttp,
            GetDecisions = () => Task.FromResult<JsonObject?>(new JsonObject { ["open"] = openDecisions.DeepClone() }),
            OnAnswerDecision = (proj, dec, choice) => { answeredDecision = $"{proj}:{dec}:{choice}"; return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); },
            OnDelegateDecision = (proj, dec) => { delegatedDecision = $"{proj}:{dec}"; return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true }); }
        });

        // 1. None waiting
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "owner-chat" },
            ["text"] = "/decisions"
        });
        Assert.Single(sentMessages);
        Assert.Contains("No decisions waiting.", sentMessages[0].Text);

        // 2. Open decisions with suggestion and break warning
        sentMessages.Clear();
        openDecisions.Add(new JsonObject
        {
            ["id"] = "dec-1",
            ["projectId"] = "proj-1",
            ["projectName"] = "Harbor",
            ["workTitle"] = "Engine Refit",
            ["text"] = "Replace boiler?",
            ["suggestion"] = "Keep original boiler",
            ["breaks"] = true
        });

        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "owner-chat" },
            ["text"] = "/decisions"
        });
        Assert.Single(sentMessages);
        Assert.Contains("<b>Harbor</b> · Engine Refit", sentMessages[0].Text);
        Assert.Contains("❓ Replace boiler? ⚠️ <b>Could break something</b>", sentMessages[0].Text);
        Assert.Contains("💡 <i>Suggestion: Keep original boiler</i>", sentMessages[0].Text);
        var markup = sentMessages[0].ReplyMarkup!;
        var inline = markup["inline_keyboard"]!.AsArray()[0]!.AsArray();
        Assert.Contains("Accept: Keep original boiler", inline[0]!["text"]!.GetValue<string>());
        Assert.Contains("Let the lead decide", inline[1]!["text"]!.GetValue<string>());

        // 3. Callback query for accept suggestion
        sentMessages.Clear();
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["callback_query"] = new JsonObject
            {
                ["id"] = "cb-1",
                ["data"] = "decide_suggest:proj-1:dec-1",
                ["message"] = new JsonObject { ["chat"] = new JsonObject { ["id"] = "owner-chat" } }
            }
        });
        Assert.Equal("proj-1:dec-1:accepted", answeredDecision);
        Assert.Single(sentMessages);
        Assert.Contains("Decision accepted with suggestion.", sentMessages[0].Text);

        // 4. Callback query for let lead decide
        sentMessages.Clear();
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["callback_query"] = new JsonObject
            {
                ["id"] = "cb-2",
                ["data"] = "decide_lead:proj-1:dec-1",
                ["message"] = new JsonObject { ["chat"] = new JsonObject { ["id"] = "owner-chat" } }
            }
        });
        Assert.Equal("proj-1:dec-1", delegatedDecision);
        Assert.Single(sentMessages);
        Assert.Contains("Let the lead decide.", sentMessages[0].Text);
    }

    [Fact]
    public async Task Concurrent_polling_and_persisted_offset_before_handler()
    {
        using var store = new TempStore();
        Phone.SavePhoneSettings(store.Path, new JsonObject
        {
            ["telegram"] = new JsonObject
            {
                ["enabled"] = true,
                ["pairedChatId"] = "owner-chat",
                ["updateOffset"] = 100
            }
        }, newToken: "fake-token");

        var handlerExecuted = false;
        Task<HttpResponseMessage> FakeHttp(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath.Contains("getUpdates"))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ok":true,"result":[{"update_id":100,"message":{"chat":{"id":"owner-chat"},"text":"crash-test"}}]}""", System.Text.Encoding.UTF8, "application/json")
                });
            }
            if (req.RequestUri.AbsolutePath.Contains("sendMessage"))
            {
                handlerExecuted = true;
                throw new InvalidOperationException("Simulated handler crash during processing");
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":{}}""", System.Text.Encoding.UTF8, "application/json")
            });
        }

        var notifier = Phone.CreatePhoneNotifier(new PhoneNotifierOptions
        {
            StoreDir = store.Path,
            Fetch = FakeHttp
        });

        // Even if sending the reply throws, offset was persisted before handler execution!
        try { await notifier.PollUpdatesAsync(); } catch { }

        Assert.True(handlerExecuted);
        var freshSettings = Phone.LoadPhoneSettings(store.Path);
        Assert.Equal(101.0, freshSettings["telegram"]!["updateOffset"]!.GetValue<double>());

        // Concurrent polling and transitions execute without deadlock or corrupted state
        var t1 = Task.Run(() => notifier.PollUpdatesAsync());
        var t2 = Task.Run(() => notifier.CheckTransitionsAsync());
        await Task.WhenAll(t1, t2);
    }

    [Fact]
    public async Task Ollama_fallback_memory_and_probe()
    {
        using var store = new TempStore();
        Phone.SavePhoneSettings(store.Path, new JsonObject
        {
            ["telegram"] = new JsonObject { ["enabled"] = true, ["pairedChatId"] = "owner-chat" },
            ["ollama"] = new JsonObject { ["enabled"] = true, ["model"] = "qwen2.5:3b", ["baseUrl"] = "http://127.0.0.1:11434" }
        }, newToken: "fake-token");

        var sentMessages = new List<string>();
        Task<HttpResponseMessage> FakeHttp(HttpRequestMessage req, CancellationToken ct)
        {
            var uri = req.RequestUri!.ToString();
            if (uri.Contains("api/tags"))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"models":[{"name":"qwen2.5:3b"}]}""", System.Text.Encoding.UTF8, "application/json")
                });
            }
            if (uri.Contains("api/chat"))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"message":{"content":"Ahoy! The engine is running smoothly."}}""", System.Text.Encoding.UTF8, "application/json")
                });
            }
            if (uri.Contains("sendMessage"))
            {
                var body = req.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
                var node = JsonNode.Parse(body)!;
                sentMessages.Add(node["text"]!.GetValue<string>());
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":{}}""", System.Text.Encoding.UTF8, "application/json")
            });
        }

        var notifier = Phone.CreatePhoneNotifier(new PhoneNotifierOptions
        {
            StoreDir = store.Path,
            Fetch = FakeHttp
        });

        // 1. Probe Ollama
        var probe = await notifier.ProbeOllamaAsync();
        Assert.True(probe["ok"]!.GetValue<bool>());
        Assert.True(probe["installed"]!.GetValue<bool>());
        Assert.Equal("qwen2.5:3b", probe["model"]!.GetValue<string>());

        // 2. Plain question falls back to Ollama
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "owner-chat" },
            ["text"] = "How is the work going?"
        });
        Assert.Single(sentMessages);
        Assert.Contains("The engine is running smoothly", sentMessages[0]);

        // 3. Clear memory command
        sentMessages.Clear();
        await notifier.ProcessTelegramMessageAsync(new JsonObject
        {
            ["chat"] = new JsonObject { ["id"] = "owner-chat" },
            ["text"] = "/dimentica"
        });
        Assert.Single(sentMessages);
        Assert.Contains("This chat memory is clear. Commands are unchanged.", sentMessages[0]);
    }
}
