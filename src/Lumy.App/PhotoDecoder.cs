using System.Runtime.InteropServices.WindowsRuntime;
using Lumy.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Lumy.App;

/// <summary>Upright BGRA pixels of a photo, 4 bytes a pixel with no row padding.</summary>
/// <param name="FullWidth">Width of the whole photo, upright; the pixels may be a smaller copy.</param>
/// <param name="Orientation">The EXIF orientation the file was stored with (1–8).</param>
public sealed record DecodedPhoto(byte[] Pixels, int Width, int Height, int FullWidth, int FullHeight, int Orientation);

/// <summary>Decoding for the editor and the export, and writing JPEGs. Safe to call from any thread.</summary>
public static class PhotoDecoder
{
    /// <summary>
    /// Decodes a photo upright, at most <paramref name="maxEdge"/> pixels on its long side (0 = every pixel).
    /// RAW files without a Windows codec fall back to their embedded preview. Null when nothing can read the file.
    /// </summary>
    public static async Task<DecodedPhoto?> DecodeAsync(string path, int maxEdge)
    {
        try
        {
            using var stream = File.OpenRead(path).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            return await DecodeAsync(decoder, await ReadOrientationAsync(decoder), maxEdge);
        }
        catch (Exception) when (PhotoFormats.IsRaw(path))
        {
            return await FromShellAsync(path, maxEdge);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<DecodedPhoto?> FromShellAsync(string path, int maxEdge)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var preview = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)(maxEdge == 0 ? 8192 : maxEdge), ThumbnailOptions.ResizeThumbnail);
            if (preview is null)
                return null;

            var decoder = await BitmapDecoder.CreateAsync(preview);
            return await DecodeAsync(decoder, orientation: 1, maxEdge);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<DecodedPhoto> DecodeAsync(BitmapDecoder decoder, int orientation, int maxEdge)
    {
        // The transform scales the stored frame first and then turns it upright, so sizes are in stored pixels.
        var storedWidth = (int)decoder.PixelWidth;
        var storedHeight = (int)decoder.PixelHeight;
        var scale = maxEdge > 0 ? Math.Min(1.0, (double)maxEdge / Math.Max(storedWidth, storedHeight)) : 1.0;
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(storedWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(storedHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        (transform.Rotation, transform.Flip) = Turn(orientation);

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(pixels.AsBuffer());

        var sideways = orientation is >= 5 and <= 8;
        return new DecodedPhoto(
            pixels, bitmap.PixelWidth, bitmap.PixelHeight,
            sideways ? storedHeight : storedWidth, sideways ? storedWidth : storedHeight, orientation);
    }

    /// <summary>The rotation and flip that stand a stored frame upright.</summary>
    public static (BitmapRotation, BitmapFlip) Turn(int orientation) => orientation switch
    {
        2 => (BitmapRotation.None, BitmapFlip.Horizontal),
        3 => (BitmapRotation.Clockwise180Degrees, BitmapFlip.None),
        4 => (BitmapRotation.None, BitmapFlip.Vertical),
        5 => (BitmapRotation.Clockwise90Degrees, BitmapFlip.Horizontal),
        6 => (BitmapRotation.Clockwise90Degrees, BitmapFlip.None),
        7 => (BitmapRotation.Clockwise270Degrees, BitmapFlip.Horizontal),
        8 => (BitmapRotation.Clockwise270Degrees, BitmapFlip.None),
        _ => (BitmapRotation.None, BitmapFlip.None),
    };

    /// <summary>The EXIF orientation of a photo (1–8); 1 when it has none or cannot be read.</summary>
    public static async Task<int> ReadOrientationAsync(string path)
    {
        try
        {
            using var stream = File.OpenRead(path).AsRandomAccessStream();
            return await ReadOrientationAsync(await BitmapDecoder.CreateAsync(stream));
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static async Task<int> ReadOrientationAsync(BitmapDecoder decoder)
    {
        try
        {
            var properties = await decoder.BitmapProperties.GetPropertiesAsync(["System.Photo.Orientation"]);
            return properties.TryGetValue("System.Photo.Orientation", out var value) && value.Value is ushort orientation ? orientation : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>A rectangle of the pixels as a new, tightly packed copy.</summary>
    public static byte[] Crop(DecodedPhoto photo, int x, int y, int width, int height)
    {
        var result = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(photo.Pixels, ((y + row) * photo.Width + x) * 4, result, row * width * 4, width * 4);
        return result;
    }

    /// <summary>Writes BGRA pixels as a JPEG of <paramref name="outWidth"/> × <paramref name="outHeight"/>, through a temporary file so a failure never leaves half a photo.</summary>
    public static async Task SaveJpegAsync(byte[] pixels, int width, int height, int outWidth, int outHeight, int quality, string target)
    {
        using var output = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(Math.Clamp(quality, 1, 100) / 100f, Windows.Foundation.PropertyType.Single),
        };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        if (outWidth != width || outHeight != height)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)outWidth;
            encoder.BitmapTransform.ScaledHeight = (uint)outHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.FlushAsync();

        var temp = target + ".lumy-tmp";
        try
        {
            using (var file = File.Create(temp))
            {
                output.Seek(0);
                await output.AsStreamForRead().CopyToAsync(file);
            }

            File.Move(temp, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
