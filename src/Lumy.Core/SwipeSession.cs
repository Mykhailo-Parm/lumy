namespace Lumy.Core;

/// <summary>
/// One pass of swipe mode: a queue of photos, one decision each. Skipped photos come back once at the end;
/// <see cref="Back"/> steps back one card and takes back its decision.
/// </summary>
public sealed class SwipeSession<T> where T : class, IRatedPhoto
{
    private readonly List<T> _queue;
    private readonly HashSet<T> _requeued = [];
    private readonly Stack<Step> _history = new();
    private readonly RatingService _ratings;
    private int _index;
    private int _skippedForGood;

    public SwipeSession(IEnumerable<T> photos, RatingService ratings)
    {
        _queue = photos.ToList();
        _ratings = ratings;
    }

    public T? Current => _index < _queue.Count ? _queue[_index] : null;

    /// <summary>The photo after <see cref="Current"/>, for preloading.</summary>
    public T? Next => _index + 1 < _queue.Count ? _queue[_index + 1] : null;

    /// <summary>The current photo and the next ones after it, for preloading.</summary>
    public IEnumerable<T> Upcoming(int count) => _queue.Skip(_index).Take(count);

    /// <summary>1-based position of the current card.</summary>
    public int Position => Math.Min(_index + 1, _queue.Count);

    public int Total => _queue.Count;

    public bool IsFinished => _index >= _queue.Count;

    public bool CanGoBack => _history.Count > 0;

    public int Liked { get; private set; }

    public int SuperLiked { get; private set; }

    public int Rejected { get; private set; }

    /// <summary>Photos skipped twice and left unrated.</summary>
    public int Skipped => _skippedForGood;

    /// <summary>
    /// Rates the current photo and moves on. With <paramref name="sameSeries"/>, every photo still ahead in the
    /// queue for which it returns true gets the same tier and leaves the queue (Shift+← rejects a whole burst).
    /// </summary>
    public void Decide(Tier tier, Func<T, bool>? sameSeries = null)
    {
        if (Current is not { } photo)
            return;

        var group = new List<T> { photo };
        var removed = new List<(int Index, T Photo)>();
        if (sameSeries is not null)
        {
            for (var i = _queue.Count - 1; i > _index; i--)
            {
                if (_queue[i] != photo && sameSeries(_queue[i]))
                {
                    removed.Add((i, _queue[i]));
                    group.Add(_queue[i]);
                    _queue.RemoveAt(i);
                }
            }

            removed.Reverse();
        }

        var changed = group.Any(p => p.Tier != tier);
        _ratings.Rate(group, tier);
        Count(tier, group.Count);
        _history.Push(new Step(StepKind.Decision, tier, group.Count, changed, removed, Requeued: false));
        _index++;
    }

    /// <summary>Leaves the current photo unrated for now. The first skip sends it to the end of the queue; a second one lets it go.</summary>
    public void Skip()
    {
        if (Current is not { } photo)
            return;

        var requeue = _requeued.Add(photo);
        if (requeue)
            _queue.Add(photo);
        else
            _skippedForGood++;

        _history.Push(new Step(StepKind.Skip, Tier.None, 0, false, [], requeue));
        _index++;
    }

    /// <summary>Returns to the previous card and takes back what was done to it.</summary>
    public void Back()
    {
        if (!_history.TryPop(out var step))
            return;

        _index--;
        if (step.Kind == StepKind.Skip)
        {
            if (step.Requeued)
            {
                _requeued.Remove(_queue[^1]);
                _queue.RemoveAt(_queue.Count - 1);
            }
            else
            {
                _skippedForGood--;
            }

            return;
        }

        if (step.RatingChanged)
            _ratings.Undo();
        Count(step.Tier, -step.GroupSize);
        foreach (var (index, photo) in step.Removed)
            _queue.Insert(index, photo);
    }

    private void Count(Tier tier, int delta)
    {
        switch (tier)
        {
            case Tier.Like: Liked += delta; break;
            case Tier.SuperLike: SuperLiked += delta; break;
            case Tier.Rejected: Rejected += delta; break;
        }
    }

    private enum StepKind
    {
        Decision,
        Skip,
    }

    private sealed record Step(StepKind Kind, Tier Tier, int GroupSize, bool RatingChanged, IReadOnlyList<(int Index, T Photo)> Removed, bool Requeued);
}
