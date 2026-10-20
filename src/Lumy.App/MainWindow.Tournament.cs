using Lumy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Lumy.App;

/// <summary>
/// The tournament: two photos side by side, pick the better one, and after a few dozen picks Lumy hands back the
/// best N in order. The algorithm is <see cref="Tournament{T}"/>; this file shows its duels and its result.
/// Undo replays every answer but the last, which the algorithm guarantees leads back to the same duel.
/// </summary>
public sealed partial class MainWindow
{
    private const int DefaultWinners = 10;

    private Tournament<PhotoItem>? _tournament;
    private readonly List<bool> _tournamentAnswers = [];
    private readonly Dictionary<long, Task<BitmapImage?>> _duelImages = [];
    private TaskCompletionSource<bool>? _duelAnswer;
    private Duel<PhotoItem>? _duel;
    private IReadOnlyList<PhotoItem> _tournamentWinners = [];
    private int _tournamentRun;
    private int _tournamentEstimate;

    private bool InTournament => TournamentView.Visibility == Visibility.Visible;

    private void Tournament_Click(object sender, RoutedEventArgs e) => _ = SetUpTournamentAsync();

    // ---------- Setup ----------

    /// <summary>B or the toolbar button: choose which photos compete and how many to keep.</summary>
    private async Task SetUpTournamentAsync()
    {
        var liked = _all.Where(p => p.Tier >= Tier.Like).ToList();
        var selected = Gallery.SelectedItems.OfType<PhotoItem>().ToList();
        var shown = _visible.ToList();
        if (shown.Count < 2 && liked.Count < 2)
        {
            ShowToast("Для турнира нужно хотя бы 2 фото");
            return;
        }

        var pools = new List<(string Label, List<PhotoItem> Photos)>();
        if (liked.Count >= 2)
            pools.Add(($"Лайки и суперлайки ({liked.Count})", liked));
        if (selected.Count >= 2)
            pools.Add(($"Выделенные ({selected.Count})", selected));
        if (shown.Count >= 2)
            pools.Add(($"Все, что сейчас показано ({shown.Count})", shown));

        var poolBox = new RadioButtons { Header = "Из каких фото" };
        foreach (var pool in pools)
            poolBox.Items.Add(pool.Label);
        poolBox.SelectedIndex = 0;

        var winnersBox = new NumberBox
        {
            Header = "Сколько оставить",
            Minimum = 1,
            Maximum = 50,
            Value = DefaultWinners,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 1,
        };
        var seriesBox = new CheckBox { Content = "Только лучший кадр из каждой серии похожих", IsChecked = true };
        var quickBox = new CheckBox { Content = "Быстрый: фото выбывает после первого проигрыша", IsChecked = false };
        var estimate = new TextBlock { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap };

        Tournament<PhotoItem> Build() => new(
            pools[Math.Max(0, poolBox.SelectedIndex)].Photos,
            (int)Math.Clamp(double.IsNaN(winnersBox.Value) ? DefaultWinners : winnersBox.Value, 1, 50),
            p => p.SeriesId,
            bestOfSeries: seriesBox.IsChecked == true,
            lives: quickBox.IsChecked == true ? 1 : 2);

        void UpdateEstimate() => estimate.Text =
            $"Примерно {Build().EstimateDuels()} дуэлей. Instagram-карусель вмещает до 20 фото.";
        poolBox.SelectionChanged += (_, _) => UpdateEstimate();
        winnersBox.ValueChanged += (_, _) => UpdateEstimate();
        seriesBox.Click += (_, _) => UpdateEstimate();
        quickBox.Click += (_, _) => UpdateEstimate();
        UpdateEstimate();

        var dialog = new ContentDialog
        {
            Title = "Турнир",
            Content = new StackPanel { Spacing = 14, MinWidth = 380, Children = { poolBox, winnersBox, seriesBox, quickBox, estimate } },
            PrimaryButtonText = "Начать",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        StartTournament(Build());
    }

    private void StartTournament(Tournament<PhotoItem> tournament)
    {
        CloseLoupe();
        if (InSwipe)
            CloseSwipe();
        if (InCompare)
            CloseCompare();

        _tournament = tournament;
        _tournamentEstimate = Math.Max(1, tournament.EstimateDuels());
        _tournamentAnswers.Clear();
        _duelImages.Clear();
        TournamentView.Visibility = Visibility.Visible;
        PlayTournament();
    }

    // ---------- Playing ----------

    /// <summary>Runs the tournament from the start, answering from memory until it reaches a new duel.</summary>
    private async void PlayTournament()
    {
        if (_tournament is not { } tournament)
            return;

        var run = ++_tournamentRun;
        var replay = _tournamentAnswers.ToList();
        var replayed = 0;
        try
        {
            var winners = await tournament.RunAsync(async duel =>
            {
                if (run != _tournamentRun)
                    throw new OperationCanceledException();
                if (replayed < replay.Count)
                    return replay[replayed++];

                var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _duelAnswer = answer;
                ShowDuel(duel);
                var leftWins = await answer.Task;
                if (run != _tournamentRun)
                    throw new OperationCanceledException(); // undone before this answer was recorded
                _tournamentAnswers.Add(leftWins);
                replayed++;
                return leftWins;
            });

            if (run == _tournamentRun)
                ShowTournamentResult(winners);
        }
        catch (OperationCanceledException)
        {
            // Undo or exit started another run, or stopped this one.
        }
        catch (Exception e)
        {
            CrashLog.Write(e);
            ShowToast("Турнир остановился из-за ошибки");
        }
    }

    private void PickDuel(bool left)
    {
        if (_duelAnswer is not { } answer)
            return;

        _duelAnswer = null;
        answer.TrySetResult(left);
    }

    private void UndoDuel()
    {
        if (_tournamentAnswers.Count == 0)
        {
            ShowToast("Это первая дуэль");
            return;
        }

        _tournamentAnswers.RemoveAt(_tournamentAnswers.Count - 1);
        _duelAnswer?.TrySetCanceled();
        _duelAnswer = null;
        PlayTournament();
    }

    private async void ShowDuel(Duel<PhotoItem> duel)
    {
        _duel = duel;
        TournamentResult.Visibility = Visibility.Collapsed;
        TournamentDuel.Visibility = Visibility.Visible;
        TournamentHint.Visibility = Visibility.Visible;

        TournamentStageText.Text = duel.Stage switch
        {
            TournamentStage.Series => $"Этап 1 из 3 · Серии: лучший кадр из каждой серии похожих · в игре {duel.Remaining}",
            TournamentStage.Elimination => $"Этап 2 из 3 · Отсев: в игре {duel.Remaining}, в финал выйдут {2 * (_tournament?.Winners ?? DefaultWinners)}",
            _ => $"Этап 3 из 3 · Финал: расставляем места · в игре {duel.Remaining}",
        };
        _tournamentEstimate = Math.Max(_tournamentEstimate, duel.Number);
        TournamentProgress.Value = Math.Min(1, (duel.Number - 1) / (double)_tournamentEstimate);
        TournamentCountText.Text = $"Дуэль {duel.Number} из ~{_tournamentEstimate}";
        TournamentLeftName.Text = duel.Left.FileName;
        TournamentRightName.Text = duel.Right.FileName;
        PushRemoteState();

        var request = duel.Number;
        TournamentLeftImage.Source = duel.Left.Thumbnail;
        TournamentRightImage.Source = duel.Right.Thumbnail;
        var left = DuelImageAsync(duel.Left);
        var right = DuelImageAsync(duel.Right);
        if (await left is { } leftImage && _duel?.Number == request)
            TournamentLeftImage.Source = leftImage;
        if (await right is { } rightImage && _duel?.Number == request)
            TournamentRightImage.Source = rightImage;
    }

    /// <summary>Half-screen bitmaps; the same photos come back duel after duel, so they stay decoded for a while.</summary>
    private Task<BitmapImage?> DuelImageAsync(PhotoItem photo)
    {
        if (_duelImages.TryGetValue(photo.Id, out var cached))
            return cached;

        if (_duelImages.Count > 40)
            _duelImages.Clear();

        var scale = TournamentDuel.XamlRoot?.RasterizationScale ?? 1.0;
        var width = (int)Math.Clamp(TournamentDuel.ActualWidth / 2 * scale, 640, 2560);
        var task = LoupeImageCache.LoadAsync(photo.Path, width);
        _duelImages[photo.Id] = task;
        return task;
    }

    private void TournamentLeft_Tapped(object sender, TappedRoutedEventArgs e) => PickDuel(left: true);

    private void TournamentRight_Tapped(object sender, TappedRoutedEventArgs e) => PickDuel(left: false);

    private void TournamentSide_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        ((Grid)sender).BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    private void TournamentSide_PointerExited(object sender, PointerRoutedEventArgs e) => ((Grid)sender).BorderBrush = null;

    private bool HandleTournamentKey(VirtualKey key, bool ctrl)
    {
        if (ctrl)
        {
            if (key == VirtualKey.Z)
                UndoDuel();
            return key == VirtualKey.Z;
        }

        var inDuel = TournamentDuel.Visibility == Visibility.Visible;
        switch (key)
        {
            case VirtualKey.Left or VirtualKey.A or VirtualKey.Number1 or VirtualKey.NumberPad1 when inDuel:
                PickDuel(left: true); return true;
            case VirtualKey.Right or VirtualKey.D or VirtualKey.Number2 or VirtualKey.NumberPad2 when inDuel:
                PickDuel(left: false); return true;
            case VirtualKey.Back:
                UndoDuel(); return true;
            case VirtualKey.Escape:
                _ = CloseTournamentAsync(ask: inDuel); return true;
            case VirtualKey.F11:
                ToggleFullScreen(); return true;
            default:
                return false;
        }
    }

    private void TournamentClose_Click(object sender, RoutedEventArgs e) =>
        _ = CloseTournamentAsync(ask: TournamentDuel.Visibility == Visibility.Visible);

    private async Task CloseTournamentAsync(bool ask)
    {
        if (ask && _tournamentAnswers.Count > 0)
        {
            var dialog = new ContentDialog
            {
                Title = "Выйти из турнира?",
                Content = $"Сделано {_tournamentAnswers.Count} выборов. Если выйти, турнир придётся начать заново.",
                PrimaryButtonText = "Выйти",
                CloseButtonText = "Продолжить",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        _tournamentRun++;
        _duelAnswer?.TrySetCanceled();
        _duelAnswer = null;
        _duel = null;
        _tournament = null;
        _duelImages.Clear();
        TournamentView.Visibility = Visibility.Collapsed;
        TournamentLeftImage.Source = null;
        TournamentRightImage.Source = null;
        TournamentWinners.Items.Clear();
        UpdateCounter();
        Gallery.Focus(FocusState.Programmatic);
    }

    // ---------- Result ----------

    private void ShowTournamentResult(IReadOnlyList<PhotoItem> winners)
    {
        _duel = null;
        _tournamentWinners = winners;
        TournamentDuel.Visibility = Visibility.Collapsed;
        TournamentHint.Visibility = Visibility.Collapsed;
        TournamentResult.Visibility = Visibility.Visible;
        TournamentProgress.Value = 1;
        TournamentStageText.Text = "Готово. Backspace — вернуться к последней дуэли";
        TournamentCountText.Text = $"{_tournamentAnswers.Count} дуэлей";
        TournamentResultTitle.Text = winners.Count == 1 ? "Лучшее фото" : $"Лучшие {winners.Count}";

        TournamentWinners.Items.Clear();
        for (var i = 0; i < winners.Count; i++)
            TournamentWinners.Items.Add(WinnerTile(i + 1, winners[i]));
        PushRemoteState();
    }

    private Grid WinnerTile(int place, PhotoItem photo)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        _ = LoadWinnerAsync(image, photo);
        var tile = new Grid
        {
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Children =
            {
                image,
                new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(8),
                    Padding = new Thickness(10, 2, 10, 2),
                    CornerRadius = new CornerRadius(10),
                    Background = place == 1 ? (Brush)Application.Current.Resources["SuperLikeBrush"] : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xCC, 0, 0, 0)),
                    Child = new TextBlock
                    {
                        Text = place.ToString(),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = place == 1 ? new SolidColorBrush(Microsoft.UI.Colors.Black) : new SolidColorBrush(Microsoft.UI.Colors.White),
                    },
                },
            },
        };
        ToolTipService.SetToolTip(tile, photo.FileName);
        tile.DoubleTapped += (_, _) =>
        {
            _ = CloseTournamentAsync(ask: false);
            if (_visible.IndexOf(photo) is var index and >= 0)
                OpenLoupe(index);
        };
        return tile;
    }

    private async Task LoadWinnerAsync(Image image, PhotoItem photo)
    {
        image.Source = await PhotoItem.LoadCachedThumbnailAsync(_thumbnails, photo.Record);
    }

    private void TournamentSuperLike_Click(object sender, RoutedEventArgs e) => SuperLikeWinners();

    private void SuperLikeWinners()
    {
        _ratings.Rate(_tournamentWinners.ToList(), Tier.SuperLike);
        UpdateCounter();
        ShowToast($"Суперлайк у {_tournamentWinners.Count} фото. Фильтр «Суперлайки» покажет только их");
    }

    private void TournamentCopy_Click(object sender, RoutedEventArgs e) =>
        _ = CopyPhotosAsync(_tournamentWinners.ToList(), pathsOnly: false);

    private void TournamentCompare_Click(object sender, RoutedEventArgs e) => _ = CompareWinnersAsync();

    private async Task CompareWinnersAsync()
    {
        var winners = _tournamentWinners.ToList();
        await CloseTournamentAsync(ask: false);
        ComparePhotos(winners);
    }

    private void TournamentShow_Click(object sender, RoutedEventArgs e) => _ = ShowWinnersAsync();

    private async Task ShowWinnersAsync()
    {
        var winners = _tournamentWinners.ToList();
        await CloseTournamentAsync(ask: false);
        if (winners.Any(w => !_visible.Contains(w)))
            SetFilter(TierFilter.All);

        Gallery.SelectedItems.Clear();
        foreach (var winner in winners)
            Gallery.SelectedItems.Add(winner);
        if (winners.Count > 0)
            Gallery.ScrollIntoView(winners[0]);
        ShowToast($"Выделены {winners.Count} победителей");
    }
}
