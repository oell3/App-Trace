using AppTrace.Helpers.RegistryEntrys;
using System.Windows.Media.Imaging;

namespace AppTrace.Models
{

    public class AppInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public double SizeinMB { get; set; }
        public DateTime? LastStartUp { get; set; } = DateTime.MinValue;

        public string InstallationPath { get; set; } = string.Empty;

        public string UninstallPath { get; set; } = string.Empty;
        public BitmapSource? Icon { get; set; }

        public List<InstalledBundlePackage> InstalledBundlePackage { get; set; } = [];

        public string RegistryKeyPath { get; set; } = string.Empty;

        public string RegistrySize { get; set; } = string.Empty;


        public string FormattedSize
        {
            get
            {
                if (SizeinMB <= 0) return $"{SizeinMB:F1} KB";
                if (SizeinMB >= 1024) return $"{SizeinMB / 1024.0:F2} GB";
                return $"{SizeinMB:F1} MB";
            }
        }
    }
}