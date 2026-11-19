using System;
using System.IO;

namespace Planwharf.FakeCli.Tests;

public static class FakePaths
{
    public static string FakeDir(string program)
    {
        var localCopy = Path.Combine(AppContext.BaseDirectory, "fakes", program);
        if (Directory.Exists(localCopy)) return localCopy;

        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "porting", "fakes", program);
            if (Directory.Exists(candidate)) return candidate;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "porting", "fakes", program);
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException($"native/porting/fakes/{program} not found");
    }
}
