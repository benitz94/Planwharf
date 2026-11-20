// Port of the ensureDefaultNotebook checks at the end of engine/task-completion.test.cjs.
// The HTTP task loop in that file is not this module.
//
// Deferred cases (job 24 sheet; not implemented):
// - attempts.js, createAttemptEngine, and every sentinel test that needs the real store
// - createServer, createOrchestrator, spawnWorker, runner.cjs, and the HTTP loops in
//   task-completion.test.cjs, grill-chat.test.cjs, project-export.test.cjs,
//   project-teams.test.cjs, start-over.test.cjs (archiveProject), and
//   worker-containment.test.cjs (launch artifacts and provider cwd)
// - Live npm test, live node --test, and live taskkill
// - Byte-identical randomUUID values across JavaScript and C#
// - gitEnvironment and within as parity JSON
// - The browser root / window / AMD wrappers, models.json, and provider-catalog
// - A second process appending to invocations.jsonl at the same time
// - Reading the checkout's plan.json or .git
//
// setPlace dropping home on schema 2 lives in plan.js, not engine/notebook-file.cjs.
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public sealed class NotebookFileTests : IDisposable
{
    readonly string home = Directory.CreateTempSubdirectory("pw-notebook-file- café").FullName;

    public void Dispose()
    {
        if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
    }

    [Fact]
    public void Default_notebook_never_overwritten()
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        var fullHome = Path.GetFullPath(home);
        Assert.StartsWith(temp, fullHome, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        Assert.StartsWith("pw-notebook-file-", Path.GetFileName(fullHome.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

        var root = Path.Combine(home, "default");
        var starter = Path.Combine(root, "fixtures", "starter-notebook.json");
        Directory.CreateDirectory(Path.GetDirectoryName(starter)!);
        File.WriteAllText(starter, "{}");
        var plan = Path.Combine(root, "plan.json");

        NotebookFile.EnsureDefaultNotebook(plan, root);
        Assert.Equal("{}", File.ReadAllText(plan));
        Assert.Equal("{}", File.ReadAllText(starter));

        File.WriteAllText(plan, "personal notebook bytes");
        NotebookFile.EnsureDefaultNotebook(plan, root);
        Assert.Equal("personal notebook bytes", File.ReadAllText(plan));

        File.Delete(starter);
        var missingStarter = Record.Exception(() => NotebookFile.EnsureDefaultNotebook(plan, root));
        Assert.NotNull(missingStarter);
        Assert.Equal("personal notebook bytes", File.ReadAllText(plan));

        var explicitFile = Path.Combine(root, "missing.json");
        NotebookFile.EnsureDefaultNotebook(explicitFile, root);
        Assert.False(File.Exists(explicitFile));
    }

    [Fact]
    public void Only_the_resolved_default_plan_is_copied_and_directories_are_not_created()
    {
        var root = Path.Combine(home, "resolved");
        var starter = Path.Combine(root, "fixtures", "starter-notebook.json");
        Directory.CreateDirectory(Path.GetDirectoryName(starter)!);
        var bytes = new byte[] { (byte)'{', (byte)'}', 0xFF };
        File.WriteAllBytes(starter, bytes);
        var plan = Path.Combine(root, "plan.json");

        var viaParent = Path.Combine(root, "fixtures", "..", "plan.json");
        NotebookFile.EnsureDefaultNotebook(viaParent, root);
        Assert.Equal(bytes, File.ReadAllBytes(plan));

        var nested = Path.Combine(root, "nope", "plan.json");
        NotebookFile.EnsureDefaultNotebook(nested, root);
        Assert.False(Directory.Exists(Path.Combine(root, "nope")));
        Assert.False(File.Exists(nested));

        var otherCase = Path.Combine(root, "Plan.json");
        if (!otherCase.Equals(plan, StringComparison.Ordinal))
        {
            NotebookFile.EnsureDefaultNotebook(otherCase, root);
            Assert.Equal(bytes, File.ReadAllBytes(plan));
            if (!OperatingSystem.IsWindows()) Assert.False(File.Exists(otherCase));
        }

        var absent = Path.Combine(home, "absent-root");
        var thrown = Record.Exception(() => NotebookFile.EnsureDefaultNotebook(Path.Combine(absent, "plan.json"), absent));
        Assert.NotNull(thrown);
        Assert.False(Directory.Exists(absent));

        var bare = Path.Combine(home, "bare");
        Directory.CreateDirectory(bare);
        var barePlan = Path.Combine(bare, "plan.json");
        Assert.ThrowsAny<IOException>(() => NotebookFile.EnsureDefaultNotebook(barePlan, bare));
        Assert.False(File.Exists(barePlan));
        Assert.False(Directory.Exists(Path.Combine(bare, "fixtures")));
    }

    [Fact]
    public void Relative_slash_and_drive_spellings_match_the_default_notebook()
    {
        var root = Path.Combine(home, "spelling");
        var starter = Path.Combine(root, "fixtures", "starter-notebook.json");
        Directory.CreateDirectory(Path.GetDirectoryName(starter)!);
        File.WriteAllText(starter, "{}");
        var plan = Path.Combine(root, "plan.json");

        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), plan);
        NotebookFile.EnsureDefaultNotebook(relative, root);
        Assert.Equal("{}", File.ReadAllText(plan));
        File.Delete(plan);

        if (OperatingSystem.IsWindows())
        {
            var slashed = plan.Replace('\\', '/');
            NotebookFile.EnsureDefaultNotebook(slashed, root);
            Assert.Equal("{}", File.ReadAllText(plan));
            File.Delete(plan);

            var flipped = char.IsLower(plan[0]) ? char.ToUpperInvariant(plan[0]) + plan[1..] : char.ToLowerInvariant(plan[0]) + plan[1..];
            NotebookFile.EnsureDefaultNotebook(flipped, root);
            Assert.Equal("{}", File.ReadAllText(plan));
        }
    }

    [Fact]
    public void Two_callers_in_one_process_leave_the_starter_bytes()
    {
        var root = Path.Combine(home, "race");
        var starterPath = Path.Combine(root, "fixtures", "starter-notebook.json");
        Directory.CreateDirectory(Path.GetDirectoryName(starterPath)!);
        var starter = "personal starter"u8.ToArray();
        File.WriteAllBytes(starterPath, starter);
        var plan = Path.Combine(root, "plan.json");

        var errors = new Exception?[2];
        var threads = new Thread[2];
        using var start = new ManualResetEventSlim(false);
        for (var n = 0; n < 2; n++)
        {
            var index = n;
            threads[n] = new Thread(() =>
            {
                start.Wait();
                try { NotebookFile.EnsureDefaultNotebook(plan, root); }
                catch (Exception error) { errors[index] = error; }
            });
            threads[n].Start();
        }
        start.Set();
        foreach (var thread in threads) thread.Join();

        Assert.Null(errors[0]);
        Assert.Null(errors[1]);
        Assert.Equal(starter, File.ReadAllBytes(plan));
    }

    [Fact]
    public void A_directory_at_the_default_path_is_not_replaced()
    {
        var root = Path.Combine(home, "occupied");
        var starter = Path.Combine(root, "fixtures", "starter-notebook.json");
        Directory.CreateDirectory(Path.GetDirectoryName(starter)!);
        File.WriteAllText(starter, "{}");
        var plan = Path.Combine(root, "plan.json");
        Directory.CreateDirectory(plan);

        Assert.NotNull(Record.Exception(() => NotebookFile.EnsureDefaultNotebook(plan, root)));
        Assert.True(Directory.Exists(plan));
        Assert.Equal("{}", File.ReadAllText(starter));
    }
}
