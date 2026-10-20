namespace Lumy.Core;

/// <summary>
/// Applies the sliders of a <see cref="PhotoEdit"/> to 8-bit BGRA pixels, the same code for the preview and the
/// export, so what you see is what you post. Exposure and white balance work in linear light, the tone sliders on
/// a curve, and saturation per pixel. Every curve is monotonic, so no slider ever turns a brighter tone darker.
/// </summary>
public static class ImageAdjust
{
    private static readonly double[] ToLinear = Enumerable.Range(0, 256).Select(v => SrgbToLinear(v / 255.0)).ToArray();

    /// <summary>Per-channel lookup tables (blue, green, red) for everything but saturation.</summary>
    public static (byte[] Blue, byte[] Green, byte[] Red) Curves(PhotoEdit edit)
    {
        var e = edit.Normalized();
        var gain = Math.Pow(2, e.Exposure);
        var temperature = e.Temperature / 100;
        var tint = e.Tint / 100;
        var red = Curve(gain * (1 + 0.22 * temperature) * (1 + 0.06 * tint), e);
        var green = Curve(gain * (1 - 0.16 * tint), e);
        var blue = Curve(gain * (1 - 0.22 * temperature) * (1 + 0.06 * tint), e);
        return (blue, green, red);
    }

    /// <summary>Adjusts <paramref name="pixels"/> in place: rows of <paramref name="stride"/> bytes, 4 bytes a pixel, BGRA.</summary>
    public static void Apply(byte[] pixels, int width, int height, int stride, PhotoEdit edit)
    {
        var e = edit.Normalized();
        if (e.HasNoAdjustments)
            return;

        var (blue, green, red) = Curves(e);
        var saturation = 1 + e.Saturation / 100;
        var vibrance = e.Vibrance / 100;
        var colour = saturation != 1 || vibrance != 0;

        Parallel.For(0, height, y =>
        {
            var row = y * stride;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                int b = blue[pixels[i]], g = green[pixels[i + 1]], r = red[pixels[i + 2]];
                if (colour)
                {
                    var luma = 0.0722 * b + 0.7152 * g + 0.2126 * r;
                    var max = Math.Max(r, Math.Max(g, b));
                    var min = Math.Min(r, Math.Min(g, b));

                    // Vibrance boosts dull colours most and leaves strong ones nearly alone.
                    var factor = saturation * (1 + vibrance * (1 - (max - min) / 255.0));
                    b = Clamp(luma + (b - luma) * factor);
                    g = Clamp(luma + (g - luma) * factor);
                    r = Clamp(luma + (r - luma) * factor);
                }

                pixels[i] = (byte)b;
                pixels[i + 1] = (byte)g;
                pixels[i + 2] = (byte)r;
            }
        });
    }

    private static byte[] Curve(double gain, PhotoEdit e)
    {
        var contrast = e.Contrast / 100;
        var highlights = e.Highlights / 100;
        var shadows = e.Shadows / 100;
        var curve = new byte[256];
        for (var v = 0; v < 256; v++)
        {
            var s = LinearToSrgb(Math.Clamp(ToLinear[v] * gain, 0, 1));

            // Shadows and highlights bend the darks and the brights, each weighted by a bump that is zero at the
            // ends. The strengths are the largest that keep each step rising for any slider value, and the
            // steps run one after another, so together they still rise.
            s += shadows * (shadows > 0 ? 0.30 : 0.13) * 4 * s * (1 - s) * (1 - s) / 0.5926;
            s = Math.Clamp(s, 0, 1);
            s += highlights * (highlights < 0 ? 0.30 : 0.13) * 4 * s * s * (1 - s) / 0.5926;
            s = Math.Clamp(s, 0, 1);

            // Contrast: towards an S-curve, or towards flat grey.
            s = contrast >= 0
                ? s + contrast * (s * s * (3 - 2 * s) - s)
                : s + -contrast * 0.5 * (0.5 - s);
            curve[v] = (byte)Math.Clamp(Math.Round(s * 255), 0, 255);
        }

        return curve;
    }

    private static int Clamp(double value) => value <= 0 ? 0 : value >= 255 ? 255 : (int)(value + 0.5);

    private static double SrgbToLinear(double s) => s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);

    private static double LinearToSrgb(double l) => l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
}
