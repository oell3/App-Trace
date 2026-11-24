using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AppTrace.Helpers.Icons
{
    internal class RegistryIcon
    {

        public BitmapSource? LoadImage(string? registryValue)
        {
            if (string.IsNullOrWhiteSpace(registryValue))
            {
                return null;
            }

            string value = Environment.ExpandEnvironmentVariables(registryValue.Trim());

            // Anführungszeichen und optionale Argumente entfernen
            string path = value.Trim('"');

            int commaIndex = path.IndexOf(',');
            if (commaIndex >= 0)
            {
                path = path[..commaIndex].Trim().Trim('"');
            }

            if (!File.Exists(path))
            {
                return null;
            }

            string extension = Path.GetExtension(path);

            if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                return LoadIconFile(path);
            }

            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return LoadExecutableIcon(path);
            }

            return null;
        }

        private BitmapSource? LoadIconFile(string path)
        {
            var image = new BitmapImage();

            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            return image;
        }

        private BitmapSource? LoadExecutableIcon(string path)
        {
            using System.Drawing.Icon? extractedIcon =
                System.Drawing.Icon.ExtractAssociatedIcon(path);

            if (extractedIcon == null)
            {
                return null;
            }

            IntPtr hIcon = extractedIcon.Handle;

            try
            {
                BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));

                bitmap.Freeze();

                return bitmap;
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }



}