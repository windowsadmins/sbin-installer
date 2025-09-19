namespace SbinInstaller.Models;

/// <summary>
/// Installation options parsed from command line arguments
/// </summary>
public class InstallOptions
{
    public string PackagePath { get; set; } = string.Empty;
    public string Target { get; set; } = "/";
    public bool Verbose { get; set; }
    public bool VerboseR { get; set; }
    public bool DumpLog { get; set; }
    public bool AllowUntrusted { get; set; }
    public bool ShowPkgInfo { get; set; }
    public bool ShowDomInfo { get; set; }
    public bool ShowVolInfo { get; set; }
    public string? QueryFlag { get; set; }
    public bool ShowVersion { get; set; }
    public bool ShowConfig { get; set; }
    public bool ShowHelp { get; set; }
    public bool PlistFormat { get; set; }
    public string? ConfigFile { get; set; }
    public string? Language { get; set; }
    public bool ListIso { get; set; }
    public bool ShowChoicesXml { get; set; }
}

/// <summary>
/// Results of installation operation
/// </summary>
public class InstallResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public List<string> Logs { get; set; } = new();
    public string? RestartAction { get; set; }
}

/// <summary>
/// Package information extracted from .pkg file
/// </summary>
public class PackageInfo
{
    public BuildInfo BuildInfo { get; set; } = new();
    public string PackagePath { get; set; } = string.Empty;
    public string ExtractedPath { get; set; } = string.Empty;
    public List<string> PayloadFiles { get; set; } = new();
    public bool HasPreInstallScript { get; set; }
    public bool HasPostInstallScript { get; set; }
    public bool HasChocolateyBeforeInstall { get; set; }
    public bool HasChocolateyInstall { get; set; }
}