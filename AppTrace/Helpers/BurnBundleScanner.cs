using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;

public record InstalledBundlePackage(
    string BundleName,
    string BundleProviderKey,
    string PackageProviderKey,
    string PackageDisplayName,
    string PackageVersion,
    string InstallLocation,
    string ProductCodeOrId
);

public static class BurnInstalledPackagesFinder
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string DependenciesPath = @"SOFTWARE\Classes\Installer\Dependencies";

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiGetProductInfo(
        string szProduct,
        string szProperty,
        StringBuilder? lpValueBuf,
        ref int pcchValueBuf);

    public static List<InstalledBundlePackage> GetAllInstalledBundlePackages()
    {
        var results = new List<InstalledBundlePackage>();

        // 1. Alle installierten Burn-Bundles (ProviderKey -> BundleName) sammeln
        var bundles = GetInstalledBurnBundles();

        // 2. Paket-Metadaten inkl. InstallLocation aus Uninstall-Schlüsseln laden
        var installedProducts = GetInstalledUninstallEntries();

        // 3. Burn-Dependencies prüfen, welche Pakete zu welchem Bundle gehören
        var scopes = new[]
        {
            (Hive: RegistryHive.LocalMachine, View: RegistryView.Registry64),
            (Hive: RegistryHive.LocalMachine, View: RegistryView.Registry32),
            (Hive: RegistryHive.CurrentUser,  View: RegistryView.Registry64)
        };

        foreach (var (hive, view) in scopes)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var depRoot = baseKey.OpenSubKey(DependenciesPath);
            if (depRoot == null) continue;

            foreach (var packageKeyName in depRoot.GetSubKeyNames())
            {
                using var packageKey = depRoot.OpenSubKey(packageKeyName);
                using var dependentsKey = packageKey?.OpenSubKey("Dependents");
                if (dependentsKey == null) continue;

                foreach (var dependentId in dependentsKey.GetSubKeyNames())
                {
                    if (!bundles.TryGetValue(dependentId, out var bundleName))
                        continue;

                    installedProducts.TryGetValue(packageKeyName, out var productInfo);

                    var displayName = productInfo.DisplayName
                        ?? packageKey?.GetValue("DisplayName") as string
                        ?? packageKeyName;

                    var version = productInfo.Version
                        ?? packageKey?.GetValue("Version") as string
                        ?? string.Empty;

                    // Installationspfad aus Registry oder als Fallback über Windows Installer API holen
                    var installLocation = !string.IsNullOrWhiteSpace(productInfo.InstallLocation)
                        ? productInfo.InstallLocation
                        : GetMsiInstallLocationFallback(packageKeyName);

                    results.Add(new InstalledBundlePackage(
                        BundleName: bundleName,
                        BundleProviderKey: dependentId,
                        PackageProviderKey: packageKeyName,
                        PackageDisplayName: displayName,
                        PackageVersion: version,
                        InstallLocation: installLocation,
                        ProductCodeOrId: packageKeyName
                    ));
                }
            }
        }

        return results
            .Distinct()
            .OrderBy(p => p.BundleName)
            .ThenBy(p => p.PackageDisplayName)
            .ToList();
    }

    private static string GetMsiInstallLocationFallback(string productCode)
    {
        // Prüfen, ob es eine gültige GUID (MSI ProductCode) ist
        if (!Guid.TryParse(productCode, out _))
            return string.Empty;

        int len = 512;
        var sb = new StringBuilder(len);
        int result = MsiGetProductInfo(productCode, "InstallLocation", sb, ref len);

        return result == 0 ? sb.ToString() : string.Empty;
    }

    private static Dictionary<string, string> GetInstalledBurnBundles()
    {
        var bundles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targets = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser,  RegistryView.Registry64)
        };

        foreach (var (hive, view) in targets)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallPath);
            if (uninstallKey == null) continue;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var subKey = uninstallKey.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                var providerKey = subKey.GetValue("BundleProviderKey") as string;
                if (string.IsNullOrEmpty(providerKey)) continue;

                var displayName = subKey.GetValue("DisplayName") as string ?? subKeyName;
                bundles[providerKey] = displayName;
            }
        }

        return bundles;
    }

    private static Dictionary<string, (string? DisplayName, string? Version, string? InstallLocation)> GetInstalledUninstallEntries()
    {
        var entries = new Dictionary<string, (string?, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        var targets = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser,  RegistryView.Registry64)
        };

        foreach (var (hive, view) in targets)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallPath);
            if (uninstallKey == null) continue;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var subKey = uninstallKey.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                if (subKey.GetValue("BundleVersion") != null) continue;

                var name = subKey.GetValue("DisplayName") as string;
                var version = subKey.GetValue("DisplayVersion") as string;
                var installLocation = subKey.GetValue("InstallLocation") as string;

                if (!string.IsNullOrEmpty(name))
                {
                    entries[subKeyName] = (name, version, installLocation);
                }
            }
        }

        return entries;
    }
}
