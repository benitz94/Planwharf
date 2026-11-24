// Tests for engine/provider-models.cjs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ProviderModelsTests
{
    [Fact]
    public async Task Legacy_grok_discovery_preserves_access_on_failed_checks()
    {
        var models = new List<JsonObject>
        {
            new() { ["id"] = "grok-fixture", ["provider"] = "grok", ["cli"] = "grok-fixture", ["available"] = true, ["enabled"] = true },
            new() { ["id"] = "other", ["provider"] = "grok", ["cli"] = "grok-other", ["available"] = false }
        };

        var result = new JsonObject
        {
            ["ok"] = true,
            ["ids"] = new JsonArray { (JsonNode)"grok-fixture" }
        };

        var discovery = ProviderModels.CreateProviderModels(models, new ProviderModels.ProviderModelsOptions
        {
            Discover = _ => Task.FromResult(result)
        });

        await discovery.RefreshAsync(true);
        Assert.True(models[0]["availabilityVerified"]?.GetValue<bool>());
        Assert.True(models[0]["available"]?.GetValue<bool>());

        // Failed check retains previous availability
        result = new JsonObject
        {
            ["ok"] = false,
            ["reason"] = "Fixture temporary quota or policy failure."
        };

        var failed = await discovery.RefreshAsync(true);
        Assert.Equal("unread", failed["status"]?.ToString());
        Assert.True(models[0]["available"]?.GetValue<bool>());
        Assert.True(models[0]["enabled"]?.GetValue<bool>());
        Assert.False(models[0]["availabilityVerified"]?.GetValue<bool>());
        Assert.False(models[1]["available"]?.GetValue<bool>());

        // New exact ID updates availability
        result = new JsonObject
        {
            ["ok"] = true,
            ["ids"] = new JsonArray { (JsonNode)"grok-other" }
        };

        await discovery.RefreshAsync(true);
        Assert.False(models[0]["available"]?.GetValue<bool>());
        Assert.True(models[1]["available"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Catalog_discovery_isolates_providers_and_respects_ttl()
    {
        var catalogModels = new List<JsonObject>
        {
            new() { ["id"] = "codex-isolated", ["providerId"] = "codex-test", ["cli"] = "gpt-isolated", ["available"] = false, ["unavailableReason"] = "Old rejected model probe", ["roles"] = new JsonObject { ["worker"] = false, ["reviewer"] = true } },
            new() { ["id"] = "grok-isolated", ["providerId"] = "grok-test", ["cli"] = "grok-isolated", ["available"] = false }
        };
        var catalogProviders = new List<JsonObject>
        {
            new() { ["id"] = "codex-test", ["adapter"] = "codex" },
            new() { ["id"] = "grok-test", ["adapter"] = "grok" }
        };

        var inspected = new List<string>();
        bool catalogReadFails = false;

        var isolated = ProviderModels.CreateProviderModels(catalogModels, new ProviderModels.ProviderModelsOptions
        {
            Providers = catalogProviders,
            Discover = provider =>
            {
                var id = provider?["id"]?.ToString() ?? "";
                inspected.Add(id);
                if (catalogReadFails) return Task.FromResult(new JsonObject { ["ok"] = false, ["reason"] = "Fixture catalog temporarily unavailable." });
                var ids = new JsonArray();
                foreach (var m in catalogModels.Where(m => m["providerId"]?.ToString() == id))
                {
                    ids.Add((JsonNode)(m["cli"]?.ToString() ?? ""));
                }
                return Task.FromResult(new JsonObject { ["ok"] = true, ["ids"] = ids });
            }
        });

        await isolated.RefreshAsync(true);
        Assert.Equal(["codex-test", "grok-test"], inspected);
        Assert.True(catalogModels.All(m => m["available"]?.GetValue<bool>() == true));
        Assert.Equal(2, catalogModels.Count);
        Assert.Null(catalogModels[0]["unavailableReason"]);
        Assert.True(catalogModels[0]["availabilityVerified"]?.GetValue<bool>());
        Assert.NotNull(catalogModels[0]["availabilityCheckedAt"]);

        // Repeated refresh within TTL does not invoke provider
        await isolated.RefreshAsync();
        Assert.Equal(2, inspected.Count);

        // Failed check retains previous availability
        catalogReadFails = true;
        await isolated.RefreshAsync(true);
        Assert.True(catalogModels[0]["available"]?.GetValue<bool>());
        Assert.False(catalogModels[0]["availabilityVerified"]?.GetValue<bool>());
        Assert.Contains("temporarily unavailable", catalogModels[0]["availabilityWarning"]?.ToString() ?? "");
        var snap = isolated.Snapshot();
        Assert.Equal("unread", (snap["providers"] as JsonArray)?[0]?["status"]?.ToString());
    }

    [Fact]
    public async Task Cache_file_persistence_survives_restart_and_hashes_account()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-models-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var cacheFile = Path.Combine(tempDir, "catalogs.json");

        try
        {
            var fixtureProviders = new List<JsonObject>
            {
                new() { ["id"] = "codex-cache", ["adapter"] = "codex", ["quotaPoolId"] = "codex", ["account"] = new JsonObject { ["key"] = "isolated-account" }, ["label"] = "codex" },
                new() { ["id"] = "grok-cache", ["adapter"] = "grok", ["quotaPoolId"] = "grok", ["account"] = new JsonObject { ["key"] = "isolated-account" }, ["label"] = "grok" }
            };

            List<JsonObject> FreshModels() =>
            [
                new() { ["id"] = "codex-cache-retired", ["providerId"] = "codex-cache", ["cli"] = "codex-retired", ["available"] = true, ["enabled"] = true, ["roles"] = new JsonObject { ["orchestrator"] = true, ["worker"] = false, ["reviewer"] = true } },
                new() { ["id"] = "codex-cache-current", ["providerId"] = "codex-cache", ["cli"] = "codex-current", ["available"] = false, ["enabled"] = true }
            ];

            var firstModels = FreshModels();
            var firstBoot = ProviderModels.CreateProviderModels(firstModels, new ProviderModels.ProviderModelsOptions
            {
                Providers = fixtureProviders,
                CacheFile = cacheFile,
                Discover = p => Task.FromResult(new JsonObject
                {
                    ["ok"] = true,
                    ["ids"] = new JsonArray { (JsonNode)(p?["adapter"]?.ToString() + "-current"), (JsonNode)(p?["adapter"]?.ToString() + "-new") }
                })
            });

            await firstBoot.RefreshAsync(true);
            var retired = firstModels.Find(m => m["id"]?.ToString() == "codex-cache-retired");
            Assert.NotNull(retired);
            Assert.True(retired["catalogExcluded"]?.GetValue<bool>());
            Assert.False(retired["available"]?.GetValue<bool>());

            var newModel = firstModels.Find(m => m["cli"]?.ToString() == "codex-new");
            Assert.NotNull(newModel);
            Assert.False(newModel["enabled"]?.GetValue<bool>());
            Assert.True(newModel["available"]?.GetValue<bool>());

            var cachedText = File.ReadAllText(cacheFile);
            Assert.DoesNotContain("isolated-account", cachedText);

            // Restart offline
            var restartedModels = FreshModels();
            var offlineBoot = ProviderModels.CreateProviderModels(restartedModels, new ProviderModels.ProviderModelsOptions
            {
                Providers = fixtureProviders,
                CacheFile = cacheFile,
                Discover = _ => Task.FromResult(new JsonObject { ["ok"] = false, ["reason"] = "Isolated network failure" })
            });

            await offlineBoot.RefreshAsync(true);
            var restartedRetired = restartedModels.Find(m => m["id"]?.ToString() == "codex-cache-retired");
            Assert.NotNull(restartedRetired);
            Assert.True(restartedRetired["catalogExcluded"]?.GetValue<bool>());
            Assert.False(restartedRetired["available"]?.GetValue<bool>());

            var restartedNew = restartedModels.Find(m => m["cli"]?.ToString() == "codex-new");
            Assert.NotNull(restartedNew);
            Assert.True(restartedNew["available"]?.GetValue<bool>());
            Assert.False(restartedNew["enabled"]?.GetValue<bool>());
            Assert.False(restartedNew["availabilityVerified"]?.GetValue<bool>());

            // Changed account cannot inherit discovered access
            var changedAccountModels = FreshModels();
            var changedProviders = new List<JsonObject>
            {
                new() { ["id"] = "codex-cache", ["adapter"] = "codex", ["quotaPoolId"] = "codex", ["account"] = new JsonObject { ["key"] = "different-account" }, ["label"] = "codex" }
            };
            var changedAccount = ProviderModels.CreateProviderModels(changedAccountModels, new ProviderModels.ProviderModelsOptions
            {
                Providers = changedProviders,
                CacheFile = cacheFile,
                Discover = _ => Task.FromResult(new JsonObject { ["ok"] = false, ["reason"] = "Isolated sign-in failure" })
            });

            await changedAccount.RefreshAsync(true);
            Assert.DoesNotContain(changedAccountModels, m => m["cli"]?.ToString() == "codex-new");

            // Malformed success cannot revoke current access
            var catalogReply = new JsonObject
            {
                ["ok"] = true,
                ["models"] = new JsonArray { (JsonNode)new JsonObject { ["cli"] = "" } }
            };
            var validation = ProviderModels.CreateProviderModels(restartedModels, new ProviderModels.ProviderModelsOptions
            {
                Providers = fixtureProviders,
                Discover = _ => Task.FromResult(catalogReply)
            });
            await validation.RefreshAsync(true);
            var cur = restartedModels.Find(m => m["cli"]?.ToString() == "codex-current");
            Assert.NotNull(cur);
            Assert.True(cur["available"]?.GetValue<bool>());

            // Explicit empty catalog revokes every listed route
            catalogReply = new JsonObject
            {
                ["ok"] = true,
                ["ids"] = new JsonArray()
            };
            await validation.RefreshAsync(true);
            Assert.True(restartedModels.All(m => m["catalogExcluded"]?.GetValue<bool>() == true && m["available"]?.GetValue<bool>() == false));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Account_invalidation_survives_restart_and_blocks_access_until_fresh_discovery()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-models-inval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var cacheFile = Path.Combine(tempDir, "catalog.json");

        try
        {
            var provider = new JsonObject
            {
                ["id"] = "scope-fixture",
                ["adapter"] = "codex",
                ["quotaPoolId"] = "codex",
                ["account"] = new JsonObject { ["key"] = "account-b-fixture" }
            };
            var providers = new List<JsonObject> { provider };

            List<JsonObject> FreshModels() =>
            [
                new() { ["id"] = "exact-fixture", ["providerId"] = "scope-fixture", ["cli"] = "gpt-fixture", ["available"] = false, ["enabled"] = true, ["roles"] = new JsonObject { ["worker"] = true, ["reviewer"] = false } }
            ];

            // Seed cache
            var seed = ProviderModels.CreateProviderModels(FreshModels(), new ProviderModels.ProviderModelsOptions
            {
                Providers = providers,
                CacheFile = cacheFile,
                Discover = _ => Task.FromResult(new JsonObject { ["ok"] = true, ["ids"] = new JsonArray { (JsonNode)"gpt-fixture" } })
            });
            await seed.RefreshAsync(true);

            // Invalidate and refresh with error
            var models = FreshModels();
            var active = ProviderModels.CreateProviderModels(models, new ProviderModels.ProviderModelsOptions
            {
                Providers = providers,
                CacheFile = cacheFile,
                Discover = _ => Task.FromResult(new JsonObject { ["ok"] = false, ["reason"] = "Synthetic catalog outage" })
            });

            await active.InvalidateAsync(provider["id"]!.ToString());
            await active.RefreshAsync(true);
            Assert.False(models[0]["available"]?.GetValue<bool>());

            // Restart cannot revive historical access
            var restarted = FreshModels();
            var reply = new JsonObject { ["ok"] = false, ["reason"] = "Still unavailable" };
            var next = ProviderModels.CreateProviderModels(restarted, new ProviderModels.ProviderModelsOptions
            {
                Providers = providers,
                CacheFile = cacheFile,
                Discover = _ => Task.FromResult(reply)
            });

            await next.RefreshAsync(true);
            Assert.False(restarted[0]["available"]?.GetValue<bool>());

            // Fresh discovery succeeds
            reply = new JsonObject { ["ok"] = true, ["ids"] = new JsonArray { (JsonNode)"gpt-fixture" } };
            await next.RefreshAsync(true);
            Assert.True(restarted[0]["available"]?.GetValue<bool>());
            Assert.True(restarted[0]["availabilityVerified"]?.GetValue<bool>());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Stale_in_flight_discovery_rejected_after_account_switch_and_generation_change()
    {
        var provider = new JsonObject
        {
            ["id"] = "scope-fixture",
            ["adapter"] = "codex",
            ["quotaPoolId"] = "codex",
            ["account"] = new JsonObject { ["key"] = "account-a-fixture" }
        };

        var models = new List<JsonObject>
        {
            new() { ["id"] = "exact-fixture", ["providerId"] = "scope-fixture", ["cli"] = "gpt-fixture", ["available"] = false, ["enabled"] = true }
        };

        var started = new TaskCompletionSource<bool>();
        var tcs = new TaskCompletionSource<JsonObject>();
        int calls = 0;

        var discovery = ProviderModels.CreateProviderModels(models, new ProviderModels.ProviderModelsOptions
        {
            Providers = [provider],
            Discover = async p =>
            {
                calls++;
                if (calls == 1)
                {
                    started.SetResult(true);
                    return await tcs.Task;
                }
                return new JsonObject { ["ok"] = false, ["reason"] = "Fresh check failed" };
            }
        });

        var pending = discovery.RefreshAsync(true);
        await started.Task;
        await discovery.InvalidateAsync(provider["id"]!.ToString());

        var queued = discovery.RefreshAsync(true, provider["id"]!.ToString());
        tcs.SetResult(new JsonObject
        {
            ["ok"] = true,
            ["ids"] = new JsonArray { (JsonNode)"gpt-fixture", (JsonNode)"gpt-stale" }
        });

        await Task.WhenAll(pending, queued);
        Assert.False(models[0]["available"]?.GetValue<bool>());
        Assert.DoesNotContain(models, m => m["cli"]?.ToString() == "gpt-stale");
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Failed_durable_invalidation_rejects_and_keeps_access_blocked()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-models-rename-fail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var cacheFile = Path.Combine(tempDir, "catalog.json");
        // Create directory at cacheFile to make atomic replace fail
        Directory.CreateDirectory(cacheFile);

        try
        {
            var provider = new JsonObject
            {
                ["id"] = "fixture",
                ["adapter"] = "codex",
                ["quotaPoolId"] = "codex",
                ["account"] = new JsonObject { ["key"] = "fake-account" }
            };

            var models = new List<JsonObject>
            {
                new() { ["id"] = "exact", ["providerId"] = "fixture", ["cli"] = "gpt-exact", ["available"] = true, ["enabled"] = true }
            };

            var tcs = new TaskCompletionSource<JsonObject>();
            // The invalidation must come while the discovery runs, as in the JavaScript, where it starts at once.
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var discovery = ProviderModels.CreateProviderModels(models, new ProviderModels.ProviderModelsOptions
            {
                Providers = [provider],
                CacheFile = cacheFile,
                Discover = async _ => { started.TrySetResult(); return await tcs.Task; }
            });

            var pending = discovery.RefreshAsync(true);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => discovery.InvalidateAsync("fixture"));
            Assert.Contains("invalidation could not be persisted", ex.Message);
            Assert.False(models[0]["available"]?.GetValue<bool>());

            tcs.SetResult(new JsonObject
            {
                ["ok"] = true,
                ["ids"] = new JsonArray { (JsonNode)"gpt-exact", (JsonNode)"gpt-stale" }
            });
            await pending;

            Assert.False(models[0]["available"]?.GetValue<bool>());
            Assert.DoesNotContain(models, m => m["cli"]?.ToString() == "gpt-stale");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Not_installed_cli_marks_models_and_provider_as_not_installed()
    {
        var provider = new JsonObject
        {
            ["id"] = "grok-tool",
            ["adapter"] = "grok",
            ["quotaPoolId"] = "grok"
        };
        var models = new List<JsonObject>
        {
            new() { ["id"] = "grok-exact", ["providerId"] = "grok-tool", ["cli"] = "grok-exact", ["available"] = true, ["enabled"] = true }
        };

        var discovery = ProviderModels.CreateProviderModels(models, new ProviderModels.ProviderModelsOptions
        {
            Providers = [provider],
            FindExecutable = _ => false
        });

        await discovery.RefreshAsync(true);

        Assert.False(models[0]["available"]?.GetValue<bool>());
        Assert.True(models[0]["notInstalled"]?.GetValue<bool>());
        Assert.False(models[0]["availabilityVerified"]?.GetValue<bool>());
        Assert.Contains("not installed", models[0]["unavailableReason"]?.ToString() ?? "");

        var snap = discovery.Snapshot();
        Assert.Equal("not-installed", (snap["providers"] as JsonArray)?[0]?["status"]?.ToString());
    }

    [Fact]
    public async Task Disabled_provider_has_disabled_status()
    {
        var provider = new JsonObject
        {
            ["id"] = "disabled-prov",
            ["adapter"] = "codex",
            ["enabled"] = false
        };
        var models = new List<JsonObject>();

        var discovery = ProviderModels.CreateProviderModels(models, new ProviderModels.ProviderModelsOptions
        {
            Providers = [provider]
        });

        var snap = discovery.Snapshot();
        Assert.Equal("disabled", (snap["providers"] as JsonArray)?[0]?["status"]?.ToString());
    }

    [Fact]
    public async Task Switch_away_and_back_rejects_old_generation()
    {
        var provider = new JsonObject
        {
            ["id"] = "fixture",
            ["adapter"] = "codex",
            ["quotaPoolId"] = "fixture",
            ["account"] = new JsonObject { ["key"] = "fake-A" }
        };
        var models = new List<JsonObject>
        {
            new() { ["id"] = "exact", ["providerId"] = "fixture", ["cli"] = "gpt-exact", ["available"] = false }
        };

        var started = new TaskCompletionSource<bool>();
        var tcs = new TaskCompletionSource<bool>();
        int calls = 0;

        var api = ProviderModels.CreateProviderModels(models, new ProviderModels.ProviderModelsOptions
        {
            Providers = [provider],
            Discover = async p =>
            {
                calls++;
                if (calls == 1)
                {
                    started.SetResult(true);
                    await tcs.Task;
                    return new JsonObject { ["ok"] = true, ["ids"] = new JsonArray { (JsonNode)"gpt-exact", (JsonNode)"gpt-stale" } };
                }
                return new JsonObject { ["ok"] = false, ["reason"] = "Fresh authorization required" };
            }
        });

        var pending = api.RefreshAsync(true);
        await started.Task;
        provider["account"]!["key"] = "fake-B";
        await api.InvalidateAsync("fixture");
        api.ObserveScope("fixture");

        provider["account"]!["key"] = "fake-A";
        await api.InvalidateAsync("fixture");
        api.ObserveScope("fixture");

        var next = api.RefreshAsync(true);
        tcs.SetResult(true);

        await Task.WhenAll(pending, next);

        Assert.DoesNotContain(models, m => m["cli"]?.ToString() == "gpt-stale");
        Assert.Equal(2, calls);
    }
}
