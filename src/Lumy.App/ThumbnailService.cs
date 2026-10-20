using System.Security.Cryptography;
using System.Text;
using Lumy.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Lumy.App;

/// <summary>
/// Lumy's own thumbnail cache: one small JPEG per photo in %LOCALAPPDATA%\Lumy\thumbs. Made once with a scaled
/// decode (JPEG decodes at 1/8 size almost for free), so a reopened folder shows every tile at once and the
/// series detector hashes these instead of decoding the photos again. Safe to call from any thread.
/// </summary>
public sealed class ThumbnailService
{
    /// <summary>Longest edge in pixels: sharp on 200 px tiles at 150% scaling, small enough to load in a blink.</summary>
    public const int Size = 360;

    private readonly string _folder;

    // Tiles on screen go through their own gate, so the background warm-up never makes them wait.
    private readonly SemaphoreSlim _visibleGate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
    private readonly SemaphoreSlim _backgroundGate = new(2);

    public ThumbnailService(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumy", "thumbs");

    /// <summary>The cache file for this version of the photo; a changed file gets a new name.</summary>
    public string PathFor(PhotoRecord photo)
    {
        var key = $"{photo.Fingerprint}|{photo.FileSize}|{photo.ModifiedUtc.Ticks}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_folder, hash[..2], hash + ".jpg");
    }

    /// <summary>Returns the cached thumbnail file, making it first if needed; null if the photo cannot be read.</summary>
    public async Task<string?> GetAsync(PhotoRecord photo, bool background, CancellationToken cancellationToken = default)
    {
        var target = PathFor(photo);
        if (File.Exists(target))
            return target;

        var gate = background ? _backgroundGate : _visibleGate;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(target))
                return target;

            return await Task.Run(() => CreateAsync(photo.Path, target), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string?> CreateAsync(string source, string target)
    {
        try
        {
            var bytes = PhotoFormats.IsRaw(source)
                ? await FromShellAsync(source).ConfigureAwait(false)
                : await FromDecoderAsync(source).ConfigureAwait(false) ?? await FromShellAsync(source).ConfigureAwait(false);
            if (bytes is null)
                return null;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (Exception)
        {
            // Unreadable, half-copied or unsupported files keep a blank tile; nothing here is worth a crash.
            return null;
        }
    }

    /// <summary>Scaled decode of JPEG, PNG, HEIC, TIFF and anything else with a Windows codec.</summary>
    private static async Task<byte[]?> FromDecoderAsync(string source)
    {
        try
        {
            using var input = File.OpenRead(source).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(input);
            return await EncodeAsync(decoder, await ReadOrientationAsync(decoder));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>RAW files: the camera's embedded preview, through the Windows thumbnail cache (already upright).</summary>
    private static async Task<byte[]?> FromShellAsync(string source)
    {
        var file = await StorageFile.GetFileFromPathAsync(source);
        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, Size, ThumbnailOptions.ResizeThumbnail);
        if (thumbnail is null)
            return null;

        var decoder = await BitmapDecoder.CreateAsync(thumbnail);
        return await EncodeAsync(decoder, orientation: 1);
    }

    private static async Task<byte[]> EncodeAsync(BitmapDecoder decoder, int orientation)
    {
        // Scale the stored (not yet rotated) frame, then let the encoder turn it upright.
        var scale = Math.Min(1.0, (double)Size / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var pixels = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);

        using var output = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.85f, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
        encoder.SetSoftwareBitmap(pixels);
        (encoder.BitmapTransform.Rotation, encoder.BitmapTransform.Flip) = orientation switch
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
        await encoder.FlushAsync();

        var bytes = new byte[output.Size];
        output.Seek(0);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>EXIF orientation (1–8); 1 when the file has none.</summary>
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
}
