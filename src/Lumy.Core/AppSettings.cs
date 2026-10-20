using System.Globalization;

namespace Lumy.Core;

/// <summary>User preferences, kept in the library database so they travel with it.</summary>
public sealed class AppSettings(LibraryDatabase database)
{
    /// <summary>Jump to the next photo after rating one.</summary>
    public bool AutoAdvance
    {
        get => GetBool("auto_advance", true);
        set => database.SetSetting("auto_advance", value.ToString());
    }

    /// <summary>Mirror ratings into RAW sidecars for Lightroom and Bridge.</summary>
    public bool WriteXmpSidecars
    {
        get => GetBool("write_xmp", true);
        set => database.SetSetting("write_xmp", value.ToString());
    }

    public string? LastFolder
    {
        get => database.GetSetting("last_folder");
        set => database.SetSetting("last_folder", value ?? "");
    }

    /// <summary>Thumbnail edge in pixels in the gallery grid.</summary>
    public double ThumbnailSize
    {
        get => double.TryParse(database.GetSetting("thumbnail_size"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 200;
        set => database.SetSetting("thumbnail_size", value.ToString(CultureInfo.InvariantCulture));
    }

    private bool GetBool(string key, bool fallback) =>
        bool.TryParse(database.GetSetting(key), out var value) ? value : fallback;
}
