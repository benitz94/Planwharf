// Deferred cases (the sheet says job 24 does not port these; they are not implemented here):
// - attempts.js, createAttemptEngine, and every sentinel test that needs the real store
// - createServer, createOrchestrator, spawnWorker, runner.cjs, and the HTTP loops in
//   task-completion.test.cjs, grill-chat.test.cjs, project-export.test.cjs, project-teams.test.cjs,
//   start-over.test.cjs (archiveProject), and worker-containment.test.cjs (launch artifacts and provider cwd)
// - Live `npm test`, live `node --test`, and live `taskkill` (the process hook covers the decision)
// - Byte-identical randomUUID values across JavaScript and C#
// - gitEnvironment and within as parity JSON
// - The browser root / window / AMD wrappers, models.json, and provider-catalog
// - A second process appending to invocations.jsonl at the same time
// - Reading the checkout's plan.json or .git
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

// Port of the project-checks cases in engine/autonomy-crew.test.cjs, plus the sheet's folder rules.
// Live npm, node --test, and taskkill are deferred. runChecks tests pass the process hook.
public sealed class ProjectChecksTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("pw checks é テスト ").FullName;

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    string Write(string relative, string text = "")
    {
        var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    [Fact]
    public void Test_files_match_test_directories_and_test_or_spec_suffixes_at_any_case()
    {
        var expected = new[]
        {
            Write("test/a.js"),
            Write("tests/b.cjs"),
            Write("__tests__/c.mjs"),
            Write("src/foo.test.js"),
            Write("src/foo.spec.cjs"),
            Write("src/foo.spec.mjs"),
            Write("src/foo.test.mjs"),
            Write("Mixed/Tests/A.CJS"),
            Write("testing/a.test.js"),
            Write("café file.test.js"),
            Write("sub dir/test/spaced.js"),
            Write("not_node_modules/a.test.js")
        };
        Write("src/plain.js");
        Write("src/test.js");
        Write("tests.js");
        Write("spec.js");
        Write("foo.mjs");
        Write("foo.cjs");
        Write("test/readme.md");
        Write("mytests/a.js");
        Write("test/.hidden.test.js");
        Write(".secret/test/a.test.js");
        Write("node_modules/pkg/test/a.test.js");
        Write("src/node_modules/a.test.js");
        Write("src/.hidden.test.js");
        Directory.CreateDirectory(Path.Combine(root, "foo.test.js"));
        File.WriteAllText(Path.Combine(root, "foo.test.js", "inside.txt"), "x");

        var found = ProjectChecks.TestFiles(root);
        Assert.Equal(expected.OrderBy(path => path, StringComparer.Ordinal), found.OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public void Test_files_stop_at_depth_4_and_at_the_next_directory_after_200()
    {
        var included = Write("depth/a/b/c/d/in.test.js");
        Write("depth/a/b/c/d/e/out.test.js");
        Write("depth/a/b/c/d/note.txt");
        Assert.Equal([included], ProjectChecks.TestFiles(Path.Combine(root, "depth")));

        for (var i = 0; i < 201; i++) Write($"only/f{i:000}.test.js");
        Assert.Equal(201, ProjectChecks.TestFiles(Path.Combine(root, "only")).Length);

        var edge = Path.Combine(root, "edge");
        for (var i = 0; i < 200; i++) Write($"edge/batch/f{i:000}.test.js");
        var border = Write("edge/later/one.test.js");
        Assert.Contains(border, ProjectChecks.TestFiles(edge));
        Write("edge/batch/f200.test.js");
        var over = ProjectChecks.TestFiles(edge);
        Assert.Equal(201, over.Count(path => path.Contains($"{Path.DirectorySeparatorChar}batch{Path.DirectorySeparatorChar}")));
        Assert.DoesNotContain(border, over);
        Assert.Empty(ProjectChecks.TestFiles(Path.Combine(root, "missing-folder")));
        Assert.Empty(ProjectChecks.TestFiles(included));
    }

    [Fact]
    public void Test_command_uses_npm_test_or_the_runtime_and_null_when_nothing_matches()
    {
        Assert.Null(ProjectChecks.TestCommand(null));
        Assert.Null(ProjectChecks.TestCommand(""));
        Assert.Null(ProjectChecks.TestCommand(root));

        Write("test/a.test.cjs", "throw new Error('boom');\n");
        var node = ProjectChecks.TestCommand(root)!;
        Assert.Equal(Environment.ProcessPath ?? "", node.Command);
        Assert.Equal(["--test"], node.Args);
        Assert.Equal("node --test", node.Label);

        Write("package.json", """{"scripts":{"test":"  "}}""");
        Assert.Equal("node --test", ProjectChecks.TestCommand(root)!.Label);
        Write("package.json", """{"scripts":{"test":1}}""");
        Assert.Equal("node --test", ProjectChecks.TestCommand(root)!.Label);
        Write("package.json", """{"scripts":{"test":"echo no test specified"}}""");
        Assert.Equal("node --test", ProjectChecks.TestCommand(root)!.Label);
        Write("package.json", "\uFEFF" + """{"scripts":{"test":"jest"}}""");
        Assert.Equal("node --test", ProjectChecks.TestCommand(root)!.Label);
        Write("package.json", "{");
        Assert.Equal("node --test", ProjectChecks.TestCommand(root)!.Label);

        Write("package.json", """{"scripts":{"test":"jest"}}""");
        var npm = ProjectChecks.TestCommand(root)!;
        Assert.Equal("npm test", npm.Label);
        if (OperatingSystem.IsWindows())
        {
            var shell = Environment.GetEnvironmentVariable("ComSpec");
            Assert.Equal(string.IsNullOrEmpty(shell) ? "cmd.exe" : shell, npm.Command);
            Assert.Equal(["/d", "/s", "/c", "npm test"], npm.Args);
        }
        else
        {
            Assert.Equal("npm", npm.Command);
            Assert.Equal(["test"], npm.Args);
        }

        var bare = Path.Combine(root, "bare");
        Directory.CreateDirectory(bare);
        File.WriteAllText(Path.Combine(bare, "package.json"), """{"scripts":{"test":"0"}}""");
        Assert.Equal("npm test", ProjectChecks.TestCommand(bare)!.Label);
        Assert.Null(ProjectChecks.TestCommand(Path.Combine(root, "test", "a.test.cjs")));
    }

    async Task<ProjectChecks.CheckResult> Run(string folder, Action<ProjectChecks.CheckEvents> drive, ProjectChecks.CheckOptions? options = null, Action<ProjectChecks.CheckRequest>? observe = null)
    {
        var result = await ProjectChecks.RunChecks(folder, options, (request, events) =>
        {
            observe?.Invoke(request);
            drive(events);
        });
        Assert.NotNull(result);
        return result!;
    }

    [Fact(Timeout = 5000)]
    public async Task Project_checks_run_node_test_when_test_files_exist_and_report_a_failure()
    {
        var calls = 0;
        Assert.Null(ProjectChecks.TestCommand(root));
        Assert.Null(await ProjectChecks.RunChecks(root, starter: (_, _) => { calls++; }));
        Assert.Equal(0, calls);

        Write("test/a.test.cjs", "require('node:test')('x', () => { throw Error('boom') });\n");
        ProjectChecks.CheckRequest? seen = null;
        var failed = await Run(root, events => { events.Output("boom"); events.Close(1); }, observe: request => seen = request);
        Assert.Equal("node --test", failed.Label);
        Assert.True(failed.ExitCode != 0);
        Assert.False(failed.TimedOut);
        Assert.NotNull(seen);
        Assert.Equal(Environment.ProcessPath ?? "", seen!.Command);
        Assert.Equal(["--test"], seen.Args);
        Assert.Equal(root, seen.WorkingDirectory);

        var passed = await Run(root, events => events.Close(0));
        Assert.Equal(0, passed.ExitCode ?? -1);
        Assert.False(passed.TimedOut);
        Assert.Equal("boom", failed.Output);
    }

    [Fact(Timeout = 5000)]
    public async Task Run_checks_merges_env_forces_ci_and_passes_the_command_list()
    {
        const string parent = "PW_PROJECT_CHECKS_PARENT";
        const string over = "PW_PROJECT_CHECKS_OVER";
        var previousParent = Environment.GetEnvironmentVariable(parent);
        var previousOver = Environment.GetEnvironmentVariable(over);
        var previousContext = Environment.GetEnvironmentVariable("NODE_TEST_CONTEXT");
        Environment.SetEnvironmentVariable(parent, "from-parent");
        Environment.SetEnvironmentVariable(over, "original");
        Environment.SetEnvironmentVariable("NODE_TEST_CONTEXT", "from-parent");
        try
        {
            Write("package.json", """{"scripts":{"test":"jest"}}""");
            ProjectChecks.CheckRequest? seen = null;
            var result = await Run(root, events => events.Close(0), new ProjectChecks.CheckOptions
            {
                Env = new Dictionary<string, string>
                {
                    [over] = "replaced",
                    ["PW_PROJECT_CHECKS_CHILD"] = "from-child",
                    ["CI"] = "0",
                    ["NODE_TEST_CONTEXT"] = "keep"
                }
            }, request => seen = request);
            Assert.Equal("npm test", result.Label);
            Assert.NotNull(seen);
            Assert.Equal(root, seen!.WorkingDirectory);
            Assert.Equal("from-parent", seen.Environment[parent]);
            Assert.Equal("replaced", seen.Environment[over]);
            Assert.Equal("from-child", seen.Environment["PW_PROJECT_CHECKS_CHILD"]);
            Assert.Equal("1", seen.Environment["CI"]);
            Assert.False(seen.Environment.ContainsKey("NODE_TEST_CONTEXT"));
            Assert.Equal(ProjectChecks.TimeoutMs, seen.TimeoutMs);
            if (OperatingSystem.IsWindows()) Assert.Equal(["/d", "/s", "/c", "npm test"], seen.Args);
            else Assert.Equal(["test"], seen.Args);
        }
        finally
        {
            Environment.SetEnvironmentVariable(parent, previousParent);
            Environment.SetEnvironmentVariable(over, previousOver);
            Environment.SetEnvironmentVariable("NODE_TEST_CONTEXT", previousContext);
        }
    }

    [Fact(Timeout = 5000)]
    public async Task Run_checks_tails_output_reports_errors_once_and_rounds_seconds()
    {
        Write("package.json", """{"scripts":{"test":"jest"}}""");
        var small = await Run(root, events => { events.Output("ab"); events.Output("cd"); events.Close(0); });
        Assert.Equal("abcd", small.Output);

        var cut = await Run(root, events => { events.Output(new string('A', 3001)); events.Close(0); });
        Assert.Equal("..." + new string('A', 3000), cut.Output);
        Assert.Equal(new string('B', 3000), ProjectChecks.Tail(new string('B', 3000)));
        Assert.Equal("..." + new string('B', 20_000), ProjectChecks.Tail(new string('B', 20_001), 20_000));
        Assert.Equal("", ProjectChecks.Tail(null));

        var error = await Run(root, events => { events.Output("hello"); events.Error("spawn ENOENT"); events.Close(9); });
        Assert.Null(error.ExitCode);
        Assert.Equal("hello\nspawn ENOENT", error.Output);
        Assert.False(error.TimedOut);

        var closed = await Run(root, events => { events.Output("out"); events.Close(4); events.Error("late"); events.Close(8); });
        Assert.Equal(4, closed.ExitCode ?? -1);
        Assert.Equal("out", closed.Output);

        Assert.Equal(0d, await Seconds(0));
        Assert.Equal(0d, await Seconds(49));
        Assert.Equal(0.1, await Seconds(50));
        Assert.Equal(0.1, await Seconds(149));
        Assert.Equal(0.2, await Seconds(150));
        Assert.Equal(0.3, await Seconds(250));

        ProjectChecks.CheckRequest? quick = null;
        await Run(root, events => events.Close(0), new ProjectChecks.CheckOptions { TimeoutMs = -5 }, request => quick = request);
        Assert.Equal(0, quick!.TimeoutMs);
    }

    async Task<double> Seconds(long elapsed)
    {
        var phase = 0;
        var result = await Run(root, events => events.Close(0), new ProjectChecks.CheckOptions
        {
            Now = () => phase++ == 0 ? 1_000 : 1_000 + elapsed
        });
        return result.Seconds;
    }

    [Fact(Timeout = 5000)]
    public async Task Run_checks_sets_timed_out_when_the_hook_is_killed()
    {
        Write("test/a.test.js");
        var killed = 0;
        var result = await Run(root, events => { events.Kill = () => { killed++; events.Close(null); }; }, new ProjectChecks.CheckOptions { TimeoutMs = 30 });
        Assert.True(result.TimedOut);
        Assert.Null(result.ExitCode);
        Assert.Equal(1, killed);
        Assert.Equal("node --test", result.Label);
    }

    [Fact(Timeout = 5000)]
    public async Task Two_checks_do_not_share_output()
    {
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        File.WriteAllText(Path.Combine(left, "package.json"), """{"scripts":{"test":"left"}}""");
        File.WriteAllText(Path.Combine(right, "package.json"), """{"scripts":{"test":"right"}}""");
        var first = ProjectChecks.RunChecks(left, starter: (_, events) => { events.Output("L"); events.Close(3); });
        var second = ProjectChecks.RunChecks(right, starter: (_, events) => { events.Output("R"); events.Close(4); });
        var results = await Task.WhenAll(first, second);
        Assert.Equal("L", results[0]!.Output);
        Assert.Equal(3, results[0]!.ExitCode ?? -1);
        Assert.Equal("R", results[1]!.Output);
        Assert.Equal(4, results[1]!.ExitCode ?? -1);
    }
}
