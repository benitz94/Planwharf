using Planwharf.Native.Core;
using Xunit;

namespace Planwharf.Native.Tests;

public class CommitMessagesTests
{
    static WorkView Work(string title, params StepResult[] results) =>
        new(title, "COMPLETE", 2, 2, [new WorkStep("s1", "Build", "Gemini 3.8 Flash (High)", "done", "g"), new WorkStep("s2", "Verify", "Grok 4.7 Business", "done", "k")], results.Select(r => r.Summary).ToList())
        { StepResults = results };

    const string Goal = "On branch `port/03-fake-ai-programs`, do job 03 from `native/porting/jobs/03-fake-ai-programs.md` following `native/porting/README.md`: one worker first builds the program.";

    // Job 03 live: the builder made its commit, the verifier's fixes were left to commit.
    [Fact]
    public void After_a_step_that_committed_the_first_line_says_what_the_later_step_did()
    {
        var message = CommitMessages.Default(Work(Goal,
            new StepResult("Build", "Gemini 3.8 Flash (High)", "Implemented Planwharf.FakeCli and automated tests, and committed changes."),
            new StepResult("Verify", "Grok 4.7 Business", "Verified job 03 against the job sheet. One failure was real: a prompt needle matched the newline.")));
        var lines = message.Split('\n');
        Assert.Equal("Verify: Verified job 03 against the job sheet", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal(Goal, lines[2]);
        Assert.Equal("Build: Gemini 3.8 Flash (High) · Verify: Grok 4.7 Business", lines[3]);
    }

    [Fact]
    public void Without_an_earlier_commit_the_first_line_is_the_goal_cut_between_words_at_72_characters()
    {
        var subject = CommitMessages.Default(Work(Goal, new StepResult("Build", "Gemini 3.8 Flash (High)", "Built the program."))).Split('\n')[0];
        Assert.True(subject.Length <= 72, subject);
        Assert.StartsWith("On branch `port/03-fake-ai-programs`, do job 03", subject);
        Assert.False(subject.EndsWith(' '));
        Assert.Equal("Add a page", CommitMessages.Default(Work("Add a page")).Split('\n')[0]);
        Assert.Equal("Work by the crew", CommitMessages.Default(Work("")).Split('\n')[0]);
    }
}
