namespace Lumy.Core;

/// <summary>How big exported photos are.</summary>
public enum ExportSize
{
    /// <summary>1080 px wide, what Instagram shows; smaller photos are never enlarged.</summary>
    Instagram,

    /// <summary>2048 px on the long side, for messengers and the web.</summary>
    LongEdge2048,

    /// <summary>Every pixel of the crop.</summary>
    Full,
}

/// <summary>What an export writes: file names, sizes, and where nothing gets overwritten.</summary>
public static class ExportPlan
{
    public const int InstagramWidth = 1080;

    /// <summary>
    /// The output name: the photo's own name as .jpg, with a running number in front when the order matters
    /// (an album for a carousel), so the files sort the way they should be posted.
    /// </summary>
    public static string FileName(string sourcePath, int? position = null, int total = 0)
    {
        // Split on both separators, so a Windows path reads the same everywhere.
        var file = sourcePath[(sourcePath.LastIndexOfAny(['\\', '/']) + 1)..];
        var name = Path.GetFileNameWithoutExtension(file) + ".jpg";
        if (position is not { } n)
            return name;

        var digits = Math.Max(2, total.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        return n.ToString("D" + digits, System.Globalization.CultureInfo.InvariantCulture) + "_" + name;
    }

    /// <summary>"name.jpg", or "name (2).jpg" and so on when that is taken; existing files are never replaced.</summary>
    public static string UniquePath(string folder, string fileName, Func<string, bool> exists)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 2; exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}){extension}");
        return path;
    }

    /// <summary>Output pixels for a crop of <paramref name="width"/> × <paramref name="height"/>; never larger than the crop.</summary>
    public static (int Width, int Height) TargetSize(ExportSize size, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return (Math.Max(width, 1), Math.Max(height, 1));

        var scale = size switch
        {
            ExportSize.Instagram => (double)InstagramWidth / width,
            ExportSize.LongEdge2048 => 2048.0 / Math.Max(width, height),
            _ => 1.0,
        };
        scale = Math.Min(scale, 1.0);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>A warning when Instagram would crop the photo again: it shows feed posts from 4:5 to 1.91:1.</summary>
    public static string? InstagramWarning(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        var ratio = (double)width / height;
        return ratio < 0.8 - 0.005 ? "выше, чем 4:5 — Instagram обрежет его в ленте"
            : ratio > 1.91 + 0.005 ? "шире, чем 1.91:1 — Instagram обрежет его в ленте"
            : null;
    }
}
