namespace Lumy.Core.Tests;

public class PhotoEditTests
{
    [Fact]
    public void Round_trips_through_json()
    {
        var edit = new PhotoEdit { Aspect = CropAspect.Portrait, Crop = new CropRect(0.1, 0, 0.5, 1), Exposure = 0.7, Contrast = 25, Vibrance = -10 };
        Assert.Equal(edit, PhotoEdit.FromJson(edit.ToJson()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not json")]
    public void Unreadable_json_is_no_edit(string? json) => Assert.Null(PhotoEdit.FromJson(json));

    [Fact]
    public void Normalizing_clamps_sliders_and_crop()
    {
        var edit = new PhotoEdit { Exposure = 9, Contrast = -500, Saturation = double.NaN, Crop = new CropRect(0.9, -1, 0.5, 2) }.Normalized();
        Assert.Equal(3, edit.Exposure);
        Assert.Equal(-100, edit.Contrast);
        Assert.Equal(0, edit.Saturation);
        Assert.Equal(new CropRect(0.5, 0, 0.5, 1), edit.Crop);
    }

    [Fact]
    public void Empty_means_no_crop_and_no_sliders()
    {
        Assert.True(PhotoEdit.None.IsEmpty);
        Assert.False((PhotoEdit.None with { Shadows = 5 }).IsEmpty);
        Assert.False((PhotoEdit.None with { Crop = new CropRect(0, 0, 0.5, 1) }).IsEmpty);
        Assert.True((PhotoEdit.None with { Crop = new CropRect(0, 0, 0.5, 1) }).HasNoAdjustments);
    }

    [Fact]
    public void Sets_sliders_by_name() =>
        Assert.Equal(40, PhotoEdit.None.With("Shadows", 40).Shadows);

    [Fact]
    public void Summary_names_what_changed()
    {
        Assert.Equal("без правок", PhotoEdit.None.Summary());
        var summary = new PhotoEdit { Exposure = 0.5, Contrast = 20, Aspect = CropAspect.Portrait, Crop = new CropRect(0.2, 0, 0.6, 1) }.Summary();
        Assert.Equal("экспозиция +0,5 · контраст +20 · кроп 4:5", summary);
    }
}

public class CropMathTests
{
    private static double PixelRatio(CropRect r, int w, int h) => r.Width * w / (r.Height * h);

    [Theory]
    [InlineData(CropAspect.Portrait, 0.8)]
    [InlineData(CropAspect.Square, 1.0)]
    [InlineData(CropAspect.Landscape, 1.91)]
    [InlineData(CropAspect.Story, 0.5625)]
    public void Fit_makes_the_biggest_frame_of_that_shape(CropAspect aspect, double ratio)
    {
        foreach (var (w, h) in new[] { (6000, 4000), (4000, 6000), (3000, 3000) })
        {
            var r = CropMath.Fit(aspect, w, h);
            Assert.Equal(ratio, PixelRatio(r, w, h), 3);
            Assert.True(Math.Abs(r.Width - 1) < 1e-9 || Math.Abs(r.Height - 1) < 1e-9, "touches two edges");
            Assert.Equal(0.5, r.X + r.Width / 2, 6);
        }
    }

    [Fact]
    public void Original_and_free_keep_the_whole_photo()
    {
        Assert.Equal(CropRect.Full, CropMath.Fit(CropAspect.Original, 6000, 4000));
        Assert.Equal(CropRect.Full, CropMath.Fit(CropAspect.Free, 6000, 4000));
    }

    [Fact]
    public void Moving_stays_inside_the_photo()
    {
        var r = CropMath.Move(new CropRect(0.2, 0.2, 0.5, 0.5), 0.9, -0.9);
        Assert.Equal(new CropRect(0.5, 0, 0.5, 0.5), r);
    }

    [Fact]
    public void Resizing_keeps_the_shape_and_the_opposite_corner()
    {
        const int w = 6000, h = 4000;
        var start = CropMath.Fit(CropAspect.Portrait, w, h);
        var r = CropMath.Resize(start, CropHandle.BottomRight, start.X + 0.1, 0.6, CropAspect.Portrait, w, h);
        Assert.Equal(start.X, r.X, 9);
        Assert.Equal(start.Y, r.Y, 9);
        Assert.Equal(0.8, PixelRatio(r, w, h), 6);
        Assert.True(r.Width < start.Width);
    }

    [Fact]
    public void Resizing_past_the_edge_stops_at_it()
    {
        var r = CropMath.Resize(new CropRect(0.4, 0.4, 0.2, 0.2), CropHandle.TopLeft, -5, -5, CropAspect.Free, 1000, 1000);
        Assert.Equal(0, r.X, 9);
        Assert.Equal(0, r.Y, 9);
        Assert.Equal(0.6, r.Width, 9);
        Assert.Equal(0.6, r.Height, 9);

        var square = CropMath.Resize(new CropRect(0.4, 0.4, 0.2, 0.2), CropHandle.BottomRight, 5, 0.9, CropAspect.Square, 2000, 1000);
        Assert.Equal(1.0, PixelRatio(square, 2000, 1000), 6);
        Assert.True(square.Right <= 1 + 1e-9 && square.Bottom <= 1 + 1e-9);
    }

    [Fact]
    public void Resizing_never_collapses_the_frame()
    {
        var r = CropMath.Resize(new CropRect(0.4, 0.4, 0.2, 0.2), CropHandle.BottomRight, 0, 0, CropAspect.Free, 1000, 1000);
        Assert.True(r.Width >= CropMath.MinSize - 1e-9 && r.Height >= CropMath.MinSize - 1e-9);
    }

    [Fact]
    public void Hit_test_finds_corners_then_the_inside()
    {
        var r = new CropRect(0.2, 0.2, 0.6, 0.6);
        Assert.Equal(CropHandle.TopLeft, CropMath.HitTest(r, 0.21, 0.19, 0.03, 0.03));
        Assert.Equal(CropHandle.BottomRight, CropMath.HitTest(r, 0.8, 0.8, 0.03, 0.03));
        Assert.Equal(CropHandle.Move, CropMath.HitTest(r, 0.5, 0.5, 0.03, 0.03));
        Assert.Equal(CropHandle.None, CropMath.HitTest(r, 0.05, 0.5, 0.03, 0.03));
    }

    [Fact]
    public void Changing_shape_keeps_the_centre()
    {
        var r = CropMath.ChangeAspect(new CropRect(0.4, 0, 0.4, 1), CropAspect.Square, 3000, 2000);
        Assert.Equal(0.6, r.X + r.Width / 2, 6);
        Assert.Equal(1.0, PixelRatio(r, 3000, 2000), 6);
    }

    [Fact]
    public void Pixels_stay_inside_the_image()
    {
        var (x, y, width, height) = CropMath.ToPixels(new CropRect(0.5, 0.5, 0.5, 0.5), 1001, 999);
        Assert.True(x + width <= 1001 && y + height <= 999);
        Assert.True(width > 0 && height > 0);
    }
}

public class ImageAdjustTests
{
    private static byte[] Pixels(params (byte B, byte G, byte R)[] colours) =>
        colours.SelectMany(c => new[] { c.B, c.G, c.R, (byte)255 }).ToArray();

    [Fact]
    public void Zero_sliders_change_nothing()
    {
        var (blue, green, red) = ImageAdjust.Curves(PhotoEdit.None);
        for (var v = 0; v < 256; v++)
        {
            Assert.Equal(v, blue[v]);
            Assert.Equal(v, green[v]);
            Assert.Equal(v, red[v]);
        }
    }

    [Fact]
    public void Every_curve_rises_even_at_the_extremes()
    {
        foreach (var value in new[] { -100.0, 100.0 })
        {
            foreach (var edit in new[]
            {
                PhotoEdit.None with { Shadows = value }, PhotoEdit.None with { Highlights = value },
                PhotoEdit.None with { Contrast = value }, PhotoEdit.None with { Exposure = value / 33, Shadows = value, Highlights = -value, Contrast = value },
            })
            {
                var (_, green, _) = ImageAdjust.Curves(edit);
                for (var v = 1; v < 256; v++)
                    Assert.True(green[v] >= green[v - 1], $"{edit.Summary()} falls at {v}");
            }
        }
    }

    [Fact]
    public void Exposure_brightens_and_darkens()
    {
        var bright = Pixels((100, 100, 100));
        ImageAdjust.Apply(bright, 1, 1, 4, PhotoEdit.None with { Exposure = 1 });
        Assert.True(bright[1] > 120);

        var dark = Pixels((100, 100, 100));
        ImageAdjust.Apply(dark, 1, 1, 4, PhotoEdit.None with { Exposure = -1 });
        Assert.True(dark[1] < 80);
    }

    [Fact]
    public void Warmer_means_more_red_and_less_blue()
    {
        var pixels = Pixels((128, 128, 128));
        ImageAdjust.Apply(pixels, 1, 1, 4, PhotoEdit.None with { Temperature = 60 });
        Assert.True(pixels[2] > 128 && pixels[0] < 128);
    }

    [Fact]
    public void No_saturation_is_grey_and_alpha_is_kept()
    {
        var pixels = Pixels((30, 90, 200));
        ImageAdjust.Apply(pixels, 1, 1, 4, PhotoEdit.None with { Saturation = -100 });
        Assert.Equal(pixels[0], pixels[1]);
        Assert.Equal(pixels[1], pixels[2]);
        Assert.Equal(255, pixels[3]);
    }

    [Fact]
    public void Vibrance_boosts_dull_colours_more_than_strong_ones()
    {
        var dull = Pixels((110, 120, 140));
        var strong = Pixels((10, 40, 240));
        ImageAdjust.Apply(dull, 1, 1, 4, PhotoEdit.None with { Vibrance = 80 });
        ImageAdjust.Apply(strong, 1, 1, 4, PhotoEdit.None with { Vibrance = 80 });
        var dullGain = (dull[2] - dull[0]) / 30.0;
        var strongGain = (strong[2] - strong[0]) / 230.0;
        Assert.True(dullGain > strongGain);
    }

    [Fact]
    public void Respects_the_stride()
    {
        // Two rows of one pixel, each padded to 8 bytes: the padding must stay untouched.
        var pixels = new byte[] { 100, 100, 100, 255, 7, 7, 7, 7, 100, 100, 100, 255, 7, 7, 7, 7 };
        ImageAdjust.Apply(pixels, 1, 2, 8, PhotoEdit.None with { Exposure = 1 });
        Assert.Equal(new byte[] { 7, 7, 7, 7 }, pixels[4..8]);
        Assert.Equal(pixels[0], pixels[8]);
    }
}

public class ExportPlanTests
{
    [Fact]
    public void Names_are_jpg_and_numbered_for_albums()
    {
        Assert.Equal("IMG_0001.jpg", ExportPlan.FileName(@"C:\a\IMG_0001.CR3"));
        Assert.Equal("03_IMG_0001.jpg", ExportPlan.FileName(@"C:\a\IMG_0001.JPG", 3, 12));
        Assert.Equal("007_IMG_0001.jpg", ExportPlan.FileName("IMG_0001.heic", 7, 120));
    }

    [Fact]
    public void Never_overwrites()
    {
        var taken = new HashSet<string> { Path.Combine("out", "a.jpg"), Path.Combine("out", "a (2).jpg") };
        Assert.Equal(Path.Combine("out", "a (3).jpg"), ExportPlan.UniquePath("out", "a.jpg", taken.Contains));
    }

    [Theory]
    [InlineData(ExportSize.Instagram, 4800, 6000, 1080, 1350)]
    [InlineData(ExportSize.Instagram, 6000, 6000, 1080, 1080)]
    [InlineData(ExportSize.Instagram, 800, 1000, 800, 1000)]
    [InlineData(ExportSize.LongEdge2048, 6000, 4000, 2048, 1365)]
    [InlineData(ExportSize.Full, 6000, 4000, 6000, 4000)]
    public void Sizes_never_enlarge(ExportSize size, int w, int h, int expectedW, int expectedH) =>
        Assert.Equal((expectedW, expectedH), ExportPlan.TargetSize(size, w, h));

    [Fact]
    public void Warns_when_instagram_would_crop()
    {
        Assert.Null(ExportPlan.InstagramWarning(1080, 1350));
        Assert.Null(ExportPlan.InstagramWarning(1080, 566));
        Assert.NotNull(ExportPlan.InstagramWarning(1080, 1920));
        Assert.NotNull(ExportPlan.InstagramWarning(3000, 1000));
    }

    [Fact]
    public void Link_or_copy_leaves_a_readable_file()
    {
        using var temp = new TempFolder();
        var source = temp.File("a.jpg", "photo");
        var target = Path.Combine(temp.Path, "out.jpg");
        FileLinks.LinkOrCopy(source, target);
        Assert.Equal("photo", File.ReadAllText(target));
        Assert.Equal("photo", File.ReadAllText(source));
    }
}

public class AlbumTests
{
    private static readonly DateTime Modified = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static List<PhotoRecord> Photos(LibraryDatabase db, int count) =>
        db.Sync(Enumerable.Range(1, count).Select(i => new PhotoFile($@"C:\shoot\{i}.jpg", null, 10, Modified)).ToList(), p => p, _ => null, _ => true).ToList();

    [Fact]
    public void Albums_keep_order_and_skip_duplicates()
    {
        using var db = new LibraryDatabase(":memory:");
        var photos = Photos(db, 4);
        var album = db.CreateAlbum("  Карусель 1 ");

        Assert.Equal(3, db.AddToAlbum(album.Id, [photos[2].Id, photos[0].Id, photos[2].Id, photos[3].Id]));
        Assert.Equal(0, db.AddToAlbum(album.Id, [photos[0].Id]));
        Assert.Equal([photos[2].Id, photos[0].Id, photos[3].Id], db.AlbumPhotos(album.Id).Select(p => p.Id));

        var listed = Assert.Single(db.Albums());
        Assert.Equal("Карусель 1", listed.Name);
        Assert.Equal(3, listed.Count);
        Assert.Equal(photos[2].Id, listed.CoverPhotoId);
    }

    [Fact]
    public void Reorders_removes_renames_and_deletes()
    {
        using var db = new LibraryDatabase(":memory:");
        var photos = Photos(db, 3);
        var album = db.CreateAlbum("A");
        db.AddToAlbum(album.Id, photos.Select(p => p.Id));

        db.SetAlbumOrder(album.Id, [photos[2].Id, photos[0].Id]);
        Assert.Equal([photos[2].Id, photos[0].Id, photos[1].Id], db.AlbumPhotos(album.Id).Select(p => p.Id));

        db.RemoveFromAlbum(album.Id, [photos[0].Id]);
        Assert.Equal([photos[2].Id, photos[1].Id], db.AlbumPhotos(album.Id).Select(p => p.Id));

        db.RenameAlbum(album.Id, "");
        Assert.Equal("Без названия", db.Albums()[0].Name);

        db.DeleteAlbum(album.Id);
        Assert.Empty(db.Albums());
        Assert.Equal(3, Photos(db, 3).Count); // the photos themselves stay
    }

    [Fact]
    public void Edits_are_saved_and_cleared()
    {
        using var db = new LibraryDatabase(":memory:");
        var photos = Photos(db, 2);
        var edit = new PhotoEdit { Exposure = 0.5, Aspect = CropAspect.Square, Crop = new CropRect(0.1, 0, 0.6, 1) };
        db.SetEdit(photos[0].Id, edit);

        var edits = db.GetEdits(photos.Select(p => p.Id));
        Assert.Equal(edit, Assert.Single(edits).Value);

        db.SetEdit(photos[0].Id, PhotoEdit.None);
        Assert.Empty(db.GetEdits(photos.Select(p => p.Id)));
    }

    [Fact]
    public void Upgrades_a_version_2_library()
    {
        using var temp = new TempFolder();
        var path = Path.Combine(temp.Path, "library.db");
        using (var db = new LibraryDatabase(path))
        {
            Photos(db, 1);
        }

        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE album_photos; DROP TABLE albums; DROP TABLE edits; PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }

        using var upgraded = new LibraryDatabase(path);
        var album = upgraded.CreateAlbum("после обновления");
        Assert.Equal(1, upgraded.AddToAlbum(album.Id, [Photos(upgraded, 1)[0].Id]));
    }
}
