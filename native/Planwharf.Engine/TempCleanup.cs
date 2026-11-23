// Port of engine/temp-cleanup.cjs.

namespace Planwharf.Engine;

public static class TempCleanup
{
    public static async Task<bool> RemoveTempDirAsync(string target, int waitMs = 15000, int stepMs = 100)
    {
        var resolved = Path.GetFullPath(target ?? "");
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var prefix = tempRoot.EndsWith(Path.DirectorySeparatorChar) ? tempRoot : tempRoot + Path.DirectorySeparatorChar;

        bool isWindows = OperatingSystem.IsWindows();
        var left = isWindows ? resolved.ToLowerInvariant() : resolved;
        var right = isWindows ? prefix.ToLowerInvariant() : prefix;

        if (!left.StartsWith(right))
            throw new InvalidOperationException("removeTempDir only removes folders inside the temp directory");

        if (!Directory.Exists(resolved) && !File.Exists(resolved))
            return true;

        var deadline = Environment.TickCount64 + waitMs;
        while (true)
        {
            try
            {
                if (Directory.Exists(resolved))
                    Directory.Delete(resolved, true);
                else if (File.Exists(resolved))
                    File.Delete(resolved);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadline)
                    throw;
                await Task.Delay(stepMs);
            }
        }
    }

    public static async Task<bool> WaitForExitAsync(Func<int, bool> isAlive, int pid, int waitMs = 5000, int stepMs = 25)
    {
        var deadline = Environment.TickCount64 + waitMs;
        while (isAlive(pid) && Environment.TickCount64 < deadline)
        {
            await Task.Delay(stepMs);
        }
        return !isAlive(pid);
    }
}
