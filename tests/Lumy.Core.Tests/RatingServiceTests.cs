namespace Lumy.Core.Tests;

public class RatingServiceTests
{
    private sealed class Photo(long id, string? raw = null) : IRatedPhoto
    {
        public long Id { get; } = id;
        public string? RawPath { get; } = raw;
        public Tier Tier { get; set; }
    }

    private static (LibraryDatabase Db, List<Photo> Photos) Library(TempFolder folder, int count)
    {
        var db = new LibraryDatabase(":memory:");
        var files = Enumerable.Range(0, count)
            .Select(i => new PhotoFile(folder.File($"p{i}.jpg"), folder.File($"p{i}.cr3"), 1, DateTime.UtcNow))
            .ToList();
        var records = db.Sync(files, p => p, _ => null, _ => true);
        return (db, records.Select(r => new Photo(r.Id, r.RawPath)).ToList());
    }

    [Fact]
    public void Rate_saves_tier_and_writes_sidecar()
    {
        using var folder = new TempFolder();
        var (db, photos) = Library(folder, 2);
        using var _ = db;
        var service = new RatingService(db);

        service.Rate([photos[0]], Tier.SuperLike);

        Assert.Equal(Tier.SuperLike, photos[0].Tier);
        Assert.Equal(Tier.SuperLike, db.Get(photos[0].Id)!.Tier);
        Assert.Equal(5, XmpSidecar.ReadRating(photos[0].RawPath!));
        Assert.Equal(Tier.None, db.Get(photos[1].Id)!.Tier);
    }

    [Fact]
    public void Undo_and_redo_restore_previous_tiers()
    {
        using var folder = new TempFolder();
        var (db, photos) = Library(folder, 2);
        using var _ = db;
        var service = new RatingService(db);
        service.Rate([photos[0]], Tier.Like);

        service.Rate(photos, Tier.Rejected);
        var undone = service.Undo();

        Assert.Equal(2, undone.Count);
        Assert.Equal(Tier.Like, photos[0].Tier);
        Assert.Equal(Tier.None, photos[1].Tier);
        Assert.Equal(Tier.None, db.Get(photos[1].Id)!.Tier);
        Assert.Null(XmpSidecar.ReadRating(photos[1].RawPath!));

        service.Redo();
        Assert.All(photos, p => Assert.Equal(Tier.Rejected, p.Tier));
        Assert.Equal(-1, XmpSidecar.ReadRating(photos[0].RawPath!));
    }

    [Fact]
    public void Rating_to_same_tier_adds_no_history()
    {
        using var folder = new TempFolder();
        var (db, photos) = Library(folder, 1);
        using var _ = db;
        var service = new RatingService(db);

        service.Rate(photos, Tier.None);

        Assert.False(service.CanUndo);
    }

    [Fact]
    public void Sidecars_are_skipped_when_disabled()
    {
        using var folder = new TempFolder();
        var (db, photos) = Library(folder, 1);
        using var _ = db;
        var service = new RatingService(db) { WriteXmpSidecars = false };

        service.Rate(photos, Tier.Like);

        Assert.False(File.Exists(XmpSidecar.PathFor(photos[0].RawPath!)));
    }

    [Fact]
    public void New_rating_clears_redo()
    {
        using var folder = new TempFolder();
        var (db, photos) = Library(folder, 1);
        using var _ = db;
        var service = new RatingService(db);
        service.Rate(photos, Tier.Like);
        service.Undo();

        service.Rate(photos, Tier.SuperLike);

        Assert.False(service.CanRedo);
    }
}
