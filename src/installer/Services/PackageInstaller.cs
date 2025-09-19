using Microsoft.Extensions.Logging;
using SbinInstaller.Models;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Principal;
using YamlDotNet.Serialization;

namespace SbinInstaller.Services;

/// <summary>
/// Core package installer service that handles .pkg extraction and installation
/// Mimics functionality of macOS /usr/sbin/installer in a Windows environment
/// </summary>
public class PackageInstaller
{
    private readonly ILogger<PackageInstaller> _logger;
    private readonly IDeserializer _yamlDeserializer;

    public PackageInstaller(ILogger<PackageInstaller> logger)
    {
        _logger = logger;
        _yamlDeserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>
    /// Extract and analyze package information without installing
    /// </summary>
    public async Task<PackageInfo> GetPackageInfoAsync(string packagePath)
    {
        if (!File.Exists(packagePath))
            throw new FileNotFoundException($"Package file not found: {packagePath}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"pkg_extract_{Guid.NewGuid():N}");
        
        try
        {
            _logger.LogDebug("Extracting package to temporary directory: {TempDir}", tempDir);
            Directory.CreateDirectory(tempDir);

            // Extract the .pkg (zip) file
            ZipFile.ExtractToDirectory(packagePath, tempDir);

            var packageInfo = new PackageInfo
            {
                PackagePath = packagePath,
                ExtractedPath = tempDir
            };

            // Read build-info.yaml if present
            var buildInfoPath = Path.Combine(tempDir, "build-info.yaml");
            if (File.Exists(buildInfoPath))
            {
                var yamlContent = await File.ReadAllTextAsync(buildInfoPath);
                packageInfo.BuildInfo = _yamlDeserializer.Deserialize<BuildInfo>(yamlContent);
            }

            // Check for scripts
            packageInfo.HasPreInstallScript = File.Exists(Path.Combine(tempDir, "scripts", "preinstall.ps1"));
            packageInfo.HasPostInstallScript = File.Exists(Path.Combine(tempDir, "scripts", "postinstall.ps1"));
            packageInfo.HasChocolateyBeforeInstall = File.Exists(Path.Combine(tempDir, "tools", "chocolateyBeforeInstall.ps1"));
            packageInfo.HasChocolateyInstall = File.Exists(Path.Combine(tempDir, "tools", "chocolateyInstall.ps1"));

            // Get payload files
            var payloadDir = Path.Combine(tempDir, "payload");
            if (Directory.Exists(payloadDir))
            {
                packageInfo.PayloadFiles = Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(payloadDir, f))
                    .ToList();
            }

            return packageInfo;
        }
        catch
        {
            // Clean up temp directory on error
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// Install package to the specified target
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task<InstallResult> InstallAsync(InstallOptions options)
    {
        var result = new InstallResult();
        var logs = new List<string>();

        try
        {
            // Validate elevation if needed
            if (!IsElevated() && options.Target == "/" || options.Target.StartsWith("C:\\"))
            {
                result.Message = "Installation to system directories requires elevation. Run as administrator.";
                result.ExitCode = 1;
                return result;
            }

            // Get package information
            var packageInfo = await GetPackageInfoAsync(options.PackagePath);
            logs.Add($"Package: {packageInfo.BuildInfo.Name} v{packageInfo.BuildInfo.Version}");

            // Determine target directory
            var targetDir = ResolveTargetDirectory(options.Target);
            logs.Add($"Target directory: {targetDir}");

            // Run pre-install script
            if (packageInfo.HasPreInstallScript)
            {
                logs.Add("Running pre-install script...");
                var preResult = await RunPowerShellScriptAsync(
                    Path.Combine(packageInfo.ExtractedPath, "scripts", "preinstall.ps1"), 
                    packageInfo.ExtractedPath);
                
                if (preResult.ExitCode != 0)
                {
                    result.Message = $"Pre-install script failed: {preResult.Output}";
                    result.ExitCode = preResult.ExitCode;
                    return result;
                }
                logs.AddRange(preResult.Logs);
            }
            else if (packageInfo.HasChocolateyBeforeInstall)
            {
                logs.Add("Running Chocolatey before-install script...");
                var preResult = await RunPowerShellScriptAsync(
                    Path.Combine(packageInfo.ExtractedPath, "tools", "chocolateyBeforeInstall.ps1"),
                    packageInfo.ExtractedPath);
                
                if (preResult.ExitCode != 0)
                {
                    result.Message = $"Chocolatey before-install script failed: {preResult.Output}";
                    result.ExitCode = preResult.ExitCode;
                    return result;
                }
                logs.AddRange(preResult.Logs);
            }

            // Mirror payload files to target
            var payloadDir = Path.Combine(packageInfo.ExtractedPath, "payload");
            if (Directory.Exists(payloadDir))
            {
                logs.Add("Installing payload files...");
                MirrorDirectory(payloadDir, targetDir);
                logs.Add($"Installed {packageInfo.PayloadFiles.Count} files");
            }

            // Run post-install script
            if (packageInfo.HasPostInstallScript)
            {
                logs.Add("Running post-install script...");
                var postResult = await RunPowerShellScriptAsync(
                    Path.Combine(packageInfo.ExtractedPath, "scripts", "postinstall.ps1"),
                    packageInfo.ExtractedPath);
                
                if (postResult.ExitCode != 0)
                {
                    result.Message = $"Post-install script failed: {postResult.Output}";
                    result.ExitCode = postResult.ExitCode;
                    return result;
                }
                logs.AddRange(postResult.Logs);
            }
            else if (packageInfo.HasChocolateyInstall)
            {
                logs.Add("Running Chocolatey install script...");
                var postResult = await RunPowerShellScriptAsync(
                    Path.Combine(packageInfo.ExtractedPath, "tools", "chocolateyInstall.ps1"),
                    packageInfo.ExtractedPath);
                
                if (postResult.ExitCode != 0)
                {
                    result.Message = $"Chocolatey install script failed: {postResult.Output}";
                    result.ExitCode = postResult.ExitCode;
                    return result;
                }
                logs.AddRange(postResult.Logs);
            }

            result.Success = true;
            result.Message = "Package installed successfully";
            result.RestartAction = packageInfo.BuildInfo.RestartAction;
            result.Logs = logs;

            // Clean up extraction directory
            try
            {
                Directory.Delete(packageInfo.ExtractedPath, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to clean up temporary directory: {Error}", ex.Message);
            }
        }
        catch (Exception ex)
        {
            result.Message = $"Installation failed: {ex.Message}";
            result.ExitCode = 1;
            _logger.LogError(ex, "Installation failed");
        }

        result.Logs = logs;
        return result;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string ResolveTargetDirectory(string target)
    {
        return target switch
        {
            "/" => "C:\\",
            "CurrentUserHomeDirectory" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            var t when t.StartsWith("/Volumes/") => t.Replace("/Volumes/", "").TrimEnd('/') + ":\\",
            var t when t.Length == 1 && char.IsLetter(t[0]) => t.ToUpper() + ":\\",
            _ => Path.GetFullPath(target)
        };
    }

    private void MirrorDirectory(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        // Copy all files
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var targetFile = Path.Combine(targetDir, relativePath);
            var targetFileDir = Path.GetDirectoryName(targetFile);
            
            if (!string.IsNullOrEmpty(targetFileDir) && !Directory.Exists(targetFileDir))
                Directory.CreateDirectory(targetFileDir);

            File.Copy(file, targetFile, true);
            _logger.LogDebug("Copied: {Source} -> {Target}", file, targetFile);
        }
    }

    private async Task<(int ExitCode, string Output, List<string> Logs)> RunPowerShellScriptAsync(string scriptPath, string workingDirectory)
    {
        var logs = new List<string>();
        
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new System.Text.StringBuilder();
        
        process.OutputDataReceived += (sender, e) => {
            if (!string.IsNullOrEmpty(e.Data))
            {
                output.AppendLine(e.Data);
                logs.Add($"[SCRIPT] {e.Data}");
                _logger.LogDebug("[SCRIPT] {Output}", e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) => {
            if (!string.IsNullOrEmpty(e.Data))
            {
                output.AppendLine(e.Data);
                logs.Add($"[SCRIPT ERROR] {e.Data}");
                _logger.LogWarning("[SCRIPT ERROR] {Error}", e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        await process.WaitForExitAsync();
        
        return (process.ExitCode, output.ToString(), logs);
    }
}