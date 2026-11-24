using System.Runtime.InteropServices;
using System.Text;

namespace AppTrace.Helpers.RegistryEntrys
{
    class MSIPackages
    {
        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        private static extern uint MsiGetProductInfo(string szProduct, string szAttribute, [Out] StringBuilder lpValueBuf, ref uint pcchValueBuf);

        public static string? GetMsiInstallLocation(string productCode)
        {
            // MSI ProductCodes sind immer GUIDs in geschweiften Klammern: {XXXXXXXX-XXXX-...}
            if (!productCode.StartsWith('{') || !productCode.EndsWith('}'))
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


    }
}
