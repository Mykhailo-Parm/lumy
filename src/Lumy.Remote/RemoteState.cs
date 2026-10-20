using Lumy.Core;

namespace Lumy.Remote;

/// <summary>What Lumy is showing right now, pushed to the phone after every change.</summary>
public sealed record RemoteState
{
    public static readonly RemoteState Empty = new();

    /// <summary>"empty", "gallery", "loupe", "swipe", "swipe-done" or "compare".</summary>
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
}

/// <summary>An image the phone shows as a preview.</summary>
public sealed record RemoteImage(byte[] Bytes, string ContentType);
