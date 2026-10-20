namespace Lumy.Core;

/// <summary>What series detection needs to know about a photo.</summary>
public readonly record struct SeriesInput(long Id, DateTime? CapturedAt, ulong? Hash);

/// <summary>
/// Groups a shoot into series: runs of frames shot within a few seconds of each other that also look alike.
/// From each series usually only one frame makes the post, so swipe, compare and the tournament work per series.
/// </summary>
public static class SeriesDetector
{
    /// <summary>Longest pause between two frames of one series.</summary>
    public static readonly TimeSpan DefaultMaxGap = TimeSpan.FromSeconds(4);

    /// <summary>Hashes up to this many bits apart count as the same scene (out of 64).</summary>
    public const int DefaultMaxDistance = 14;

    /// <summary>
    /// Returns the series as lists of photo ids in capture order, singletons included.
    /// Photos without a capture time each stay on their own. A photo without a hash joins on time alone.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<long>> Group(
        IEnumerable<SeriesInput> photos, TimeSpan? maxGap = null, int maxDistance = DefaultMaxDistance)
    {
        var gap = maxGap ?? DefaultMaxGap;
        var result = new List<IReadOnlyList<long>>();
        List<long>? current = null;
        SeriesInput previous = default;

        foreach (var photo in photos.OrderBy(p => p.CapturedAt ?? DateTime.MaxValue).ThenBy(p => p.Id))
        {
            var joins = current is not null
                && photo.CapturedAt is { } taken
                && previous.CapturedAt is { } before
                && taken - before <= gap
                && (photo.Hash is not { } h || previous.Hash is not { } ph || PerceptualHash.Distance(h, ph) <= maxDistance);

            if (joins)
            {
                current!.Add(photo.Id);
            }
            else
            {
                current = [photo.Id];
                result.Add(current);
            }

            previous = photo;
        }

        return result;
    }
}
