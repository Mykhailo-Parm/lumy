using Lumy.Core;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Lumy.App;

/// <summary>
/// Screen-sized bitmaps for the full-screen viewer. The photos either side of the current one are decoded
/// ahead of time, so stepping through a shoot never waits on the disk. Must be used from the UI thread.
/// </summary>
public sealed class LoupeImageCache(int decodeWidth)
{
    private const int Radius = 3;
    private readonly Dictionary<long, Task<BitmapImage?>> _screen = [];

    /// <summary>The bitmap sized for the screen; starts decoding if it is not already cached.</summary>
    public Task<BitmapImage?> GetAsync(PhotoItem item)
    {
        if (!_screen.TryGetValue(item.Id, out var task))
        {
            task = LoadAsync(item.Path, decodeWidth);
            _screen[item.Id] = task;
        }

        return task;
    }

    /// <summary>Decodes the neighbours of <paramref name="index"/> and forgets bitmaps that are far away.</summary>
    public void Prefetch(IReadOnlyList<PhotoItem> items, int index)
    {
        var from = Math.Max(0, index - Radius);
        var to = Math.Min(items.Count - 1, index + Radius);
        Retain(Enumerable.Range(from, Math.Max(0, to - from + 1)).Select(i => items[i]));
    }

    /// <summary>Decodes exactly these photos and forgets every other bitmap.</summary>
    public void Retain(IEnumerable<PhotoItem> items)
    {
        var keep = new HashSet<long>();
        foreach (var item in items)
        {
            keep.Add(item.Id);
            _ = GetAsync(item);
        }

        foreach (var id in _screen.Keys.Where(id => !keep.Contains(id)).ToList())
            _screen.Remove(id);
    }

    public void Clear() => _screen.Clear();

    /// <summary>The image at its native resolution, for checking focus at 100%. Not cached: only one is needed at a time.</summary>
    public static Task<BitmapImage?> LoadFullResolutionAsync(PhotoItem item) => LoadAsync(item.Path, 0);

    /// <summary>Decodes a photo at the given width in physical pixels (0 = native). Used directly by compare, which sizes per cell.</summary>
    public static async Task<BitmapImage?> LoadAsync(string path, int width)
    {
        try
        {
            var bitmap = new BitmapImage();

            // RAW files are shown through their embedded preview, which Windows hands out quickly;
            // decoding the sensor data is not needed for picking shots.
            if (PhotoFormats.IsRaw(path))
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var preview = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)(width == 0 ? 8192 : width), ThumbnailOptions.ResizeThumbnail);
                if (preview is null)
                    return null;
                await bitmap.SetSourceAsync(preview);
                return bitmap;
            }

            if (width > 0)
            {
                bitmap.DecodePixelType = DecodePixelType.Physical;
                bitmap.DecodePixelWidth = width;
            }

            using var stream = File.OpenRead(path).AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception)
        {
            // Unreadable or unsupported files show their thumbnail instead; never a reason to crash.
            return null;
        }
    }
}
