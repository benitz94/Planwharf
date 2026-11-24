// Tests for ClaudeUsage.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ClaudeUsageTests
{
    [Fact]
    public void ParseClaudeUsage_FiveHourAndWeeklyWindows_ComputesTightestRemainingQuota()
    {
        var body = new JsonObject
        {
            ["five_hour"] = new JsonObject { ["utilization"] = 40.0, ["resets_at"] = "2026-11-23T18:00:00Z" },
            ["seven_day"] = new JsonObject { ["utilization"] = 75.0, ["resets_at"] = "2026-11-30T00:00:00Z" }
        };

        var usage = ClaudeUsage.ParseClaudeUsage(body);
        Assert.True(usage["readable"]?.GetValue<bool>());
        Assert.Equal(25.0, usage["leftPct"]?.GetValue<double>()); // 100 - 75 = 25
        Assert.Equal(75.0, usage["pct"]?.GetValue<double>());
    }

    [Fact]
    public void ParseClaudeUsage_ExhaustedWindow_SetsExhaustedTrue()
    {
        var body = new JsonObject
        {
            ["five_hour"] = new JsonObject { ["utilization"] = 100.0 }
        };

        var usage = ClaudeUsage.ParseClaudeUsage(body);
        Assert.True(usage["exhausted"]?.GetValue<bool>());
        Assert.Equal(0.0, usage["leftPct"]?.GetValue<double>());
    }

    [Fact]
    public async Task ReadClaudeAuthAsync_ApiGatewayOverridePresent_ReturnsNull()
    {
        var env = new Dictionary<string, string>
        {
            ["ANTHROPIC_API_KEY"] = "sk-ant-12345"
        };
        var auth = await ClaudeUsage.ReadClaudeAuthAsync(env);
        Assert.Null(auth);
    }

    [Fact]
    public async Task CreateClaudeUsageReader_ConcurrentCalls_SharesPendingRequestAndCachesResult()
    {
        int fetchCount = 0;
        Task<ClaudeAuthInfo?> ReadAuth() => Task.FromResult<ClaudeAuthInfo?>(new ClaudeAuthInfo("test-token", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()));

        Task<HttpResponseMessage> FetchImpl(string url, HttpRequestMessage req)
        {
            fetchCount++;
            var json = "{\"five_hour\":{\"utilization\":20.0}}";
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
            return Task.FromResult(resp);
        }

        var reader = ClaudeUsage.CreateClaudeUsageReader(ReadAuth, FetchImpl);
        var t1 = reader();
        var t2 = reader();
        var results = await Task.WhenAll(t1, t2);

        Assert.Equal(1, fetchCount);
        Assert.Equal(80.0, results[0]["leftPct"]?.GetValue<double>());
        Assert.Equal(80.0, results[1]["leftPct"]?.GetValue<double>());
    }

    [Fact]
    public async Task ReadClaudeAuthAsync_CredentialsFile_ExtractsAccessTokenAndExpiry()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "pw_claude_auth_" + Guid.NewGuid().ToString("N"));
        var claudeDir = Path.Combine(tempHome, ".claude");
        Directory.CreateDirectory(claudeDir);
        try
        {
            var creds = new JsonObject
            {
                ["claudeAiOauth"] = new JsonObject
                {
                    ["accessToken"] = "sk-ant-oauth-token-12345",
                    ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds()
                }
            };
            File.WriteAllText(Path.Combine(claudeDir, ".credentials.json"), creds.ToJsonString());

            var auth = await ClaudeUsage.ReadClaudeAuthAsync(home: tempHome);
            Assert.NotNull(auth);
            Assert.Equal("sk-ant-oauth-token-12345", auth.Token);
            Assert.NotNull(auth.ExpiresAt);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    [Fact]
    public async Task ReadClaudeAuthAsync_ExpiredOrOversized_ReturnsNull()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), "pw_claude_auth_exp_" + Guid.NewGuid().ToString("N"));
        var claudeDir = Path.Combine(tempHome, ".claude");
        Directory.CreateDirectory(claudeDir);
        try
        {
            // Expired credentials
            var creds = new JsonObject
            {
                ["claudeAiOauth"] = new JsonObject
                {
                    ["accessToken"] = "expired-token",
                    ["expiresAt"] = 1000 // In the past
                }
            };
            var credFile = Path.Combine(claudeDir, ".credentials.json");
            File.WriteAllText(credFile, creds.ToJsonString());

            var auth = await ClaudeUsage.ReadClaudeAuthAsync(home: tempHome);
            Assert.Null(auth);

            // Oversized credentials (> 65536 bytes)
            var bigData = "{\"claudeAiOauth\":{\"accessToken\":\"token\",\"pad\":\"" + new string('x', 70000) + "\"}}";
            File.WriteAllText(credFile, bigData);
            var bigAuth = await ClaudeUsage.ReadClaudeAuthAsync(home: tempHome);
            Assert.Null(bigAuth);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    [Fact]
    public async Task CreateClaudeUsageReader_RateLimited429_AppliesRetryAfterHeader()
    {
        Task<ClaudeAuthInfo?> ReadAuth() => Task.FromResult<ClaudeAuthInfo?>(new ClaudeAuthInfo("test-token", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()));

        Task<HttpResponseMessage> FetchImpl(string url, HttpRequestMessage req)
        {
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            resp.Headers.Add("retry-after", "60");
            return Task.FromResult(resp);
        }

        var reader = ClaudeUsage.CreateClaudeUsageReader(ReadAuth, FetchImpl);
        var res = await reader();

        Assert.False(res["readable"]?.GetValue<bool>());
        Assert.Contains("rate limited", res["reason"]?.GetValue<string>());
    }

    [Fact]
    public async Task CreateClaudeUsageReader_Unauthorized401_ReturnsUnreadLoginExpired()
    {
        Task<ClaudeAuthInfo?> ReadAuth() => Task.FromResult<ClaudeAuthInfo?>(new ClaudeAuthInfo("test-token", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()));

        Task<HttpResponseMessage> FetchImpl(string url, HttpRequestMessage req)
        {
            var resp = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
            return Task.FromResult(resp);
        }

        var reader = ClaudeUsage.CreateClaudeUsageReader(ReadAuth, FetchImpl);
        var res = await reader();

        Assert.False(res["readable"]?.GetValue<bool>());
        Assert.Contains("refused the saved login", res["reason"]?.GetValue<string>());
        // A refused token at the usage service does not keep the Claude models out.
        Assert.Equal("unknown", res["planStatus"]?.GetValue<string>());
    }

    [Fact]
    public void ParseClaudeUsage_InvalidOrNoWindows_ReturnsUnread()
    {
        var empty = ClaudeUsage.ParseClaudeUsage(new JsonObject());
        Assert.False(empty["readable"]?.GetValue<bool>());
        Assert.Contains("no numeric subscription quota windows", empty["reason"]?.GetValue<string>());

        var invalidUtil = new JsonObject
        {
            ["five_hour"] = new JsonObject { ["utilization"] = 150.0 }
        };
        var invalid = ClaudeUsage.ParseClaudeUsage(invalidUtil);
        Assert.False(invalid["readable"]?.GetValue<bool>());
        Assert.Contains("invalid quota window", invalid["reason"]?.GetValue<string>());
    }
}
