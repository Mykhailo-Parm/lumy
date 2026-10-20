namespace Lumy.Core.Tests;

public class PhotoQueryTests
{
    [Theory]
    [InlineData(TierFilter.All, Tier.Rejected, true)]
    [InlineData(TierFilter.LikedOrBetter, Tier.Like, true)]
    [InlineData(TierFilter.LikedOrBetter, Tier.SuperLike, true)]
    [InlineData(TierFilter.LikedOrBetter, Tier.None, false)]
    [InlineData(TierFilter.SuperLikes, Tier.Like, false)]
    [InlineData(TierFilter.Unrated, Tier.None, true)]
    [InlineData(TierFilter.Rejected, Tier.None, false)]
    public void Filter_matches_tiers(TierFilter filter, Tier tier, bool expected) =>
        Assert.Equal(expected, filter.Matches(tier));

    [Fact]
    public void Sorts_by_capture_time_then_name()
    {
        var file = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        PhotoRecord Photo(string name, DateTime? taken) => new(0, name, null, "", 0, file, taken, Tier.None);
        var photos = new[]
        {
            Photo("c.jpg", new DateTime(2026, 5, 1, 10, 0, 2)),
            Photo("b.jpg", new DateTime(2026, 5, 1, 10, 0, 1)),
            Photo("a.jpg", new DateTime(2026, 5, 1, 10, 0, 1)),
        };

        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], photos.Sort(PhotoSort.CaptureTime).Select(p => p.FileName));
    }

    [Fact]
    public void Tier_maps_to_xmp_rating()
    {
        Assert.Equal(4, Tier.Like.ToXmpRating());
        Assert.Equal(5, Tier.SuperLike.ToXmpRating());
        Assert.Equal(-1, Tier.Rejected.ToXmpRating());
        Assert.Null(Tier.None.ToXmpRating());
    }
}
