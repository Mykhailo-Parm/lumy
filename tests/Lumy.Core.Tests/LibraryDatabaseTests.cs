namespace Lumy.Core.Tests;

public class LibraryDatabaseTests
{
    private static readonly DateTime Modified = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<PhotoRecord> Sync(LibraryDatabase db, IReadOnlyList<PhotoFile> files, Func<string, bool>? exists = null) =>
        db.Sync(files, FakeFingerprint, _ => null, exists ?? (_ => true));

    // Windows-style paths in these tests, so split by hand: "a.jpg" and "a_x.jpg" count as the same content.
    private static string FakeFingerprint(string path) =>
        "fp-" + path.Split('\\', '/')[^1].Split('.')[0].Replace("_x", "");

    [Fact]
    public void Keeps_tier_across_reopening_the_folder()
    {
        using var db = new LibraryDatabase(":memory:");
        var files = new[] { new PhotoFile(@"C:\shoot\a.jpg", null, 10, Modified) };

        var first = Sync(db, files).Single();
        db.SetTier([first.Id], Tier.SuperLike);
        var again = Sync(db, files).Single();

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(Tier.SuperLike, again.Tier);
    }

    [Fact]
    public void Renamed_file_keeps_its_tier()
    {
        using var db = new LibraryDatabase(":memory:");
        var original = Sync(db, [new PhotoFile(@"C:\shoot\a.jpg", null, 10, Modified)]).Single();
        db.SetTier([original.Id], Tier.Like);

        var renamed = Sync(db, [new PhotoFile(@"D:\moved\a_x.jpg", null, 10, Modified)], exists: p => p != @"C:\shoot\a.jpg").Single();

        Assert.Equal(original.Id, renamed.Id);
        Assert.Equal(@"D:\moved\a_x.jpg", renamed.Path);
        Assert.Equal(Tier.Like, renamed.Tier);
    }

    [Fact]
    public void Copy_of_a_file_that_still_exists_is_a_new_photo()
    {
        using var db = new LibraryDatabase(":memory:");
        var original = Sync(db, [new PhotoFile(@"C:\shoot\a.jpg", null, 10, Modified)]).Single();
        db.SetTier([original.Id], Tier.Like);

        var copy = Sync(db, [new PhotoFile(@"C:\other\a.jpg", null, 10, Modified)]).Single();

        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal(Tier.None, copy.Tier);
    }

    [Fact]
    public void Edited_file_keeps_tier_and_updates_size()
    {
        using var db = new LibraryDatabase(":memory:");
        var original = Sync(db, [new PhotoFile(@"C:\shoot\a.jpg", null, 10, Modified)]).Single();
        db.SetTier([original.Id], Tier.Rejected);

        var edited = Sync(db, [new PhotoFile(@"C:\shoot\a.jpg", null, 20, Modified.AddMinutes(1))]).Single();

        Assert.Equal(original.Id, edited.Id);
        Assert.Equal(20, edited.FileSize);
        Assert.Equal(Tier.Rejected, edited.Tier);
    }

    [Fact]
    public void Path_lookup_ignores_case()
    {
        using var db = new LibraryDatabase(":memory:");
        var a = Sync(db, [new PhotoFile(@"C:\Shoot\A.JPG", null, 10, Modified)]).Single();
        var b = Sync(db, [new PhotoFile(@"c:\shoot\a.jpg", null, 10, Modified)]).Single();

        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public void Stores_capture_time()
    {
        using var db = new LibraryDatabase(":memory:");
        var taken = new DateTime(2026, 9, 30, 18, 45, 12);

        var record = db.Sync([new PhotoFile(@"C:\a.jpg", null, 1, Modified)], _ => "fp", _ => taken, _ => true).Single();

        Assert.Equal(taken, record.CapturedAt);
        Assert.Equal(taken, db.Get(record.Id)!.CapturedAt);
    }

    [Fact]
    public void Settings_round_trip()
    {
        using var db = new LibraryDatabase(":memory:");
        var settings = new AppSettings(db);

        Assert.True(settings.AutoAdvance);
        settings.AutoAdvance = false;
        settings.ThumbnailSize = 256.5;

        Assert.False(settings.AutoAdvance);
        Assert.Equal(256.5, settings.ThumbnailSize);
        Assert.Null(settings.LastFolder);

        Assert.False(settings.RemoteEnabled);
        Assert.Null(settings.RemoteToken);
        Assert.Null(settings.RemotePort);
        settings.RemoteEnabled = true;
        settings.RemoteToken = "abc";
        settings.RemotePort = 8766;
        Assert.True(settings.RemoteEnabled);
        Assert.Equal("abc", settings.RemoteToken);
        Assert.Equal(8766, settings.RemotePort);
    }

    [Fact]
    public void Recent_folders_are_newest_first_without_duplicates()
    {
        using var db = new LibraryDatabase(":memory:");
        var settings = new AppSettings(db);
        Assert.Empty(settings.RecentFolders);

        settings.AddRecentFolder(@"D:\photos\a");
        settings.AddRecentFolder(@"D:\photos\b");
        settings.AddRecentFolder(@"d:\Photos\A\");
        Assert.Equal([@"d:\Photos\A\", @"D:\photos\b"], settings.RecentFolders);

        for (var i = 0; i < 20; i++)
            settings.AddRecentFolder($@"E:\shoot{i}");
        Assert.Equal(AppSettings.MaxRecentFolders, settings.RecentFolders.Count);
        Assert.Equal(@"E:\shoot19", settings.RecentFolders[0]);

        settings.ClearRecentFolders();
        Assert.Empty(settings.RecentFolders);
    }

    [Fact]
    public void Persists_to_disk()
    {
        using var folder = new TempFolder();
        var path = System.IO.Path.Combine(folder.Path, "nested", "library.db");
        long id;
        using (var db = new LibraryDatabase(path))
        {
            id = Sync(db, [new PhotoFile(@"C:\a.jpg", null, 1, Modified)]).Single().Id;
            db.SetTier([id], Tier.Like);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var reopened = new LibraryDatabase(path);
        Assert.Equal(Tier.Like, reopened.Get(id)!.Tier);
    }
}
