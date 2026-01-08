using Microsoft.Extensions.Logging;
using SbinInstaller.Models;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Principal;
using System.Xml;
using System.Xml.Serialization;
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
    /// Determine package type based on file extension
    /// </summary>
    private static PackageType DetectPackageType(string packagePath)
    {
        var extension = Path.GetExtension(packagePath).ToLowerInvariant();
        return extension switch
        {
            ".nupkg" => PackageType.Nupkg,
            ".pkg" => PackageType.Pkg,
            _ => throw new NotSupportedException($"Unsupported package type: {extension}")
        };
    }

    /// <summary>
    /// Find and parse .nuspec file from extracted .nupkg
    /// </summary>
    private Task<NuspecPackage?> ParseNuspecAsync(string extractedPath)
    {
        // Look for .nuspec files in root directory
        var nuspecFiles = Directory.GetFiles(extractedPath, "*.nuspec", SearchOption.TopDirectoryOnly);
        
        if (nuspecFiles.Length == 0)
        {
            _logger.LogWarning("No .nuspec file found in package");
            return Task.FromResult<NuspecPackage?>(null);
        }

        if (nuspecFiles.Length > 1)
        {
            _logger.LogWarning("Multiple .nuspec files found, using first: {NuspecFile}", nuspecFiles[0]);
        }

        var nuspecPath = nuspecFiles[0];
        _logger.LogDebug("Parsing .nuspec file: {NuspecPath}", nuspecPath);

        try
        {
            // Load XML and strip namespaces to support all NuGet schema versions
            var xmlDoc = System.Xml.Linq.XDocument.Load(nuspecPath);
            
            // Remove all namespace declarations and attributes
            foreach (var element in xmlDoc.Descendants())
            {
                element.Name = element.Name.LocalName;
                element.ReplaceAttributes(element.Attributes()
                    .Where(a => !a.IsNamespaceDeclaration));
            }
            
            // Serialize the namespace-free XML to a string, then deserialize it
            // This ensures the XmlSerializer gets a clean document without any namespace artifacts
            var serializer = new XmlSerializer(typeof(NuspecPackage));
            using var stringWriter = new StringWriter();
            xmlDoc.Save(stringWriter);
            var cleanXml = stringWriter.ToString();
            
            using var stringReader = new StringReader(cleanXml);
            var nuspec = (NuspecPackage?)serializer.Deserialize(stringReader);
            return Task.FromResult(nuspec);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse .nuspec file: {NuspecPath}", nuspecPath);
            throw;
        }
    }

    /// <summary>
    /// Extract and analyze package information without installing
    /// </summary>
    public async Task<PackageInfo> GetPackageInfoAsync(string packagePath)
    {
        if (!File.Exists(packagePath))
            throw new FileNotFoundException($"Package file not found: {packagePath}");

        var packageType = DetectPackageType(packagePath);
        var tempDir = Path.Combine(Path.GetTempPath(), $"pkg_extract_{Guid.NewGuid():N}");
        
        try
        {
            _logger.LogDebug("Extracting {PackageType} package to temporary directory: {TempDir}", packageType, tempDir);
            Directory.CreateDirectory(tempDir);

            // Validate package integrity before extraction
            try
            {
                using (var archive = ZipFile.OpenRead(packagePath))
                {
                    // Successfully opened - package structure is valid
                    _logger.LogDebug("Package validation passed: {EntryCount} entries found", archive.Entries.Count);
                }
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("End of Central Directory"))
            {
                var fileInfo = new FileInfo(packagePath);
                var sizeGB = fileInfo.Length / (1024.0 * 1024.0 * 1024.0);
                
                throw new InvalidDataException(
                    $"Package file is corrupted or incomplete:\n" +
                    $"  File: {packagePath}\n" +
                    $"  Size: {sizeGB:F2} GB ({fileInfo.Length:N0} bytes)\n" +
                    $"  Error: {ex.Message}\n\n" +
                    $"Possible causes:\n" +
                    $"  • Incomplete download or file transfer\n" +
                    $"  • Disk write error during package creation\n" +
                    $"  • Build process failure\n" +
                    $"  • Storage corruption\n\n" +
                    $"To fix: rebuild the package\n",
                    ex);
            }

            // Extract the package (both .pkg and .nupkg are ZIP files)
            ZipFile.ExtractToDirectory(packagePath, tempDir);

            var packageInfo = new PackageInfo
            {
                PackageType = packageType,
                PackagePath = packagePath,
                ExtractedPath = tempDir
            };

            // Parse metadata based on package type
            if (packageType == PackageType.Nupkg)
            {
                // Parse .nuspec for .nupkg files
                packageInfo.NuspecInfo = await ParseNuspecAsync(tempDir);
            }
            else
            {
                // Read build-info.yaml for .pkg files
                var buildInfoPath = Path.Combine(tempDir, "build-info.yaml");
                if (File.Exists(buildInfoPath))
                {
                    var yamlContent = await File.ReadAllTextAsync(buildInfoPath);
                    packageInfo.BuildInfo = _yamlDeserializer.Deserialize<BuildInfo>(yamlContent);
                }
            }

            // Check for scripts - both formats support tools directory
            packageInfo.HasPreInstallScript = File.Exists(Path.Combine(tempDir, "scripts", "preinstall.ps1"));
            packageInfo.HasPostInstallScript = File.Exists(Path.Combine(tempDir, "scripts", "postinstall.ps1"));
            packageInfo.HasChocolateyBeforeInstall = File.Exists(Path.Combine(tempDir, "tools", "chocolateyBeforeInstall.ps1"));
            packageInfo.HasChocolateyInstall = File.Exists(Path.Combine(tempDir, "tools", "chocolateyInstall.ps1"));

            // Get payload files - different structure for each package type
            if (packageType == PackageType.Nupkg)
            {
                // For .nupkg, content files are typically in lib/, content/, or directly in root
                // Exclude metadata directories
                var excludedDirs = new HashSet<string> { "_rels", "package", "tools" };
                var allFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)
                    .Where(f => !excludedDirs.Any(dir => f.Contains($"{Path.DirectorySeparatorChar}{dir}{Path.DirectorySeparatorChar}") 
                                                      || f.StartsWith(Path.Combine(tempDir, dir))))
                    .Where(f => !f.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                    .Select(f => Path.GetRelativePath(tempDir, f))
                    .ToList();
                
                packageInfo.PayloadFiles = allFiles;
            }
            else
            {
                // For .pkg files, payload is in the payload/ directory
                var payloadDir = Path.Combine(tempDir, "payload");
                if (Directory.Exists(payloadDir))
                {
                    packageInfo.PayloadFiles = Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories)
                        .Select(f => Path.GetRelativePath(payloadDir, f))
                        .ToList();
                }
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
            // Get package information first
            var packageInfo = await GetPackageInfoAsync(options.PackagePath);
            
            // Determine if elevation will likely be needed
            var packageInstallLocation = packageInfo.GetInstallLocation();
            var needsElevation = false;
            
            if (packageInfo.IsCopyTypePackage() && !string.IsNullOrEmpty(packageInstallLocation))
            {
                var rootDir = ResolveTargetDirectory(options.Target);
                var resolvedInstallPath = Path.IsPathRooted(packageInstallLocation) 
                    ? packageInstallLocation 
                    : Path.Combine(rootDir, packageInstallLocation.TrimStart('\\', '/'));
                    
                // Check if we need elevation for the install path
                needsElevation = RequiresElevation(resolvedInstallPath);
            }
            else if (packageInfo.HasChocolateyInstall || packageInfo.HasChocolateyBeforeInstall || packageInfo.HasPreInstallScript || packageInfo.HasPostInstallScript)
            {
                // Scripts often need elevation
                needsElevation = true;
            }

            // Check elevation if needed and provide helpful feedback
            if (needsElevation && !IsElevated())
            {
                var targetDesc = packageInfo.IsCopyTypePackage() 
                    ? $"installing to {packageInstallLocation}"
                    : "running Chocolatey scripts";
                    
                result.Message = $"This package requires administrator privileges for {targetDesc}.\n" +
                               "Please run with elevation:\n" +
                               $"  sudo .\\installer.exe --pkg \"{options.PackagePath}\"";
                result.ExitCode = 1;
                return result;
            }

            var packageName = packageInfo.GetPackageName();
            var packageVersion = packageInfo.GetPackageVersion();
            
            // Format package info more professionally
            if (!string.IsNullOrEmpty(packageName) && !string.IsNullOrEmpty(packageVersion))
            {
                logs.Add($"Package: {packageName} v{packageVersion} ({packageInfo.PackageType})");
            }
            else if (!string.IsNullOrEmpty(packageName))
            {
                logs.Add($"Package: {packageName} ({packageInfo.PackageType})");
            }
            else
            {
                logs.Add($"Package: {Path.GetFileNameWithoutExtension(options.PackagePath)} ({packageInfo.PackageType})");
            }
            
            if (IsElevated())
            {
                logs.Add("Privileges: Running with administrator privileges");
            }

            // Resolve target root (like macOS installer)
            var targetRoot = ResolveTargetDirectory(options.Target);
            logs.Add($"Target: {targetRoot}");
            
            // Determine installation behavior
            var isCopyType = packageInfo.IsCopyTypePackage();
            var installLocation = packageInfo.GetInstallLocation();
            
            if (isCopyType && !string.IsNullOrEmpty(installLocation))
            {
                // Copy-type package: install to specific location
                var resolvedInstallPath = Path.IsPathRooted(installLocation) 
                    ? installLocation 
                    : Path.Combine(targetRoot, installLocation.TrimStart('\\', '/'));
                logs.Add($"Mode: Copy-type package -> {resolvedInstallPath}");
            }
            else
            {
                // Installer-type package: scripts handle installation
                logs.Add("Mode: Installer-type package (scripts handle installation)");
            }

            // Run pre-install script
            if (packageInfo.HasPreInstallScript)
            {
                logs.Add("Script: Running scripts/preinstall.ps1...");
                var preResult = await RunPowerShellScriptAsync(
                    Path.Combine(packageInfo.ExtractedPath, "scripts", "preinstall.ps1"), 
                    packageInfo.ExtractedPath, options);
                
                if (preResult.ExitCode != 0)
                {
                    result.Message = $"Pre-install script failed: {preResult.Output}";
                    result.ExitCode = preResult.ExitCode;
                    return result;
                }
                logs.AddRange(preResult.Logs);
                logs.Add("Script: scripts/preinstall.ps1 completed successfully");
            }
            else if (packageInfo.HasChocolateyBeforeInstall)
            {
                logs.Add("Script: Running tools/chocolateyBeforeInstall.ps1...");
                var preResult = await RunPowerShellScriptAsync(
                    Path.Combine(packageInfo.ExtractedPath, "tools", "chocolateyBeforeInstall.ps1"),
                    packageInfo.ExtractedPath, options);
                
                if (preResult.ExitCode != 0)
                {
                    result.Message = $"Chocolatey before-install script failed: {preResult.Output}";
                    result.ExitCode = preResult.ExitCode;
                    return result;
                }
                logs.AddRange(preResult.Logs);
                logs.Add("Script: tools/chocolateyBeforeInstall.ps1 completed successfully");
            }

            // Install payload files based on package type and configuration
            if (isCopyType && !string.IsNullOrEmpty(installLocation))
            {
                // Copy-type: Copy payload files to install_location
                var resolvedInstallPath = Path.IsPathRooted(installLocation) 
                    ? installLocation 
                    : Path.Combine(targetRoot, installLocation.TrimStart('\\', '/'));

                if (packageInfo.PackageType == PackageType.Nupkg)
                {
                    logs.Add("Files: Installing .nupkg content files...");
                    InstallNupkgContent(packageInfo.ExtractedPath, resolvedInstallPath);
                    logs.Add($"Files: Installed {packageInfo.PayloadFiles.Count} files successfully");
                }
                else
                {
                    // .pkg file with payload directory
                    var payloadDir = Path.Combine(packageInfo.ExtractedPath, "payload");
                    if (Directory.Exists(payloadDir))
                    {
                        logs.Add("Files: Installing payload files...");
                        MirrorDirectory(payloadDir, resolvedInstallPath);
                        logs.Add($"Files: Installed {packageInfo.PayloadFiles.Count} files successfully");
                    }
                }
            }
            else
            {
                // Installer-type: Files stay in extraction directory for scripts to use
                logs.Add("Files: Payload remains in temp directory for script processing");
            }

            // Run post-install script
            if (packageInfo.HasPostInstallScript)
            {
                logs.Add("Script: Running scripts/postinstall.ps1...");
                var postResult = await RunPowerShellScriptAsync(
                    Path.Combine(packageInfo.ExtractedPath, "scripts", "postinstall.ps1"),
                    packageInfo.ExtractedPath, options);
                
                if (postResult.ExitCode != 0)
                {
                    result.Message = $"Post-install script failed: {postResult.Output}";
                    result.ExitCode = postResult.ExitCode;
                    return result;
                }
                logs.AddRange(postResult.Logs);
                logs.Add("Script: scripts/postinstall.ps1 completed successfully");
            }
            else if (packageInfo.HasChocolateyInstall)
            {
                logs.Add("Script: Running tools/chocolateyInstall.ps1...");
                var postResult = await RunPowerShellScriptAsync(
                    Path.Combine(packageInfo.ExtractedPath, "tools", "chocolateyInstall.ps1"),
                    packageInfo.ExtractedPath, options);
                
                if (postResult.ExitCode != 0)
                {
                    result.Message = $"Chocolatey install script failed: {postResult.Output}";
                    result.ExitCode = postResult.ExitCode;
                    return result;
                }
                logs.AddRange(postResult.Logs);
                logs.Add("Script: tools/chocolateyInstall.ps1 completed successfully");
            }

            result.Success = true;
            result.Message = "Package installed successfully";
            result.RestartAction = packageInfo.PackageType == PackageType.Nupkg ? null : packageInfo.BuildInfo.RestartAction;
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

    private static bool RequiresElevation(string path)
    {
        // Check if path requires elevation (system directories)
        var normalizedPath = Path.GetFullPath(path).ToUpperInvariant();
        
        return normalizedPath.StartsWith("C:\\PROGRAM FILES") ||
               normalizedPath.StartsWith("C:\\WINDOWS") ||
               normalizedPath.StartsWith("C:\\PROGRAMDATA") ||
               normalizedPath.StartsWith("C:\\SYSTEM") ||
               normalizedPath == "C:\\";
    }

    private static string ResolveTargetDirectory(string target)
    {
        // Target parameter works like macOS installer - it's just the root volume/drive
        // The actual install paths come from package metadata (install_location)
        return target switch
        {
            "/" or "\\" => "C:\\", // Root volume
            "CurrentUserHomeDirectory" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            var t when t.StartsWith("/Volumes/") => t.Replace("/Volumes/", "").TrimEnd('/') + ":\\",
            var t when t.Length == 1 && char.IsLetter(t[0]) => t.ToUpper() + ":\\",
            _ => Path.GetFullPath(target)
        };
    }

    /// <summary>
    /// Extract the chocolatey-helpers.ps1 shim from embedded resources
    /// </summary>
    private async Task ExtractChocolateyHelpersAsync(string targetPath)
    {
        var assembly = typeof(PackageInstaller).Assembly;
        var resourceName = "SbinInstaller.Resources.chocolatey-helpers.ps1";
        
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            // Fallback: read from file system if embedded resource not found
            var helpersPath = Path.Combine(AppContext.BaseDirectory, "Resources", "chocolatey-helpers.ps1");
            if (File.Exists(helpersPath))
            {
                File.Copy(helpersPath, targetPath, true);
                return;
            }
            
            throw new FileNotFoundException($"Could not find chocolatey-helpers.ps1 as embedded resource or file: {resourceName}");
        }
        
        // Read with UTF-8 encoding and write with UTF-8 encoding to preserve characters
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        await File.WriteAllTextAsync(targetPath, content, System.Text.Encoding.UTF8);
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

    private void InstallNupkgContent(string extractedPath, string targetDir)
    {
        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        // Define directories to exclude from installation
        var excludedDirs = new HashSet<string> { "_rels", "package", "tools" };

        // Get all files except those in excluded directories and .nuspec files
        var filesToInstall = Directory.GetFiles(extractedPath, "*", SearchOption.AllDirectories)
            .Where(f => !excludedDirs.Any(dir => f.Contains($"{Path.DirectorySeparatorChar}{dir}{Path.DirectorySeparatorChar}") 
                                              || f.StartsWith(Path.Combine(extractedPath, dir))))
            .Where(f => !f.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var file in filesToInstall)
        {
            var relativePath = Path.GetRelativePath(extractedPath, file);
            var targetFile = Path.Combine(targetDir, relativePath);
            var targetFileDir = Path.GetDirectoryName(targetFile);
            
            if (!string.IsNullOrEmpty(targetFileDir) && !Directory.Exists(targetFileDir))
                Directory.CreateDirectory(targetFileDir);

            File.Copy(file, targetFile, true);
            _logger.LogDebug("Copied .nupkg content: {Source} -> {Target}", file, targetFile);
        }
    }

    private async Task<(int ExitCode, string Output, List<string> Logs)> RunPowerShellScriptAsync(string scriptPath, string workingDirectory, InstallOptions options)
    {
        var logs = new List<string>();
        
        // Check if this is a Chocolatey script (in tools/ directory)
        var isChocolateyScript = scriptPath.Contains($"{Path.DirectorySeparatorChar}tools{Path.DirectorySeparatorChar}");
        
        // Prepare the command - for Chocolatey scripts, inject helper shim
        string arguments;
        if (isChocolateyScript)
        {
            // Extract chocolatey-helpers.ps1 from embedded resources to temp directory
            var helpersPath = Path.Combine(Path.GetTempPath(), $"chocolatey-helpers-{Guid.NewGuid():N}.ps1");
            await ExtractChocolateyHelpersAsync(helpersPath);
            
            // Build command that dots-sources the helpers then runs the script
            arguments = $"-ExecutionPolicy Bypass -Command \"& {{ . '{helpersPath}'; . '{scriptPath}' }}\"";
            
            _logger.LogDebug("Running Chocolatey script with helper shim: {ScriptPath}", scriptPath);
        }
        else
        {
            arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"";
        }
        
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        // Copy all existing environment variables to the process
        // This is critical - if we don't copy the environment, the child process will have NO environment variables
        foreach (System.Collections.DictionaryEntry envVar in Environment.GetEnvironmentVariables())
        {
            var key = envVar.Key?.ToString();
            var value = envVar.Value?.ToString();
            if (!string.IsNullOrEmpty(key) && value != null)
            {
                startInfo.EnvironmentVariables[key] = value;
            }
        }
        
        // Set environment variables based on package type
        if (isChocolateyScript)
        {
            // Chocolatey-style environment variables
            var packageName = Path.GetFileNameWithoutExtension(scriptPath);
            startInfo.EnvironmentVariables["ChocolateyPackageName"] = packageName;
            startInfo.EnvironmentVariables["ChocolateyPackageFolder"] = workingDirectory;
            startInfo.EnvironmentVariables["ChocolateyPackageVersion"] = "unknown";
        }
        else
        {
            // .pkg structure is always: /payload, /scripts, build-info.yaml
            // Always set payload directory path - scripts can rely on this
            string payloadDir = Path.Combine(workingDirectory, "payload");
            startInfo.EnvironmentVariables["payloadRoot"] = payloadDir;
            startInfo.EnvironmentVariables["payloadDir"] = payloadDir;
            startInfo.EnvironmentVariables["PAYLOAD_ROOT"] = payloadDir;
            startInfo.EnvironmentVariables["PAYLOAD_DIR"] = payloadDir;

            // Also set installLocation if available (for copy-type packages)
            // This allows scripts to know where files were copied to
            try 
            {
                // We need to re-parse package info or pass it down to get install location
                // For now, we'll check if we can get it from the options/context
                // Since we don't have easy access to PackageInfo here without refactoring,
                // we'll rely on the fact that for copy-type packages, the files are already at the target
                // But for installer-type, installLocation is empty anyway.
                
                // Ideally we would pass PackageInfo to this method.
                // For this specific request, payloadRoot is the critical one.
            }
            catch {}
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new System.Text.StringBuilder();
        
        process.OutputDataReceived += (sender, e) => {
            if (!string.IsNullOrEmpty(e.Data))
            {
                // Clean up common Unicode artifacts that don't display well in console
                var cleanedOutput = CleanScriptOutput(e.Data);
                output.AppendLine(cleanedOutput);
                logs.Add(cleanedOutput);
                // Only show script output when VerboseR or DumpLog is enabled
                if (options.VerboseR || options.DumpLog)
                {
                    _logger.LogDebug("Script output: {Output}", cleanedOutput);
                }
            }
        };

        process.ErrorDataReceived += (sender, e) => {
            if (!string.IsNullOrEmpty(e.Data))
            {
                var cleanedError = CleanScriptOutput(e.Data);
                output.AppendLine(cleanedError);
                logs.Add($"ERROR: {cleanedError}");
                // Only show script errors when VerboseR or DumpLog is enabled
                if (options.VerboseR || options.DumpLog)
                {
                    _logger.LogDebug("Script error: {Error}", cleanedError);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        await process.WaitForExitAsync();
        
        return (process.ExitCode, output.ToString(), logs);
    }

    /// <summary>
    /// Clean up common Unicode artifacts and formatting issues from script output
    /// </summary>
    private static string CleanScriptOutput(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;
            
        // Replace common Unicode artifacts with ASCII equivalents
        // Use character codes to avoid encoding issues in source
        var cleaned = input;
        
        // Replace various check mark Unicode variations with simple check
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"â[oœ]\.?", "✓");
        
        // Replace arrow variants with simple ASCII arrow
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[â†'âžœâž¡→]", "->");
        
        // Replace bullet point variations
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"â€¢", "•");
        
        // Replace other common artifacts
        cleaned = cleaned.Replace("âœ–", "×");     // Cross mark -> ASCII X
        cleaned = cleaned.Replace("â–¶", ">");     // Play button -> greater than
        
        return cleaned.Trim();
    }
}