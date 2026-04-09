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
    /// Automatically detects and removes conflicting installations at the same location.
    /// </summary>
    public InstallResult Install(string msiPath, InstallOptions options)
    {
        var result = new InstallResult();
        var logs = new List<string>();
        var conflicts = new List<ConflictingProduct>();

        // Resolve to an absolute backslash path. Windows Installer's MsiInstallProduct
        // requires an absolute path and doesn't accept forward slashes or relative
        // paths — passing either produces a generic ERROR_FILE_NOT_FOUND that
        // masks the real cause. Path.GetFullPath normalizes both at once.
        try
        {
            msiPath = Path.GetFullPath(msiPath);
        }
        catch (Exception ex)
        {
            result.Message = $"Invalid MSI path '{msiPath}': {ex.Message}";
            result.ExitCode = 1;
            return result;
        }

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
            string upgradeCode = "";
            string installLocation = "";
            try
            {
                using var db = new Database(msiPath, DatabaseOpenMode.ReadOnly);
                productName = db.ExecuteScalar(
                    "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductName'")?.ToString() ?? "Unknown";
                productVersion = db.ExecuteScalar(
                    "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'")?.ToString() ?? "";
                upgradeCode = db.ExecuteScalar(
                    "SELECT `Value` FROM `Property` WHERE `Property` = 'UpgradeCode'")?.ToString() ?? "";
                // ARPINSTALLLOCATION or custom property for install path
                installLocation = db.ExecuteScalar(
                    "SELECT `Value` FROM `Property` WHERE `Property` = 'ARPINSTALLLOCATION'")?.ToString() ?? "";
                if (string.IsNullOrEmpty(installLocation))
                {
                    // Try to get from Directory table - resolve INSTALLFOLDER
                    installLocation = db.ExecuteScalar(
                        "SELECT `DefaultDir` FROM `Directory` WHERE `Directory` = 'INSTALLFOLDER'")?.ToString() ?? "";
                }
                logs.Add($"Product: {productName} {productVersion}");
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not pre-read MSI metadata: {Error}", ex.Message);
            }

            // Find conflicting installations (don't remove yet -- remove after successful install)
            conflicts = FindConflictingProducts(productName, upgradeCode, logs);

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

            // Post-install: remove conflicting products, then repair to restore any
            // files the old uninstall may have deleted (different component GUIDs)
            if (conflicts.Count > 0)
            {
                logs.Add($"Removing {conflicts.Count} conflicting product(s) after successful install...");
                RemoveProducts(conflicts, logs);

                var repaired = RepairInstallation(msiPath, logs);
                if (!repaired)
                {
                    logs.Add("Warning: repair did not succeed; some files may need manual restoration");
                    _logger.LogWarning("Post-conflict repair failed for {Product}", productName);
                }
            }

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

            // Check for reboot-required (3010) -- install succeeded, just needs reboot
            if (ex.ErrorCode == 3010)
            {
                logs.Add("Reboot required to complete installation");
                result.Success = true;
                result.RestartAction = "restart";
                result.ExitCode = 0;

                // Still handle conflicts on 3010 (install succeeded)
                if (conflicts.Count > 0)
                {
                    logs.Add($"Removing {conflicts.Count} conflicting product(s) after install...");
                    RemoveProducts(conflicts, logs);
                    RepairInstallation(msiPath, logs);
                }
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

            // If the product is already absent (e.g. Windows Installer's own
            // RemoveExistingProducts already removed it during a major upgrade),
            // skip the ConfigureProduct call so we don't surface a 1605 error.
            try
            {
                var existing = new ProductInstallation(productCode);
                if (!existing.IsInstalled)
                {
                    logs.Add($"Product {productCode} is already uninstalled");
                    result.Success = true;
                    result.ExitCode = 0;
                    result.Logs = logs;
                    return result;
                }
            }
            catch (ArgumentException)
            {
                // ProductInstallation throws if product code isn't recognized — treat as absent
                logs.Add($"Product {productCode} not found (already uninstalled)");
                result.Success = true;
                result.ExitCode = 0;
                result.Logs = logs;
                return result;
            }

            Installer.SetInternalUI(InstallUIOptions.Silent);
            Installer.ConfigureProduct(productCode, 0, InstallState.Absent,
                "REBOOT=ReallySuppress");

            logs.Add("Uninstall completed successfully");
            result.Success = true;
            result.ExitCode = 0;
        }
        catch (InstallerException ex)
        {
            // ERROR_UNKNOWN_PRODUCT (1605): product wasn't installed in the first place.
            // This can happen if a major upgrade already removed it between our
            // ProductInstallation check and the ConfigureProduct call.
            if (ex.ErrorCode == 1605)
            {
                logs.Add($"Product {productCode} is no longer installed — treating as removed");
                result.Success = true;
                result.ExitCode = 0;
                result.Logs = logs;
                return result;
            }

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
    /// Find any existing installations of the same product that would conflict.
    /// Searches all installed MSI products by name to find conflicting
    /// installations regardless of UpgradeCode (handles WiX-to-cimipkg transitions).
    /// Does NOT remove them -- caller decides when to remove.
    /// </summary>
    private List<ConflictingProduct> FindConflictingProducts(string productName, string newUpgradeCode, List<string> logs)
    {
        var conflicts = new List<ConflictingProduct>();

        if (string.IsNullOrEmpty(productName) || productName == "Unknown")
            return conflicts;

        // Build a set of products that share our new UpgradeCode. Windows Installer's
        // own RemoveExistingProducts action will clean those up during a major upgrade,
        // so we must exclude them from our cross-product conflict list — otherwise we
        // end up calling ConfigureProduct on an already-removed product after the
        // install completes, which surfaces as a misleading 1605 error.
        var sameUpgradeCodeProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(newUpgradeCode))
        {
            try
            {
                var upgradeCodeForQuery = newUpgradeCode.StartsWith("{")
                    ? newUpgradeCode
                    : "{" + newUpgradeCode + "}";
                foreach (var related in ProductInstallation.GetRelatedProducts(upgradeCodeForQuery))
                {
                    sameUpgradeCodeProducts.Add(related.ProductCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("GetRelatedProducts failed for {UpgradeCode}: {Error}",
                    newUpgradeCode, ex.Message);
            }
        }

        try
        {
            foreach (var product in ProductInstallation.AllProducts)
            {
                try
                {
                    var installedName = product.ProductName;

                    if (string.IsNullOrEmpty(installedName))
                        continue;

                    // Skip products sharing our UpgradeCode — Windows Installer handles them
                    if (sameUpgradeCodeProducts.Contains(product.ProductCode))
                        continue;

                    // Match by product name (handles cross-UpgradeCode conflicts like
                    // the WiX→cimipkg transition where the product name stays the same
                    // but the UpgradeCode changed)
                    if (string.Equals(installedName, productName, StringComparison.OrdinalIgnoreCase) ||
                        installedName.StartsWith(productName, StringComparison.OrdinalIgnoreCase))
                    {
                        logs.Add($"Found conflicting installation: {installedName} ({product.ProductCode})");
                        _logger.LogInformation("Found conflicting product: {Name} ({Code})",
                            installedName, product.ProductCode);
                        conflicts.Add(new ConflictingProduct(product.ProductCode, installedName));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Could not inspect product: {Error}", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not enumerate installed products: {Error}", ex.Message);
        }

        return conflicts;
    }

    /// <summary>
    /// Remove a list of conflicting products by their product codes.
    /// </summary>
    private void RemoveProducts(List<ConflictingProduct> conflicts, List<string> logs)
    {
        foreach (var conflict in conflicts)
        {
            _logger.LogInformation("Removing conflicting product: {Name} ({Code})",
                conflict.ProductName, conflict.ProductCode);

            var uninstallResult = Uninstall(conflict.ProductCode);
            if (uninstallResult.Success)
            {
                logs.Add($"Removed: {conflict.ProductName}");
            }
            else
            {
                logs.Add($"Warning: could not remove {conflict.ProductName}: {uninstallResult.Message}");
                _logger.LogWarning("Failed to remove conflicting product {Name}: {Error}",
                    conflict.ProductName, uninstallResult.Message);
            }
        }
    }

    /// <summary>
    /// Repair a newly installed MSI to restore files that may have been removed
    /// when conflicting products (with different component GUIDs) were uninstalled.
    /// </summary>
    private bool RepairInstallation(string msiPath, List<string> logs)
    {
        try
        {
            logs.Add("Repairing installation to restore files removed during conflict cleanup...");
            _logger.LogInformation("Running repair on {Msi} after conflict removal", Path.GetFileName(msiPath));

            Installer.SetInternalUI(InstallUIOptions.Silent);
            Installer.InstallProduct(msiPath,
                "REINSTALL=ALL REINSTALLMODE=amus REBOOT=ReallySuppress ALLUSERS=1");

            logs.Add("Repair completed successfully");
            return true;
        }
        catch (InstallerException ex)
        {
            if (ex.ErrorCode == 3010)
            {
                logs.Add("Repair completed (reboot required)");
                return true;
            }

            logs.Add($"Warning: repair failed ({ex.Message}), installation may have missing files");
            _logger.LogWarning("Repair failed for {Msi}: {Error} (code {Code})",
                Path.GetFileName(msiPath), ex.Message, ex.ErrorCode);
            return false;
        }
        catch (Exception ex)
        {
            logs.Add($"Warning: repair failed unexpectedly ({ex.Message})");
            _logger.LogWarning("Unexpected repair failure for {Msi}: {Error}",
                Path.GetFileName(msiPath), ex.Message);
            return false;
        }
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

/// <summary>
/// Represents a conflicting product found during pre-install scan.
/// </summary>
public record ConflictingProduct(string ProductCode, string ProductName);
