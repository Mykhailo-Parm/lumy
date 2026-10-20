namespace Lumy.Core.Tests;

public class PhotoFingerprintTests
{
    [Fact]
    public void Same_content_same_fingerprint_different_content_different()
    {
        using var folder = new TempFolder();
        var a = folder.File("a.jpg", "hello");
        var b = folder.File("b.jpg", "hello");
        var c = folder.File("c.jpg", "hellO");

        Assert.Equal(PhotoFingerprint.Compute(a), PhotoFingerprint.Compute(b));
        Assert.NotEqual(PhotoFingerprint.Compute(a), PhotoFingerprint.Compute(c));
    }

    [Fact]
    public void Missing_exif_gives_no_capture_time()
    {
        using var folder = new TempFolder();
        Assert.Null(PhotoMetadata.TryReadCaptureTime(folder.File("x.jpg", "not a jpeg")));
    }
}

public class PhotoMetadataTests
{
    [Theory]
    [InlineData("Canon", "Canon EOS R6", "Canon EOS R6")]
    [InlineData("NIKON CORPORATION", "NIKON Z 6", "NIKON Z 6")]
    [InlineData("SONY", "ILCE-7M3", "SONY ILCE-7M3")]
    [InlineData(null, "X100V", "X100V")]
    [InlineData("FUJIFILM", null, "FUJIFILM")]
    [InlineData(null, null, null)]
    public void Camera_name_does_not_repeat_the_brand(string? make, string? model, string? expected) =>
        Assert.Equal(expected, PhotoMetadata.CameraName(make, model));

    [Fact]
    public void Summary_skips_missing_fields()
    {
        var details = new PhotoDetails("Canon EOS R6", null, "50 mm", "f/1.8", "1/500 с", 100, 6000, 4000);
        Assert.Equal("Canon EOS R6  ·  50 mm  ·  f/1.8  ·  1/500 с  ·  ISO 100  ·  6000 × 4000", details.Summary);
        Assert.Equal("", new PhotoDetails(null, null, null, null, null, null, null, null).Summary);
    }

    [Theory]
    [InlineData(500, "500 Б")]
    [InlineData(2048, "2 КБ")]
    [InlineData(8598323, "8,2 МБ")]
    public void Formats_sizes(long bytes, string expected) => Assert.Equal(expected, PhotoMetadata.FormatSize(bytes));

    [Fact]
    public void Missing_or_non_photo_files_have_no_details()
    {
        using var folder = new TempFolder();
        var path = System.IO.Path.Combine(folder.Path, "a.jpg");
        File.WriteAllText(path, "not a photo");
        Assert.Null(PhotoMetadata.TryReadDetails(path));
    }
}
