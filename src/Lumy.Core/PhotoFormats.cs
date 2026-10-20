namespace Lumy.Core;

/// <summary>File extensions Lumy shows, split into display formats and camera RAW formats.</summary>
public static class PhotoFormats
{
    public static readonly IReadOnlySet<string> Display = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".tif", ".tiff", ".bmp", ".avif",
    };

    public static readonly IReadOnlySet<string> Raw = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf", ".rw2",
        ".pef", ".dng", ".srw", ".x3f", ".3fr", ".iiq", ".erf", ".mef", ".mos", ".kdc", ".rwl",
    };

    public static bool IsRaw(string path) => Raw.Contains(Path.GetExtension(path));

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return Display.Contains(ext) || Raw.Contains(ext);
    }
}
