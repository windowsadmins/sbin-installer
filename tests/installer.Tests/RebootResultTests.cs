using SbinInstaller.Models;
using SbinInstaller.Services;
using SbinInstaller.Services.Msi;
using Xunit;

namespace SbinInstaller.Tests;

/// <summary>
/// A package that needs a reboot returns 3010 under REBOOT=ReallySuppress (or 1641
/// when Windows Installer restarts itself). Both are successes, and the caller must
/// be told a restart is needed.
/// </summary>
public class RebootResultTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(3010u)]
    [InlineData(1641u)]
    public void SuccessCodes_AreReturned(uint code)
    {
        Assert.Equal(code, WindowsInstaller.CheckInstallResult(code));
    }

    [Theory]
    [InlineData(1603u)]
    [InlineData(1618u)]
    [InlineData(1605u)]
    public void FailureCodes_Throw(uint code)
    {
        var ex = Assert.Throws<MsiException>(() => WindowsInstaller.CheckInstallResult(code));
        Assert.Equal((int)code, ex.ErrorCode);
    }

    [Theory]
    [InlineData(3010u)]
    [InlineData(1641u)]
    public void RebootCodes_SetRestartAction(uint code)
    {
        var result = new InstallResult { Success = true };
        var logs = new List<string>();

        MsiInstaller.ApplyRebootResult(result, code, logs);

        Assert.Equal("restart", result.RestartAction);
        Assert.True(result.Success);
        Assert.Contains(logs, l => l.Contains(code.ToString()));
    }

    [Fact]
    public void PlainSuccess_LeavesRestartActionUnset()
    {
        var result = new InstallResult { Success = true };
        var logs = new List<string>();

        MsiInstaller.ApplyRebootResult(result, 0, logs);

        Assert.Null(result.RestartAction);
        Assert.Empty(logs);
    }
}
