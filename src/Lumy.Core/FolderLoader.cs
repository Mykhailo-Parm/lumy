namespace Lumy.Core;

/// <summary>Opens a folder: finds its photos and syncs them with the library. Run it off the UI thread.</summary>
public static class FolderLoader
{
    public static IReadOnlyList<PhotoRecord> Load(LibraryDatabase database, string folder, PhotoSort sort, CancellationToken cancellationToken = default)
    {
        var files = FolderScanner.Scan(folder, recursive: false, cancellationToken);
        var records = database.Sync(files, PhotoFingerprint.Compute, PhotoMetadata.TryReadCaptureTime, File.Exists, cancellationToken);
        return records.Sort(sort).ToList();
    }
}
