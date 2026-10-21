namespace Lumy.Remote;

/// <summary>What the phone asks Lumy to do. Sent over the socket as <c>{"cmd":"like"}</c>, some with an <c>"arg"</c>.</summary>
public enum RemoteCommand
{
    Next,
    Previous,
    NextUnrated,
    Like,
    SuperLike,
    Reject,
    RejectSeries,
    Clear,
    Skip,
    Undo,
    Redo,
    Open,
    Back,
    Zoom,
    StartSwipe,

    /// <summary>Show this photo; arg is its id.</summary>
    Goto,

    /// <summary>arg: a <c>TierFilter</c> name.</summary>
    Filter,

    /// <summary>arg: a <c>PhotoSort</c> name.</summary>
    Sort,

    /// <summary>Open a recent folder; arg is its index in <see cref="RemoteState.Folders"/>.</summary>
    OpenFolder,

    /// <summary>Compare; arg is a comma-separated list of photo ids, or empty for the selection or series.</summary>
    Compare,

    /// <summary>Point at one photo of the comparison or tournament result; arg is its index.</summary>
    Pick,

    /// <summary>Drop one photo from the comparison; arg is its index.</summary>
    Remove,

    /// <summary>Start a tournament; arg is "pool:winners", pool being "likes", "shown" or a comma-separated id list.</summary>
    Tournament,

    /// <summary>Superlike every tournament winner.</summary>
    WinnersSuperLike,

    /// <summary>Select the winners in the gallery.</summary>
    WinnersShow,

    /// <summary>Compare the winners side by side.</summary>
    WinnersCompare,

    /// <summary>Move photos to the Recycle Bin, already confirmed on the phone; arg is a comma-separated id list, or empty for the current photo.</summary>
    Delete,

    FullScreen,
    Filmstrip,
    AutoAdvance,

    /// <summary>Open the editor on the current photo, or on the photo whose id is the arg.</summary>
    Edit,

    /// <summary>Move one slider; arg is "name:value", such as "exposure:0.5".</summary>
    EditSet,

    /// <summary>Choose the crop shape; arg is a <c>CropAspect</c> name.</summary>
    EditAspect,

    /// <summary>Nudge the crop frame; arg is "dx:dy:scale", the move in fractions of the photo and the size factor.</summary>
    EditCrop,

    EditReset,

    /// <summary>Show the photo without the edit, or with it again.</summary>
    EditBefore,

    /// <summary>Paste the copied colour into the photo being edited.</summary>
    EditPaste,

    EditSave,

    /// <summary>Leave the editor without saving, already confirmed on the phone.</summary>
    EditCancel,

    /// <summary>Export with the last settings and no dialog; arg is "album", "likes", "supers" or a comma-separated id list, empty for the current photo.</summary>
    Export,

    /// <summary>Open an album; arg is its id.</summary>
    AlbumOpen,

    /// <summary>Make an album of the current photo or the selection; arg is its name.</summary>
    AlbumNew,

    /// <summary>Add the current photo or the selection to an album; arg is its id.</summary>
    AlbumAdd,

    /// <summary>Take photos out of the open album; arg is a comma-separated id list, empty for the current photo.</summary>
    AlbumRemove,

    /// <summary>Move a photo within the open album; arg is "id:position", the position counted from 0.</summary>
    AlbumMove,

    /// <summary>Close the open album and go back to the folder.</summary>
    AlbumClose,

    /// <summary>In comparison, put two photos on top of each other, or back side by side.</summary>
    Overlay,

    /// <summary>arg: "wipe" or "fade".</summary>
    OverlayMode,

    /// <summary>Where the wipe is, or how much of the top photo shows; arg is 0 to 1.</summary>
    OverlayValue,

    OverlaySwap,

    /// <summary>Pinch and drag on the phone: arg is "zoom:x:y", the zoom and the point of the photo in the middle of the screen, in fractions.</summary>
    View,
}

/// <summary>A command with its argument, if it takes one.</summary>
public readonly record struct RemoteRequest(RemoteCommand Command, string? Arg = null)
{
    public static implicit operator RemoteRequest(RemoteCommand command) => new(command);

    /// <summary>The argument as photo ids or indexes; numbers that do not parse are skipped.</summary>
    public IReadOnlyList<long> Numbers() =>
        (Arg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : (long?)null)
            .OfType<long>()
            .ToList();

    /// <summary>The argument split on ':' into numbers, such as "0.1:0:1" for a crop nudge; parts that do not parse are NaN.</summary>
    public IReadOnlyList<double> Values() =>
        (Arg ?? "").Split(':', StringSplitOptions.TrimEntries)
            .Select(s => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : double.NaN)
            .ToList();
}

public static class RemoteCommands
{
    private static readonly Dictionary<string, RemoteCommand> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["next"] = RemoteCommand.Next,
        ["prev"] = RemoteCommand.Previous,
        ["next-unrated"] = RemoteCommand.NextUnrated,
        ["like"] = RemoteCommand.Like,
        ["super"] = RemoteCommand.SuperLike,
        ["reject"] = RemoteCommand.Reject,
        ["reject-series"] = RemoteCommand.RejectSeries,
        ["clear"] = RemoteCommand.Clear,
        ["skip"] = RemoteCommand.Skip,
        ["undo"] = RemoteCommand.Undo,
        ["redo"] = RemoteCommand.Redo,
        ["open"] = RemoteCommand.Open,
        ["back"] = RemoteCommand.Back,
        ["zoom"] = RemoteCommand.Zoom,
        ["swipe"] = RemoteCommand.StartSwipe,
        ["goto"] = RemoteCommand.Goto,
        ["filter"] = RemoteCommand.Filter,
        ["sort"] = RemoteCommand.Sort,
        ["folder"] = RemoteCommand.OpenFolder,
        ["compare"] = RemoteCommand.Compare,
        ["pick"] = RemoteCommand.Pick,
        ["remove"] = RemoteCommand.Remove,
        ["tournament"] = RemoteCommand.Tournament,
        ["winners-super"] = RemoteCommand.WinnersSuperLike,
        ["winners-show"] = RemoteCommand.WinnersShow,
        ["winners-compare"] = RemoteCommand.WinnersCompare,
        ["delete"] = RemoteCommand.Delete,
        ["fullscreen"] = RemoteCommand.FullScreen,
        ["filmstrip"] = RemoteCommand.Filmstrip,
        ["auto-advance"] = RemoteCommand.AutoAdvance,
        ["edit"] = RemoteCommand.Edit,
        ["edit-set"] = RemoteCommand.EditSet,
        ["edit-aspect"] = RemoteCommand.EditAspect,
        ["edit-crop"] = RemoteCommand.EditCrop,
        ["edit-reset"] = RemoteCommand.EditReset,
        ["edit-before"] = RemoteCommand.EditBefore,
        ["edit-paste"] = RemoteCommand.EditPaste,
        ["edit-save"] = RemoteCommand.EditSave,
        ["edit-cancel"] = RemoteCommand.EditCancel,
        ["export"] = RemoteCommand.Export,
        ["album"] = RemoteCommand.AlbumOpen,
        ["album-new"] = RemoteCommand.AlbumNew,
        ["album-add"] = RemoteCommand.AlbumAdd,
        ["album-remove"] = RemoteCommand.AlbumRemove,
        ["album-move"] = RemoteCommand.AlbumMove,
        ["album-close"] = RemoteCommand.AlbumClose,
        ["overlay"] = RemoteCommand.Overlay,
        ["overlay-mode"] = RemoteCommand.OverlayMode,
        ["overlay-value"] = RemoteCommand.OverlayValue,
        ["overlay-swap"] = RemoteCommand.OverlaySwap,
        ["view"] = RemoteCommand.View,
    };

    public static bool TryParse(string? name, out RemoteCommand command) =>
        ByName.TryGetValue(name ?? "", out command);
}
