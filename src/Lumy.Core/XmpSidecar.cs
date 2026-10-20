using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Lumy.Core;

/// <summary>
/// Writes the star rating into the <c>.xmp</c> sidecar next to a RAW file, the way Lightroom, Bridge and
/// Camera Raw read it. An existing sidecar is updated in place: everything else Lightroom stored there
/// (develop settings, keywords) is kept byte-for-byte apart from the rating.
/// </summary>
public static class XmpSidecar
{
    private static readonly XNamespace X = "adobe:ns:meta/";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Crs = "http://ns.adobe.com/camera-raw-settings/1.0/";

    /// <summary>The Camera Raw settings Lumy writes; any other develop setting in the sidecar is left alone.</summary>
    private static readonly string[] DevelopNames =
    [
        "Exposure2012", "Contrast2012", "Highlights2012", "Shadows2012", "Saturation", "Vibrance",
        "HasCrop", "CropTop", "CropLeft", "CropBottom", "CropRight", "CropAngle",
    ];

    /// <summary>Lightroom's naming: <c>IMG_0001.CR3</c> → <c>IMG_0001.xmp</c>.</summary>
    public static string PathFor(string rawPath) => Path.ChangeExtension(rawPath, ".xmp");

    /// <summary>Sets <c>xmp:Rating</c>, or removes it when <paramref name="rating"/> is null.</summary>
    public static void SetRating(string rawPath, int? rating)
    {
        var path = PathFor(rawPath);
        if (Load(path, create: rating is not null) is not { } document)
            return;

        var descriptions = Descriptions(document, path);
        foreach (var description in descriptions)
        {
            description.Attribute(Xmp + "Rating")?.Remove();
            description.Elements(Xmp + "Rating").Remove();
        }

        if (rating is not null)
            Target(document, path, Xmp, "xmp").SetAttributeValue(Xmp + "Rating", rating.Value);

        Save(document, path);
    }

    /// <summary>
    /// Writes Lumy's crop and sliders as Camera Raw settings, so Lightroom and Photoshop open the RAW looking the
    /// same; null or an empty edit removes them. White balance is left to Lightroom: Camera Raw stores it in
    /// Kelvin for RAW files, which Lumy does not know. <paramref name="orientation"/> is the EXIF orientation
    /// (1–8): Camera Raw measures the crop on the sensor's frame, Lumy on the upright photo.
    /// </summary>
    public static void SetDevelop(string rawPath, PhotoEdit? edit, int orientation = 1)
    {
        var path = PathFor(rawPath);
        var empty = edit is null || edit.IsEmpty;
        if (Load(path, create: !empty) is not { } document)
            return;

        foreach (var description in Descriptions(document, path))
        {
            foreach (var name in DevelopNames)
            {
                description.Attribute(Crs + name)?.Remove();
                description.Elements(Crs + name).Remove();
            }
        }

        if (!empty)
        {
            var e = edit!.Normalized();
            var target = Target(document, path, Crs, "crs");
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            void Set(string name, double value, string format) => target.SetAttributeValue(Crs + name, value.ToString(format, culture));

            // Process version 2012 or later, or Lightroom ignores the 2012 sliders; an existing newer one stays.
            if (target.Attribute(Crs + "ProcessVersion") is null && target.Element(Crs + "ProcessVersion") is null)
                target.SetAttributeValue(Crs + "ProcessVersion", "11.0");

            Set("Exposure2012", e.Exposure, "+0.00;-0.00;0.00");
            Set("Contrast2012", e.Contrast, "+0;-0;0");
            Set("Highlights2012", e.Highlights, "+0;-0;0");
            Set("Shadows2012", e.Shadows, "+0;-0;0");
            Set("Saturation", e.Saturation, "+0;-0;0");
            Set("Vibrance", e.Vibrance, "+0;-0;0");

            var crop = ToSensor(e.Crop, orientation);
            target.SetAttributeValue(Crs + "HasCrop", e.Crop.IsFull ? "False" : "True");
            Set("CropTop", crop.Y, "0.######");
            Set("CropLeft", crop.X, "0.######");
            Set("CropBottom", crop.Bottom, "0.######");
            Set("CropRight", crop.Right, "0.######");
            Set("CropAngle", 0, "0");
        }

        Save(document, path);
    }

    /// <summary>Maps a crop on the upright photo to the stored (sensor) frame for an EXIF orientation.</summary>
    internal static CropRect ToSensor(CropRect upright, int orientation)
    {
        var r = upright.Clamped();
        return orientation switch
        {
            3 => new CropRect(1 - r.Right, 1 - r.Bottom, r.Width, r.Height),

            // 6: the stored frame is turned 90° clockwise to stand up; a point (u, v) upright was at (v, 1 - u).
            6 => new CropRect(r.Y, 1 - r.Right, r.Height, r.Width),

            // 8: turned 90° counter-clockwise; (u, v) upright was at (1 - v, u).
            8 => new CropRect(1 - r.Bottom, r.X, r.Height, r.Width),
            _ => r,
        };
    }

    /// <summary>Reads <c>xmp:Rating</c> from the sidecar, if there is one.</summary>
    public static int? ReadRating(string rawPath)
    {
        var path = PathFor(rawPath);
        if (!File.Exists(path))
            return null;

        var document = XDocument.Load(path);
        foreach (var description in document.Descendants(Rdf + "Description"))
        {
            var value = (string?)description.Attribute(Xmp + "Rating") ?? (string?)description.Element(Xmp + "Rating");
            if (int.TryParse(value, out var rating))
                return rating;
        }

        return null;
    }

    private static XDocument? Load(string path, bool create)
    {
        if (File.Exists(path))
            return XDocument.Load(path, LoadOptions.PreserveWhitespace);
        return create ? NewDocument() : null;
    }

    private static XElement RdfOf(XDocument document, string path)
    {
        var rdf = document.Descendants(Rdf + "RDF").FirstOrDefault();
        if (rdf is null)
        {
            rdf = new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName));
            (document.Root ?? throw new InvalidDataException($"{path} has no root element.")).Add(rdf);
        }

        return rdf;
    }

    private static List<XElement> Descriptions(XDocument document, string path) =>
        RdfOf(document, path).Elements(Rdf + "Description").ToList();

    /// <summary>The description new values go on, with the namespace declared.</summary>
    private static XElement Target(XDocument document, string path, XNamespace ns, string prefix)
    {
        var rdf = RdfOf(document, path);
        var target = rdf.Elements(Rdf + "Description").FirstOrDefault();
        if (target is null)
        {
            target = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""));
            rdf.Add(target);
        }

        if (target.GetPrefixOfNamespace(ns) is null)
            target.Add(new XAttribute(XNamespace.Xmlns + prefix, ns.NamespaceName));
        return target;
    }

    private static XDocument NewDocument() => new(
        new XElement(X + "xmpmeta",
            new XAttribute(XNamespace.Xmlns + "x", X.NamespaceName),
            new XAttribute(X + "xmptk", "Lumy"),
            new XElement(Rdf + "RDF",
                new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName),
                new XElement(Rdf + "Description",
                    new XAttribute(Rdf + "about", ""),
                    new XAttribute(XNamespace.Xmlns + "xmp", Xmp.NamespaceName)))));

    private static void Save(XDocument document, string path)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = document.Declaration is null,
            Indent = !File.Exists(path),
        };

        // Write next to the target and swap, so a crash never leaves Lightroom a half-written sidecar.
        var temp = path + ".lumy-tmp";
        using (var writer = XmlWriter.Create(temp, settings))
            document.Save(writer);
        File.Move(temp, path, overwrite: true);
    }
}
