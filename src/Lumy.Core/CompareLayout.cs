namespace Lumy.Core;

/// <summary>Picks how to arrange photos side by side so each one is shown as large as possible.</summary>
public static class CompareLayout
{
    /// <summary>
    /// Returns the rows and columns that give every photo the biggest area in a <paramref name="width"/> × <paramref name="height"/>
    /// area, for photos of the given aspect ratio (width / height; 1.5 for a landscape 3:2 frame).
    /// </summary>
    public static (int Rows, int Columns) Best(int count, double width, double height, double aspect = 1.5)
    {
        if (count <= 0)
            return (0, 0);

        var best = (Rows: 1, Columns: count);
        var bestArea = -1.0;
        for (var columns = 1; columns <= count; columns++)
        {
            var rows = (count + columns - 1) / columns;
            var cellWidth = width / columns;
            var cellHeight = height / rows;
            var imageWidth = Math.Min(cellWidth, cellHeight * aspect);
            var area = imageWidth * (imageWidth / aspect);
            if (area > bestArea + 0.5)
            {
                bestArea = area;
                best = (rows, columns);
            }
        }

        return best;
    }
}
