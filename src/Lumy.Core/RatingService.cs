namespace Lumy.Core;

/// <summary>A photo that can be rated; implemented by the app's view models so the UI updates as tiers change.</summary>
public interface IRatedPhoto
{
    long Id { get; }

    /// <summary>The RAW file whose sidecar receives the rating, if the photo has one.</summary>
    string? RawPath { get; }

    Tier Tier { get; set; }
}

/// <summary>
/// Applies ratings: updates the photos, saves them to the library, mirrors them to XMP sidecars when enabled,
/// and keeps the undo/redo history.
/// </summary>
public sealed class RatingService(LibraryDatabase database)
{
    private readonly Stack<Change> _undo = new();
    private readonly Stack<Change> _redo = new();

    /// <summary>Whether ratings are also written to RAW sidecars for Lightroom and Bridge.</summary>
    public bool WriteXmpSidecars { get; set; } = true;

    /// <summary>Raised when a sidecar could not be written (read-only card, file locked). The rating is still saved in the library.</summary>
    public event Action<string, Exception>? SidecarWriteFailed;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Sets <paramref name="tier"/> on every photo. Photos already at that tier are left alone.</summary>
    public void Rate(IReadOnlyList<IRatedPhoto> photos, Tier tier)
    {
        var entries = photos.Where(p => p.Tier != tier).Select(p => new Entry(p, p.Tier, tier)).ToList();
        if (entries.Count == 0)
            return;

        var change = new Change(entries);
        Apply(change, forward: true);
        _undo.Push(change);
        _redo.Clear();
    }

    /// <summary>Reverts the last rating action. Returns the photos it touched so the UI can show them.</summary>
    public IReadOnlyList<IRatedPhoto> Undo()
    {
        if (!_undo.TryPop(out var change))
            return [];
        Apply(change, forward: false);
        _redo.Push(change);
        return change.Entries.Select(e => e.Photo).ToList();
    }

    public IReadOnlyList<IRatedPhoto> Redo()
    {
        if (!_redo.TryPop(out var change))
            return [];
        Apply(change, forward: true);
        _undo.Push(change);
        return change.Entries.Select(e => e.Photo).ToList();
    }

    private void Apply(Change change, bool forward)
    {
        var updates = change.Entries.Select(e => (e.Photo, Tier: forward ? e.To : e.From)).ToList();
        database.SetTiers(updates.Select(u => (u.Photo.Id, u.Tier)));
        foreach (var (photo, tier) in updates)
        {
            photo.Tier = tier;
            if (WriteXmpSidecars && photo.RawPath is { } raw)
                TryWriteSidecar(raw, tier);
        }
    }

    private void TryWriteSidecar(string rawPath, Tier tier)
    {
        try
        {
            XmpSidecar.SetRating(rawPath, tier.ToXmpRating());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
        {
            SidecarWriteFailed?.Invoke(rawPath, e);
        }
    }

    private sealed record Entry(IRatedPhoto Photo, Tier From, Tier To);

    private sealed record Change(IReadOnlyList<Entry> Entries);
}
