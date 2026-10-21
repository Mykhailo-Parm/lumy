using Lumy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Lumy.App;

/// <summary>
/// Overlay (O in comparison): two frames exactly on top of each other, with a wipe that follows the mouse or a fade
/// between them, so a closed eye or a slightly sharper face shows at once.
/// </summary>
public sealed partial class MainWindow
{
    private const string CompareGridHint = "Наведи на фото: F лайк  ·  R суперлайк  ·  X отказ (ещё раз — снять)  ·  1–9 убрать  ·  двойной клик или Ctrl+колесо — зум, мышью — сдвиг  ·  L синхронно  ·  O наложение  ·  Esc назад";
    private const string OverlayHint = "Тяни шторку мышью  ·  колесо или ← → тоже  ·  M шторка / прозрачность  ·  S поменять местами  ·  F R X оценивают фото под мышью  ·  O или Esc — к сетке";

    private CompareCell? _overlayLeft;
    private CompareCell? _overlayRight;
    private bool _overlayFade;
    private bool _overlaySyncing;
    private int _overlayRequest;
    private bool _overlayDragging;

    private bool InOverlay => OverlayStage.Visibility == Visibility.Visible;

    /// <summary>O: the photo under the mouse (or the first) and its neighbour on top of each other; O again goes back.</summary>
    private void ToggleOverlay()
    {
        if (InOverlay)
        {
            CloseOverlay();
            return;
        }

        if (_compareCells.Count < 2)
        {
            ShowToast("Для наложения нужно хотя бы 2 фото");
            return;
        }

        var hovered = _hoveredCell is { } cell ? _compareCells.IndexOf(cell) : -1;
        var first = Math.Max(hovered, 0);
        var second = first + 1 < _compareCells.Count ? first + 1 : first - 1;
        OpenOverlay(_compareCells[Math.Min(first, second)], _compareCells[Math.Max(first, second)]);
    }

    private void OpenOverlay(CompareCell left, CompareCell right)
    {
        _overlayLeft = left;
        _overlayRight = right;
        CompareGrid.Visibility = Visibility.Collapsed;
        OverlayStage.Visibility = Visibility.Visible;
        OverlayBar.Visibility = Visibility.Visible;
        CompareHint.Text = OverlayHint;
        SetOverlayValue(0.5);
        SetOverlayFade(_overlayFade);
        _ = LoadOverlayAsync();
        UpdateCompareInfo();
    }

    private void CloseOverlay()
    {
        if (!InOverlay)
            return;

        _overlayRequest++;
        OverlayStage.Visibility = Visibility.Collapsed;
        OverlayBar.Visibility = Visibility.Collapsed;
        CompareGrid.Visibility = Visibility.Visible;
        CompareHint.Text = CompareGridHint;
        OverlayTop.Source = null;
        OverlayBottom.Source = null;
        _overlayLeft = null;
        _overlayRight = null;
        if (InCompare)
            UpdateCompareInfo();
    }

    private async Task LoadOverlayAsync()
    {
        if (_overlayLeft is not { } left || _overlayRight is not { } right)
            return;

        var request = ++_overlayRequest;
        OverlayLeftName.Text = $"{_compareCells.IndexOf(left) + 1}  ·  {left.Photo.FileName}";
        OverlayRightName.Text = $"{_compareCells.IndexOf(right) + 1}  ·  {right.Photo.FileName}";
        OverlayBottom.Source = left.Photo.Thumbnail;
        OverlayTop.Source = right.Photo.Thumbnail;

        var scale = OverlayStage.XamlRoot?.RasterizationScale ?? 1.0;
        var width = (int)Math.Min(4096, Math.Max(OverlayStage.ActualWidth, 800) * scale);
        var bottom = LoupeImageCache.LoadAsync(left.Photo.Path, width);
        var top = LoupeImageCache.LoadAsync(right.Photo.Path, width);
        var (bottomImage, topImage) = (await bottom, await top);
        if (request != _overlayRequest)
            return;

        OverlayBottom.Source = bottomImage ?? OverlayBottom.Source;
        OverlayTop.Source = topImage ?? OverlayTop.Source;
    }

    private void SwapOverlay()
    {
        if (_overlayLeft is null || _overlayRight is null)
            return;

        (_overlayLeft, _overlayRight) = (_overlayRight, _overlayLeft);
        _ = LoadOverlayAsync();
        PushRemoteState();
    }

    private void SetOverlayFade(bool fade)
    {
        _overlayFade = fade;
        OverlayWipeButton.IsChecked = !fade;
        OverlayFadeButton.IsChecked = fade;
        UpdateOverlay();
        PushRemoteState();
    }

    private void SetOverlayValue(double value)
    {
        _overlaySyncing = true;
        OverlaySlider.Value = Math.Clamp(value, 0, 1);
        _overlaySyncing = false;
        UpdateOverlay();
    }

    /// <summary>Clips the top photo at the wipe, or fades it, and points rating at the photo that shows most.</summary>
    private void UpdateOverlay()
    {
        if (!InOverlay)
            return;

        var value = OverlaySlider.Value;
        var image = OverlayImageRect();
        if (_overlayFade || image.IsEmpty)
        {
            OverlayTop.Clip = null;
            OverlayTop.Opacity = _overlayFade ? value : 1;
            OverlaySplit.Visibility = Visibility.Collapsed;
        }
        else
        {
            // The top photo is the right one: it shows to the right of the wipe.
            OverlayTop.Opacity = 1;
            var split = image.Width * value;
            OverlayTop.Clip = new RectangleGeometry { Rect = new Rect(split, 0, Math.Max(0, image.Width - split), image.Height) };
            OverlaySplit.Visibility = Visibility.Visible;
            OverlaySplit.Height = image.Height;
            Canvas.SetLeft(OverlaySplit, image.X + split - OverlaySplit.Width / 2);
            Canvas.SetTop(OverlaySplit, image.Y);
        }

        if (_overlayFade)
            (value >= 0.5 ? _overlayRight : _overlayLeft)?.Point();
        PushRemoteState();
    }

    /// <summary>Where the top photo is drawn inside the stage.</summary>
    private Rect OverlayImageRect()
    {
        if (OverlayTop.ActualWidth <= 0 || OverlayTop.ActualHeight <= 0)
            return Rect.Empty;

        var origin = OverlayTop.TransformToVisual(OverlayStage).TransformPoint(new Point(0, 0));
        return new Rect(origin.X, origin.Y, OverlayTop.ActualWidth, OverlayTop.ActualHeight);
    }

    private bool HandleOverlayKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.O or VirtualKey.Escape: CloseOverlay(); return true;
            case VirtualKey.M: SetOverlayFade(!_overlayFade); return true;
            case VirtualKey.S: SwapOverlay(); return true;
            case VirtualKey.Left: SetOverlayValue(OverlaySlider.Value - 0.05); return true;
            case VirtualKey.Right: SetOverlayValue(OverlaySlider.Value + 0.05); return true;
            default: return false;
        }
    }

    private void OverlayStage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_overlayFade)
            return;

        _overlayDragging = true;
        OverlayStage.CapturePointer(e.Pointer);
        FollowPointer(e);
        e.Handled = true;
    }

    private void OverlayStage_PointerMoved(object sender, PointerRoutedEventArgs e) => FollowPointer(e);

    private void OverlayStage_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_overlayDragging)
            return;

        _overlayDragging = false;
        OverlayStage.ReleasePointerCapture(e.Pointer);
    }

    private void OverlayStage_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _overlayDragging = false;

    /// <summary>Dragging moves the wipe; the photo on the mouse's side of it is the one F, R and X rate.</summary>
    private void FollowPointer(PointerRoutedEventArgs e)
    {
        var image = OverlayImageRect();
        if (image.IsEmpty || _overlayFade)
            return;

        var x = (e.GetCurrentPoint(OverlayStage).Position.X - image.X) / image.Width;
        if (_overlayDragging)
            SetOverlayValue(x);
        (x >= OverlaySlider.Value ? _overlayRight : _overlayLeft)?.Point();
        PushRemoteState();
    }

    private void OverlayStage_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(OverlayStage).Properties.MouseWheelDelta;
        SetOverlayValue(OverlaySlider.Value + Math.Sign(delta) * 0.05);
        e.Handled = true;
    }

    private void OverlayStage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateOverlay();

    private void OverlayWipe_Click(object sender, RoutedEventArgs e) => SetOverlayFade(false);

    private void OverlayFade_Click(object sender, RoutedEventArgs e) => SetOverlayFade(true);

    private void OverlaySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_overlaySyncing)
            UpdateOverlay();
    }

    private void OverlaySwap_Click(object sender, RoutedEventArgs e) => SwapOverlay();
}
