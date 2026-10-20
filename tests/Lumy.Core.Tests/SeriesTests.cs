namespace Lumy.Core.Tests;

public class SeriesTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 15, 0, 0);

    [Fact]
    public void Hash_of_identical_images_matches_and_inverted_differs()
    {
        var gradient = Enumerable.Range(0, 72).Select(i => (byte)(255 - i * 3)).ToArray();
        var inverted = gradient.Select(b => (byte)(255 - b)).ToArray();

        var a = PerceptualHash.FromGrey(gradient);

        Assert.Equal(0, PerceptualHash.Distance(a, PerceptualHash.FromGrey(gradient)));
        Assert.True(PerceptualHash.Distance(a, PerceptualHash.FromGrey(inverted)) > 40);
    }

    [Fact]
    public void Bgra_is_converted_to_grey()
    {
        var grey = Enumerable.Range(0, 72).Select(i => (byte)(i * 3 % 256)).ToArray();
        var bgra = grey.SelectMany(g => new byte[] { g, g, g, 255 }).ToArray();

        Assert.Equal(PerceptualHash.FromGrey(grey), PerceptualHash.FromBgra(bgra));
    }

    [Fact]
    public void Groups_frames_close_in_time_and_similar()
    {
        const ulong scene = 0xF0F0_F0F0_F0F0_F0F0;
        const ulong other = 0x0F0F_0F0F_0F0F_0F0F;
        var photos = new[]
        {
            new SeriesInput(1, T0, scene),
            new SeriesInput(2, T0.AddSeconds(1), scene ^ 0b111),          // 3 bits off: same burst
            new SeriesInput(3, T0.AddSeconds(2), other),                  // close in time, different scene
            new SeriesInput(4, T0.AddSeconds(30), other),                 // same scene, but long pause
            new SeriesInput(5, T0.AddSeconds(31), null),                  // no hash yet: time decides
        };

        var series = SeriesDetector.Group(photos);

        Assert.Equal([[1L, 2L], [3L], [4L, 5L]], series.Select(s => s.ToArray()).ToArray());
    }

    [Fact]
    public void Photos_without_capture_time_stay_alone()
    {
        var series = SeriesDetector.Group([new(1, null, 1), new(2, null, 1)]);

        Assert.Equal(2, series.Count);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(3, 2, 2)]
    [InlineData(9, 3, 3)]
    public void Compare_layout_fills_a_wide_screen(int count, int rows, int columns) =>
        Assert.Equal((rows, columns), CompareLayout.Best(count, 1920, 1080));

    [Fact]
    public void Compare_layout_stacks_portrait_photos_side_by_side()
    {
        Assert.Equal((1, 4), CompareLayout.Best(4, 1920, 1080, aspect: 2.0 / 3));
    }

    [Fact]
    public void Hash_survives_the_database()
    {
        using var db = new LibraryDatabase(":memory:");
        var record = db.Sync([new PhotoFile(@"C:\a.jpg", null, 1, DateTime.UtcNow)], _ => "fp", _ => null, _ => true).Single();

        db.SetHashes([(record.Id, ulong.MaxValue - 5)]);

        Assert.Equal(ulong.MaxValue - 5, db.Get(record.Id)!.Hash);
    }
}
