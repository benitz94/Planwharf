namespace Planwharf.Engine;

// Port of engine/atomic-rename.cjs.
// Windows refuses a rename while another process (an antivirus scan, an indexer, a reader
// in this app) briefly holds the target open. Retry for about a second before failing.
public static class AtomicRename
{
    // EPERM, EBUSY and EACCES in Node: access denied, sharing violation, lock violation.
    const int AccessDenied = 5, SharingViolation = 32, LockViolation = 33;

    public static bool IsRetryable(Exception error) => error switch
    {
        UnauthorizedAccessException => true,
        FileNotFoundException or DirectoryNotFoundException => false,
        IOException io => (io.HResult & 0xFFFF) is AccessDenied or SharingViolation or LockViolation,
        _ => false
    };

    // Returns the attempt that succeeded. On a final failure the source file is removed and the error rethrown.
    public static int RenameWithRetry(string from, string to, int attempts = 12, Action<string, string>? rename = null)
    {
        rename ??= (source, target) => File.Move(source, target, overwrite: true);
        for (var attempt = 1; ; attempt++)
        {
            try { rename(from, to); return attempt; }
            catch (Exception error) when (!IsRetryable(error) || attempt >= attempts)
            {
                try { File.Delete(from); } catch { /* the source may already be gone */ }
                throw;
            }
            catch (Exception)
            {
                Thread.Sleep(Math.Min(200, 10 * (1 << (attempt - 1))));
            }
        }
    }
}
