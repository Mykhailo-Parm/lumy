using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

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
}
