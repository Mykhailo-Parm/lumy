namespace Lumy.Core;

/// <summary>Which part of the tournament a duel belongs to.</summary>
public enum TournamentStage
{
    /// <summary>Near-identical frames of one series play each other; only the best frame goes on.</summary>
    Series,

    /// <summary>Swiss rounds; two losses and a photo is out, until twice as many as needed are left.</summary>
    Elimination,

    /// <summary>The finalists are ranked by binary insertion, so every place in the top N is decided.</summary>
    Final,
}

/// <summary>A duel to show: which two photos, and how far along the tournament is.</summary>
/// <param name="Remaining">Photos still in the running.</param>
/// <param name="StageDone">Duels played in this stage so far.</param>
/// <param name="StageTotal">Duels this stage takes; an estimate during elimination.</param>
public sealed record Duel<T>(T Left, T Right, TournamentStage Stage, int Number, int Remaining, int StageDone, int StageTotal);

/// <summary>
/// Picks the best <c>N</c> photos out of many by asking "which of these two?" as few times as it can.
/// <list type="number">
/// <item>Series: in each series of near-identical frames, king of the hill; only the winner goes on. Skipped when
/// that would leave fewer than N photos.</item>
/// <item>Elimination: Swiss pairing (equal records meet, no rematches), out after two losses (one in quick mode),
/// until 2N are left. A photo is never knocked out by one bad comparison, and the best one can never lose at all.</item>
/// <item>Final: binary insertion of the finalists into a ranked list capped at N, so the last duels
/// decide the order of the winners, not the fate of photos that are already out.</item>
/// </list>
/// The run is deterministic: the same answers always lead to the same duels. The viewer uses that for undo by
/// replaying all answers but the last.
/// </summary>
public sealed class Tournament<T> where T : notnull
{
    private readonly IReadOnlyList<T> _candidates;
    private readonly int _winners;
    private readonly Func<T, int> _seriesOf;
    private readonly bool _bestOfSeries;
    private readonly int _lives;
    private int _number;

    /// <param name="candidates">The photos to choose from, in gallery order.</param>
    /// <param name="winners">How many to keep.</param>
    /// <param name="seriesOf">Series id of a photo; 0 for one that stands alone.</param>
    /// <param name="bestOfSeries">Keep only the best frame of each series, so the result has no near-duplicates.</param>
    /// <param name="lives">Losses that knock a photo out during elimination: 2 is fairer, 1 takes about half the duels.</param>
    public Tournament(IReadOnlyList<T> candidates, int winners, Func<T, int> seriesOf, bool bestOfSeries = true, int lives = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(winners, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(lives, 1);
        _lives = lives;
        _candidates = candidates.Distinct().ToList();
        _winners = winners;
        _seriesOf = seriesOf;
        _bestOfSeries = bestOfSeries;
    }

    /// <summary>How many photos the tournament keeps.</summary>
    public int Winners => _winners;

    /// <summary>
    /// Plays the tournament. <paramref name="judge"/> shows a duel and returns true when the left photo wins.
    /// Returns the winners, best first.
    /// </summary>
    public async Task<IReadOnlyList<T>> RunAsync(Func<Duel<T>, Task<bool>> judge)
    {
        _number = 0;
        var field = _candidates.ToList();
        if (field.Count <= 1)
            return field;

        if (_bestOfSeries)
            field = await SeriesStageAsync(field, judge);
        field = await EliminationStageAsync(field, judge);
        return await FinalStageAsync(field, judge);
    }

    /// <summary>A rough count of the duels a whole run takes, for the progress bar before it starts.</summary>
    public int EstimateDuels()
    {
        var count = _candidates.Count;
        var duels = 0;
        if (_bestOfSeries && SeriesCollapse(_candidates) is { } collapsed)
        {
            duels += count - collapsed;
            count = collapsed;
        }

        if (count > 2 * _winners)
            duels += EliminationEstimate(count);
        return duels + FinalEstimate(Math.Min(count, 2 * _winners));
    }

    private async Task<List<T>> SeriesStageAsync(List<T> field, Func<Duel<T>, Task<bool>> judge)
    {
        if (SeriesCollapse(field) is null)
            return field;

        var groups = field.GroupBy(p => _seriesOf(p) is var id and > 0 ? (object)id : p).ToList();
        var total = groups.Sum(g => g.Count() - 1);
        var done = 0;
        var remaining = field.Count;
        var result = new List<T>();
        foreach (var group in groups)
        {
            var king = group.First();
            foreach (var challenger in group.Skip(1))
            {
                var kingWins = await judge(new Duel<T>(king, challenger, TournamentStage.Series, ++_number, remaining, done++, total));
                if (!kingWins)
                    king = challenger;
                remaining--;
            }

            result.Add(king);
        }

        return result;
    }

    /// <summary>How many photos are left after keeping one per series; null when that is not worth doing.</summary>
    private int? SeriesCollapse(IReadOnlyList<T> field)
    {
        var after = field.Select(p => _seriesOf(p) is var id and > 0 ? (object)id : p).Distinct().Count();
        return after < field.Count && after >= _winners ? after : null;
    }

    private async Task<List<T>> EliminationStageAsync(List<T> field, Func<Duel<T>, Task<bool>> judge)
    {
        var target = 2 * _winners;
        if (field.Count <= target)
            return field;

        var order = field.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var wins = field.ToDictionary(p => p, _ => 0);
        var losses = field.ToDictionary(p => p, _ => 0);
        var met = new HashSet<(int, int)>();
        var estimate = EliminationEstimate(field.Count);
        var done = 0;

        List<T> Alive() => field.Where(p => losses[p] < _lives).ToList();

        while (Alive() is var alive && alive.Count > target)
        {
            // Equal records meet: unbeaten against unbeaten, once-beaten against once-beaten.
            var standing = alive
                .OrderBy(p => losses[p]).ThenByDescending(p => wins[p]).ThenBy(p => order[p])
                .ToList();
            var pairs = new List<(T, T)>();
            var paired = new bool[standing.Count];
            for (var i = 0; i < standing.Count; i++)
            {
                if (paired[i])
                    continue;

                var j = -1;
                for (var k = i + 1; k < standing.Count && j < 0; k++)
                {
                    if (!paired[k] && !met.Contains(Key(order[standing[i]], order[standing[k]])))
                        j = k;
                }

                for (var k = i + 1; k < standing.Count && j < 0; k++)
                {
                    if (!paired[k])
                        j = k; // everyone left was met already: a rematch beats sitting out
                }

                if (j < 0)
                    break; // odd one out sits this round
                paired[i] = paired[j] = true;
                pairs.Add((standing[i], standing[j]));
            }

            if (pairs.Count == 0)
                break;

            foreach (var (a, b) in pairs)
            {
                var remaining = Alive().Count;
                if (remaining <= target)
                    break;

                // Alternate sides so neither side is favoured by habit.
                var (left, right) = (_number % 2 == 0) ? (a, b) : (b, a);
                var leftWins = await judge(new Duel<T>(left, right, TournamentStage.Elimination, ++_number, remaining, done++, Math.Max(estimate, done)));
                var (winner, loser) = leftWins ? (left, right) : (right, left);
                wins[winner]++;
                losses[loser]++;
                met.Add(Key(order[a], order[b]));
            }
        }

        return Alive();
    }

    private async Task<List<T>> FinalStageAsync(List<T> field, Func<Duel<T>, Task<bool>> judge)
    {
        var ranked = new List<T>();
        var total = FinalEstimate(field.Count);
        var done = 0;
        var remaining = field.Count;
        foreach (var photo in field)
        {
            // Binary search for its place among the current top N; past the end means it is out.
            int low = 0, high = Math.Min(ranked.Count, _winners);
            while (low < high)
            {
                var middle = (low + high) / 2;
                var better = await judge(new Duel<T>(photo, ranked[middle], TournamentStage.Final, ++_number, remaining, done++, Math.Max(total, done)));
                if (better)
                    high = middle;
                else
                    low = middle + 1;
            }

            if (low < _winners)
            {
                ranked.Insert(low, photo);
                if (ranked.Count > _winners)
                {
                    ranked.RemoveAt(ranked.Count - 1);
                    remaining--;
                }
            }
            else
            {
                remaining--;
            }
        }

        return ranked;
    }

    private int EliminationEstimate(int count) =>
        (int)Math.Ceiling((count - 2 * _winners) * (_lives == 1 ? 1.0 : 1.8));

    private int FinalEstimate(int count)
    {
        var duels = 0;
        for (var i = 1; i < count; i++)
            duels += (int)Math.Ceiling(Math.Log2(Math.Min(i, _winners) + 1));
        return duels;
    }

    private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
}
