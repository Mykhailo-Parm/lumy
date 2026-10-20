using System.Numerics;

namespace Lumy.Core;

/// <summary>
/// A 64-bit difference hash (dHash): near-identical frames from a burst get hashes a few bits apart,
/// different scenes get hashes about 32 bits apart. The app shrinks each thumbnail to 9×8 grey pixels
/// and hands them here, so this stays independent of any image library.
/// </summary>
public static class PerceptualHash
{
    public const int Width = 9;
    public const int Height = 8;

    /// <summary>Hashes 72 grey values laid out row by row, 9 per row. Each bit says whether a pixel is brighter than its right neighbour.</summary>
    public static ulong FromGrey(ReadOnlySpan<byte> pixels)
    {
        if (pixels.Length != Width * Height)
            throw new ArgumentException($"Expected {Width * Height} grey pixels, got {pixels.Length}.", nameof(pixels));

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width - 1; x++, bit++)
            {
                if (pixels[y * Width + x] > pixels[y * Width + x + 1])
                    hash |= 1UL << bit;
            }
        }

        return hash;
    }

    /// <summary>Converts BGRA pixels (as Windows decoders return them) to the grey values <see cref="FromGrey"/> expects.</summary>
    public static ulong FromBgra(ReadOnlySpan<byte> bgra)
    {
        Span<byte> grey = stackalloc byte[Width * Height];
        if (bgra.Length != grey.Length * 4)
            throw new ArgumentException($"Expected {grey.Length * 4} BGRA bytes, got {bgra.Length}.", nameof(bgra));

        for (var i = 0; i < grey.Length; i++)
            grey[i] = (byte)((bgra[i * 4] * 29 + bgra[i * 4 + 1] * 150 + bgra[i * 4 + 2] * 77) >> 8);
        return FromGrey(grey);
    }

    public static int Distance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
