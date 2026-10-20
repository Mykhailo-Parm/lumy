namespace Lumy.Core;

/// <summary>How the user rated a photo. Stored as an integer in the library database.</summary>
public enum Tier
{
    Rejected = -1,
    None = 0,
    Like = 1,
    SuperLike = 2,
}

public static class TierExtensions
{
    /// <summary>Maps a tier to the standard <c>xmp:Rating</c> value that Lightroom and Bridge understand.</summary>
    public static int? ToXmpRating(this Tier tier) => tier switch
    {
        Tier.Rejected => -1,
        Tier.Like => 4,
        Tier.SuperLike => 5,
        _ => null,
    };
}
