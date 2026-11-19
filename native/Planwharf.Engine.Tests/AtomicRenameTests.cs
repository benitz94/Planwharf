using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

// Port of the rename cases in engine/autonomy-crew.test.cjs.
public class AtomicRenameTests
{
    [Fact]
    public void A_locked_rename_is_retried_other_errors_are_not()
    {
        var calls = 0;
        void Busy(string from, string to) { calls++; if (calls < 3) throw new IOException("locked", unchecked((int)0x80070020)); }
        Assert.Equal(3, AtomicRename.RenameWithRetry("a", "b", rename: Busy));
        var gone = Assert.Throws<FileNotFoundException>(() => AtomicRename.RenameWithRetry("a", "b", rename: (_, _) => throw new FileNotFoundException("gone")));
        Assert.Equal("gone", gone.Message);
    }

    [Fact]
    public void A_real_rename_replaces_the_target_and_a_final_failure_removes_the_source()
    {
        var dir = Directory.CreateTempSubdirectory("pw-engine-rename-");
        try
        {
            var from = Path.Combine(dir.FullName, "next.tmp");
            var to = Path.Combine(dir.FullName, "plan.json");
            File.WriteAllText(to, "old");
            File.WriteAllText(from, "new");
            Assert.Equal(1, AtomicRename.RenameWithRetry(from, to));
            Assert.Equal("new", File.ReadAllText(to));
            Assert.False(File.Exists(from));

            File.WriteAllText(from, "again");
            Assert.Throws<UnauthorizedAccessException>(() => AtomicRename.RenameWithRetry(from, to, attempts: 2, rename: (_, _) => throw new UnauthorizedAccessException("held")));
            Assert.False(File.Exists(from), "the temporary file is not left behind");
        }
        finally { dir.Delete(true); }
    }
}
