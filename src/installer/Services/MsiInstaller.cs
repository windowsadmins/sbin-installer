using Microsoft.Extensions.Logging;
using SbinInstaller.Models;
using WixToolset.Dtf.WindowsInstaller;

namespace SbinInstaller.Services;

/// <summary>
/// Installs MSI packages using DTF (direct msi.dll interop), replacing msiexec.exe.
/// Provides in-process installation with rich progress callbacks and better error info.
/// </summary>
public class MsiInstaller
{
    private readonly ILogger _logger;

    public MsiInstaller(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Install an MSI package natively using the Windows Installer API.
    /// </summary>
    public InstallResult Install(string msiPath, InstallOptions options)
    {
        var result = new InstallResult();
        var logs = new List<string>();

        if (!File.Exists(msiPath))
        {
            result.Message = $"MSI file not found: {msiPath}";
            result.ExitCode = 1;
            return result;
        }

        try
        {
            logs.Add($"Installing MSI: {Path.GetFileName(msiPath)}");

            // Read MSI metadata before installation
            string productName = "Unknown";
            string productVersion = "";
            try
            {
                using var db = new Database(msiPath, DatabaseOpenMode.ReadOnly);
                productName = db.ExecuteScalar(
                    "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductName'")?.ToString() ?? "Unknown";
                productVersion = db.ExecuteScalar(
                    "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'")?.ToString() ?? "";
                logs.Add($"Product: {productName} {productVersion}");
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not pre-read MSI metadata: {Error}", ex.Message);
            }

            // Configure silent UI
            Installer.SetInternalUI(InstallUIOptions.Silent);

            // Set up logging
            var logPath = Path.Combine(
                Path.GetTempPath(),
                $"cimian_msi_{Path.GetFileNameWithoutExtension(msiPath)}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            Installer.EnableLog(InstallLogModes.Verbose, logPath);
            logs.Add($"Log: {logPath}");

            // Set up external UI handler for progress tracking
            var messageHandler = new ExternalUIHandler((messageType, message, buttons, icon, defaultButton) =>
            {
                if (options.Verbose && !string.IsNullOrEmpty(message))
                {
                    _logger.LogDebug("MSI: {Message}", message);
                }
                return MessageResult.None;
            });

            Installer.SetExternalUI(messageHandler,
                InstallLogModes.ActionStart | InstallLogModes.Error |
                InstallLogModes.Warning | InstallLogModes.Progress);

            // Build property string
            var properties = "REBOOT=ReallySuppress ALLUSERS=1 MSIRESTARTMANAGERCONTROL=Disable";

            // Install
            logs.Add("Installing...");
            Installer.InstallProduct(msiPath, properties);

            logs.Add($"Installation completed successfully: {productName} {productVersion}");
            result.Success = true;
            result.Message = $"Installed {productName} {productVersion}";
            result.ExitCode = 0;
        }
        catch (InstallerException ex)
        {
            logs.Add($"MSI installation failed: {ex.Message} (Error code: {ex.ErrorCode})");
            result.Message = $"MSI installation failed: {ex.Message}";
            result.ExitCode = ex.ErrorCode;

            // Check for reboot-required (3010)
            if (ex.ErrorCode == 3010)
            {
                logs.Add("Reboot required to complete installation");
                result.Success = true;
                result.RestartAction = "restart";
                result.ExitCode = 0;
            }
        }
        catch (Exception ex)
        {
            logs.Add($"Unexpected error during MSI installation: {ex.Message}");
            result.Message = $"Installation failed: {ex.Message}";
            result.ExitCode = 1;
        }

        result.Logs = logs;
        return result;
    }

    /// <summary>
    /// Uninstall an MSI by product code.
    /// </summary>
    public InstallResult Uninstall(string productCode)
    {
        var result = new InstallResult();
        var logs = new List<string>();

        try
        {
            logs.Add($"Uninstalling product: {productCode}");

            Installer.SetInternalUI(InstallUIOptions.Silent);
            Installer.ConfigureProduct(productCode, 0, InstallState.Absent,
                "REBOOT=ReallySuppress");

            logs.Add("Uninstall completed successfully");
            result.Success = true;
            result.ExitCode = 0;
        }
        catch (InstallerException ex)
        {
            logs.Add($"MSI uninstall failed: {ex.Message}");
            result.Message = ex.Message;
            result.ExitCode = ex.ErrorCode;

            if (ex.ErrorCode == 3010)
            {
                result.Success = true;
                result.RestartAction = "restart";
                result.ExitCode = 0;
            }
        }

        result.Logs = logs;
        return result;
    }

    /// <summary>
    /// Query installed products by upgrade code using native Windows Installer API.
    /// Replaces the fragile PackGuid + registry walking approach.
    /// </summary>
    public IReadOnlyList<InstalledProduct> FindByUpgradeCode(string upgradeCode)
    {
        var products = new List<InstalledProduct>();

        try
        {
            foreach (var installation in ProductInstallation.GetRelatedProducts(upgradeCode))
            {
                try
                {
                    products.Add(new InstalledProduct
                    {
                        ProductCode = installation.ProductCode,
                        ProductName = installation.ProductName ?? "Unknown",
                        ProductVersion = installation.ProductVersion?.ToString() ?? "",
                        InstallDate = installation.InstallDate.ToString("yyyy-MM-dd"),
                        InstallLocation = installation.InstallLocation ?? ""
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Could not query product {ProductCode}: {Error}",
                        installation.ProductCode, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not enumerate related products for {UpgradeCode}: {Error}",
                upgradeCode, ex.Message);
        }

        return products;
    }
}

public class InstalledProduct
{
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string ProductVersion { get; set; } = string.Empty;
    public string InstallDate { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
}
