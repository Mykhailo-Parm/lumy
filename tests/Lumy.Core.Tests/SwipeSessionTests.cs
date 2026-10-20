namespace Lumy.Core.Tests;

public class SwipeSessionTests
{
    private sealed class Photo(long id, int series = 0) : IRatedPhoto
    {
        public long Id { get; } = id;
        public string? RawPath => null;
        public Tier Tier { get; set; }
        public int Series { get; } = series;
        public override string ToString() => $"#{Id}";
    }

    private static (SwipeSession<Photo> Session, List<Photo> Photos, LibraryDatabase Db) Start(params int[] series)
    {
        var db = new LibraryDatabase(":memory:");
        var records = db.Sync(
            series.Select((_, i) => new PhotoFile($@"C:\p{i}.jpg", null, 1, DateTime.UtcNow)).ToList(), p => p, _ => null, _ => true);
        var photos = records.Select((r, i) => new Photo(r.Id, series[i])).ToList();
        return (new SwipeSession<Photo>(photos, new RatingService(db)), photos, db);
    }

    [Fact]
    public void Decisions_rate_and_advance()
    {
        var (session, photos, db) = Start(0, 0, 0);
        using var _ = db;

        session.Decide(Tier.Like);
        session.Decide(Tier.Rejected);
        session.Decide(Tier.SuperLike);

        Assert.True(session.IsFinished);
        Assert.Equal([Tier.Like, Tier.Rejected, Tier.SuperLike], photos.Select(p => p.Tier));
        Assert.Equal((1, 1, 1), (session.Liked, session.SuperLiked, session.Rejected));
        Assert.Equal(Tier.Rejected, db.Get(photos[1].Id)!.Tier);
    }

    [Fact]
    public void Skipped_photo_comes_back_once_at_the_end()
    {
        var (session, photos, db) = Start(0, 0);
        using var _ = db;

        session.Skip();
        Assert.Same(photos[1], session.Current);
        session.Decide(Tier.Like);
        Assert.Same(photos[0], session.Current);
        session.Skip();

        Assert.True(session.IsFinished);
        Assert.Equal(1, session.Skipped);
        Assert.Equal(Tier.None, photos[0].Tier);
    }

    [Fact]
    public void Back_undoes_the_last_decision()
    {
        var (session, photos, db) = Start(0, 0);
        using var _ = db;
        session.Decide(Tier.Like);
        session.Decide(Tier.Rejected);

        session.Back();

        Assert.Same(photos[1], session.Current);
        Assert.Equal(Tier.None, photos[1].Tier);
        Assert.Equal(Tier.Like, photos[0].Tier);
        Assert.Equal(0, session.Rejected);
    }

    [Fact]
    public void Back_after_skip_removes_the_requeued_copy()
    {
        var (session, photos, db) = Start(0, 0);
        using var _ = db;
        session.Skip();

        session.Back();

        Assert.Same(photos[0], session.Current);
        Assert.Equal(2, session.Total);
    }

    [Fact]
    public void Rejecting_a_series_rates_the_whole_burst_and_back_restores_it()
    {
        var (session, photos, db) = Start(1, 1, 2, 1);
        using var _ = db;

        session.Decide(Tier.Rejected, p => p.Series == 1);

        Assert.Same(photos[2], session.Current);
        Assert.Equal(2, session.Total);
        Assert.All(new[] { photos[0], photos[1], photos[3] }, p => Assert.Equal(Tier.Rejected, p.Tier));
        Assert.Equal(3, session.Rejected);

        session.Back();

        Assert.Same(photos[0], session.Current);
        Assert.Equal(4, session.Total);
        Assert.All(photos, p => Assert.Equal(Tier.None, p.Tier));
        session.Decide(Tier.Like);
        Assert.Same(photos[1], session.Current);
    }
}
