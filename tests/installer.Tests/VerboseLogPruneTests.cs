using ManagedUtilities;
using SbinInstaller.Services;
using Xunit;

namespace SbinInstaller.Tests;

public sealed class VerboseLogPruneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sbin-installer-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _logsDir;
    private readonly FileLog _fileLog;

    public VerboseLogPruneTests()
    {
        _logsDir = Path.Combine(_root, "temp");
        Directory.CreateDirectory(_logsDir);
        _fileLog = new FileLog(Path.Combine(_root, "log", "sbin-installer.log"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CreateLog(string name, TimeSpan age)
    {
        var path = Path.Combine(_logsDir, name);
        File.WriteAllText(path, "log");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void DeletesOnlyOldMsiLogs()
    {
        var oldLog = CreateLog("cimian_msi_Old_20250101_000000.log", TimeSpan.FromDays(45));
        var newLog = CreateLog("cimian_msi_New_20260101_000000.log", TimeSpan.FromDays(2));
        var unrelated = CreateLog("other_tool.log", TimeSpan.FromDays(45));

        var deleted = MsiInstaller.PruneVerboseLogsIn(_fileLog, _logsDir, TimeSpan.FromDays(30));

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(newLog));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void StopsAtMaxDeletes()
    {
        for (var i = 0; i < 5; i++)
            CreateLog($"cimian_msi_App{i}_20250101_000000.log", TimeSpan.FromDays(60));

        var deleted = MsiInstaller.PruneVerboseLogsIn(_fileLog, _logsDir, TimeSpan.FromDays(30), maxDeletes: 3);

        Assert.Equal(3, deleted);
        Assert.Equal(2, Directory.GetFiles(_logsDir, "cimian_msi_*.log").Length);
    }

    [Fact]
    public void MissingDirectoryReturnsZero()
    {
        var deleted = MsiInstaller.PruneVerboseLogsIn(_fileLog, Path.Combine(_root, "absent"), TimeSpan.FromDays(30));

        Assert.Equal(0, deleted);
    }

    [Fact]
    public void LegacyTempSweepNeverThrows()
    {
        var deleted = MsiInstaller.PruneLegacyTempLogs(_fileLog, TimeSpan.FromDays(30), maxDeletes: 0);

        Assert.Equal(0, deleted);
    }
}
