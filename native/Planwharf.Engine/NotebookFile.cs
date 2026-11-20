// Port of engine/notebook-file.cjs.
namespace Planwharf.Engine;

// Create only the default local notebook; explicit missing paths remain errors.
// The JavaScript default root is the parent of engine/. Callers pass that root:
// this file has no __dirname, and it must not guess a folder that holds plan.json.
public static class NotebookFile
{
    // ERROR_FILE_EXISTS, ERROR_ALREADY_EXISTS, and POSIX EEXIST. Node swallows only EEXIST.
    const int ErrorFileExists = 80, ErrorAlreadyExists = 183, Eexist = 17;

    public static void EnsureDefaultNotebook(string file, string root)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(root);
        // path.resolve(file) !== path.join(path.resolve(root), 'plan.json')
        if (NodePath.Resolve(file) != NodePath.Join(NodePath.Resolve(root), "plan.json")) return;
        try
        {
            // COPYFILE_EXCL. The destination is the caller's path, not the resolved spelling.
            CopyExclusive(NodePath.Join(root, "fixtures", "starter-notebook.json"), file);
        }
        catch (IOException error) when (IsEexist(error))
        {
            // A notebook that is already there keeps its bytes.
            // On Linux and macOS, O_CREAT|O_EXCL against a directory is also EEXIST, so a
            // directory at plan.json is left in place. Windows reports access denied instead.
        }
    }

    static void CopyExclusive(string source, string destination)
    {
        // Node's copyFile on Windows creates plan.json from a path that ends in a separator.
        // FileStream reports that separator as an invalid directory name. POSIX leaves it and fails.
        if (OperatingSystem.IsWindows()) destination = destination.TrimEnd('\\', '/');
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    static bool IsEexist(Exception error)
    {
        var code = error.HResult & 0xFFFF;
        return code is ErrorFileExists or ErrorAlreadyExists or Eexist;
    }

    // Lexical path.resolve and path.join. Path.GetFullPath asks Windows for the
    // on-disk spelling, so two casings of one folder can compare equal. Node does not.
    static class NodePath
    {
        public static string Resolve(string path) =>
            OperatingSystem.IsWindows() ? ResolveWindows(path) : ResolvePosix(path);

        public static string Join(string left, string right) =>
            OperatingSystem.IsWindows() ? NormalizeWindows(JoinRaw(left, right, '\\')) : NormalizePosix(JoinRaw(left, right, '/'));

        public static string Join(string left, string middle, string right) =>
            Join(Join(left, middle), right);

        static string JoinRaw(string left, string right, char separator)
        {
            if (left.Length == 0) return right.Length == 0 ? "." : right;
            if (right.Length == 0) return left;
            return left + separator + right;
        }

        static string ResolvePosix(string path)
        {
            var resolved = "";
            var absolute = false;
            for (var i = 0; i >= -1 && !absolute; i--)
            {
                var part = i >= 0 ? path : Directory.GetCurrentDirectory();
                if (part.Length == 0) continue;
                resolved = part + "/" + resolved;
                absolute = part[0] == '/';
            }
            resolved = NormalizeString(resolved, !absolute, '/', windows: false);
            if (absolute) return "/" + resolved;
            return resolved.Length > 0 ? resolved : ".";
        }

        static string NormalizePosix(string path)
        {
            if (path.Length == 0) return ".";
            var absolute = path[0] == '/';
            var trailing = path[^1] == '/';
            path = NormalizeString(path, !absolute, '/', windows: false);
            if (path.Length == 0 && !absolute) path = ".";
            if (path.Length > 0 && trailing) path += "/";
            return absolute ? "/" + path : path;
        }

        static string ResolveWindows(string path)
        {
            var resolvedDevice = "";
            var resolvedTail = "";
            var resolvedAbsolute = false;
            for (var i = 0; i >= -1; i--)
            {
                string part;
                if (i >= 0) part = path;
                else if (resolvedDevice.Length == 0) part = Directory.GetCurrentDirectory();
                else part = DriveCwd(resolvedDevice);

                if (part.Length == 0) continue;
                var rootEnd = 0;
                var device = "";
                var isAbsolute = false;
                var code = part[0];
                if (part.Length == 1)
                {
                    if (IsWindowsSep(code)) { rootEnd = 1; isAbsolute = true; }
                }
                else if (IsWindowsSep(code))
                {
                    isAbsolute = true;
                    if (IsWindowsSep(part[1]))
                    {
                        var parsed = UncRoot(part);
                        if (parsed.Device.Length > 0) { device = parsed.Device; rootEnd = parsed.RootEnd; }
                    }
                    else rootEnd = 1;
                }
                else if (IsDeviceRoot(code) && part[1] == ':')
                {
                    device = part[..2];
                    rootEnd = 2;
                    if (part.Length > 2 && IsWindowsSep(part[2])) { isAbsolute = true; rootEnd = 3; }
                }

                if (device.Length > 0)
                {
                    if (resolvedDevice.Length > 0)
                    {
                        if (!device.Equals(resolvedDevice, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    else resolvedDevice = device;
                }
                if (resolvedAbsolute)
                {
                    if (resolvedDevice.Length > 0) break;
                    continue;
                }
                resolvedTail = part[rootEnd..] + "\\" + resolvedTail;
                resolvedAbsolute = isAbsolute;
                if (isAbsolute && resolvedDevice.Length > 0) break;
            }

            resolvedTail = NormalizeString(resolvedTail, !resolvedAbsolute, '\\', windows: true);
            // Node lowercases the device. Invariant keeps the check off the machine's locale.
            var full = resolvedDevice.ToLowerInvariant() + (resolvedAbsolute ? "\\" : "") + resolvedTail;
            return full.Length > 0 ? full : ".";
        }

        static string DriveCwd(string device)
        {
            var cwd = Environment.GetEnvironmentVariable("=" + device) ?? Directory.GetCurrentDirectory();
            var prefix = device.ToLowerInvariant() + "\\";
            if (cwd.Length < 3 || !cwd[..3].Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return device + "\\";
            return cwd;
        }

        static (string Device, int RootEnd) UncRoot(string path)
        {
            var j = 2;
            var last = j;
            while (j < path.Length && !IsWindowsSep(path[j])) j++;
            if (j >= path.Length || j == last) return ("", 0);
            var firstPart = path[last..j];
            while (j < path.Length && IsWindowsSep(path[j])) j++;
            last = j;
            while (j < path.Length && !IsWindowsSep(path[j])) j++;
            if (j == last) return ("", 0);
            var share = path[last..j];
            return ("\\\\" + firstPart + "\\" + share, j);
        }

        static string NormalizeWindows(string path)
        {
            if (path.Length == 0) return ".";
            var rootEnd = 0;
            string? device = null;
            var isAbsolute = false;
            var code = path[0];
            if (path.Length == 1) return IsWindowsSep(code) ? "\\" : path;
            if (IsWindowsSep(code))
            {
                isAbsolute = true;
                if (IsWindowsSep(path[1]))
                {
                    var parsed = UncRoot(path);
                    if (parsed.Device.Length > 0) { device = parsed.Device; rootEnd = parsed.RootEnd; }
                    else rootEnd = 1;
                }
                else rootEnd = 1;
            }
            else if (IsDeviceRoot(code) && path[1] == ':')
            {
                device = path[..2];
                rootEnd = 2;
                if (path.Length > 2 && IsWindowsSep(path[2])) { isAbsolute = true; rootEnd = 3; }
            }

            var tail = NormalizeString(path[rootEnd..], !isAbsolute, '\\', windows: true);
            if (tail.Length == 0 && !isAbsolute) tail = ".";
            if (tail.Length > 0 && IsWindowsSep(path[^1])) tail += "\\";
            if (device == null) return isAbsolute ? "\\" + tail : tail;
            return isAbsolute ? device + "\\" + tail : device + tail;
        }

        static bool IsWindowsSep(char code) => code is '/' or '\\';
        static bool IsDeviceRoot(char code) => code is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

        static string NormalizeString(string path, bool allowAboveRoot, char separator, bool windows)
        {
            var res = "";
            var lastSegmentLength = 0;
            var lastSlash = -1;
            var dots = 0;
            var code = 0;
            for (var i = 0; i <= path.Length; i++)
            {
                if (i < path.Length) code = path[i];
                else if (IsSep(code, windows)) break;
                else code = '/';

                if (IsSep(code, windows))
                {
                    if (lastSlash == i - 1 || dots == 1)
                    {
                    }
                    else if (dots == 2)
                    {
                        if (res.Length < 2 || lastSegmentLength != 2 || res[^1] != '.' || res[^2] != '.')
                        {
                            if (res.Length > 2)
                            {
                                var lastSlashIndex = res.LastIndexOf(separator);
                                if (lastSlashIndex < 0) { res = ""; lastSegmentLength = 0; }
                                else
                                {
                                    res = res[..lastSlashIndex];
                                    lastSegmentLength = res.Length - 1 - res.LastIndexOf(separator);
                                }
                                lastSlash = i;
                                dots = 0;
                                continue;
                            }
                            if (res.Length != 0)
                            {
                                res = "";
                                lastSegmentLength = 0;
                                lastSlash = i;
                                dots = 0;
                                continue;
                            }
                        }
                        if (allowAboveRoot)
                        {
                            res += res.Length > 0 ? separator + ".." : "..";
                            lastSegmentLength = 2;
                        }
                    }
                    else
                    {
                        var segment = path[(lastSlash + 1)..i];
                        res = res.Length > 0 ? res + separator + segment : segment;
                        lastSegmentLength = i - lastSlash - 1;
                    }
                    lastSlash = i;
                    dots = 0;
                }
                else if (code == '.' && dots != -1) dots++;
                else dots = -1;
            }
            return res;
        }

        static bool IsSep(int code, bool windows) => code == '/' || (windows && code == '\\');
    }
}
