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
    };

    public static bool TryParse(string? name, out RemoteCommand command) =>
        ByName.TryGetValue(name ?? "", out command);
}
