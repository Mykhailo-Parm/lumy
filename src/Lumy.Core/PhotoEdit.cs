using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumy.Core;

/// <summary>Crop shapes; the first ones are what Instagram shows without cropping again.</summary>
public enum CropAspect
{
    /// <summary>The photo's own proportions.</summary>
    Original,

    /// <summary>4:5, the tallest a feed post can be.</summary>
    Portrait,

    /// <summary>1:1.</summary>
    Square,

    /// <summary>1.91:1, the widest a feed post can be.</summary>
    Landscape,

    /// <summary>9:16, stories and reels.</summary>
    Story,

    /// <summary>Any shape.</summary>
    Free,
}

/// <summary>Part of the upright photo, in fractions of its width and height, so it fits any resolution.</summary>
public sealed record CropRect(double X, double Y, double Width, double Height)
{
    public static readonly CropRect Full = new(0, 0, 1, 1);

    public bool IsFull => X <= 1e-6 && Y <= 1e-6 && Width >= 1 - 1e-6 && Height >= 1 - 1e-6;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    /// <summary>The same rectangle, moved and shrunk until it lies inside the photo.</summary>
    public CropRect Clamped()
    {
        var width = Math.Clamp(Width, 0.01, 1);
        var height = Math.Clamp(Height, 0.01, 1);
        return new CropRect(Math.Clamp(X, 0, 1 - width), Math.Clamp(Y, 0, 1 - height), width, height);
    }
}

/// <summary>
/// How a photo should look when exported: a crop and a few basic sliders. Never written into the photo itself;
/// it lives in Lumy's library (and, for RAW files, as Camera Raw settings in the .xmp sidecar).
/// Sliders run from -100 to 100 like Lightroom's, exposure in stops from -3 to 3.
/// </summary>
public sealed record PhotoEdit
{
    public static readonly PhotoEdit None = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public CropAspect Aspect { get; init; } = CropAspect.Original;

    public CropRect Crop { get; init; } = CropRect.Full;

    public double Exposure { get; init; }

    public double Contrast { get; init; }

    public double Highlights { get; init; }

    public double Shadows { get; init; }

    /// <summary>Warmer (positive) or cooler (negative).</summary>
    public double Temperature { get; init; }

    /// <summary>Towards magenta (positive) or green (negative).</summary>
    public double Tint { get; init; }

    public double Saturation { get; init; }

    /// <summary>Saturation that spares colours that are already strong, and skin.</summary>
    public double Vibrance { get; init; }

    /// <summary>Nothing to change in the pixels: every slider at zero.</summary>
    public bool HasNoAdjustments =>
        Exposure == 0 && Contrast == 0 && Highlights == 0 && Shadows == 0
        && Temperature == 0 && Tint == 0 && Saturation == 0 && Vibrance == 0;

    /// <summary>The photo would export exactly as it is.</summary>
    public bool IsEmpty => HasNoAdjustments && Crop.IsFull;

    /// <summary>The slider values pulled into their ranges, and the crop into the photo.</summary>
    public PhotoEdit Normalized() => this with
    {
        Crop = Crop.Clamped(),
        Exposure = Math.Clamp(Finite(Exposure), -3, 3),
        Contrast = Slider(Contrast),
        Highlights = Slider(Highlights),
        Shadows = Slider(Shadows),
        Temperature = Slider(Temperature),
        Tint = Slider(Tint),
        Saturation = Slider(Saturation),
        Vibrance = Slider(Vibrance),
    };

    /// <summary>One slider by its name, for the phone remote: "exposure", "contrast", ….</summary>
    public PhotoEdit With(string slider, double value) => (slider.ToLowerInvariant() switch
    {
        "exposure" => this with { Exposure = value },
        "contrast" => this with { Contrast = value },
        "highlights" => this with { Highlights = value },
        "shadows" => this with { Shadows = value },
        "temperature" => this with { Temperature = value },
        "tint" => this with { Tint = value },
        "saturation" => this with { Saturation = value },
        "vibrance" => this with { Vibrance = value },
        _ => this,
    }).Normalized();

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads what <see cref="ToJson"/> wrote; null for anything unreadable, so a bad row never blocks a folder.</summary>
    public static PhotoEdit? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PhotoEdit>(json, Json)?.Normalized();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"Экспозиция +0,5 · Контраст +20 · кроп 4:5", for tooltips and the phone.</summary>
    public string Summary()
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var parts = new List<string>();
        if (Exposure != 0)
            parts.Add($"экспозиция {Exposure.ToString("+0.0#;-0.0#", culture)}");
        void Add(string name, double value)
        {
            if (value != 0)
                parts.Add($"{name} {value.ToString("+0;-0", culture)}");
        }

        Add("контраст", Contrast);
        Add("света", Highlights);
        Add("тени", Shadows);
        Add("температура", Temperature);
        Add("оттенок", Tint);
        Add("насыщенность", Saturation);
        Add("сочность", Vibrance);
        if (!Crop.IsFull)
            parts.Add("кроп " + AspectName(Aspect));
        return parts.Count == 0 ? "без правок" : string.Join(" · ", parts);
    }

    public static string AspectName(CropAspect aspect) => aspect switch
    {
        CropAspect.Portrait => "4:5",
        CropAspect.Square => "1:1",
        CropAspect.Landscape => "1.91:1",
        CropAspect.Story => "9:16",
        CropAspect.Free => "свободный",
        _ => "как в оригинале",
    };

    private static double Slider(double value) => Math.Clamp(Finite(value), -100, 100);

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;
}
