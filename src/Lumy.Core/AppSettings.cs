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

    /// <summary>Whether the full-screen viewer shows the strip of neighbouring photos at the bottom.</summary>
    public bool FilmstripVisible
    {
        get => GetBool("filmstrip_visible", true);
        set => database.SetSetting("filmstrip_visible", value.ToString());
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

    /// <summary>Folders opened lately, newest first, for the arrow next to "Открыть папку".</summary>
    public IReadOnlyList<string> RecentFolders =>
        (database.GetSetting("recent_folders") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public const int MaxRecentFolders = 8;

    public void AddRecentFolder(string folder)
    {
        var folders = RecentFolders
            .Where(f => !string.Equals(f.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            .Prepend(folder)
            .Take(MaxRecentFolders);
        database.SetSetting("recent_folders", string.Join('\n', folders));
    }

    public void ClearRecentFolders() => database.SetSetting("recent_folders", "");

    /// <summary>The photo that was selected when Lumy closed, so reopening the folder picks up where it left off.</summary>
    public string? LastPhoto
    {
        get => database.GetSetting("last_photo") is { Length: > 0 } path ? path : null;
        set => database.SetSetting("last_photo", value ?? "");
    }

    /// <summary>Gallery sort order, by its <see cref="PhotoSort"/> name.</summary>
    public string? Sort
    {
        get => database.GetSetting("sort") is { Length: > 0 } sort ? sort : null;
        set => database.SetSetting("sort", value ?? "");
    }

    /// <summary>Window bounds when not maximized, plus whether it was maximized: "x,y,width,height,max".</summary>
    public string? WindowPlacement
    {
        get => database.GetSetting("window_placement") is { Length: > 0 } placement ? placement : null;
        set => database.SetSetting("window_placement", value ?? "");
    }

    /// <summary>Where exports go; null until the first export picks the Pictures folder.</summary>
    public string? ExportFolder
    {
        get => database.GetSetting("export_folder") is { Length: > 0 } folder ? folder : null;
        set => database.SetSetting("export_folder", value ?? "");
    }

    public ExportSize ExportSize
    {
        get => Enum.TryParse<ExportSize>(database.GetSetting("export_size"), out var size) ? size : ExportSize.Instagram;
        set => database.SetSetting("export_size", value.ToString());
    }

    /// <summary>JPEG quality for exports, 60 to 100.</summary>
    public int ExportQuality
    {
        get => int.TryParse(database.GetSetting("export_quality"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) ? Math.Clamp(q, 60, 100) : 92;
        set => database.SetSetting("export_quality", Math.Clamp(value, 60, 100).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Open the export folder in Explorer when an export finishes.</summary>
    public bool ExportOpenFolder
    {
        get => GetBool("export_open_folder", true);
        set => database.SetSetting("export_open_folder", value.ToString());
    }

    private bool GetBool(string key, bool fallback) =>
        bool.TryParse(database.GetSetting(key), out var value) ? value : fallback;
}
