using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SbinInstaller.Models;
using SbinInstaller.Services;
using System.CommandLine;
using System.CommandLine.Binding;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SbinInstaller;

/// <summary>
/// Main program entry point with command-line argument parsing
/// Mimics macOS /usr/sbin/installer command structure and behavior
/// </summary>
class Program
{
    [SupportedOSPlatform("windows")]
    static async Task<int> Main(string[] args)
    {
        // Set up dependency injection
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
        services.AddSingleton<PackageInstaller>();
        
        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var installer = serviceProvider.GetRequiredService<PackageInstaller>();

        // Create command-line interface
        var rootCommand = new RootCommand("A lightweight .pkg installer for Windows, inspired by macOS /usr/sbin/installer")
        {
            Name = "installer"
        };

        // Core installation options
        var pkgOption = new Option<string>(
            aliases: new[] { "--pkg", "-pkg" },
            description: "Path to the .pkg file to install")
        {
            IsRequired = false
        };

        var targetOption = new Option<string>(
            aliases: new[] { "--target", "-target" },
            description: "Target directory or device for installation",
            getDefaultValue: () => "/");

        // Information options
        var pkgInfoOption = new Option<bool>(
            aliases: new[] { "--pkginfo", "-pkginfo" },
            description: "Display package information");

        var domInfoOption = new Option<bool>(
            aliases: new[] { "--dominfo", "-dominfo" },
            description: "Display domains that can be installed into");

        var volInfoOption = new Option<bool>(
            aliases: new[] { "--volinfo", "-volinfo" },
            description: "Display volumes that can be installed onto");

        var queryOption = new Option<string?>(
            aliases: new[] { "--query", "-query" },
            description: "Query package metadata flag (e.g., RestartAction)");

        // Output format options
        var verboseOption = new Option<bool>(
            aliases: new[] { "--verbose", "-verbose" },
            description: "Display detailed information");

        var verboseROption = new Option<bool>(
            aliases: new[] { "--verboseR", "-verboseR" },
            description: "Display detailed information with simplified progress output");

        var dumpLogOption = new Option<bool>(
            aliases: new[] { "--dumplog", "-dumplog" },
            description: "Write log information to standard error");

        var plistOption = new Option<bool>(
            aliases: new[] { "--plist", "-plist" },
            description: "Display information in XML plist format");

        // Security options
        var allowUntrustedOption = new Option<bool>(
            aliases: new[] { "--allowUntrusted", "-allowUntrusted" },
            description: "Allow installing packages with untrusted signatures");

        // Version and help
        var versionOption = new Option<bool>(
            aliases: new[] { "--vers", "-vers" },
            description: "Display version information");

        var configOption = new Option<bool>(
            aliases: new[] { "--config", "-config" },
            description: "Display command line parameters");

        // Add all options to root command
        rootCommand.AddOption(pkgOption);
        rootCommand.AddOption(targetOption);
        rootCommand.AddOption(pkgInfoOption);
        rootCommand.AddOption(domInfoOption);
        rootCommand.AddOption(volInfoOption);
        rootCommand.AddOption(queryOption);
        rootCommand.AddOption(verboseOption);
        rootCommand.AddOption(verboseROption);
        rootCommand.AddOption(dumpLogOption);
        rootCommand.AddOption(plistOption);
        rootCommand.AddOption(allowUntrustedOption);
        rootCommand.AddOption(versionOption);
        rootCommand.AddOption(configOption);

        // Set up command handler
        rootCommand.SetHandler(async (options) =>
        {
            try
            {
                await HandleCommandAsync(options, logger, installer);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception occurred");
                Environment.Exit(1);
            }
        },
        new InstallOptionsBinder(
            pkgOption, targetOption, pkgInfoOption, domInfoOption, volInfoOption,
            queryOption, verboseOption, verboseROption, dumpLogOption, plistOption,
            allowUntrustedOption, versionOption, configOption));

        return await rootCommand.InvokeAsync(args);
    }

    [SupportedOSPlatform("windows")]
    static async Task HandleCommandAsync(InstallOptions options, ILogger<Program> logger, PackageInstaller installer)
    {
        // Handle version display
        if (options.ShowVersion)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var buildDate = GetBuildTimestamp();
            Console.WriteLine($"installer version {buildDate}");
            return;
        }

        // Handle config display
        if (options.ShowConfig)
        {
            var config = SerializeConfigForDisplay(options);
            Console.WriteLine(config);
            return;
        }

        // Handle domain info
        if (options.ShowDomInfo)
        {
            ShowDomainInfo(options.PlistFormat);
            return;
        }

        // Handle volume info
        if (options.ShowVolInfo)
        {
            ShowVolumeInfo(options.PlistFormat);
            return;
        }

        // Require package path for other operations
        if (string.IsNullOrEmpty(options.PackagePath))
        {
            logger.LogError("Package path is required. Use --pkg <pathToPackage>");
            Environment.Exit(1);
        }

        // Set logging level based on verbosity
        if (options.Verbose || options.VerboseR || options.DumpLog)
        {
            // This would require reconfiguring the logger, for now just note the preference
            logger.LogInformation("Verbose logging enabled");
        }

        // Handle package info display
        if (options.ShowPkgInfo)
        {
            await ShowPackageInfoAsync(options, installer, logger);
            return;
        }

        // Handle query operations
        if (!string.IsNullOrEmpty(options.QueryFlag))
        {
            await HandleQueryAsync(options, installer, logger);
            return;
        }

        // Default: Install the package
        logger.LogInformation("Installing package: {PackagePath}", options.PackagePath);
        
        var result = await installer.InstallAsync(options);
        
        // Display logs if verbose or dumplog is enabled
        if (options.Verbose || options.VerboseR || options.DumpLog)
        {
            foreach (var log in result.Logs)
            {
                Console.WriteLine(log);
            }
        }

        if (result.Success)
        {
            logger.LogInformation("Installation completed successfully");
            if (!string.IsNullOrEmpty(result.RestartAction) && result.RestartAction != "None")
            {
                logger.LogWarning("Restart required: {RestartAction}", result.RestartAction);
            }
        }
        else
        {
            logger.LogError("Installation failed: {Message}", result.Message);
            Environment.Exit(result.ExitCode);
        }
    }
    
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Configuration display is for debugging, trimming compatibility not critical")]
    private static string SerializeConfigForDisplay(InstallOptions options)
    {
        return JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string GetBuildTimestamp()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null && version.Major > 2000)
        {
            // If version is in timestamp format (YYYY.MM.DD.HHMM), use it directly
            return $"{version.Major}.{version.Minor:D2}.{version.Build:D2}.{version.Revision:D4}";
        }
        else
        {
            // Fallback to current timestamp format
            var now = DateTime.Now;
            return $"{now.Year}.{now.Month:D2}.{now.Day:D2}.{now.Hour:D2}{now.Minute:D2}";
        }
    }

    static void ShowDomainInfo(bool plistFormat)
    {
        var domains = new[] { "LocalSystem", "CurrentUserHomeDirectory", "AdminTools" };
        
        if (plistFormat)
        {
            Console.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            Console.WriteLine("<plist version=\"1.0\">");
            Console.WriteLine("<array>");
            foreach (var domain in domains)
            {
                Console.WriteLine($"    <string>{domain}</string>");
            }
            Console.WriteLine("</array>");
            Console.WriteLine("</plist>");
        }
        else
        {
            Console.WriteLine("Domains available for installation:");
            foreach (var domain in domains)
            {
                Console.WriteLine($"  {domain}");
            }
        }
    }

    static void ShowVolumeInfo(bool plistFormat)
    {
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady);
        
        if (plistFormat)
        {
            Console.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            Console.WriteLine("<plist version=\"1.0\">");
            Console.WriteLine("<array>");
            foreach (var drive in drives)
            {
                Console.WriteLine($"    <string>{drive.Name}</string>");
            }
            Console.WriteLine("</array>");
            Console.WriteLine("</plist>");
        }
        else
        {
            Console.WriteLine("Volumes available for installation:");
            foreach (var drive in drives)
            {
                var freeSpace = drive.AvailableFreeSpace / (1024 * 1024 * 1024);
                var totalSize = drive.TotalSize / (1024 * 1024 * 1024);
                Console.WriteLine($"  {drive.Name} - {freeSpace:F1}GB free of {totalSize:F1}GB ({drive.DriveFormat})");
            }
        }
    }

    static async Task ShowPackageInfoAsync(InstallOptions options, PackageInstaller installer, ILogger<Program> logger)
    {
        try
        {
            var packageInfo = await installer.GetPackageInfoAsync(options.PackagePath);
            
            if (options.PlistFormat)
            {
                // Simple XML representation
                Console.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                Console.WriteLine("<plist version=\"1.0\">");
                Console.WriteLine("<dict>");
                Console.WriteLine($"    <key>Name</key><string>{packageInfo.BuildInfo.Name}</string>");
                Console.WriteLine($"    <key>Version</key><string>{packageInfo.BuildInfo.Version}</string>");
                Console.WriteLine($"    <key>Description</key><string>{packageInfo.BuildInfo.Description}</string>");
                Console.WriteLine($"    <key>RestartAction</key><string>{packageInfo.BuildInfo.RestartAction}</string>");
                Console.WriteLine("</dict>");
                Console.WriteLine("</plist>");
            }
            else
            {
                Console.WriteLine($"Package: {packageInfo.BuildInfo.Name}");
                Console.WriteLine($"Version: {packageInfo.BuildInfo.Version}");
                Console.WriteLine($"Description: {packageInfo.BuildInfo.Description}");
                Console.WriteLine($"Author: {packageInfo.BuildInfo.Author}");
                Console.WriteLine($"License: {packageInfo.BuildInfo.License}");
                Console.WriteLine($"Target: {packageInfo.BuildInfo.Target}");
                Console.WriteLine($"Restart Action: {packageInfo.BuildInfo.RestartAction}");
                Console.WriteLine($"Payload Files: {packageInfo.PayloadFiles.Count}");
                Console.WriteLine($"Has Pre-install Script: {packageInfo.HasPreInstallScript}");
                Console.WriteLine($"Has Post-install Script: {packageInfo.HasPostInstallScript}");
                Console.WriteLine($"Has Chocolatey Scripts: {packageInfo.HasChocolateyBeforeInstall || packageInfo.HasChocolateyInstall}");
            }
            
            // Clean up temp directory
            if (Directory.Exists(packageInfo.ExtractedPath))
            {
                try { Directory.Delete(packageInfo.ExtractedPath, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read package information");
            Environment.Exit(1);
        }
    }

    static async Task HandleQueryAsync(InstallOptions options, PackageInstaller installer, ILogger<Program> logger)
    {
        try
        {
            var packageInfo = await installer.GetPackageInfoAsync(options.PackagePath);
            
            var result = options.QueryFlag?.ToLower() switch
            {
                "restartaction" => packageInfo.BuildInfo.RestartAction,
                "name" => packageInfo.BuildInfo.Name,
                "version" => packageInfo.BuildInfo.Version,
                "description" => packageInfo.BuildInfo.Description,
                "author" => packageInfo.BuildInfo.Author,
                "license" => packageInfo.BuildInfo.License,
                _ => $"Unknown query flag: {options.QueryFlag}"
            };
            
            Console.WriteLine(result);
            
            // Clean up temp directory
            if (Directory.Exists(packageInfo.ExtractedPath))
            {
                try { Directory.Delete(packageInfo.ExtractedPath, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Query failed");
            Environment.Exit(1);
        }
    }
}

/// <summary>
/// Custom binder for InstallOptions to handle System.CommandLine binding
/// </summary>
public class InstallOptionsBinder : BinderBase<InstallOptions>
{
    private readonly Option<string> _pkgOption;
    private readonly Option<string> _targetOption;
    private readonly Option<bool> _pkgInfoOption;
    private readonly Option<bool> _domInfoOption;
    private readonly Option<bool> _volInfoOption;
    private readonly Option<string?> _queryOption;
    private readonly Option<bool> _verboseOption;
    private readonly Option<bool> _verboseROption;
    private readonly Option<bool> _dumpLogOption;
    private readonly Option<bool> _plistOption;
    private readonly Option<bool> _allowUntrustedOption;
    private readonly Option<bool> _versionOption;
    private readonly Option<bool> _configOption;

    public InstallOptionsBinder(
        Option<string> pkgOption, Option<string> targetOption, Option<bool> pkgInfoOption,
        Option<bool> domInfoOption, Option<bool> volInfoOption, Option<string?> queryOption,
        Option<bool> verboseOption, Option<bool> verboseROption, Option<bool> dumpLogOption,
        Option<bool> plistOption, Option<bool> allowUntrustedOption, Option<bool> versionOption,
        Option<bool> configOption)
    {
        _pkgOption = pkgOption;
        _targetOption = targetOption;
        _pkgInfoOption = pkgInfoOption;
        _domInfoOption = domInfoOption;
        _volInfoOption = volInfoOption;
        _queryOption = queryOption;
        _verboseOption = verboseOption;
        _verboseROption = verboseROption;
        _dumpLogOption = dumpLogOption;
        _plistOption = plistOption;
        _allowUntrustedOption = allowUntrustedOption;
        _versionOption = versionOption;
        _configOption = configOption;
    }

    protected override InstallOptions GetBoundValue(BindingContext bindingContext) =>
        new InstallOptions
        {
            PackagePath = bindingContext.ParseResult.GetValueForOption(_pkgOption) ?? string.Empty,
            Target = bindingContext.ParseResult.GetValueForOption(_targetOption) ?? "/",
            ShowPkgInfo = bindingContext.ParseResult.GetValueForOption(_pkgInfoOption),
            ShowDomInfo = bindingContext.ParseResult.GetValueForOption(_domInfoOption),
            ShowVolInfo = bindingContext.ParseResult.GetValueForOption(_volInfoOption),
            QueryFlag = bindingContext.ParseResult.GetValueForOption(_queryOption),
            Verbose = bindingContext.ParseResult.GetValueForOption(_verboseOption),
            VerboseR = bindingContext.ParseResult.GetValueForOption(_verboseROption),
            DumpLog = bindingContext.ParseResult.GetValueForOption(_dumpLogOption),
            PlistFormat = bindingContext.ParseResult.GetValueForOption(_plistOption),
            AllowUntrusted = bindingContext.ParseResult.GetValueForOption(_allowUntrustedOption),
            ShowVersion = bindingContext.ParseResult.GetValueForOption(_versionOption),
            ShowConfig = bindingContext.ParseResult.GetValueForOption(_configOption)
        };
}