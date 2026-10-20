using System.Security.Cryptography;

namespace Lumy.Core;

/// <summary>
/// Identifies a photo independently of its path, so ratings survive renaming the file or moving the folder.
/// The fingerprint is the file size plus a hash of the first 64 KB, which holds the EXIF block with the
/// capture time and camera serial, so two different shots practically never collide.
/// </summary>
public static class PhotoFingerprint
{
    private const int HeadBytes = 64 * 1024;

    public static string Compute(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        var buffer = new byte[(int)Math.Min(HeadBytes, stream.Length)];
        stream.ReadExactly(buffer);
        var hash = SHA256.HashData(buffer);
        return $"{stream.Length:x}-{Convert.ToHexStringLower(hash, 0, 12)}";
    }
}
