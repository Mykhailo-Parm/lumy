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
