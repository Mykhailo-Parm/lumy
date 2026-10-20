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

    /// <summary>Whether the phone remote starts with Lumy, so a phone that scanned the code once reconnects by itself.</summary>
    public bool RemoteEnabled
    {
        get => GetBool("remote_enabled", false);
        set => database.SetSetting("remote_enabled", value.ToString());
    }

    /// <summary>The pairing code in the phone's link; kept so the link keeps working after a restart.</summary>
    public string? RemoteToken
    {
        get => database.GetSetting("remote_token") is { Length: > 0 } token ? token : null;
        set => database.SetSetting("remote_token", value ?? "");
    }

    /// <summary>The port the remote last listened on, tried first next time so the phone's link stays the same.</summary>
    public int? RemotePort
    {
        get => int.TryParse(database.GetSetting("remote_port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : null;
        set => database.SetSetting("remote_port", value?.ToString(CultureInfo.InvariantCulture) ?? "");
    }

    private bool GetBool(string key, bool fallback) =>
        bool.TryParse(database.GetSetting(key), out var value) ? value : fallback;
}
