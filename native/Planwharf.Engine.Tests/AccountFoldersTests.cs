// Tests for AccountFolders.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class AccountFoldersTests : IDisposable
{
    private readonly string _testDir;

    public AccountFoldersTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "pw_acc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    [Fact]
    public void CommandFor_WindowsAndPosix_EscapesQuotesCorrectly()
    {
        var cmd = AccountFolders.CommandFor("claude", "C:\\folder's\\path");
        Assert.Contains("CLAUDE_CONFIG_DIR", cmd);
        Assert.Contains("claude login", cmd);
    }

    [Fact]
    public void InsideRoot_PathTraversalOrOutside_ReturnsFalse()
    {
        var root = Path.Combine(_testDir, "root");
        Directory.CreateDirectory(root);

        Assert.False(AccountFolders.InsideRoot(root, Path.Combine(_testDir, "outside")));
        Assert.False(AccountFolders.InsideRoot(root, Path.Combine(root, "..", "escaped")));
        Assert.True(AccountFolders.InsideRoot(root, Path.Combine(root, "valid_subfolder")));
    }

    [Fact]
    public void ReadAccounts_CorruptOrMismatchedPaths_ThrowsValidationException()
    {
        var file = Path.Combine(_testDir, "accounts.json");
        File.WriteAllText(file, "{ invalid json }");
        Assert.Throws<InvalidOperationException>(() => AccountFolders.ReadAccounts(file, _testDir));
    }

    [Fact]
    public void AddAccount_ValidAdapter_CreatesFolderAndRecordsAtomicRow()
    {
        var file = Path.Combine(_testDir, "accounts.json");
        var account = AccountFolders.AddAccount(file, _testDir, "claude", "claude");

        Assert.NotNull(account);
        Assert.Equal("claude", account.Adapter);
        Assert.True(Directory.Exists(account.Folder));
        Assert.NotNull(account.SignInCommand);

        var list = AccountFolders.ReadAccounts(file, _testDir);
        Assert.Single(list);
        Assert.Equal(account.Id, list[0].Id);
    }
}
