namespace Lumy.Core;

/// <summary>Which part of the crop frame is being dragged.</summary>
public enum CropHandle
{
    None,
    Move,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// Crop frame geometry. Rectangles are in fractions of the upright photo; ratios are width / height in pixels,
/// so a 4:5 frame on a landscape photo is narrow in fractions but exactly 4:5 in pixels.
/// </summary>
public static class CropMath
{
    /// <summary>Smallest frame side, as a fraction of the photo, so a frame can always be grabbed again.</summary>
    public const double MinSize = 0.05;

    /// <summary>Width / height of the shape in pixels; null when any shape goes.</summary>
    public static double? Ratio(CropAspect aspect, int imageWidth, int imageHeight) => aspect switch
    {
        CropAspect.Portrait => 4.0 / 5.0,
        CropAspect.Square => 1.0,
        CropAspect.Landscape => 1.91,
        CropAspect.Story => 9.0 / 16.0,
        CropAspect.Original => imageHeight > 0 ? (double)imageWidth / imageHeight : 1.0,
        _ => null,
    };

    /// <summary>The biggest frame of that shape, centred on <paramref name="centerX"/>, <paramref name="centerY"/>.</summary>
    public static CropRect Fit(CropAspect aspect, int imageWidth, int imageHeight, double centerX = 0.5, double centerY = 0.5)
    {
        if (Ratio(aspect, imageWidth, imageHeight) is not { } ratio || imageWidth <= 0 || imageHeight <= 0)
            return CropRect.Full;

        var imageRatio = (double)imageWidth / imageHeight;
        var (width, height) = ratio >= imageRatio ? (1.0, imageRatio / ratio) : (ratio / imageRatio, 1.0);
        return new CropRect(centerX - width / 2, centerY - height / 2, width, height).Clamped();
    }

    /// <summary>Switching shape keeps the frame where it was, as big as the new shape allows.</summary>
    public static CropRect ChangeAspect(CropRect current, CropAspect aspect, int imageWidth, int imageHeight) =>
        aspect == CropAspect.Free
            ? current.Clamped()
            : Fit(aspect, imageWidth, imageHeight, current.X + current.Width / 2, current.Y + current.Height / 2);

    public static CropRect Move(CropRect rect, double dx, double dy) =>
        (rect with { X = rect.X + dx, Y = rect.Y + dy }).Clamped();

    /// <summary>Makes the frame <paramref name="factor"/> times bigger around its centre, keeping its shape and staying inside the photo.</summary>
    public static CropRect Scale(CropRect rect, double factor)
    {
        if (factor <= 0 || double.IsNaN(factor))
            return rect;

        var width = rect.Width * factor;
        var height = rect.Height * factor;
        var fit = Math.Min(1, Math.Min(1 / width, 1 / height));
        width *= fit;
        height *= fit;
        var grow = Math.Max(1, Math.Max(MinSize / width, MinSize / height));
        width = Math.Min(1, width * grow);
        height = Math.Min(1, height * grow);
        var centerX = rect.X + rect.Width / 2;
        var centerY = rect.Y + rect.Height / 2;
        return new CropRect(centerX - width / 2, centerY - height / 2, width, height).Clamped();
    }

    /// <summary>
    /// Drags a corner to (<paramref name="x"/>, <paramref name="y"/>) while the opposite corner stays put, keeping the
    /// shape when <paramref name="aspect"/> has one, and never leaving the photo.
    /// </summary>
    public static CropRect Resize(CropRect rect, CropHandle corner, double x, double y, CropAspect aspect, int imageWidth, int imageHeight)
    {
        var (anchorX, anchorY, signX, signY) = corner switch
        {
            CropHandle.TopLeft => (rect.Right, rect.Bottom, -1, -1),
            CropHandle.TopRight => (rect.X, rect.Bottom, 1, -1),
            CropHandle.BottomLeft => (rect.Right, rect.Y, -1, 1),
            CropHandle.BottomRight => (rect.X, rect.Y, 1, 1),
            _ => (double.NaN, double.NaN, 0, 0),
        };
        if (double.IsNaN(anchorX))
            return rect;

        // Room between the fixed corner and the photo's edges on the side being dragged.
        var roomX = signX > 0 ? 1 - anchorX : anchorX;
        var roomY = signY > 0 ? 1 - anchorY : anchorY;
        var width = Math.Clamp((x - anchorX) * signX, MinSize, roomX);
        var height = Math.Clamp((y - anchorY) * signY, MinSize, roomY);

        if (Ratio(aspect, imageWidth, imageHeight) is { } ratio && imageWidth > 0 && imageHeight > 0)
        {
            // In fractions, a ratio r means height = width * W / (H * r).
            var k = (double)imageWidth / imageHeight / ratio;
            if (height > width * k)
                height = width * k;
            else
                width = height / k;

            if (height > roomY)
                (height, width) = (roomY, roomY / k);
            if (width > roomX)
                (width, height) = (roomX, roomX * k);
        }

        var left = signX > 0 ? anchorX : anchorX - width;
        var top = signY > 0 ? anchorY : anchorY - height;
        return new CropRect(left, top, width, height).Clamped();
    }

    /// <summary>What a press at (<paramref name="x"/>, <paramref name="y"/>) grabs; the grab sizes are how near a corner counts, in fractions.</summary>
    public static CropHandle HitTest(CropRect rect, double x, double y, double grabX, double grabY)
    {
        bool Near(double a, double b, double size) => Math.Abs(a - b) <= size;
        if (Near(x, rect.X, grabX) && Near(y, rect.Y, grabY))
            return CropHandle.TopLeft;
        if (Near(x, rect.Right, grabX) && Near(y, rect.Y, grabY))
            return CropHandle.TopRight;
        if (Near(x, rect.X, grabX) && Near(y, rect.Bottom, grabY))
            return CropHandle.BottomLeft;
        if (Near(x, rect.Right, grabX) && Near(y, rect.Bottom, grabY))
            return CropHandle.BottomRight;
        return x >= rect.X && x <= rect.Right && y >= rect.Y && y <= rect.Bottom ? CropHandle.Move : CropHandle.None;
    }

    /// <summary>The frame in whole pixels of a <paramref name="imageWidth"/> × <paramref name="imageHeight"/> photo.</summary>
    public static (int X, int Y, int Width, int Height) ToPixels(CropRect rect, int imageWidth, int imageHeight)
    {
        var r = rect.Clamped();
        var x = (int)Math.Round(r.X * imageWidth);
        var y = (int)Math.Round(r.Y * imageHeight);
        var width = Math.Clamp((int)Math.Round(r.Width * imageWidth), 1, imageWidth - x);
        var height = Math.Clamp((int)Math.Round(r.Height * imageHeight), 1, imageHeight - y);
        return (x, y, width, height);
    }
}
