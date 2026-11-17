using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace ZenStationShell.Services;

internal static class ImageLoader
{
    /// <summary>Laster bilde fra Steams lokale cache, ellers fra Steams CDN. Må kalles fra UI-tråden.</summary>
    public static async Task<ImageSource?> LoadAsync(string? path, string fallbackUrl, int decodeWidth)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                var bytes = await File.ReadAllBytesAsync(path);
                using var ms = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(ms))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }
                ms.Seek(0);

                var bmp = new BitmapImage { DecodePixelWidth = decodeWidth };
                await bmp.SetSourceAsync(ms);
                return bmp;
            }
        }
        catch
        {
            // faller tilbake til CDN under
        }

        return new BitmapImage(new Uri(fallbackUrl)) { DecodePixelWidth = decodeWidth };
    }

    /// <summary>Ikon for en lagt-til app: exe-ikon fra Windows, eller favicon for nettsider.</summary>
    public static async Task<ImageSource?> LoadAppIconAsync(AppEntry app)
    {
        try
        {
            if (app.Target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var host = new Uri(app.Target).Host;
                return new BitmapImage(new Uri($"https://www.google.com/s2/favicons?domain={host}&sz=128"))
                {
                    DecodePixelWidth = 128
                };
            }

            if (File.Exists(app.Target))
            {
                var file = await StorageFile.GetFileFromPathAsync(app.Target);
                using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 256, ThumbnailOptions.UseCurrentScale);
                if (thumb is not null && thumb.Size > 0)
                {
                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(thumb);
                    return bmp;
                }
            }
        }
        catch
        {
            // ingen ikon: bokstavflisen brukes
        }
        return null;
    }
}
