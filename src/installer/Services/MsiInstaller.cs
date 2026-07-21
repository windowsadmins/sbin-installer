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

        string logPath = string.Empty;

        try
        {
            logs.Add($"Installing MSI: {Path.GetFileName(msiPath)}");

            // Read MSI metadata before installation
            string productName = "Unknown";
            string productVersion = "";
            string upgradeCode = "";
            string productCode = "";
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
                productCode = db.ExecuteScalar(
                    "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductCode'")?.ToString() ?? "";
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

            // Configure silent UI + verbose logging FIRST, so the uninstall activity from
            // removing conflicting products below lands in the same %TEMP%\cimian_msi_*.log.
            Installer.SetInternalUI(InstallUIOptions.Silent);
            logPath = Path.Combine(
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

            // Find conflicting installations and remove them BEFORE installing, so the new
            // package's files are laid down LAST (nothing a conflict's uninstall touched is
            // left missing) and we never run a REINSTALL repair pass -- that trips Windows
            // SecureRepair and aborts with 1603/1625 on managed clients. The repair engine is
            // deliberately never invoked.
            //
            // Self-sufficient by design: removes BOTH other installed versions sharing this
            // UpgradeCode (any version of this product, except the exact ProductCode we're
            // about to install -- no version gating, so a deliberate downgrade still installs,
            // matching cimipkg's most-recent-install-wins supersede) AND different-UpgradeCode
            // same-name products (e.g. a WiX-built predecessor). It does NOT rely on the MSI
            // carrying its own Upgrade table -- when the MSI also supersedes (cimipkg), the
            // in-MSI RemoveExistingProducts simply finds nothing left to do. installer and
            // cimipkg each work standalone; together they overlap harmlessly.
            conflicts = FindConflictingProducts(productName, upgradeCode, productCode, logs);
            if (conflicts.Count > 0)
            {
                logs.Add($"Removing {conflicts.Count} conflicting product(s) before install...");
                RemoveProducts(conflicts, logs);
            }

            // Build property string. Deliberately NO REINSTALL/REINSTALLMODE: a plain install
            // lets the cimipkg MSI run its own sequence (scripts every install via custom
            // actions, payload overwrite via synthetic File.Version, supersede via the Upgrade
            // table) without ever invoking the Windows Installer repair/SecureRepair path.
            var properties = "REBOOT=ReallySuppress ALLUSERS=1 MSIRESTARTMANAGERCONTROL=Disable";

            // Serialize behind any other MSI transaction (Intune, another agent, a
            // user-launched installer). Windows Installer allows one execute sequence
            // at a time machine-wide; starting anyway fails with 1618. Waiting here
            // makes concurrent installers a queue, not an error.
            if (!WaitForWindowsInstallerIdle(600, logs))
            {
                logs.Add("Windows Installer still busy after 10 minutes; attempting install anyway");
            }

            // Install (conflicts, if any, were already removed above).
            logs.Add("Installing...");
            try
            {
                Installer.InstallProduct(msiPath, properties);
            }
            catch (InstallerException iex) when (iex.ErrorCode == 1618)
            {
                // Lost a race with an install that started after our idle check.
                // Wait for it and retry once before giving up.
                logs.Add("Another installation started concurrently (1618); waiting to retry once...");
                WaitForWindowsInstallerIdle(600, logs);
                Installer.InstallProduct(msiPath, properties);
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
                // Overwrite the failure message set above -- this is a successful install.
                result.Message = "Installation completed successfully (reboot required)";
                result.RestartAction = "restart";
                result.ExitCode = 0;
                // Conflicts (if any) were already removed before the install above.
            }
            else
            {
                // A bare 1603 is undiagnosable from the caller's log. The verbose MSI
                // log enabled above holds the actual cause -- the failing action, any
                // LaunchCondition text, and the CimianPre/Postinstall script output
                // that cimipkg CAs stream in via Session.Log. Surface those lines here
                // so the managing client's log answers "why" without a per-box hunt.
                var diagnostics = ExtractFailureDiagnostics(logPath);
                if (diagnostics.Count > 0)
                {
                    logs.Add("--- failure diagnostics from MSI log ---");
                    logs.AddRange(diagnostics);
                    result.Message = $"MSI installation failed: {ex.Message} | {diagnostics[^1]}";
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
    /// Waits until no MSI execute sequence is running machine-wide, by probing the
    /// Global\_MSIExecute mutex the Windows Installer service holds for the duration
    /// of any install. Best-effort: if the mutex can't be opened or inspected the
    /// installer is treated as idle so a permissions quirk never blocks an install.
    /// Returns true once idle, false if the wait timed out.
    /// </summary>
    private bool WaitForWindowsInstallerIdle(int maxWaitSeconds, List<string> logs)
    {
        var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        var logged = false;
        while (true)
        {
            try
            {
                if (!System.Threading.Mutex.TryOpenExisting(@"Global\_MSIExecute", out var mutex))
                {
                    return true; // mutex absent => no install executing
                }
                using (mutex)
                {
                    var acquired = false;
                    try
                    {
                        acquired = mutex.WaitOne(0);
                        if (acquired) return true;
                    }
                    catch (System.Threading.AbandonedMutexException)
                    {
                        return true; // previous owner died; installer is idle
                    }
                    finally
                    {
                        if (acquired) mutex.ReleaseMutex();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Could not inspect _MSIExecute mutex: {Error}", ex.Message);
                return true;
            }

            if (DateTime.UtcNow >= deadline) return false;
            if (!logged)
            {
                logs.Add("Windows Installer busy (another MSI transaction active) - waiting before installing");
                _logger.LogInformation("Windows Installer busy - waiting for the active MSI transaction to finish");
                logged = true;
            }
            System.Threading.Thread.Sleep(2000);
        }
    }

    /// <summary>
    /// Pulls the lines that explain a failed install out of the verbose MSI log:
    /// custom-action script output (the "CimianXxx | ..." lines cimipkg CAs write
    /// via Session.Log), explicit script-failure markers, LaunchCondition text,
    /// and the failing action ("Return value 3") with the action that produced it.
    /// Returns at most a few dozen lines; never throws.
    /// </summary>
    private List<string> ExtractFailureDiagnostics(string logPath)
    {
        var diagnostics = new List<string>();
        const int maxLines = 40;

        try
        {
            if (!File.Exists(logPath))
            {
                return diagnostics;
            }

            // The Windows Installer engine may still hold the log open — share fully.
            using var stream = new FileStream(
                logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            string? lastActionStart = null;
            string? line;
            while ((line = reader.ReadLine()) != null && diagnostics.Count < maxLines)
            {
                if (line.Contains("Action start", StringComparison.OrdinalIgnoreCase))
                {
                    lastActionStart = line;
                    continue;
                }

                // Custom-action script output and failure markers (cimipkg CAs).
                if (line.Contains("CimianPreinstall", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("CimianPostinstall", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("CimianUninstall", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(line.Trim());
                    continue;
                }

                // A LaunchCondition rejection logs its Description verbatim.
                if (line.Contains("pending Windows reboot", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(line.Trim());
                    continue;
                }

                // The action that aborted the install.
                if (line.Contains("Return value 3", StringComparison.OrdinalIgnoreCase))
                {
                    if (lastActionStart != null)
                    {
                        diagnostics.Add(lastActionStart.Trim());
                    }
                    diagnostics.Add(line.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not extract MSI failure diagnostics: {Error}", ex.Message);
        }

        return diagnostics;
    }

    /// <summary>
    /// Uninstall an MSI by product code.
    /// </summary>
    public InstallResult Uninstall(string productCode)
    {
        var result = new InstallResult();
        var logs = new List<string>();

        // Reject malformed product codes up-front so a later "already uninstalled"
        // shortcut can't mask a typo or garbage input. Windows Installer product
        // codes are braced GUIDs: {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}.
        if (string.IsNullOrWhiteSpace(productCode) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                productCode,
                @"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$"))
        {
            result.Message = $"Invalid product code format: '{productCode}' (expected braced GUID)";
            result.ExitCode = 1;
            result.Logs = logs;
            return result;
        }

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
                // Valid-format GUID that Windows Installer doesn't recognize — treat as absent
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
    /// Find existing installations that conflict with the MSI we're about to install:
    ///   - other installed versions sharing this UpgradeCode (any version of this same
    ///     product -- not version-compared), and
    ///   - different-UpgradeCode products with the same (or prefix) ProductName
    ///     (e.g. a WiX-built predecessor whose UpgradeCode changed).
    /// The exact ProductCode we're installing is never listed -- a same-version re-run is
    /// left to the install's own maintenance, not uninstalled+reinstalled. Does NOT remove
    /// them; the caller removes them BEFORE installing. Handling same-UpgradeCode ourselves
    /// is what makes installer self-sufficient: it never relies on the MSI carrying its own
    /// Upgrade table. When the MSI does (cimipkg), the two simply overlap harmlessly.
    /// </summary>
    private List<ConflictingProduct> FindConflictingProducts(string productName, string newUpgradeCode, string newProductCode, List<string> logs)
    {
        var conflicts = new List<ConflictingProduct>();
        // Never touch the exact product we're about to install (same-version re-run).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(newProductCode))
            seen.Add(newProductCode);

        // 1) Other installed versions sharing this UpgradeCode -- the same product, any
        //    version (we do NOT version-compare; matching cimipkg's most-recent-install-wins
        //    supersede, a deliberate downgrade still installs). Remove them ourselves rather
        //    than relying on the MSI's own RemoveExistingProducts (which only runs if the MSI
        //    carries an Upgrade table). Because the caller removes them BEFORE the install, an
        //    in-MSI RemoveExistingProducts (if present) just finds nothing left -- no
        //    double-remove, no misleading 1605.
        if (!string.IsNullOrEmpty(newUpgradeCode))
        {
            try
            {
                var upgradeCodeForQuery = newUpgradeCode.StartsWith("{")
                    ? newUpgradeCode
                    : "{" + newUpgradeCode + "}";
                foreach (var related in ProductInstallation.GetRelatedProducts(upgradeCodeForQuery))
                {
                    if (!seen.Add(related.ProductCode))
                        continue;
                    string name;
                    try { name = string.IsNullOrEmpty(related.ProductName) ? productName : related.ProductName; }
                    catch { name = productName; }
                    logs.Add($"Found related product (same UpgradeCode): {name} ({related.ProductCode})");
                    _logger.LogInformation("Related product (same UpgradeCode): {Name} ({Code})",
                        name, related.ProductCode);
                    conflicts.Add(new ConflictingProduct(related.ProductCode, name));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("GetRelatedProducts failed for {UpgradeCode}: {Error}",
                    newUpgradeCode, ex.Message);
            }
        }

        // 2) Different-UpgradeCode products with a matching name (WiX->cimipkg transitions,
        //    where the product name stays the same but the UpgradeCode changed).
        if (!string.IsNullOrEmpty(productName) && productName != "Unknown")
        {
            try
            {
                foreach (var product in ProductInstallation.AllProducts)
                {
                    try
                    {
                        if (seen.Contains(product.ProductCode))
                            continue;

                        var installedName = product.ProductName;
                        if (string.IsNullOrEmpty(installedName))
                            continue;

                        if (string.Equals(installedName, productName, StringComparison.OrdinalIgnoreCase) ||
                            installedName.StartsWith(productName, StringComparison.OrdinalIgnoreCase))
                        {
                            seen.Add(product.ProductCode);
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
