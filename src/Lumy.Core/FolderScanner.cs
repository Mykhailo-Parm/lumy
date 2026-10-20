namespace Lumy.Core;

/// <summary>Lists the photos in a folder and pairs RAW files with the JPEG (or other image) shot alongside them.</summary>
public static class FolderScanner
{
    // When several display files share a RAW's name, the RAW is paired with the first of these.
    private static readonly string[] PairPreference = [".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff", ".png", ".webp", ".avif", ".bmp"];

    public static IReadOnlyList<PhotoFile> Scan(string folder, bool recursive = false, CancellationToken cancellationToken = default)
    {
        var files = new DirectoryInfo(folder)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
            .Where(f => PhotoFormats.IsSupported(f.Name));

        var photos = new List<PhotoFile>();
        var groups = files.GroupBy(
            f => System.IO.Path.Combine(f.DirectoryName ?? "", System.IO.Path.GetFileNameWithoutExtension(f.Name)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var raws = group.Where(f => PhotoFormats.IsRaw(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var displays = group.Where(f => !PhotoFormats.IsRaw(f.Name))
                .OrderBy(f => PreferenceRank(f.Extension))
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var raw = raws.FirstOrDefault();
            if (displays.Count == 0)
            {
                photos.AddRange(raws.Select(r => new PhotoFile(r.FullName, r.FullName, r.Length, r.LastWriteTimeUtc)));
                continue;
            }

            for (var i = 0; i < displays.Count; i++)
            {
                var d = displays[i];
                photos.Add(new PhotoFile(d.FullName, i == 0 ? raw?.FullName : null, d.Length, d.LastWriteTimeUtc));
            }

            // Extra RAWs with the same name (rare: IMG_1.CR2 and IMG_1.DNG) stay visible as their own photos.
            photos.AddRange(raws.Skip(1).Select(r => new PhotoFile(r.FullName, r.FullName, r.Length, r.LastWriteTimeUtc)));
        }

        return photos.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static int PreferenceRank(string extension)
    {
        var index = Array.FindIndex(PairPreference, e => e.Equals(extension, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }
}
