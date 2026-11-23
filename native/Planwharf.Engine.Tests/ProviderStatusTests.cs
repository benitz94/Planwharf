// Tests for ProviderStatus.cs.
using Xunit;

namespace Planwharf.Engine.Tests;

public class ProviderStatusTests
{
    [Fact]
    public async Task GetProviderStatus_AllOperational_ReportsUpTrue()
    {
        Task<(bool, string)> MockFetch(string url)
        {
            return Task.FromResult((true, "All systems operational. No incidents."));
        }

        var statuses = await ProviderStatus.GetProviderStatusAsync(MockFetch);
        Assert.Equal(4, statuses.Count);
        Assert.All(statuses, s => Assert.True(s.Up));
        Assert.All(statuses, s => Assert.Equal("Operational.", s.Note));
    }

    [Fact]
    public async Task GetProviderStatus_DegradedPerformance_ReportsUpFalseWithIncidentOpen()
    {
        Task<(bool, string)> MockFetch(string url)
        {
            if (url.Contains("openai"))
                return Task.FromResult((true, "We are currently investigating degraded performance."));
            return Task.FromResult((true, "All systems operational."));
        }

        var statuses = await ProviderStatus.GetProviderStatusAsync(MockFetch);
        var openai = statuses.First(s => s.Id == "openai");
        Assert.False(openai.Up);
        Assert.Equal("An incident is open.", openai.Note);
    }

    [Fact]
    public async Task GetProviderStatus_NetworkTimeout_ReportsUpFalseWithUnreachableNote()
    {
        Task<(bool, string)> MockFetch(string url)
        {
            throw new HttpRequestException("Timeout");
        }

        var statuses = await ProviderStatus.GetProviderStatusAsync(MockFetch);
        Assert.All(statuses, s => Assert.False(s.Up));
        Assert.All(statuses, s => Assert.Equal("Could not reach the status page.", s.Note));
    }
}
