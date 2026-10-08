using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SbinInstaller.Services.Msi;

/// <summary>
/// A Windows Installer call failed. <see cref="ErrorCode"/> is the Win32 error
/// it returned (1603, 1618, 1605, ...).
/// </summary>
public sealed class MsiException : Exception
{
    public MsiException(int errorCode)
        : base(new Win32Exception(errorCode).Message)
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }
}

/// <summary>
/// The Windows Installer calls sbin-installer makes, over msi.dll directly.
///
/// Install and configure calls follow DTF's result rules, which the callers were
/// written against: 0, 3010 (reboot required) and 1641 (reboot initiated) return
/// normally, and any other code throws <see cref="MsiException"/>.
/// </summary>
public static class WindowsInstaller
{
    private const uint INSTALLUILEVEL_NONE = 2;
    private const uint INSTALLLOGATTRIBUTES_FLUSHEACHLINE = 2;
    private const uint ERROR_SUCCESS_REBOOT_INITIATED = 1641;
    private const uint ERROR_SUCCESS_REBOOT_REQUIRED = 3010;
    private const uint ERROR_FILE_INVALID = 1006;
    private const int INSTALLSTATE_ABSENT = 2;
    private const int INSTALLSTATE_DEFAULT = 5;

    /// <summary>INSTALLLOGMODE_VERBOSE: the log-only verbose bit, as DTF's InstallLogModes.Verbose.</summary>
    public const uint LogModeVerbose = 1u << 12;

    /// <summary>Message filter for <see cref="SetExternalUI"/>: errors, warnings, action starts and progress.</summary>
    public const uint UiFilterErrorWarningActionProgress = (1u << 1) | (1u << 2) | (1u << 8) | (1u << 10);

    /// <summary>The handler Windows Installer calls with each message; return 0 to let it carry on.</summary>
    public delegate int ExternalUIHandler(IntPtr context, uint messageType, string? message);

    // Windows Installer keeps calling the handler for the rest of the process, so
    // the delegate must stay reachable or the GC collects it out from under msi.dll.
    private static ExternalUIHandler? s_externalUI;

    /// <summary>Turns off Windows Installer's own UI for installs from this process.</summary>
    public static void SetInternalUISilent() => MsiSetInternalUI(INSTALLUILEVEL_NONE, IntPtr.Zero);

    /// <summary>Writes a verbose log of every install from this process to <paramref name="logFile"/>, flushed per line.</summary>
    public static void EnableVerboseLog(string logFile)
    {
        var result = MsiEnableLogW(LogModeVerbose, logFile, INSTALLLOGATTRIBUTES_FLUSHEACHLINE);
        if (result != 0 && result != ERROR_FILE_INVALID)
        {
            throw new MsiException((int)result);
        }
    }

    /// <summary>Routes the messages in <paramref name="filter"/> to <paramref name="handler"/>.</summary>
    public static void SetExternalUI(ExternalUIHandler handler, uint filter)
    {
        s_externalUI = handler;
        MsiSetExternalUIW(handler, filter, IntPtr.Zero);
    }

    public static void InstallProduct(string packagePath, string commandLine) =>
        CheckInstallResult(MsiInstallProductW(packagePath, commandLine));

    /// <summary>Uninstalls <paramref name="productCode"/> (the whole product, set to absent).</summary>
    public static void RemoveProduct(string productCode, string commandLine) =>
        CheckInstallResult(MsiConfigureProductExW(productCode, 0, INSTALLSTATE_ABSENT, commandLine));

    /// <summary>True when Windows Installer reports the product installed (INSTALLSTATE_DEFAULT).</summary>
    public static bool IsInstalled(string productCode) =>
        MsiQueryProductStateW(productCode) == INSTALLSTATE_DEFAULT;

    /// <summary>
    /// Installed products that share <paramref name="upgradeCode"/>. Enumerated lazily;
    /// throws <see cref="MsiException"/> if the enumeration fails.
    /// </summary>
    public static IEnumerable<InstalledMsiProduct> GetRelatedProducts(string upgradeCode)
    {
        var buffer = new StringBuilder(39);
        for (uint i = 0; ; i++)
        {
            var result = MsiEnumRelatedProductsW(upgradeCode, 0, i, buffer);
            if (result == ERROR_NO_MORE_ITEMS) yield break;
            if (result != 0) throw new MsiException((int)result);
            yield return new InstalledMsiProduct(buffer.ToString(), null, null);
        }
    }

    /// <summary>
    /// Every product installed per-machine or for the current user, with the context
    /// it was found in. Enumerated lazily; throws <see cref="MsiException"/> on failure.
    /// </summary>
    public static IEnumerable<InstalledMsiProduct> GetAllProducts()
    {
        var code = new StringBuilder(39);
        var sid = new StringBuilder(40);
        for (uint i = 0; ; i++)
        {
            var sidLength = (uint)sid.Capacity;
            var result = MsiEnumProductsExW(null, null, MSIINSTALLCONTEXT_ALL, i, code, out var context, sid, ref sidLength);
            if (result == ERROR_MORE_DATA)
            {
                sid = new StringBuilder((int)++sidLength);
                result = MsiEnumProductsExW(null, null, MSIINSTALLCONTEXT_ALL, i, code, out context, sid, ref sidLength);
            }
            if (result == ERROR_NO_MORE_ITEMS) yield break;
            if (result != 0) throw new MsiException((int)result);
            var userSid = sid.ToString();
            yield return new InstalledMsiProduct(code.ToString(), userSid.Length == 0 ? null : userSid, context);
        }
    }

    private static void CheckInstallResult(uint result)
    {
        if (result is 0 or ERROR_SUCCESS_REBOOT_REQUIRED or ERROR_SUCCESS_REBOOT_INITIATED)
        {
            return;
        }
        throw new MsiException((int)result);
    }

    internal const uint ERROR_MORE_DATA = 234;
    internal const uint ERROR_NO_MORE_ITEMS = 259;
    private const uint MSIINSTALLCONTEXT_ALL = 7;

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiSetInternalUI(uint uiLevel, IntPtr window);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnableLogW(uint logMode, string logFile, uint logAttributes);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr MsiSetExternalUIW(ExternalUIHandler handler, uint messageFilter, IntPtr context);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiInstallProductW(string packagePath, string commandLine);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiConfigureProductExW(string product, int installLevel, int installState, string commandLine);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MsiQueryProductStateW(string product);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumRelatedProductsW(string upgradeCode, uint reserved, uint index, StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumProductsExW(string? productCode, string? userSid, uint context, uint index,
        StringBuilder installedProductCode, out uint installedContext, StringBuilder sid, ref uint sidLength);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint MsiGetProductInfoW(string product, string property, StringBuilder value, ref uint length);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint MsiGetProductInfoExW(string product, string? userSid, uint context, string property, StringBuilder value, ref uint length);
}

/// <summary>
/// An installed product. Property reads follow DTF's ProductInstallation: a
/// property that cannot be read is null.
/// </summary>
public sealed class InstalledMsiProduct
{
    private readonly string? _userSid;
    private readonly uint? _context;

    internal InstalledMsiProduct(string productCode, string? userSid, uint? context)
    {
        ProductCode = productCode;
        _userSid = userSid;
        _context = context;
    }

    public string ProductCode { get; }

    /// <summary>The product's name as installed (INSTALLPROPERTY_INSTALLEDPRODUCTNAME).</summary>
    public string? ProductName => this["InstalledProductName"];

    public string? InstallLocation => this["InstallLocation"];

    /// <summary>VersionString, normalised the way DTF did; null when unreadable.</summary>
    public Version? ProductVersion => ParseVersion(this["VersionString"]);

    /// <summary>
    /// InstallDate (yyyyMMdd). As in DTF, a malformed date reads as
    /// <see cref="DateTime.MinValue"/> and a missing one throws.
    /// </summary>
    public DateTime InstallDate
    {
        get
        {
            try
            {
                return DateTime.ParseExact(this["InstallDate"]!, "yyyyMMdd", CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return DateTime.MinValue;
            }
        }
    }

    public string? this[string property]
    {
        get
        {
            uint length = 40;
            var buffer = new StringBuilder((int)length);
            uint result;
            if (_context is { } context)
            {
                result = WindowsInstaller.MsiGetProductInfoExW(ProductCode, _userSid, context, property, buffer, ref length);
                if (result == WindowsInstaller.ERROR_MORE_DATA)
                {
                    buffer = new StringBuilder((int)++length);
                    result = WindowsInstaller.MsiGetProductInfoExW(ProductCode, _userSid, context, property, buffer, ref length);
                }
            }
            else
            {
                result = WindowsInstaller.MsiGetProductInfoW(ProductCode, property, buffer, ref length);
                if (result == WindowsInstaller.ERROR_MORE_DATA)
                {
                    buffer = new StringBuilder((int)++length);
                    result = WindowsInstaller.MsiGetProductInfoW(ProductCode, property, buffer, ref length);
                }
            }
            return result == 0 ? buffer.ToString() : null;
        }
    }

    /// <summary>DTF's ProductInstallation.ParseVersion, kept so reported versions do not change.</summary>
    internal static Version? ParseVersion(string? version)
    {
        if (version == null) return null;

        var dots = 0;
        for (var i = 0; i < version.Length; i++)
        {
            var c = version[i];
            if (c == '.')
            {
                dots++;
            }
            else if (!char.IsDigit(c))
            {
                version = version[..i];
                break;
            }
        }

        if (version.Length == 0) return null;
        if (dots == 0) version += ".0";
        else if (dots > 3) version = string.Join(".", version.Split('.'), 0, 4);

        try
        {
            return new Version(version);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
