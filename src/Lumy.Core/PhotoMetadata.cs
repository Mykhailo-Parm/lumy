using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Jpeg;

namespace Lumy.Core;

/// <summary>Reads the few EXIF fields Lumy needs while indexing a folder.</summary>
public static class PhotoMetadata
{
    /// <summary>Returns when the photo was taken (camera local time, stored as-is), or null if the file has no EXIF date.</summary>
    public static DateTime? TryReadCaptureTime(string path)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (subIfd is not null && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var original))
                return DateTime.SpecifyKind(original, DateTimeKind.Unspecified);

            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (ifd0 is not null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var modified))
                return DateTime.SpecifyKind(modified, DateTimeKind.Unspecified);
        }
        catch (Exception e) when (e is ImageProcessingException or IOException or UnauthorizedAccessException or FormatException)
        {
        }

        return null;
    }

    /// <summary>Camera, lens and exposure for the viewer's info line; null when the file has no EXIF.</summary>
    public static PhotoDetails? TryReadDetails(string path)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            var exif = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            var jpeg = directories.OfType<JpegDirectory>().FirstOrDefault();

            int? width = exif?.TryGetInt32(ExifDirectoryBase.TagExifImageWidth, out var w) == true ? w
                : jpeg?.TryGetInt32(JpegDirectory.TagImageWidth, out var jw) == true ? jw : null;
            int? height = exif?.TryGetInt32(ExifDirectoryBase.TagExifImageHeight, out var h) == true ? h
                : jpeg?.TryGetInt32(JpegDirectory.TagImageHeight, out var jh) == true ? jh : null;

            return new PhotoDetails(
                Camera: CameraName(ifd0?.GetString(ExifDirectoryBase.TagMake), ifd0?.GetString(ExifDirectoryBase.TagModel)),
                Lens: exif?.GetString(ExifDirectoryBase.TagLensModel)?.Trim(),
                FocalLength: exif?.GetDescription(ExifDirectoryBase.TagFocalLength),
                Aperture: exif?.GetDescription(ExifDirectoryBase.TagFNumber),
                Exposure: exif?.GetDescription(ExifDirectoryBase.TagExposureTime)?.Replace(" sec", " с"),
                Iso: exif?.TryGetInt32(ExifDirectoryBase.TagIsoEquivalent, out var iso) == true ? iso : null,
                Width: width,
                Height: height);
        }
        catch (Exception e) when (e is ImageProcessingException or IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    /// <summary>"Canon EOS R6", not "Canon Canon EOS R6": most cameras already put the brand in the model name.</summary>
    public static string? CameraName(string? make, string? model)
    {
        make = make?.Trim();
        model = model?.Trim();
        if (string.IsNullOrEmpty(model))
            return string.IsNullOrEmpty(make) ? null : make;
        if (string.IsNullOrEmpty(make))
            return model;

        var brand = make.Split(' ')[0];
        return model.Contains(brand, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
    }

    /// <summary>File size the way Explorer shows it: "8,2 МБ".</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => string.Format(CultureInfo.GetCultureInfo("ru-RU"), "{0:0.0} МБ", bytes / 1024d / 1024d),
        >= 1024 => $"{bytes / 1024} КБ",
        _ => $"{bytes} Б",
    };
}

/// <summary>What the camera recorded about a shot. Every field may be missing.</summary>
public sealed record PhotoDetails(
    string? Camera, string? Lens, string? FocalLength, string? Aperture, string? Exposure, int? Iso, int? Width, int? Height)
{
    /// <summary>"Canon EOS R6 · 50 mm · f/1.8 · 1/500 с · ISO 100 · 6000 × 4000".</summary>
    public string Summary => string.Join("  ·  ", new[]
    {
        Camera,
        FocalLength,
        Aperture,
        Exposure,
        Iso is { } iso ? $"ISO {iso}" : null,
        Width is { } w && Height is { } h ? $"{w} × {h}" : null,
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
}
