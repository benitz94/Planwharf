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
}
