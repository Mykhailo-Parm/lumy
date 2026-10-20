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

public class CameraRawXmpTests
{
    [Fact]
    public void Writes_and_removes_develop_settings_keeping_the_rating()
    {
        using var temp = new TempFolder();
        var raw = temp.File("IMG_1.CR3");
        XmpSidecar.SetRating(raw, 4);
        XmpSidecar.SetDevelop(raw, new PhotoEdit { Exposure = 0.5, Contrast = -20, Aspect = CropAspect.Square, Crop = new CropRect(0.25, 0, 0.5, 1) });

        var xml = File.ReadAllText(XmpSidecar.PathFor(raw));
        Assert.Contains("Exposure2012=\"+0.50\"", xml);
        Assert.Contains("Contrast2012=\"-20\"", xml);
        Assert.Contains("HasCrop=\"True\"", xml);
        Assert.Contains("CropLeft=\"0.25\"", xml);
        Assert.Contains("CropRight=\"0.75\"", xml);
        Assert.Contains("ProcessVersion=\"11.0\"", xml);
        Assert.Equal(4, XmpSidecar.ReadRating(raw));

        XmpSidecar.SetDevelop(raw, null);
        xml = File.ReadAllText(XmpSidecar.PathFor(raw));
        Assert.DoesNotContain("Exposure2012", xml);
        Assert.Equal(4, XmpSidecar.ReadRating(raw));
    }

    [Fact]
    public void An_empty_edit_creates_no_sidecar()
    {
        using var temp = new TempFolder();
        var raw = temp.File("IMG_2.NEF");
        XmpSidecar.SetDevelop(raw, PhotoEdit.None);
        Assert.False(File.Exists(XmpSidecar.PathFor(raw)));
    }

    [Theory]
    [InlineData(1, 0.1, 0.2, 0.3, 0.4)]
    [InlineData(3, 0.6, 0.4, 0.3, 0.4)]
    [InlineData(6, 0.2, 0.6, 0.4, 0.3)]
    [InlineData(8, 0.4, 0.1, 0.4, 0.3)]
    public void Maps_the_crop_to_the_sensor_frame(int orientation, double x, double y, double w, double h)
    {
        var sensor = XmpSidecar.ToSensor(new CropRect(0.1, 0.2, 0.3, 0.4), orientation);
        Assert.Equal(x, sensor.X, 9);
        Assert.Equal(y, sensor.Y, 9);
        Assert.Equal(w, sensor.Width, 9);
        Assert.Equal(h, sensor.Height, 9);
    }
}
