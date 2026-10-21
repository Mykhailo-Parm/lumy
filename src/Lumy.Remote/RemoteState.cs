using Lumy.Core;

namespace Lumy.Remote;

/// <summary>What Lumy is showing right now, pushed to the phone after every change.</summary>
public sealed record RemoteState
{
    public static readonly RemoteState Empty = new();

    /// <summary>"empty", "gallery", "loupe", "swipe", "swipe-done", "compare", "tournament", "tournament-done" or "editor".</summary>
    public string Mode { get; init; } = "empty";

    public long? PhotoId { get; init; }

    public string? FileName { get; init; }

    public int Position { get; init; }

    public int Total { get; init; }

    public Tier Tier { get; init; }

    /// <summary>A short extra line, such as "серия 3/8".</summary>
    public string? Detail { get; init; }

    public int Liked { get; init; }

    public int SuperLiked { get; init; }

    public int Rejected { get; init; }

    /// <summary>The photos on screen together: the comparison, the two in a duel, or the tournament winners.</summary>
    public IReadOnlyList<RemotePhoto> Photos { get; init; } = [];

    /// <summary>Index into <see cref="Photos"/> of the one being pointed at; -1 for none.</summary>
    public int Focus { get; init; } = -1;

    /// <summary>The open folder's name.</summary>
    public string? Folder { get; init; }

    /// <summary>Recent folders, newest first, as names; <see cref="RemoteCommand.OpenFolder"/> takes an index.</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    public string Filter { get; init; } = "All";

    public string Sort { get; init; } = "CaptureTime";

    /// <summary>Counts for the whole folder, for the filter buttons.</summary>
    public RemoteCounts Counts { get; init; } = new();

    /// <summary>Changes whenever the gallery's list of photos does; the phone then fetches /photos again.</summary>
    public int ListVersion { get; init; }

    public bool AutoAdvance { get; init; }

    public bool FilmstripVisible { get; init; }

    public bool FullScreen { get; init; }

    /// <summary>The last message Lumy showed on screen, with an id so the same text twice still shows twice.</summary>
    public string? Message { get; init; }

    public int MessageId { get; init; }

    /// <summary>The crop and sliders while the editor is open.</summary>
    public RemoteEditor? Editor { get; init; }

    /// <summary>Every album, newest first.</summary>
    public IReadOnlyList<RemoteAlbum> Albums { get; init; } = [];

    /// <summary>The album open in the gallery instead of a folder.</summary>
    public RemoteAlbum? Album { get; init; }

    /// <summary>In comparison: "wipe" or "fade" while two photos lie on top of each other, otherwise null.</summary>
    public string? Overlay { get; init; }

    /// <summary>Where the wipe is, or how much of the top photo shows, from 0 to 1.</summary>
    public double OverlayValue { get; init; }
}

/// <summary>The editor's state: the crop shape and frame (x, y, width, height in fractions of the photo), every slider by name, and a line about the frame's size.</summary>
public sealed record RemoteEditor(string Aspect, IReadOnlyList<double> Crop, IReadOnlyDictionary<string, double> Values, bool Before, bool CanPaste, string? Info);

public sealed record RemoteAlbum(long Id, string Name, int Count);

public sealed record RemotePhoto(long Id, string FileName, Tier Tier, string? Label = null);

public sealed record RemoteCounts(int All = 0, int Liked = 0, int SuperLiked = 0, int Unrated = 0, int Rejected = 0);

/// <summary>The gallery's photos in order, served at /photos.</summary>
public sealed record RemotePhotoList(int Version, IReadOnlyList<RemotePhoto> Photos)
{
    public static readonly RemotePhotoList Empty = new(0, []);
}

/// <summary>An image the phone shows as a preview.</summary>
public sealed record RemoteImage(byte[] Bytes, string ContentType);
