namespace Lumy.Core;

public enum TierFilter
{
    All,
    LikedOrBetter,
    SuperLikes,
    Unrated,
    Rejected,
}

public enum PhotoSort
{
    CaptureTime,
    Name,
}

public static class PhotoQuery
{
    public static bool Matches(this TierFilter filter, Tier tier) => filter switch
    {
        TierFilter.LikedOrBetter => tier >= Tier.Like,
        TierFilter.SuperLikes => tier == Tier.SuperLike,
        TierFilter.Unrated => tier == Tier.None,
        TierFilter.Rejected => tier == Tier.Rejected,
        _ => true,
    };

    /// <summary>Orders photos by when they were taken, falling back to file time and then name for photos without EXIF.</summary>
    public static IEnumerable<PhotoRecord> Sort(this IEnumerable<PhotoRecord> photos, PhotoSort sort) => sort switch
    {
        PhotoSort.Name => photos.OrderBy(p => p.FileName, StringComparer.CurrentCultureIgnoreCase),
        _ => photos
            .OrderBy(p => p.CapturedAt ?? p.ModifiedUtc.ToLocalTime())
            .ThenBy(p => p.FileName, StringComparer.CurrentCultureIgnoreCase),
    };
}
