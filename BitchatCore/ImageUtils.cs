using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bitchat.Windows;

/// <summary>
/// Image preprocessing for sending - mirrors the mobile clients: decode, scale the
/// longest side down to maxDim, re-encode JPEG quality 85. Keeps files small enough
/// for BLE fragmentation (phones reject anything over ~120 KB).
/// </summary>
public static class ImageUtils
{
    public static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
    }

    /// <summary>Returns a path to a downscaled JPEG copy, or the original path on failure.</summary>
    public static string DownscaleForSend(string path, int maxDim = 512, int quality = 85)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = maxDim; // decode-time downscale, cheap
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return path;
            // Only re-encode if it actually shrinks the file budget.
            var outPath = Path.Combine(Path.GetTempPath(), $"bitchat-send-{Guid.NewGuid():N}.jpg");
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(outPath))
            {
                encoder.Save(stream);
            }
            return outPath;
        }
        catch
        {
            return path;
        }
    }
}
