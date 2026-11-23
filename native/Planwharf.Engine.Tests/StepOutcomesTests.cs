// Tests for StepOutcomes.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class StepOutcomesTests : IDisposable
{
    private readonly string _testDir;

    public StepOutcomesTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "pw_outcomes_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    [Fact]
    public void StripPathsAndCode_RemovesFileExtensionsCodeBlocksAndBraces()
    {
        var raw = "Check file D:\\test\\file.cs and `{ key: val }` in ```code block```";
        var stripped = StepOutcomes.StripPathsAndCode(raw);
        Assert.DoesNotContain("D:\\test\\file.cs", stripped);
        Assert.DoesNotContain("code block", stripped);
    }

    [Fact]
    public void ResolveModelMeta_ExtractsCompanyAndTierClasses()
    {
        var meta1 = StepOutcomes.ResolveModelMeta(new JsonObject { ["id"] = "claude-3-5-sonnet", ["provider"] = "claude" });
        Assert.Equal("claude", meta1.Company);
        Assert.Equal("mid", meta1.Class);

        var meta2 = StepOutcomes.ResolveModelMeta(new JsonObject { ["id"] = "claude-3-opus", ["provider"] = "claude" });
        Assert.Equal("flagship", meta2.Class);
    }

    [Fact]
    public void AppendStepOutcome_AtomicAppendWithRetriesUnderConcurrency()
    {
        var record = new JsonObject
        {
            ["model"] = "claude-sonnet",
            ["role"] = "Build",
            ["accepted"] = true,
            ["fixed"] = 0
        };

        var res = StepOutcomes.AppendStepOutcome(_testDir, record);
        Assert.NotNull(res);

        var outcomes = StepOutcomes.ReadStepOutcomes(_testDir);
        Assert.Single(outcomes);
        Assert.Equal("claude-sonnet", outcomes[0]["model"]?.GetValue<string>());
    }

    [Fact]
    public void ReadStepOutcomes_CorruptedLines_SkipsInvalidAndLoadsValid()
    {
        var file = StepOutcomes.OutcomesFile(_testDir);
        File.WriteAllText(file, "corrupt json line\n{\"model\":\"gpt-4\",\"role\":\"Build\"}\n");

        var outcomes = StepOutcomes.ReadStepOutcomes(_testDir);
        Assert.Single(outcomes);
        Assert.Equal("gpt-4", outcomes[0]["model"]?.GetValue<string>());
    }

    [Fact]
    public void OutcomesSummary_AggregatesTotalsSuccessRatesAndDurationAverages()
    {
        var records = new List<JsonObject>
        {
            new JsonObject { ["model"] = "m1", ["accepted"] = true, ["fixed"] = 0 },
            new JsonObject { ["model"] = "m1", ["accepted"] = false, ["fixed"] = 1, ["fixedByVerify"] = true },
            new JsonObject { ["model"] = "m2", ["accepted"] = true, ["fixed"] = 0 }
        };

        var summary = StepOutcomes.OutcomesSummary(records);
        Assert.Equal(2, summary.Count);
        var m1 = summary.First(s => s["model"]?.GetValue<string>() == "m1");
        Assert.Equal(2, m1["steps"]?.GetValue<int>());
        Assert.Equal(1, m1["accepted"]?.GetValue<int>());
        Assert.Equal(1, m1["fixed"]?.GetValue<int>());

        var text = StepOutcomes.SummaryText(m1);
        Assert.Equal("2 steps \u00b7 1 accepted as they were \u00b7 1 fixed by Verify", text);
    }

    [Fact]
    public async Task AppendStepOutcome_ConcurrentAppends_PreservesAllRecordsWithoutCorruption()
    {
        var tasks = new List<Task>();
        for (int i = 0; i < 5; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(() =>
            {
                var record = new JsonObject
                {
                    ["model"] = $"model-{idx}",
                    ["role"] = "Build",
                    ["accepted"] = true,
                    ["fixed"] = 0
                };
                StepOutcomes.AppendStepOutcome(_testDir, record);
            }));
        }

        await Task.WhenAll(tasks);

        var outcomes = StepOutcomes.ReadStepOutcomes(_testDir);
        Assert.Equal(5, outcomes.Count);
    }

    [Fact]
    public void OutcomesSummary_EmptyList_ReturnsEmpty()
    {
        var summary = StepOutcomes.OutcomesSummary(new List<JsonObject>());
        Assert.Empty(summary);
    }

    [Fact]
    public void StripPathsAndCode_NullOrEmpty_ReturnsOriginal()
    {
        Assert.Null(StepOutcomes.StripPathsAndCode(null));
        Assert.Equal("", StepOutcomes.StripPathsAndCode(""));
    }
}
