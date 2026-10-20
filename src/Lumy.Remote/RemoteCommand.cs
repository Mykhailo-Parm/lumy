namespace Lumy.Remote;

/// <summary>What the phone asks Lumy to do. Sent over the socket as <c>{"cmd":"like"}</c>.</summary>
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
    Open,
    Back,
    Zoom,
    StartSwipe,
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
        ["open"] = RemoteCommand.Open,
        ["back"] = RemoteCommand.Back,
        ["zoom"] = RemoteCommand.Zoom,
        ["swipe"] = RemoteCommand.StartSwipe,
    };

    public static bool TryParse(string? name, out RemoteCommand command) =>
        ByName.TryGetValue(name ?? "", out command);
}
