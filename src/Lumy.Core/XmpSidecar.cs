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

    /// <summary>Lightroom's naming: <c>IMG_0001.CR3</c> → <c>IMG_0001.xmp</c>.</summary>
    public static string PathFor(string rawPath) => Path.ChangeExtension(rawPath, ".xmp");

    /// <summary>Sets <c>xmp:Rating</c>, or removes it when <paramref name="rating"/> is null.</summary>
    public static void SetRating(string rawPath, int? rating)
    {
        var path = PathFor(rawPath);
        XDocument document;
        if (File.Exists(path))
        {
            document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        }
        else
        {
            if (rating is null)
                return;
            document = NewDocument();
        }

        var rdf = document.Descendants(Rdf + "RDF").FirstOrDefault();
        if (rdf is null)
        {
            rdf = new XElement(Rdf + "RDF", new XAttribute(XNamespace.Xmlns + "rdf", Rdf.NamespaceName));
            (document.Root ?? throw new InvalidDataException($"{path} has no root element.")).Add(rdf);
        }

        var descriptions = rdf.Elements(Rdf + "Description").ToList();
        foreach (var description in descriptions)
        {
            description.Attribute(Xmp + "Rating")?.Remove();
            description.Elements(Xmp + "Rating").Remove();
        }

        if (rating is not null)
        {
            var target = descriptions.FirstOrDefault();
            if (target is null)
            {
                target = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""));
                rdf.Add(target);
            }

            if (target.GetPrefixOfNamespace(Xmp) is null)
                target.Add(new XAttribute(XNamespace.Xmlns + "xmp", Xmp.NamespaceName));
            target.SetAttributeValue(Xmp + "Rating", rating.Value);
        }

        Save(document, path);
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
