// Port of engine/provider-status.cjs.
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public record StatusPageRecord(string Id, string Name, string Url, bool Up, string Note);

public static class ProviderStatus
{
    public static readonly IReadOnlyList<(string Id, string Name, string Url)> Pages =
    [
        ("openai", "OpenAI", "https://status.openai.com"),
        ("anthropic", "Anthropic", "https://status.claude.com"),
        ("xai", "xAI", "https://status.x.ai"),
        ("google", "Google", "https://status.cloud.google.com")
    ];

    private static readonly Regex IncidentRegex = new(@"major outage|partial outage|investigating|degraded performance", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
    private static readonly Regex OperationalRegex = new(@"no incidents|all systems operational|operational", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);

    public static async Task<StatusPageRecord> ReadStatusAsync(
        string id, string name, string url,
        Func<string, Task<(bool Ok, string Body)>> fetchImpl)
    {
        try
        {
            var (ok, body) = await fetchImpl(url);
            if (!ok)
                return new StatusPageRecord(id, name, url, false, "Status page did not answer.");

            var text = body.ToLowerInvariant();
            bool incident = IncidentRegex.IsMatch(text) && !OperationalRegex.IsMatch(text);
            return new StatusPageRecord(id, name, url, !incident, incident ? "An incident is open." : "Operational.");
        }
        catch
        {
            return new StatusPageRecord(id, name, url, false, "Could not reach the status page.");
        }
    }

    public static async Task<IReadOnlyList<StatusPageRecord>> GetProviderStatusAsync(
        Func<string, Task<(bool Ok, string Body)>>? fetchImpl = null)
    {
        if (fetchImpl == null)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            fetchImpl = async u =>
            {
                using var res = await client.GetAsync(u);
                var content = await res.Content.ReadAsStringAsync();
                return (res.IsSuccessStatusCode, content);
            };
        }

        var tasks = Pages.Select(p => ReadStatusAsync(p.Id, p.Name, p.Url, fetchImpl));
        return await Task.WhenAll(tasks);
    }
}
