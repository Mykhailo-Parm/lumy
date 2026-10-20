namespace Lumy.Core;

/// <summary>
/// One photo as found on disk. A RAW+JPEG pair shot together is a single photo:
/// <see cref="Path"/> is the file Lumy displays (the JPEG), <see cref="RawPath"/> the RAW next to it.
/// A RAW-only photo has both set to the RAW file.
/// </summary>
public sealed record PhotoFile(string Path, string? RawPath, long FileSize, DateTime ModifiedUtc)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}
