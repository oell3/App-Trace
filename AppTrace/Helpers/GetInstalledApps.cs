using AppTrace.Models;
using Microsoft.Win32;
using System.Diagnostics;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Policy;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Management.Deployment;

namespace AppTrace.Helpers
{
    public class InstalledAppsReader
    {

        private HistoryResult? _history;

        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiGetProductInfo(string szProduct, string szAttribute, [Out] StringBuilder lpValueBuf, ref uint pcchValueBuf);

        private string? GetMsiInstallLocation(string productCode)
        {
            // MSI ProductCodes sind immer GUIDs in geschweiften Klammern: {XXXXXXXX-XXXX-...}
            if (!productCode.StartsWith("{") || !productCode.EndsWith("}"))
                return null;

            uint len = 512;
            var sb = new StringBuilder((int)len);
            uint result = MsiGetProductInfo(productCode, "InstallLocation", sb, ref len);

            if (result == 0 && sb.Length > 0) // 0 = ERROR_SUCCESS
            {
                return sb.ToString();
            }

            return null;
        }


        public List<AppInfo> GetInstalledApps()
        {
            _history = ProgramHistory.GetAllLastRuns();
            var apps = new List<AppInfo>();
            var addedNames = new HashSet<string>(); // to avoid duplicates based on DisplayName

            string[] registryPath =
            {
                 @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var path in registryPath)
            {
                ReadFromRegistry(Registry.LocalMachine, path, apps, addedNames);
            }

            ReadFromRegistry(Registry.CurrentUser, registryPath[0], apps, addedNames);

            ReadStoreApps(apps, addedNames);

            var burnBundles = BurnInstalledPackagesFinder.GetAllInstalledBundlePackages();

            // Merge burn bundles with regular installed apps
            foreach (var bundle in burnBundles)
            {
                apps.Add(new AppInfo
                {
                    Name = bundle.BundleName,
                    Version = bundle.PackageVersion,
                    InstallationPath = bundle.InstallLocation
                });
            }

            return apps.OrderBy(a => a.Name).ToList();
        }

        private Dictionary<string, string>? _startMenuShortcuts;

        private Dictionary<string, string> GetStartMenuShortcuts()
        {
            

            if (_startMenuShortcuts != null)
                return _startMenuShortcuts;

            _startMenuShortcuts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string[] startMenuPaths =
            {
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), // C:\ProgramData\Microsoft\Windows\Start Menu
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)        // %AppData%\Microsoft\Windows\Start Menu
    };

            if (_history != null)
            {
                foreach (string dirPath in _history.ByDirectory.Keys)
                {
                    if (IsValidAppDirectory(dirPath))
                    {
                        string folderName = Path.GetFileName(dirPath);
                        if (!string.IsNullOrWhiteSpace(folderName))
                        {
                            _startMenuShortcuts.TryAdd(folderName, dirPath);
                        }
                    }
                }
            }

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return _startMenuShortcuts;

            dynamic shell = Activator.CreateInstance(shellType)!;

            foreach (var menuPath in startMenuPaths)
            {
                if (!Directory.Exists(menuPath)) continue;

                try
                {
                    foreach (string lnkFile in Directory.EnumerateFiles(menuPath, "*.lnk", SearchOption.AllDirectories))
                    {
                        try
                        {

                            string shortcutName = Path.GetFileNameWithoutExtension(lnkFile);

                            // Deinstallations-Verknüpfungen ignorieren
                            if (shortcutName.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) ||
                                shortcutName.Contains("Deinstall", StringComparison.OrdinalIgnoreCase))
                                continue;

                            dynamic shortcut = shell.CreateShortcut(lnkFile);
                            string targetPath = shortcut.TargetPath as string ?? string.Empty;

                            if (!string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath))
                            {
                                string? targetDir = Path.GetDirectoryName(targetPath);
                                if (IsValidAppDirectory(targetDir) && !_startMenuShortcuts.ContainsKey(shortcutName))
                                {
                                    _startMenuShortcuts[shortcutName] = targetDir!;
                                }
                                // Beim Einlesen der .lnk-Dateien in GetStartMenuShortcuts():
                                string? parentFolderName = Path.GetFileName(Path.GetDirectoryName(lnkFile));
                                if (!string.IsNullOrWhiteSpace(parentFolderName) &&
                                    !parentFolderName.Equals("Programs", StringComparison.OrdinalIgnoreCase) &&
                                    !parentFolderName.Equals("Start Menu", StringComparison.OrdinalIgnoreCase))
                                {
                                    _startMenuShortcuts.TryAdd(parentFolderName, targetDir!);
                                }
                            }
                        }
                        catch
                        {
                            // Einzelne defekte Verknüpfungen überspringen
                        }
                    }
                }
                catch
                {
                    // Nicht zugängliche Ordner überspringen
                }
            }

            AddAppPathsToCache(_startMenuShortcuts);

            AddWindowsServicesToCache(_startMenuShortcuts);

            return _startMenuShortcuts;
        }

        private void AddWindowsServicesToCache(Dictionary<string, string> cache)
        {
            using var servicesKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (servicesKey == null) return;

            foreach (string serviceName in servicesKey.GetSubKeyNames())
            {
                using var sub = servicesKey.OpenSubKey(serviceName);
                string? imagePath = sub?.GetValue("ImagePath") as string;
                string? dir = ExtractDirectoryFromCommand(imagePath);

                if (IsValidAppDirectory(dir))
                {
                    string displayName = sub?.GetValue("DisplayName") as string ?? serviceName;
                    cache.TryAdd(serviceName, dir!);
                    cache.TryAdd(displayName, dir!);
                }
            }
        }


        private void ReadFromRegistry(RegistryKey root, string subKeyPath, List<AppInfo> apps, HashSet<string> addedNames)
        {
            using RegistryKey? rk = root.OpenSubKey(subKeyPath);
            if (rk == null) return;

            // Einmalig VOR der Schleife laden statt 100x darin:
      

            foreach (string skName in rk.GetSubKeyNames())
            {
                using RegistryKey? sk = rk.OpenSubKey(skName);
                if (sk == null) continue; // <-- Hier stand vorher skName == null

                try
                {
                    var displayName = sk.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName) || addedNames.Contains(displayName))
                        continue;

                    if (sk.GetValue("SystemComponent") is int sysComp && sysComp == 1)
                        continue;

                    addedNames.Add(displayName);

                    double sizeMB = 0;
                    if (sk.GetValue("EstimatedSize") is int sizeKB)
                    {
                        sizeMB = sizeKB / 1024.0;
                    }

                    string? publisher = sk.GetValue("Publisher") as string;
                    string installPath = GetCleanInstallPath(sk, skName, displayName, publisher);

                    apps.Add(new AppInfo
                    {
                        Name = displayName,
                        Publisher = publisher ?? "Unknown",
                        Version = sk.GetValue("DisplayVersion") as string ?? "Unknown",
                        SizeinMB = sizeMB,
                        LastStartUp = _history != null ? FindLastStartup(displayName, installPath, _history) : null,
                        InstallationPath = installPath,
                        UninstallPath = sk.GetValue("UninstallString") as string ?? string.Empty
                    });
                }
                catch
                {
                    // skip private or inaccessible registry keys
                }
            }
        }

        private Dictionary<string, string>? _msiProductFolderCache;

        private string? GetMsiFolderFromComponents(string productCode)
        {
            if (!productCode.StartsWith("{") || !productCode.EndsWith("}"))
                return null;

            // 1. Einmalig beim ersten Aufruf alle MSI-Komponenten aus der Registry lesen (~30 ms!)
            if (_msiProductFolderCache == null)
            {
                _msiProductFolderCache = BuildMsiProductFolderCache();
            }

            // 2. Normale GUID "{12345678-1234-...}" in das gepackte MSI-Format umwandeln
            string packedGuid = PackMsiGuid(productCode);
            if (_msiProductFolderCache.TryGetValue(packedGuid, out string? folder))
            {
                return folder;
            }

            return null;
        }

        private Dictionary<string, string> BuildMsiProductFolderCache()
        {
            var exeCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var anyFileCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string componentsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Components";

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var compRoot = baseKey.OpenSubKey(componentsPath);
                if (compRoot == null) return exeCache;

                foreach (string compGuid in compRoot.GetSubKeyNames())
                {
                    using var compKey = compRoot.OpenSubKey(compGuid);
                    if (compKey == null) continue;

                    foreach (string packedProductCode in compKey.GetValueNames())
                    {
                        if (exeCache.ContainsKey(packedProductCode)) continue;

                        if (compKey.GetValue(packedProductCode) is string filePath)
                        {
                            string? dir = Path.GetDirectoryName(filePath);
                            if (!IsValidAppDirectory(dir)) continue;

                            if (filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                exeCache[packedProductCode] = dir!;
                            }
                            else
                            {
                                anyFileCache.TryAdd(packedProductCode, dir!);
                            }
                        }
                    }
                }

                // Fallbacks (Ordner mit DLLs/Daten) für Pakete ohne eigene .exe übernehmen
                foreach (var kvp in anyFileCache)
                {
                    exeCache.TryAdd(kvp.Key, kvp.Value);
                }
            }
            catch { }

            return exeCache;
        }

        // Wandelt "{12345678-1234-1234-1234-1234567890AB}" in das interne MSI-Format ("87654321432143212143658709BA") um
        private static string PackMsiGuid(string guid)
        {
            string clean = guid.Trim('{', '}').Replace("-", "");
            if (clean.Length != 32) return string.Empty;

            char[] packed = new char[32];
            int[] reverseMap = {
        7,6,5,4,3,2,1,0,
        11,10,9,8,
        15,14,13,12,
        17,16,19,18,
        21,20,23,22,25,24,27,26,29,28,31,30
    };

            for (int i = 0; i < 32; i++)
            {
                packed[i] = clean[reverseMap[i]];
            }

            return new string(packed);
        }



        private void ReadStoreApps(List<AppInfo> apps, HashSet<string> addedNames)
        {
            try
            {
                var packageManager = new PackageManager();
                // FindPackagesForUser("") liefert alle Pakete des aktuell angemeldeten Benutzers
                var packages = packageManager.FindPackagesForUser(string.Empty);

                foreach (var pkg in packages)
                {
                    // System-Frameworks (z. B. VCLibs, .NET Native, UI.Xaml) ausfiltern
                    if (pkg.IsFramework || pkg.IsResourcePackage || pkg.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System)
                        continue;

                    string displayName = pkg.DisplayName;
                    if (string.IsNullOrWhiteSpace(displayName) || addedNames.Contains(displayName))
                        continue;

                    string installPath = "-";
                    try
                    {
                        // InstalledPath liefert den exakten Ordner (z. B. C:\Program Files\WindowsApps\...)
                        if (!string.IsNullOrWhiteSpace(pkg.InstalledPath))
                        {
                            installPath = pkg.InstalledPath;
                        }
                    }
                    catch
                    {
                        // Bei manchen geschützten Systempaketen kann InstalledPath eine Exception werfen
                    }

                    var v = pkg.Id.Version;
                    string version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";

                    addedNames.Add(displayName);
                    apps.Add(new AppInfo
                    {
                        Name = displayName,
                        Publisher = pkg.PublisherDisplayName ?? "-",
                        Version = version,
                        SizeinMB = 0, // Store-Pakete speichern keine EstimatedSize in der Registry
                        InstallationPath = installPath
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Fehler beim Lesen der Store-Apps: {ex.Message}");
            }
        }

        private string GetCleanInstallPath(RegistryKey sk, string skName, string displayName, string? publisher)
        {
            // 1. Direkter Versuch über Registry InstallLocation
            var location = sk.GetValue("InstallLocation") as string;
            if (IsValidAppDirectory(location))
                return location!.Trim('"').TrimEnd('\\');

            // 2. NEU: Windows Installer (MSI) Datenbank abfragen -> findet PuTTY, Node.js, HSETU, Blackmagic usw.!
            string? msiLocation = GetMsiInstallLocation(skName);
            if (IsValidAppDirectory(msiLocation))
                return msiLocation!.Trim('"').TrimEnd('\\');


            // 2b. NEU: MSI-Komponentenpfad prüfen -> findet JEDES MSI-Paket!
            string? msiCompDir = GetMsiFolderFromComponents(skName);
            if (IsValidAppDirectory(msiCompDir))
                return msiCompDir!;

            // 3. Startmenü- & Desktop-Verknüpfungen (.lnk) + App Paths
            string? dirFromShortcut = FindInStartMenu(displayName);
            if (IsValidAppDirectory(dirFromShortcut))
                return dirFromShortcut!;

            // 4. Fallback: DisplayIcon
            string? dirFromIcon = ExtractDirectoryFromCommand(sk.GetValue("DisplayIcon") as string);
            if (IsValidAppDirectory(dirFromIcon))
                return dirFromIcon!;

            // 5. Fallback: UninstallString
            string? dirFromUninstall = ExtractDirectoryFromCommand(sk.GetValue("UninstallString") as string);
            if (IsValidAppDirectory(dirFromUninstall))
                return dirFromUninstall!;

            // 6. Fallback: Standardordner inkl. Hersteller-Unterordner & Teilnamen
            string? guessedDir = FindInStandardFolders(displayName, publisher);
            if (IsValidAppDirectory(guessedDir))
                return guessedDir!;

            return "-";
        }


        private string? FindInStartMenu(string appName)
        {
            var shortcuts = GetStartMenuShortcuts();

            // 1. Exakter Treffer (z. B. "DaVinci Resolve" == "DaVinci Resolve.lnk")
            if (shortcuts.TryGetValue(appName, out string? exactDir))
                return exactDir;

            // 2. Treffer ohne Versionsnummer (z. B. "VLC media player 3.0.21" -> "VLC media player")
            string cleanName = Regex.Replace(appName, @"\s*(\d+(\.\d+)+|\(.*?\)|\[.*?\]).*$", "").Trim();
            if (cleanName.Length >= 3 && shortcuts.TryGetValue(cleanName, out string? cleanDir))
                return cleanDir;

            // 3. Teil-Treffer (z. B. App heißt "Adobe Acrobat (64-bit)", Verknüpfung heißt "Adobe Acrobat")
            if (cleanName.Length >= 4)
            {
                var partialMatch = shortcuts.FirstOrDefault(kvp =>
                    kvp.Key.StartsWith(cleanName, StringComparison.OrdinalIgnoreCase) ||
                    cleanName.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(partialMatch.Value))
                    return partialMatch.Value;
            }

            return null;
        }


        // Extrahiert einen Dateipfad aus Befehlszeilen (inkl. Anführungszeichen & Parametern)
        private string? ExtractDirectoryFromCommand(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            // Sucht nach einem Pfad wie C:\...\datei.exe (mit oder ohne Anführungszeichen)
            var match = Regex.Match(command, @"""?([a-zA-Z]:\\[^""*?|<>]+\.[a-zA-Z0-9]{2,4})""?");
            if (match.Success)
            {
                string filePath = match.Groups[1].Value;
                return Path.GetDirectoryName(filePath);
            }

            return null;
        }

        // Prüft, ob der Ordner existiert und kein allgemeiner Windows-System/Cache-Ordner ist
        private bool IsValidAppDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            string clean = path.Trim('"').Trim();
            if (!Directory.Exists(clean)) return false;

            // Systemordner wie System32 oder Windows\Installer ausschließen, da msiexec.exe dort liegt
            string lower = clean.ToLowerInvariant();
            if (lower.Contains(@"\windows\installer") ||
    lower.Contains(@"\windows\system32") ||
    lower.Contains(@"\windows\syswow64") ||
    lower.Contains(@"\programdata\package cache") ||
    lower.EndsWith(@"\program files") ||
    lower.EndsWith(@"\program files (x86)"))
            {
                return false;
            }

            return true;
        }

        // Sucht in den typischen Programm-Ordnern nach einem passenden Unterordner
        // Hilfsmethode: Entfernt Leerzeichen, Bindestriche & Sonderzeichen ("U-Therm" -> "utherm")
        private static string NormalizeName(string input)
        {
            return Regex.Replace(input, @"[^a-zA-Z0-9äöüÄÖÜß]", "").ToLowerInvariant();
        }

        private string? FindInStandardFolders(string appName, string? publisher)
        {
            string baseName = Regex.Replace(appName, @"\s*(release|\d+(\.\d+)+|\(.*?\)|\[.*?\]|Common Components).*$", "", RegexOptions.IgnoreCase).Trim();
            if (baseName.Length < 3) return null;

            string[] searchRoots =
            {
         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),    // C:\Program Files\Common Files
    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), // C:\Program Files (x86)\Common Files
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
    };

            // Mögliche übergeordnete Hersteller-/Gruppenordner sammeln:
            // 1. Der Publisher selbst ("Hottgenroth Software AG")
            // 2. Das erste Wort des App-Namens ("HSETU" bei "HSETU U-Therm")
            var vendorCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(publisher) && publisher != "Unknown" && publisher != "-")
                vendorCandidates.Add(publisher.Trim());

            string[] nameWords = baseName.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (nameWords.Length >= 2 && nameWords[0].Length >= 3)
                vendorCandidates.Add(nameWords[0]);

            string normFullApp = NormalizeName(baseName);

            foreach (var root in searchRoots)
            {
                if (!Directory.Exists(root)) continue;

                // 1. Direkter Ordner unter Program Files (z. B. C:\Program Files\Nodejs)
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(root))
                    {
                        string normDir = NormalizeName(Path.GetFileName(dir));
                        if (normDir.Length >= 3 && normDir == normFullApp)
                            return dir;
                    }
                }
                catch { }

                // 2. Hersteller-/Gruppenordner prüfen (z. B. C:\Program Files (x86)\HSETU)
                foreach (var vendor in vendorCandidates)
                {
                    string vendorDir = Path.Combine(root, vendor);
                    if (!Directory.Exists(vendorDir)) continue;

                    // Restname ohne das Hersteller-Präfix bilden ("HSETU U-Therm" -> "U-Therm")
                    string strippedName = baseName.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)
                        ? baseName.Substring(vendor.Length).Trim(' ', '-', '_')
                        : baseName;

                    string normStripped = NormalizeName(strippedName);
                    if (normStripped.Length < 3) continue;

                    try
                    {
                        foreach (var subDir in Directory.EnumerateDirectories(vendorDir))
                        {
                            string normSub = NormalizeName(Path.GetFileName(subDir));
                            if (normSub.Length >= 4 &&
                                (normSub == normStripped || (normStripped.Length >= 5 && normSub.StartsWith(normStripped))))
                            {
                                return subDir;
                            }
                        }
                  
                    }
                    catch { }

                    

                }
                // 3. Allgemeiner 2-Ebenen-Scan: C:\Program Files\<BeliebigerOrdner>\<AppName>
                // Findet z. B. C:\Program Files\HSETU\ECO-CAD (auch wenn Publisher "ETU Software GmbH" heißt)
                try
                {
                    foreach (var vendorDir in Directory.EnumerateDirectories(root))
                    {
                        // Systemordner wie WindowsApps oder Common Files überspringen
                        string vendorName = Path.GetFileName(vendorDir);
                        if (vendorName.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                            vendorName.Equals("Common Files", StringComparison.OrdinalIgnoreCase) ||
                            vendorName.Equals("Reference Assemblies", StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            foreach (var subDir in Directory.EnumerateDirectories(vendorDir))
                            {
                                string normSub = NormalizeName(Path.GetFileName(subDir));
                                if (normSub.Length >= 4 && normSub == normFullApp)
                                {
                                    return subDir;
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

            }

            return null;
        }



        private void AddAppPathsToCache(Dictionary<string, string> cache)
        {
            string appPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using RegistryKey? rk = root.OpenSubKey(appPathsKey);
                if (rk == null) continue;

                foreach (string exeKeyName in rk.GetSubKeyNames())
                {
                    using RegistryKey? sub = rk.OpenSubKey(exeKeyName);
                    string? exeFullPath = (sub?.GetValue("") as string)?.Trim('"');
                    if (string.IsNullOrWhiteSpace(exeFullPath)) continue;

                    string? dir = Path.GetDirectoryName(exeFullPath);
                    if (IsValidAppDirectory(dir))
                    {
                        string keyName = Path.GetFileNameWithoutExtension(exeKeyName);
                        cache.TryAdd(keyName, dir!);
                    }
                }
            }
        }

        private DateTime? FindLastStartup(string displayName, string installPath, HistoryResult history)
        {
            DateTime latest = DateTime.MinValue;

            // 1. Treffer über den Ordnerpfad (aus UserAssist / BAM)
            if (installPath != "-" && history.ByDirectory.TryGetValue(installPath, out DateTime dirTime) && dirTime > latest)
            {
                latest = dirTime;
            }

            // 2. Alle .exe-Dateien im Installationsordner prüfen (z. B. Resolve.exe im Ordner DaVinci Resolve)
            if (installPath != "-" && Directory.Exists(installPath))
            {
                try
                {
                    foreach (string exeFile in Directory.EnumerateFiles(installPath, "*.exe", SearchOption.TopDirectoryOnly))
                    {
                        string exeNoExt = Path.GetFileNameWithoutExtension(exeFile);
                        if (history.ByExeName.TryGetValue(exeNoExt, out DateTime exeTime) && exeTime > latest)
                        {
                            latest = exeTime;
                        }
                    }
                }
                catch { }
            }

            // 3. Fallback: Abgleich über den Programmnamen
            if (history.ByExeName.TryGetValue(displayName, out DateTime nameTime) && nameTime > latest)
            {
                latest = nameTime;
            }

            return latest > DateTime.MinValue ? latest : null;
        }





        /*
        private Dictionary<string, DateTime> ReadLastStartUp(List<AppInfo> apps, HashSet<string> addedNames)
        {

            var ListStartUp = new Dictionary<string, DateTime>();

            string sid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
            if (sid != string.Empty)
            {



                string path = $@"SYSTEM\CurrentControlSet\Services\bam\State\UserSettings\{sid}";

                using RegistryKey key = Registry.LocalMachine.OpenSubKey(path);
                if (key != null)
                {
                    foreach (string valueName in key.GetValueNames())
                    {
                        if (key.GetValue(valueName) is byte[] data && data.Length >= 8)
                        {
                            long fileTime = BitConverter.ToInt64(data, 0);
                            DateTime lastStartUp = DateTime.FromFileTime(fileTime);
                            ListStartUp.Add(valueName, lastStartUp);
                        }
                    }
                }
            }
            return ListStartUp;
        }
        */

    }
}
