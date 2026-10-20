namespace Lumy.Core.Tests;

public class FolderScannerTests
{
    [Fact]
    public void Pairs_raw_with_jpeg_of_the_same_name()
    {
        using var folder = new TempFolder();
        var jpg = folder.File("IMG_0001.JPG");
        var raw = folder.File("IMG_0001.CR3");
        folder.File("IMG_0002.jpg");
        folder.File("notes.txt");

        var photos = FolderScanner.Scan(folder.Path);

        Assert.Equal(2, photos.Count);
        Assert.Equal(jpg, photos[0].Path);
        Assert.Equal(raw, photos[0].RawPath);
        Assert.Null(photos[1].RawPath);
    }

    [Fact]
    public void Raw_without_jpeg_is_its_own_photo()
    {
        using var folder = new TempFolder();
        var raw = folder.File("DSC_1.NEF");

        var photo = Assert.Single(FolderScanner.Scan(folder.Path));

        Assert.Equal(raw, photo.Path);
        Assert.Equal(raw, photo.RawPath);
    }

    [Fact]
    public void Prefers_jpeg_over_other_formats_for_the_pair()
    {
        using var folder = new TempFolder();
        folder.File("A.png");
        var jpg = folder.File("A.jpg");
        var raw = folder.File("A.arw");

        var photos = FolderScanner.Scan(folder.Path);

        Assert.Equal(2, photos.Count);
        Assert.Equal(raw, photos.Single(p => p.Path == jpg).RawPath);
        Assert.Null(photos.Single(p => p.Path != jpg).RawPath);
    }

    [Fact]
    public void Skips_subfolders_unless_recursive()
    {
        using var folder = new TempFolder();
        folder.File("a.jpg");
        folder.File(System.IO.Path.Combine("sub", "b.jpg"));

        Assert.Single(FolderScanner.Scan(folder.Path));
        Assert.Equal(2, FolderScanner.Scan(folder.Path, recursive: true).Count);
    }
}
