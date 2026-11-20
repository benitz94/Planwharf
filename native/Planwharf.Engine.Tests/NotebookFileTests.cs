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
            // Node compares the spellings as strings. File.Exists("Plan.json") is true on a
            // case-insensitive volume (default macOS) because plan.json is already there.
            // The directory entry keeps the spelling that was created.
            var names = Directory.EnumerateFiles(root).Select(Path.GetFileName).ToList();
            Assert.Contains("plan.json", names);
            Assert.DoesNotContain("Plan.json", names);
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

        var error = Record.Exception(() => NotebookFile.EnsureDefaultNotebook(plan, root));
        // Node's COPYFILE_EXCL reports EPERM on Windows and EEXIST on Linux and macOS
        // (open with O_CREAT|O_EXCL on a directory). Only EEXIST is ignored.
        if (OperatingSystem.IsWindows()) Assert.NotNull(error);
        else Assert.Null(error);
        Assert.True(Directory.Exists(plan));
        Assert.Empty(Directory.EnumerateFileSystemEntries(plan));
        Assert.Equal("{}", File.ReadAllText(starter));
    }

    [Fact]
    public void Wrong_case_missing_starter_and_a_file_named_fixtures_do_not_create_a_notebook()
    {
        var root = Path.Combine(home, "edges");
        var starter = Path.Combine(root, "fixtures", "starter-notebook.json");
        Directory.CreateDirectory(Path.GetDirectoryName(starter)!);
        File.WriteAllText(starter, "starter");
        var plan = Path.Combine(root, "plan.json");

        // A different spelling is a different path in Node, even on a case-insensitive volume.
        NotebookFile.EnsureDefaultNotebook(Path.Combine(root, "Plan.json"), root);
        Assert.Empty(Directory.EnumerateFiles(root));

        File.Delete(starter);
        var missing = Record.Exception(() => NotebookFile.EnsureDefaultNotebook(plan, root));
        Assert.NotNull(missing);
        Assert.False(File.Exists(plan));

        File.WriteAllText(starter, "RECOVERED");
        NotebookFile.EnsureDefaultNotebook(plan, root);
        Assert.Equal("RECOVERED", File.ReadAllText(plan));

        var blocked = Path.Combine(home, "blocked");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, "fixtures"), "not-a-directory");
        var blockedPlan = Path.Combine(blocked, "plan.json");
        Assert.NotNull(Record.Exception(() => NotebookFile.EnsureDefaultNotebook(blockedPlan, blocked)));
        Assert.False(File.Exists(blockedPlan));
        Assert.Equal("not-a-directory", File.ReadAllText(Path.Combine(blocked, "fixtures")));
    }

    [Fact]
    public void A_directory_starter_does_not_become_the_notebook()
    {
        var root = Path.Combine(home, "starter-dir");
        var starter = Path.Combine(root, "fixtures", "starter-notebook.json");
        Directory.CreateDirectory(starter);
        var plan = Path.Combine(root, "plan.json");
        Assert.NotNull(Record.Exception(() => NotebookFile.EnsureDefaultNotebook(plan, root)));
        Assert.False(File.Exists(plan));
        Assert.True(Directory.Exists(starter));
    }

    [Fact]
    public void Read_only_and_open_notebooks_keep_their_bytes()
    {
        var root = Path.Combine(home, "keep");
        var starter = Path.Combine(root, "fixtures", "starter-notebook.json");
        Directory.CreateDirectory(Path.GetDirectoryName(starter)!);
        File.WriteAllText(starter, "STARTER");
        var plan = Path.Combine(root, "plan.json");
        File.WriteAllText(plan, "KEEP");
        var info = new FileInfo(plan) { IsReadOnly = true };
        try
        {
            NotebookFile.EnsureDefaultNotebook(plan, root);
            Assert.Equal("KEEP", File.ReadAllText(plan));
            using var held = new FileStream(plan, FileMode.Open, FileAccess.Read, FileShare.Read);
            NotebookFile.EnsureDefaultNotebook(plan, root);
        }
        finally
        {
            info.IsReadOnly = false;
        }
        Assert.Equal("KEEP", File.ReadAllText(plan));
    }

    [Fact]
    public void Trailing_slash_relative_root_and_dot_segment_still_copy()
    {
        var root = Path.Combine(home, "spell2");
        var starter = Path.Combine(root, "fixtures", "starter-notebook.json");
        Directory.CreateDirectory(Path.GetDirectoryName(starter)!);
        File.WriteAllText(starter, "S");
        var plan = Path.Combine(root, "plan.json");

        // Windows copyFile strips a trailing separator. POSIX open of plan.json/ does not create the file.
        if (OperatingSystem.IsWindows())
        {
            NotebookFile.EnsureDefaultNotebook(plan + Path.DirectorySeparatorChar, root);
            Assert.Equal("S", File.ReadAllText(plan));
            File.Delete(plan);
            NotebookFile.EnsureDefaultNotebook(plan.Replace('\\', '/') + "/", root);
            Assert.Equal("S", File.ReadAllText(plan));
            File.Delete(plan);
        }
        else
        {
            Assert.NotNull(Record.Exception(() => NotebookFile.EnsureDefaultNotebook(plan + Path.DirectorySeparatorChar, root)));
            Assert.False(File.Exists(plan));
        }

        NotebookFile.EnsureDefaultNotebook(plan, root + Path.DirectorySeparatorChar);
        Assert.Equal("S", File.ReadAllText(plan));
        File.Delete(plan);

        NotebookFile.EnsureDefaultNotebook(Path.Combine(root, ".", "plan.json"), root);
        Assert.Equal("S", File.ReadAllText(plan));
        File.Delete(plan);

        var relativeRoot = Path.GetRelativePath(Directory.GetCurrentDirectory(), root);
        NotebookFile.EnsureDefaultNotebook(plan, relativeRoot);
        Assert.Equal("S", File.ReadAllText(plan));
    }

    [Fact]
    public void Directory_casing_and_empty_or_null_paths_do_not_create_a_notebook()
    {
        var root = Path.Combine(home, "CaseDir");
        Directory.CreateDirectory(Path.Combine(root, "fixtures"));
        File.WriteAllText(Path.Combine(root, "fixtures", "starter-notebook.json"), "C");
        Assert.Throws<ArgumentNullException>(() => NotebookFile.EnsureDefaultNotebook(null!, root));
        Assert.Throws<ArgumentNullException>(() => NotebookFile.EnsureDefaultNotebook(Path.Combine(root, "plan.json"), null!));
        NotebookFile.EnsureDefaultNotebook("", root);
        Assert.False(File.Exists(Path.Combine(root, "plan.json")));

        var flipped = Path.Combine(Path.GetDirectoryName(root)!, "caseDir", "plan.json");
        if (!flipped.Equals(Path.Combine(root, "plan.json"), StringComparison.Ordinal))
        {
            NotebookFile.EnsureDefaultNotebook(flipped, root);
            Assert.False(File.Exists(Path.Combine(root, "plan.json")));
        }
    }

    [Fact]
    public void A_symlink_and_a_filled_directory_are_left_in_place()
    {
        var root = Path.Combine(home, "links");
        var starter = Path.Combine(root, "fixtures", "starter-notebook.json");
        Directory.CreateDirectory(Path.GetDirectoryName(starter)!);
        File.WriteAllText(starter, "STARTER");
        var target = Path.Combine(root, "target.json");
        File.WriteAllText(target, "TARGET");
        var plan = Path.Combine(root, "plan.json");
        if (Links.TryFile(plan, target))
        {
            NotebookFile.EnsureDefaultNotebook(plan, root);
            Assert.Equal("TARGET", File.ReadAllText(target));
            Assert.Equal("TARGET", File.ReadAllText(plan));
            Assert.True((File.GetAttributes(plan) & FileAttributes.ReparsePoint) != 0);
            File.Delete(plan);
        }

        var linkedRoot = Path.Combine(home, "dirlink");
        Directory.CreateDirectory(Path.Combine(linkedRoot, "fixtures"));
        File.WriteAllText(Path.Combine(linkedRoot, "fixtures", "starter-notebook.json"), "{}");
        var inside = Path.Combine(linkedRoot, "inside");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "keep.txt"), "child");
        var link = Path.Combine(linkedRoot, "plan.json");
        Links.Folder(link, inside);
        var linkError = Record.Exception(() => NotebookFile.EnsureDefaultNotebook(link, linkedRoot));
        if (OperatingSystem.IsWindows()) Assert.NotNull(linkError);
        else Assert.Null(linkError);
        Assert.Equal("child", File.ReadAllText(Path.Combine(inside, "keep.txt")));
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        Directory.Delete(link);

        var occupied = Path.Combine(home, "filled");
        Directory.CreateDirectory(Path.Combine(occupied, "fixtures"));
        File.WriteAllText(Path.Combine(occupied, "fixtures", "starter-notebook.json"), "{}");
        var dir = Path.Combine(occupied, "plan.json");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "child");
        var error = Record.Exception(() => NotebookFile.EnsureDefaultNotebook(dir, occupied));
        if (OperatingSystem.IsWindows()) Assert.NotNull(error);
        else Assert.Null(error);
        Assert.Equal("child", File.ReadAllText(Path.Combine(dir, "keep.txt")));
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void Many_callers_copy_once_and_leave_an_existing_notebook()
    {
        var root = Path.Combine(home, "crowd");
        var starterPath = Path.Combine(root, "fixtures", "starter-notebook.json");
        Directory.CreateDirectory(Path.GetDirectoryName(starterPath)!);
        var starter = "starter-bytes"u8.ToArray();
        File.WriteAllBytes(starterPath, starter);
        var plan = Path.Combine(root, "plan.json");

        foreach (var error in RunTogether(8, () => NotebookFile.EnsureDefaultNotebook(plan, root))) Assert.Null(error);
        Assert.Equal(starter, File.ReadAllBytes(plan));

        var personal = "personal"u8.ToArray();
        File.WriteAllBytes(plan, personal);
        foreach (var error in RunTogether(8, () => NotebookFile.EnsureDefaultNotebook(plan, root))) Assert.Null(error);
        Assert.Equal(personal, File.ReadAllBytes(plan));
    }

    static Exception?[] RunTogether(int count, Action action)
    {
        var errors = new Exception?[count];
        var threads = new Thread[count];
        using var start = new ManualResetEventSlim(false);
        for (var n = 0; n < count; n++)
        {
            var index = n;
            threads[n] = new Thread(() =>
            {
                start.Wait();
                try { action(); }
                catch (Exception error) { errors[index] = error; }
            });
            threads[n].Start();
        }
        start.Set();
        foreach (var thread in threads) thread.Join();
        return errors;
    }
}
