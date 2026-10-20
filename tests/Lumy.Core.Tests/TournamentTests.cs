namespace Lumy.Core.Tests;

public class TournamentTests
{
    /// <summary>A judge with a fixed taste: the photo with the higher score always wins.</summary>
    private static Func<Duel<string>, Task<bool>> Judge(Dictionary<string, int> score, List<Duel<string>>? log = null) => duel =>
    {
        log?.Add(duel);
        return Task.FromResult(score[duel.Left] > score[duel.Right]);
    };

    private static (List<string> Photos, Dictionary<string, int> Score) Field(int count, int seed)
    {
        var random = new Random(seed);
        var photos = Enumerable.Range(1, count).Select(i => $"IMG_{i:0000}").ToList();
        var scores = Enumerable.Range(0, count).OrderBy(_ => random.Next()).ToList();
        return (photos, photos.Zip(scores).ToDictionary(x => x.First, x => x.Second));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 3)]
    [InlineData(12, 10)]
    [InlineData(40, 10)]
    [InlineData(150, 10)]
    [InlineData(300, 20)]
    public void Returns_the_requested_number_of_distinct_winners_best_first(int count, int winners)
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var (photos, score) = Field(count, seed);
            var result = new Tournament<string>(photos, winners, _ => 0).RunAsync(Judge(score)).Result;

            Assert.Equal(Math.Min(count, winners), result.Count);
            Assert.Equal(result.Count, result.Distinct().Count());
            Assert.Equal(result.OrderByDescending(p => score[p]), result);
        }
    }

    [Fact]
    public void The_two_best_photos_always_make_it_and_the_best_is_first()
    {
        for (var seed = 0; seed < 50; seed++)
        {
            var (photos, score) = Field(120, seed);
            var best = photos.OrderByDescending(p => score[p]).Take(2).ToList();
            var result = new Tournament<string>(photos, 10, _ => 0).RunAsync(Judge(score)).Result;

            Assert.Equal(best[0], result[0]);
            Assert.Contains(best[1], result);
        }
    }

    [Fact]
    public void Keeps_most_of_the_true_top_n()
    {
        var hits = 0;
        for (var seed = 0; seed < 50; seed++)
        {
            var (photos, score) = Field(120, seed);
            var top = photos.OrderByDescending(p => score[p]).Take(10).ToHashSet();
            hits += new Tournament<string>(photos, 10, _ => 0).RunAsync(Judge(score)).Result.Count(top.Contains);
        }

        Assert.True(hits >= 50 * 8, $"kept {hits / 50.0:0.0} of the real top 10 on average");
    }

    [Fact]
    public void Uses_far_fewer_duels_than_comparing_everything()
    {
        var (photos, score) = Field(200, 7);
        var log = new List<Duel<string>>();
        var tournament = new Tournament<string>(photos, 10, _ => 0);
        tournament.RunAsync(Judge(score, log)).Wait();

        Assert.InRange(log.Count, 1, 450); // a full sort would take about 1300
        Assert.InRange(tournament.EstimateDuels(), log.Count / 2, log.Count * 2);
        Assert.Equal(Enumerable.Range(1, log.Count), log.Select(d => d.Number));
    }

    [Fact]
    public void Keeps_only_the_best_frame_of_each_series()
    {
        // 30 photos in 10 series of 3; the best of each series is the one ending in 2.
        var photos = Enumerable.Range(0, 30).Select(i => $"S{i / 3}-{i % 3}").ToList();
        var score = photos.ToDictionary(p => p, p => int.Parse(p[1..p.IndexOf('-')]) * 10 + (p.EndsWith("-2") ? 5 : p.EndsWith("-1") ? 2 : 0));
        var log = new List<Duel<string>>();
        var result = new Tournament<string>(photos, 5, p => int.Parse(p[1..p.IndexOf('-')]) + 1).RunAsync(Judge(score, log)).Result;

        Assert.Equal(["S9-2", "S8-2", "S7-2", "S6-2", "S5-2"], result);
        Assert.Equal(20, log.Count(d => d.Stage == TournamentStage.Series));
    }

    [Fact]
    public void Skips_the_series_stage_when_it_would_leave_too_few_photos()
    {
        var photos = Enumerable.Range(0, 6).Select(i => $"P{i}").ToList();
        var log = new List<Duel<string>>();
        new Tournament<string>(photos, 4, _ => 1).RunAsync(Judge(photos.ToDictionary(p => p, p => (int)p[1]), log)).Wait();

        Assert.DoesNotContain(log, d => d.Stage == TournamentStage.Series);
    }

    [Fact]
    public void Same_answers_give_the_same_duels_so_undo_can_replay()
    {
        var (photos, score) = Field(60, 3);
        var first = new List<Duel<string>>();
        var second = new List<Duel<string>>();
        new Tournament<string>(photos, 8, p => score[p] % 7 == 0 ? 1 : 0).RunAsync(Judge(score, first)).Wait();
        new Tournament<string>(photos, 8, p => score[p] % 7 == 0 ? 1 : 0).RunAsync(Judge(score, second)).Wait();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Never_shows_a_photo_against_itself()
    {
        var (photos, score) = Field(77, 11);
        var log = new List<Duel<string>>();
        new Tournament<string>(photos, 6, _ => 0).RunAsync(Judge(score, log)).Wait();

        Assert.All(log, d => Assert.NotEqual(d.Left, d.Right));
    }
}


public class QuickTournamentTests
{
    [Fact]
    public void Quick_mode_takes_fewer_duels_and_still_finds_the_best()
    {
        var random = new Random(5);
        var photos = Enumerable.Range(0, 100).ToList();
        var score = photos.ToDictionary(p => p, _ => random.Next());
        int Count(int lives)
        {
            var duels = 0;
            var result = new Tournament<int>(photos, 10, _ => 0, lives: lives)
                .RunAsync(d => { duels++; return Task.FromResult(score[d.Left] > score[d.Right]); }).Result;
            Assert.Equal(photos.MaxBy(p => score[p]), result[0]);
            Assert.Equal(10, result.Count);
            return duels;
        }

        Assert.True(Count(lives: 1) < Count(lives: 2) * 0.75);
    }
}
