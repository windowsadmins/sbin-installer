using System.IO;
using System.Linq;

namespace SbinInstaller.Models;

/// <summary>
/// Supported package types
/// </summary>
public enum PackageType
{
    /// <summary>
    /// Custom .pkg format with build-info.yaml
    /// </summary>
    Pkg,
    /// <summary>
    /// NuGet .nupkg format with .nuspec metadata
    /// </summary>
    Nupkg
}

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
/// Package information extracted from .pkg or .nupkg file
/// </summary>
public class PackageInfo
{
    public PackageType PackageType { get; set; }
    public BuildInfo BuildInfo { get; set; } = new();
    public NuspecPackage? NuspecInfo { get; set; }
    public string PackagePath { get; set; } = string.Empty;
    public string ExtractedPath { get; set; } = string.Empty;
    public List<string> PayloadFiles { get; set; } = new();
    public bool HasPreInstallScript { get; set; }
    public bool HasPostInstallScript { get; set; }
    public bool HasChocolateyBeforeInstall { get; set; }
    public bool HasChocolateyInstall { get; set; }

    /// <summary>
    /// Get package name, preferring .nuspec id over build-info name
    /// </summary>
    public string GetPackageName()
    {
        if (PackageType == PackageType.Nupkg && NuspecInfo != null)
        {
            // Try title, then id, then extract from id if it looks like a reverse domain
            var title = string.IsNullOrEmpty(NuspecInfo.Metadata.Title) ? null : NuspecInfo.Metadata.Title;
            var id = NuspecInfo.Metadata.Id;
            
            var name = title ?? id;
            if (!string.IsNullOrEmpty(name))
            {
                // If it's a reverse domain style ID (like ca.emilycarru.winadmins.MayaReset), extract the last part
                if (name.Contains('.') && !name.Contains(' '))
                {
                    var parts = name.Split('.');
                    return parts[^1]; // Take the last part (MayaReset)
                }
                return name;
            }
        }
        
        // Fallback to BuildInfo name, or extract from filename if nothing else works
        var buildInfoName = BuildInfo?.Name;
        if (!string.IsNullOrEmpty(buildInfoName))
        {
            return buildInfoName;
        }
        
        // Final fallback: extract name from package filename
        if (!string.IsNullOrEmpty(PackagePath))
        {
            var fileName = Path.GetFileNameWithoutExtension(PackagePath);
            // Remove version-like patterns from filename (e.g., "Package-v1.2.3" -> "Package")
            var versionPattern = @"-v?\d+(\.\d+)*(-\w+)*$";
            return System.Text.RegularExpressions.Regex.Replace(fileName, versionPattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        
        return "Unknown Package";
    }

    /// <summary>
    /// Get package version from appropriate source
    /// </summary>
    public string GetPackageVersion()
    {
        if (PackageType == PackageType.Nupkg && NuspecInfo != null && !string.IsNullOrEmpty(NuspecInfo.Metadata.Version))
        {
            return NuspecInfo.Metadata.Version;
        }
        
        if (!string.IsNullOrEmpty(BuildInfo?.Version))
        {
            return BuildInfo.Version;
        }
        
        // Try to extract version from package filename as fallback
        if (!string.IsNullOrEmpty(PackagePath))
        {
            var fileName = Path.GetFileNameWithoutExtension(PackagePath);
            var versionMatch = System.Text.RegularExpressions.Regex.Match(fileName, @"v?(\d+(?:\.\d+)+(?:-\w+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (versionMatch.Success)
            {
                return versionMatch.Groups[1].Value;
            }
        }
        
        return string.Empty;
    }

    /// <summary>
    /// Get package description from appropriate source
    /// </summary>
    public string GetPackageDescription()
    {
        return PackageType == PackageType.Nupkg && NuspecInfo != null
            ? NuspecInfo.Metadata.Description
            : BuildInfo.Description;
    }

    /// <summary>
    /// Determine if this is a copy-type package (files need to be copied to install_location)
    /// vs installer-type package (scripts handle everything)
    /// 
    /// Logic based on cimipkg source code:
    /// - If no payload → NOT installer-type (script-only)
    /// - If payload AND install_location is empty/blank → installer-type (return false)
    /// - If payload AND install_location has value → copy-type (return true)
    /// </summary>
    public bool IsCopyTypePackage()
    {
        // If no payload files exist, this is a script-only package (not installer-type, not copy-type)
        if (!PayloadFiles.Any())
        {
            return false;
        }

        // For .pkg packages, use install_location from build-info.yaml
        if (PackageType == PackageType.Pkg)
        {
            // Copy-type if install_location has a value, installer-type if empty/blank
            return !string.IsNullOrWhiteSpace(BuildInfo.InstallLocation);
        }
        
        // For .nupkg packages, we need to infer the intent since they don't have explicit install_location
        // We'll use payload content analysis as a heuristic:
        // - If payload contains installer executables → likely installer-type (return false)
        // - If payload contains other files → likely copy-type (return true)
        if (PackageType == PackageType.Nupkg)
        {
            // Check for obvious installer files (more specific patterns)
            bool hasInstallerFiles = PayloadFiles.Any(file => 
            {
                string fileName = Path.GetFileName(file).ToLowerInvariant();
                string extension = Path.GetExtension(file).ToLowerInvariant();
                
                // More specific installer detection patterns
                return extension == ".msi" ||
                       fileName.Contains("setup") ||
                       fileName.Contains("installer") ||
                       fileName.Contains("install") ||
                       (extension == ".exe" && (
                           fileName.Contains("setup") ||
                           fileName.Contains("installer") ||
                           fileName.Contains("install") ||
                           fileName.EndsWith("_setup.exe") ||
                           fileName.EndsWith("_installer.exe") ||
                           fileName.EndsWith("_install.exe") ||
                           // Pattern for vendor installers like "AppName_Version_Win.exe"
                           (fileName.Contains("_win.exe") && fileName.Split('_').Length >= 3)
                       ));
            });
            
            // Installer files in payload = installer-type package (false)
            // Other files in payload = copy-type package (true)
            return !hasInstallerFiles;
        }

        return false;
    }

    /// <summary>
    /// Get the installation location for copy-type packages
    /// </summary>
    public string GetInstallLocation()
    {
        // Only .pkg packages can have install_location from build-info.yaml
        if (PackageType == PackageType.Pkg && !string.IsNullOrWhiteSpace(BuildInfo.InstallLocation))
        {
            return BuildInfo.InstallLocation;
        }
        
        // For copy-type .nupkg packages, we need a default install location
        // This is a reasonable default based on common package contents
        if (PackageType == PackageType.Nupkg && IsCopyTypePackage())
        {
            // Analyze payload to suggest appropriate location
            if (PayloadFiles.Any(file => Path.GetExtension(file).Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
                                       Path.GetExtension(file).Equals(".otf", StringComparison.OrdinalIgnoreCase)))
            {
                return @"C:\Windows\Fonts\";
            }
            
            // Default to a program-specific folder
            var packageName = GetPackageName();
            if (!string.IsNullOrEmpty(packageName))
            {
                return $@"C:\Program Files\{packageName}\";
            }
            
            return @"C:\ProgramData\";
        }
        
        // Installer-type packages don't need install_location
        return string.Empty;
    }
}