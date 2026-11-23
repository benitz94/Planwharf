// Tests for TempCleanup.cs.
using Xunit;

namespace Planwharf.Engine.Tests;

public class TempCleanupTests
{
    [Fact]
    public async Task RemoveTempDir_OutsideTempDirectory_ThrowsSecurityException()
    {
        var nonTemp = Path.Combine(Directory.GetCurrentDirectory(), "forbidden_test_dir");
        await Assert.ThrowsAsync<InvalidOperationException>(() => TempCleanup.RemoveTempDirAsync(nonTemp));

        // Sibling path escape attempt: e.g. /tmp-evil
        var tempRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var siblingEscape = tempRoot + "-evil";
        await Assert.ThrowsAsync<InvalidOperationException>(() => TempCleanup.RemoveTempDirAsync(siblingEscape));
    }

    [Fact]
    public async Task RemoveTempDir_NonExistentPath_ReturnsTrue()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "pw_non_existent_" + Guid.NewGuid().ToString("N"));
        var result = await TempCleanup.RemoveTempDirAsync(nonExistent);
        Assert.True(result);
    }

    [Fact]
    public async Task RemoveTempDir_FolderLockedByProcess_RetriesUntilFreed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pw_retry_cleanup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "lock.txt");

        // Lock file briefly
        var stream = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            stream.Dispose();
        });

        var result = await TempCleanup.RemoveTempDirAsync(dir, waitMs: 3000, stepMs: 50);
        Assert.True(result);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task WaitForExit_ProcessExitsPromptly_ReturnsTrue()
    {
        int checkCount = 0;
        bool IsAlive(int pid) => ++checkCount < 3;

        var exited = await TempCleanup.WaitForExitAsync(IsAlive, 1234, waitMs: 1000, stepMs: 10);
        Assert.True(exited);
    }

    [Fact]
    public async Task WaitForExit_ProcessStuck_TimesOutAndReturnsFalse()
    {
        bool IsAlive(int pid) => true;

        var exited = await TempCleanup.WaitForExitAsync(IsAlive, 9999, waitMs: 100, stepMs: 20);
        Assert.False(exited);
    }
}
