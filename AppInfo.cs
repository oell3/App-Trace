namespace AppTrace.Models
{

    public class AppInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public double SizeinMB { get; set; }


        public string FormattedSize
        {
            get
            {
                if (SizeinMB <= 0) return "-";
                if (SizeinMB >= 1024) return $"{SizeinMB / 1024.0:F2} GB";
                return $"{SizeinMB:F1}"
            }
        }
    }
}