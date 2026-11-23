// Port of engine/account-folders.cjs.
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public record AccountFolderRecord(string Id, string Adapter, string ProviderId, string Folder, string? SignInCommand = null);

public static class AccountFolders
{
    private static readonly HashSet<string> SupportedAdapters = new(StringComparer.Ordinal) { "claude", "codex" };
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal) { "adapter", "folder", "id", "providerId" };
    private static readonly Regex IdPattern = new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.ECMAScript);
    private static readonly Regex UnsafeChars = new(@"[\r\n\0]", RegexOptions.ECMAScript);

    public static string CommandFor(string adapter, string folder)
    {
        if (string.IsNullOrEmpty(folder) || UnsafeChars.IsMatch(folder))
            throw new InvalidOperationException("Invalid account folder.");

        bool isWindows = OperatingSystem.IsWindows();
        var escaped = isWindows ? folder.Replace("'", "''") : folder.Replace("'", "'\\''");
        var envVar = adapter == "claude" ? "CLAUDE_CONFIG_DIR" : "CODEX_HOME";
        var cli = adapter == "claude" ? "claude" : "codex";

        return isWindows
            ? $"$env:{envVar}='{escaped}'; {cli} login"
            : $"{envVar}='{escaped}' {cli} login";
    }

    public static bool InsideRoot(string root, string folder)
    {
        var rootFull = Path.GetFullPath(root);
        var folderFull = Path.GetFullPath(folder);

        string Rel(string from, string to)
        {
            var relative = Path.GetRelativePath(from, to);
            return relative;
        }

        var rel = Rel(rootFull, folderFull);
        if (rel == "." || rel.StartsWith("..") || Path.IsPathRooted(rel)) return false;

        try
        {
            if (Directory.Exists(folderFull) || File.Exists(folderFull))
            {
                var realFolder = Path.GetFullPath(folderFull);
                var realRel = Rel(rootFull, realFolder);
                if (realRel == "." || realRel.StartsWith("..") || Path.IsPathRooted(realRel)) return false;
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }

        return true;
    }

    public static IReadOnlyList<AccountFolderRecord> ReadAccounts(string file, string root)
    {
        if (!File.Exists(file)) return Array.Empty<AccountFolderRecord>();

        JsonNode? parsed;
        try
        {
            var text = File.ReadAllText(file);
            parsed = JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new InvalidOperationException("Invalid account folders file.");
        }

        if (parsed is not JsonArray arr)
            throw new InvalidOperationException("Invalid account folders file.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AccountFolderRecord>();

        foreach (var item in arr)
        {
            if (item is not JsonObject row)
                throw new InvalidOperationException("Invalid account folders file.");

            if (row.Count != AllowedKeys.Count || row.Select(p => p.Key).Any(k => !AllowedKeys.Contains(k)))
                throw new InvalidOperationException("Invalid account folders file.");

            var id = row["id"]?.GetValue<string>() ?? "";
            var adapter = row["adapter"]?.GetValue<string>() ?? "";
            var providerId = row["providerId"]?.GetValue<string>() ?? "";
            var folder = row["folder"]?.GetValue<string>() ?? "";

            if (!SupportedAdapters.Contains(adapter) || !IdPattern.IsMatch(id) || !seen.Add(id) || providerId != adapter)
                throw new InvalidOperationException("Invalid account folders file.");

            var expectedFolder = Path.Combine(root, adapter, id);
            if (Path.GetFullPath(folder) != Path.GetFullPath(expectedFolder) || !InsideRoot(root, folder))
                throw new InvalidOperationException("Invalid account folders file.");

            result.Add(new AccountFolderRecord(id, adapter, providerId, folder));
        }

        return result;
    }

    private static bool PidAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void ReclaimLock(string lockFile)
    {
        if (!File.Exists(lockFile)) return;
        try
        {
            var text = File.ReadAllText(lockFile);
            var parts = text.Split(':');
            if (parts.Length > 1 && int.TryParse(parts[0], out var pid) && pid > 0)
            {
                if (!PidAlive(pid))
                {
                    try { File.Delete(lockFile); } catch { }
                }
                return;
            }

            var writeTime = File.GetLastWriteTimeUtc(lockFile);
            if ((DateTime.UtcNow - writeTime).TotalMilliseconds > 10000)
            {
                try { File.Delete(lockFile); } catch { }
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static T WithLock<T>(string file, Func<T> fn)
    {
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var lockFile = file + ".lock";
        var owner = Environment.ProcessId + ":" + Guid.NewGuid().ToString("N");
        var started = Environment.TickCount64;

        while (true)
        {
            FileStream? fs = null;
            try
            {
                fs = new FileStream(lockFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                using (var writer = new StreamWriter(fs, leaveOpen: false))
                {
                    writer.Write(owner);
                }
                fs = null;

                try
                {
                    return fn();
                }
                finally
                {
                    try
                    {
                        if (File.Exists(lockFile) && File.ReadAllText(lockFile) == owner)
                            File.Delete(lockFile);
                    }
                    catch { }
                }
            }
            catch (IOException)
            {
                try { fs?.Dispose(); } catch { }
                ReclaimLock(lockFile);
                if (Environment.TickCount64 - started > 5000)
                    throw new InvalidOperationException("Account folders file is busy.");
                Thread.Sleep(15);
            }
        }
    }

    public static AccountFolderRecord AddAccount(string file, string root, string adapter, string providerId, JsonArray? existing = null)
    {
        if (!SupportedAdapters.Contains(adapter) || providerId != adapter)
            throw new InvalidOperationException("This AI tool does not support another account.");

        if (existing != null && existing is not JsonArray)
            throw new InvalidOperationException("Invalid account folders file.");

        if (string.IsNullOrEmpty(root) || UnsafeChars.IsMatch(root))
            throw new InvalidOperationException("Invalid account folder.");

        return WithLock(file, () =>
        {
            var saved = ReadAccounts(file, root);
            var id = Guid.NewGuid().ToString();
            var folder = Path.Combine(root, adapter, id);
            Directory.CreateDirectory(folder);

            var row = new AccountFolderRecord(id, adapter, providerId, folder);
            var temporary = file + "." + id + ".tmp";
            try
            {
                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var list = new JsonArray();
                foreach (var s in saved)
                {
                    list.Add((JsonNode)new JsonObject
                    {
                        ["id"] = s.Id,
                        ["adapter"] = s.Adapter,
                        ["providerId"] = s.ProviderId,
                        ["folder"] = s.Folder
                    });
                }
                list.Add((JsonNode)new JsonObject
                {
                    ["id"] = row.Id,
                    ["adapter"] = row.Adapter,
                    ["providerId"] = row.ProviderId,
                    ["folder"] = row.Folder
                });

                File.WriteAllText(temporary, list.ToJsonString() + "\n");
                AtomicRename.RenameWithRetry(temporary, file);

                return row with { SignInCommand = CommandFor(adapter, folder) };
            }
            catch
            {
                try { File.Delete(temporary); } catch { }
                try { Directory.Delete(folder, true); } catch { }
                throw;
            }
        });
    }
}
