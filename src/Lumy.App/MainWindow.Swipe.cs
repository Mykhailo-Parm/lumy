using Lumy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;

namespace Lumy.App;

public sealed partial class MainWindow
{
    private SwipeSession<PhotoItem>? _swipe;
    private Storyboard? _swipeOut;
    private int _swipeRequest;

    private enum SwipeMove
    {
        Like,
        SuperLike,
        Reject,
        RejectSeries,
        Skip,
    }

    private bool InSwipe => SwipeView.Visibility == Visibility.Visible;

    /// <summary>S: swipe through the photos of the current view that have no rating yet.</summary>
    private void StartSwipe(IEnumerable<PhotoItem>? photos = null)
    {
        var queue = (photos ?? _visible.Where(p => p.Tier == Tier.None)).ToList();
        if (queue.Count == 0)
        {
            ShowToast("Все фото здесь уже оценены");
            return;
        }

        CloseLoupe();
        _swipe = new SwipeSession<PhotoItem>(queue, _ratings);
        SwipeSummary.Visibility = Visibility.Collapsed;
        SwipeCard.Visibility = Visibility.Visible;
        SwipeNextImage.Visibility = Visibility.Visible;
        SwipeView.Visibility = Visibility.Visible;
        SwipeProgressBar.Maximum = queue.Count;
        ShowSwipeCard();
    }

    private void CloseSwipe()
    {
        FinishSwipeAnimation();
        _swipe = null;
        SwipeView.Visibility = Visibility.Collapsed;
        SwipeImage.Source = null;
        SwipeNextImage.Source = null;
        UpdateCounter();
        Gallery.Focus(FocusState.Programmatic);
    }

    private bool HandleSwipeKey(VirtualKey key, bool ctrl, bool shift)
    {
        if (_swipe is null)
            return false;

        if (ctrl)
        {
            if (key == VirtualKey.Z)
                SwipeBack();
            return key == VirtualKey.Z;
        }

        switch (key)
        {
            case VirtualKey.Right or VirtualKey.D or VirtualKey.F or VirtualKey.Number1 or VirtualKey.NumberPad1:
                Swipe(SwipeMove.Like); return true;
            case VirtualKey.Up or VirtualKey.W or VirtualKey.R or VirtualKey.Number2 or VirtualKey.NumberPad2:
                Swipe(SwipeMove.SuperLike); return true;
            case VirtualKey.Left or VirtualKey.A or VirtualKey.X:
                Swipe(shift ? SwipeMove.RejectSeries : SwipeMove.Reject); return true;
            case VirtualKey.Down or VirtualKey.S:
                Swipe(SwipeMove.Skip); return true;
            case VirtualKey.Back:
                SwipeBack(); return true;
            case VirtualKey.Escape:
                CloseSwipe(); return true;
            case VirtualKey.Enter when _swipe.IsFinished:
                CloseSwipe(); return true;
            case VirtualKey.F11:
                ToggleFullScreen(); return true;
            default:
                return false;
        }
    }

    private void Swipe(SwipeMove move)
    {
        if (_swipe is not { IsFinished: false } session)
            return;

        FinishSwipeAnimation();
        var photo = session.Current!;
        switch (move)
        {
            case SwipeMove.Like: session.Decide(Tier.Like); break;
            case SwipeMove.SuperLike: session.Decide(Tier.SuperLike); break;
            case SwipeMove.Reject: session.Decide(Tier.Rejected); break;
            case SwipeMove.RejectSeries:
                session.Decide(Tier.Rejected, p => photo.SeriesId != 0 && p.SeriesId == photo.SeriesId);
                break;
            case SwipeMove.Skip: session.Skip(); break;
        }

        var (dx, dy) = move switch
        {
            SwipeMove.Like => (1, 0),
            SwipeMove.SuperLike => (0, -1),
            SwipeMove.Skip => (0, 1),
            _ => (-1, 0),
        };
        ShowSwipeHint(move, 1);
        AnimateCardOut(dx, dy);
        ShowSwipeBehind();
        UpdateSwipeChrome();
    }

    private void SwipeBack()
    {
        if (_swipe is not { CanGoBack: true } session)
            return;

        FinishSwipeAnimation();
        session.Back();
        SwipeSummary.Visibility = Visibility.Collapsed;
        SwipeCard.Visibility = Visibility.Visible;
        SwipeNextImage.Visibility = Visibility.Visible;
        ShowSwipeCard();
    }

    /// <summary>Puts the current photo on the card and the next one behind it.</summary>
    private async void ShowSwipeCard()
    {
        if (_swipe is not { } session)
            return;

        ResetCard();
        UpdateSwipeChrome();
        if (session.IsFinished)
        {
            ShowSwipeSummary();
            return;
        }

        var request = ++_swipeRequest;
        var photo = session.Current!;
        _loupeCache.Retain(session.Upcoming(4));
        SwipeImage.Source = photo.Thumbnail;
        ShowSwipeBehind();

        var bitmap = await _loupeCache.GetAsync(photo);
        if (request == _swipeRequest && bitmap is not null)
            SwipeImage.Source = bitmap;
    }

    private async void ShowSwipeBehind()
    {
        var next = _swipe?.IsFinished == false ? _swipe.Current : null;

        // While the card flies off, the photo now current waits underneath; after that, the one after it.
        if (_swipeOut is null)
            next = _swipe?.Next;

        if (next is null)
        {
            SwipeNextImage.Source = null;
            return;
        }

        var request = _swipeRequest;
        SwipeNextImage.Source = next.Thumbnail;
        var bitmap = await _loupeCache.GetAsync(next);
        if (request == _swipeRequest && bitmap is not null && SwipeNextImage.Source == next.Thumbnail)
            SwipeNextImage.Source = bitmap;
    }

    private void UpdateSwipeChrome()
    {
        if (_swipe is not { } session)
            return;

        PushRemoteState();

        SwipeProgressBar.Maximum = Math.Max(1, session.Total);
        SwipeProgressBar.Value = session.IsFinished ? session.Total : session.Position - 1;
        SwipeProgressText.Text = session.IsFinished
            ? $"{session.Total} / {session.Total}"
            : $"{session.Position} / {session.Total}  ·  ♥ {session.Liked}  ·  ★ {session.SuperLiked}  ·  👎 {session.Rejected}";
        SwipeSeriesText.Text = session.Current is { SeriesSize: > 1 } p
            ? $"Серия: кадр {p.SeriesPosition} из {p.SeriesSize}  ·  Shift+← отклонить всю серию"
            : "";
    }

    private void ShowSwipeSummary()
    {
        if (_swipe is not { } session)
            return;

        SwipeCard.Visibility = Visibility.Collapsed;
        SwipeNextImage.Visibility = Visibility.Collapsed;
        SwipeSeriesText.Text = "";
        SwipeSummaryText.Text = $"♥ {session.Liked} лайков  ·  ★ {session.SuperLiked} суперлайков  ·  👎 {session.Rejected} отклонено"
            + (session.Skipped > 0 ? $"  ·  {session.Skipped} без решения" : "");
        SwipeSummary.Visibility = Visibility.Visible;
    }

    // ---------- Card animation and dragging ----------

    private void AnimateCardOut(int dx, int dy)
    {
        var width = Math.Max(SwipeStage.ActualWidth, 1);
        var height = Math.Max(SwipeStage.ActualHeight, 1);
        var duration = new Duration(TimeSpan.FromMilliseconds(160));
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

        var storyboard = new Storyboard();
        void Animate(DependencyObject target, string property, double to)
        {
            var animation = new DoubleAnimation { To = to, Duration = duration, EasingFunction = ease };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }

        Animate(SwipeTransform, "TranslateX", dx * width * 1.1);
        Animate(SwipeTransform, "TranslateY", dy * height * 1.1);
        Animate(SwipeTransform, "Rotation", dx * 14);
        Animate(SwipeCard, "Opacity", 0);

        storyboard.Completed += (_, _) =>
        {
            if (_swipeOut != storyboard)
                return;
            _swipeOut = null;
            ShowSwipeCard();
        };
        _swipeOut = storyboard;
        storyboard.Begin();
    }

    /// <summary>A new key press while a card is still flying lands it at once, so fast culling never waits on animation.</summary>
    private void FinishSwipeAnimation()
    {
        if (_swipeOut is not { } storyboard)
            return;

        _swipeOut = null;
        storyboard.Stop();
        ShowSwipeCard();
    }

    private void ResetCard()
    {
        SwipeTransform.TranslateX = 0;
        SwipeTransform.TranslateY = 0;
        SwipeTransform.Rotation = 0;
        SwipeCard.Opacity = 1;
        SwipeHint.Opacity = 0;
    }

    private void SwipeCard_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (_swipe is not { IsFinished: false } || _swipeOut is not null)
            return;

        SwipeTransform.TranslateX += e.Delta.Translation.X;
        SwipeTransform.TranslateY += e.Delta.Translation.Y;
        SwipeTransform.Rotation = SwipeTransform.TranslateX / Math.Max(SwipeStage.ActualWidth, 1) * 14;

        var (move, strength) = DragIntent();
        if (move is { } m)
            ShowSwipeHint(m, strength);
        else
            SwipeHint.Opacity = 0;
    }

    private void SwipeCard_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (_swipe is not { IsFinished: false } || _swipeOut is not null)
            return;

        var (move, strength) = DragIntent();
        if (move is { } m && strength >= 1)
            Swipe(m);
        else
            ResetCard();
    }

    /// <summary>Which way the card is being dragged, and how far toward the decision point (1 = far enough).</summary>
    private (SwipeMove? Move, double Strength) DragIntent()
    {
        var x = SwipeTransform.TranslateX / Math.Max(SwipeStage.ActualWidth * 0.25, 1);
        var y = SwipeTransform.TranslateY / Math.Max(SwipeStage.ActualHeight * 0.2, 1);
        if (Math.Abs(x) < 0.15 && Math.Abs(y) < 0.15)
            return (null, 0);

        return Math.Abs(x) >= Math.Abs(y)
            ? (x > 0 ? SwipeMove.Like : SwipeMove.Reject, Math.Abs(x))
            : (y < 0 ? SwipeMove.SuperLike : SwipeMove.Skip, Math.Abs(y));
    }

    private void ShowSwipeHint(SwipeMove move, double strength)
    {
        var tier = move switch
        {
            SwipeMove.Like => Tier.Like,
            SwipeMove.SuperLike => Tier.SuperLike,
            SwipeMove.Skip => Tier.None,
            _ => Tier.Rejected,
        };
        SwipeHintIcon.Glyph = tier == Tier.None ? "\uE74B" : PhotoItem.GlyphFor(tier);
        SwipeHintIcon.Foreground = PhotoItem.BrushFor(tier) ?? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        SwipeHint.Opacity = Math.Clamp(strength, 0, 1);
    }

    // ---------- Buttons ----------

    private void SwipeLike_Click(object sender, RoutedEventArgs e) => Swipe(SwipeMove.Like);

    private void SwipeSuper_Click(object sender, RoutedEventArgs e) => Swipe(SwipeMove.SuperLike);

    private void SwipeReject_Click(object sender, RoutedEventArgs e) =>
        Swipe(IsDown(VirtualKey.Shift) ? SwipeMove.RejectSeries : SwipeMove.Reject);

    private void SwipeSkip_Click(object sender, RoutedEventArgs e) => Swipe(SwipeMove.Skip);

    private void SwipeBack_Click(object sender, RoutedEventArgs e) => SwipeBack();

    private void SwipeClose_Click(object sender, RoutedEventArgs e) => CloseSwipe();

    private void SwipeShowLikes_Click(object sender, RoutedEventArgs e)
    {
        CloseSwipe();
        SetFilter(TierFilter.LikedOrBetter);
    }

    /// <summary>A second, stricter pass over the likes: → keeps, ↑ promotes, ← drops.</summary>
    private void SwipeAgainLikes_Click(object sender, RoutedEventArgs e)
    {
        var likes = _all.Where(p => p.Tier >= Tier.Like).ToList();
        CloseSwipe();
        StartSwipe(likes);
    }
}
