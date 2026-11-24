using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using WinRegistry = Microsoft.Win32.Registry;
using WinRegistryKey = Microsoft.Win32.RegistryKey;

namespace AppTrace.Helpers.RegistryEntrys
{
    public class HistoryResult
    {
        public Dictionary<string, DateTime> ByExeName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTime> ByDirectory { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class ProgramHistory
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);

        private static readonly Dictionary<string, string> KnownFolderGuids = new(StringComparer.OrdinalIgnoreCase)
        {
            ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            ["{905E63B6-C1BF-494E-B29C-65B732D3D21A}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.GetFolderPath(Environment.SpecialFolder.System),
            ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            ["{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["{62AB5D82-FDC1-4DC3-A9DD-070D1D495D97}"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        public static HistoryResult GetAllLastRuns()
        {
            var result = new HistoryResult();
            var deviceMap = BuildDeviceToDriveMap();

            void AddOrUpdate(string rawPathOrName, DateTime time)
            {
                if (string.IsNullOrWhiteSpace(rawPathOrName) || time.Year < 2000 || time > DateTime.Now.AddDays(1))
                    return;

                string exeNameNoExt = Path.GetFileNameWithoutExtension(rawPathOrName);
                if (!string.IsNullOrWhiteSpace(exeNameNoExt))
                {
                    if (!result.ByExeName.TryGetValue(exeNameNoExt, out DateTime existingExe) || time > existingExe)
                    {
                        result.ByExeName[exeNameNoExt] = time;
                    }
                }

                if (Path.IsPathRooted(rawPathOrName))
                {
                    string? dir = Path.GetDirectoryName(rawPathOrName);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        if (!result.ByDirectory.TryGetValue(dir, out DateTime existingDir) || time > existingDir)
                        {
                            result.ByDirectory[dir] = time;
                        }
                    }
                }
            }

            ReadBam(AddOrUpdate, deviceMap);
            ReadUserAssist(AddOrUpdate);
            ReadPrefetch(AddOrUpdate);

            return result;
        }

        private static void ReadBam(Action<string, DateTime> add, Dictionary<string, string> deviceMap)
        {
            try
            {
                string? sid = WindowsIdentity.GetCurrent().User?.Value;
                if (string.IsNullOrEmpty(sid)) return;

                string path = $@"SYSTEM\CurrentControlSet\Services\bam\State\UserSettings\{sid}";
                using var baseKey = WinRegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(path);
                if (key == null) return;

                foreach (string name in key.GetValueNames())
                {
                    if (key.GetValue(name) is byte[] data && data.Length >= 8)
                    {
                        long ft = BitConverter.ToInt64(data, 0);
                        if (ft <= 0) continue;

                        string resolvedPath = ResolveDevicePath(name, deviceMap);
                        add(resolvedPath, DateTime.FromFileTime(ft));
                    }
                }
            }
            catch { }
        }

        private static void ReadUserAssist(Action<string, DateTime> add)
        {
            try
            {
                string basePath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
                using var uaKey = WinRegistry.CurrentUser.OpenSubKey(basePath);
                if (uaKey == null) return;

                foreach (string guid in uaKey.GetSubKeyNames())
                {
                    using var countKey = uaKey.OpenSubKey($@"{guid}\Count");
                    if (countKey == null) continue;

                    foreach (string encodedName in countKey.GetValueNames())
                    {
                        if (countKey.GetValue(encodedName) is byte[] data && data.Length >= 68)
                        {
                            long ft = BitConverter.ToInt64(data, 60);
                            if (ft <= 0) continue;

                            string decoded = Rot13(encodedName);
                            if (decoded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                decoded.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                            {
                                string resolvedPath = ResolveKnownFolderGuid(decoded);
                                add(resolvedPath, DateTime.FromFileTime(ft));
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static void ReadPrefetch(Action<string, DateTime> add)
        {
            try
            {
                string prefetchDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
                if (!Directory.Exists(prefetchDir)) return;

                foreach (string file in Directory.EnumerateFiles(prefetchDir, "*.pf"))
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    int dashIndex = fileName.LastIndexOf('-');
                    if (dashIndex > 0)
                    {
                        string exeName = fileName[..dashIndex];
                        DateTime lastRun = File.GetLastWriteTime(file);
                        add(exeName, lastRun);
                    }
                }
            }
            catch { }
        }

        private static Dictionary<string, string> BuildDeviceToDriveMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder(260);

            foreach (var drive in DriveInfo.GetDrives())
            {
                string driveLetter = drive.Name.TrimEnd('\\');
                if (QueryDosDevice(driveLetter, sb, (uint)sb.Capacity) > 0)
                {
                    map[sb.ToString()] = driveLetter;
                }
            }
            return map;
        }

        private static string ResolveDevicePath(string path, Dictionary<string, string> deviceMap)
        {
            foreach (var kvp in deviceMap)
            {
                if (path.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Concat(kvp.Value, path.AsSpan(kvp.Key.Length));
                }
            }
            return path;
        }

        private static string ResolveKnownFolderGuid(string path)
        {
            if (path.StartsWith('{') && path.Length >= 38)
            {
                string guidPart = path[..38];
                if (KnownFolderGuids.TryGetValue(guidPart, out string? baseFolder) && !string.IsNullOrEmpty(baseFolder))
                {
                    return baseFolder + path[38..];
                }
            }
            return path;
        }

        private static string Rot13(string input)
        {
            char[] array = input.ToCharArray();
            for (int i = 0; i < array.Length; i++)
            {
                int number = array[i];
                if (number >= 'a' && number <= 'z')
                    array[i] = (char)(number > 'm' ? number - 13 : number + 13);
                else if (number >= 'A' && number <= 'Z')
                    array[i] = (char)(number > 'M' ? number - 13 : number + 13);
            }
            return new string(array);
        }
    }
}
