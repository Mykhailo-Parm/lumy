namespace Lumy.Core.Tests;

public class XmpSidecarTests
{
    private const string LightroomSidecar =
        """
        <x:xmpmeta xmlns:x="adobe:ns:meta/" x:xmptk="Adobe XMP Core 7.0-c000">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:xmp="http://ns.adobe.com/xap/1.0/"
            xmlns:crs="http://ns.adobe.com/camera-raw-settings/1.0/"
           xmp:Rating="2"
           crs:Exposure2012="+0.35"
           crs:CropTop="0.1">
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>
        """;

    [Fact]
    public void Creates_sidecar_with_rating()
    {
        using var folder = new TempFolder();
        var raw = folder.File("IMG_1.CR3");

        XmpSidecar.SetRating(raw, 5);

        Assert.True(File.Exists(System.IO.Path.Combine(folder.Path, "IMG_1.xmp")));
        Assert.Equal(5, XmpSidecar.ReadRating(raw));
    }

    [Fact]
    public void Updates_existing_sidecar_and_keeps_develop_settings()
    {
        using var folder = new TempFolder();
        var raw = folder.File("IMG_2.NEF");
        folder.File("IMG_2.xmp", LightroomSidecar);

        XmpSidecar.SetRating(raw, 4);

        var text = File.ReadAllText(XmpSidecar.PathFor(raw));
        Assert.Equal(4, XmpSidecar.ReadRating(raw));
        Assert.Contains("crs:Exposure2012=\"+0.35\"", text);
        Assert.Contains("crs:CropTop=\"0.1\"", text);
        Assert.DoesNotContain("xmp:Rating=\"2\"", text);
        Assert.DoesNotContain("p1:", text);
    }

    [Fact]
    public void Clearing_removes_rating_but_keeps_file()
    {
        using var folder = new TempFolder();
        var raw = folder.File("IMG_3.ARW");
        folder.File("IMG_3.xmp", LightroomSidecar);

        XmpSidecar.SetRating(raw, null);

        Assert.Null(XmpSidecar.ReadRating(raw));
        Assert.Contains("crs:Exposure2012", File.ReadAllText(XmpSidecar.PathFor(raw)));
    }

    [Fact]
    public void Clearing_without_sidecar_creates_nothing()
    {
        using var folder = new TempFolder();
        var raw = folder.File("IMG_4.RAF");

        XmpSidecar.SetRating(raw, null);

        Assert.False(File.Exists(XmpSidecar.PathFor(raw)));
    }

    [Fact]
    public void Replaces_rating_written_as_element()
    {
        using var folder = new TempFolder();
        var raw = folder.File("IMG_5.DNG");
        folder.File("IMG_5.xmp",
            """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/"><xmp:Rating>3</xmp:Rating></rdf:Description>
            </rdf:RDF></x:xmpmeta>
            """);

        XmpSidecar.SetRating(raw, -1);

        Assert.Equal(-1, XmpSidecar.ReadRating(raw));
        Assert.DoesNotContain("<xmp:Rating>", File.ReadAllText(XmpSidecar.PathFor(raw)));
    }
}
