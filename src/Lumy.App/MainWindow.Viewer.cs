using Lumy.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Lumy.App;

/// <summary>
/// The full-screen viewer's controls: rating buttons, filmstrip, mouse wheel and drag, shooting details.
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<long, string?> _details = [];
    private ScrollViewer? _filmstripScroll;
    private double? _filmstripTarget;
    private bool _syncingFilmstrip;
    private int _wheelAccumulator;
    private (Point Start, double Horizontal, double Vertical)? _pan;

    private void SetUpViewer()
    {
        Filmstrip.ItemsSource = _visible;
        ApplyFilmstripVisibility(_settings.FilmstripVisible);

        // The scroll viewer marks wheel and press events handled for itself; these still need to see them.
        LoupeScroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(LoupeScroll_PointerWheelChanged), handledEventsToo: true);
        LoupeScroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(LoupeScroll_PointerPressed), handledEventsToo: true);
        LoupeScroll.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(LoupeScroll_PointerMoved), handledEventsToo: true);
        LoupeScroll.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(LoupeScroll_PointerReleased), handledEventsToo: true);
        LoupeScroll.PointerCaptureLost += (_, _) => _pan = null;

        Filmstrip.PointerWheelChanged += Filmstrip_PointerWheelChanged;
        Filmstrip.Loaded += (_, _) =>
        {
            _filmstripScroll = FindChild<ScrollViewer>(Filmstrip);
            if (_filmstripScroll is not null)
                _filmstripScroll.ViewChanged += (_, e) =>
                {
                    if (!e.IsIntermediate)
                        _filmstripTarget = null;
                };
        };
    }

    private void LoupeClose_Click(object sender, RoutedEventArgs e) => CloseLoupe();

    // ---------- Rating buttons ----------

    private void LoupeReject_Click(object sender, RoutedEventArgs e) => ToggleLoupeTier(Tier.Rejected);

    private void LoupeLike_Click(object sender, RoutedEventArgs e) => ToggleLoupeTier(Tier.Like);

    private void LoupeSuper_Click(object sender, RoutedEventArgs e) => ToggleLoupeTier(Tier.SuperLike);

    /// <summary>A click sets the rating; a click on the rating the photo already has clears it.</summary>
    private void ToggleLoupeTier(Tier tier)
    {
        if (_loupeIndex < 0 || _loupeIndex >= _visible.Count)
            return;

        Rate(_visible[_loupeIndex].Tier == tier ? Tier.None : tier);
    }

    private void UpdateRatingButtons(Tier tier)
    {
        var idle = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        LoupeRejectIcon.Foreground = tier == Tier.Rejected ? PhotoItem.BrushFor(Tier.Rejected) : idle;
        LoupeRejectButton.Background = tier == Tier.Rejected ? (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"] : null;
        LoupeLikeIcon.Glyph = tier == Tier.Like ? "\uEB52" : "\uEB51";      // filled / outline heart
        LoupeLikeIcon.Foreground = tier == Tier.Like ? PhotoItem.BrushFor(Tier.Like) : idle;
        LoupeSuperIcon.Glyph = tier == Tier.SuperLike ? "\uE735" : "\uE734"; // filled / outline star
        LoupeSuperIcon.Foreground = tier == Tier.SuperLike ? PhotoItem.BrushFor(Tier.SuperLike) : idle;
    }

    // ---------- Filmstrip ----------

    private void FilmstripToggle_Click(object sender, RoutedEventArgs e) => ToggleFilmstrip();

    private void ToggleFilmstrip()
    {
        _settings.FilmstripVisible = !_settings.FilmstripVisible;
        ApplyFilmstripVisibility(_settings.FilmstripVisible);
        if (_settings.FilmstripVisible)
            SyncFilmstrip(_loupeIndex);
    }

    private void ApplyFilmstripVisibility(bool visible)
    {
        FilmstripPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FilmstripToggleIcon.Glyph = visible ? "\uE70D" : "\uE70E"; // chevron down hides it, chevron up shows it
    }

    private void Filmstrip_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        _filmstripThumbnails.OnContainerContentChanging(args);

    private void Filmstrip_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PhotoItem item && _visible.IndexOf(item) is var index and >= 0)
            ShowInLoupe(index);
    }

    /// <summary>Marks the open photo in the strip and scrolls it to the middle.</summary>
    private void SyncFilmstrip(int index)
    {
        if (FilmstripPanel.Visibility != Visibility.Visible || index < 0 || index >= _visible.Count || _syncingFilmstrip)
            return;

        _syncingFilmstrip = true;
        Filmstrip.SelectedIndex = index;
        _syncingFilmstrip = false;

        // After layout, so the strip knows its width the first time the viewer opens.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => CenterFilmstrip(index));
    }

    private void CenterFilmstrip(int index)
    {
        if (index < 0 || index >= _visible.Count)
            return;

        if (_filmstripScroll is not { } scroll || Filmstrip.ContainerFromIndex(index) is null)
        {
            // Far away from what the strip has laid out: jump there first, then centre.
            Filmstrip.ScrollIntoView(_visible[index]);
            if (_filmstripScroll is null)
                return;
            scroll = _filmstripScroll;
        }

        var pitch = (double)Application.Current.Resources["FilmstripItemWidth"] + 4; // tile + its 2 px margins
        var target = Filmstrip.Padding.Left + index * pitch + pitch / 2 - scroll.ViewportWidth / 2;
        _filmstripTarget = null;
        scroll.ChangeView(Math.Clamp(target, 0, Math.Max(0, scroll.ScrollableWidth)), null, null, disableAnimation: false);
    }

    /// <summary>The wheel over the strip only scrolls the strip; the open photo stays.</summary>
    private void Filmstrip_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_filmstripScroll is not { } scroll)
            return;

        var delta = e.GetCurrentPoint(Filmstrip).Properties.MouseWheelDelta;
        var from = _filmstripTarget ?? scroll.HorizontalOffset;
        _filmstripTarget = Math.Clamp(from - delta * 1.6, 0, Math.Max(0, scroll.ScrollableWidth));
        scroll.ChangeView(_filmstripTarget, null, null, disableAnimation: false);
        e.Handled = true;
    }

    // ---------- Wheel and drag on the photo ----------

    /// <summary>The wheel steps through photos; with Ctrl it zooms, and on a zoomed photo it scrolls.</summary>
    private void LoupeScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(LoupeScroll).Properties;
        if (IsDown(VirtualKey.Control) || properties.IsHorizontalMouseWheel || LoupeScroll.ZoomFactor > 1.01f)
            return;

        // Precision touchpads send many small steps; one photo per full notch.
        _wheelAccumulator += properties.MouseWheelDelta;
        while (_wheelAccumulator >= 120)
        {
            _wheelAccumulator -= 120;
            StepLoupe(-1);
        }

        while (_wheelAccumulator <= -120)
        {
            _wheelAccumulator += 120;
            StepLoupe(+1);
        }

        e.Handled = true;
    }

    private void LoupeScroll_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(LoupeScroll);
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse || !point.Properties.IsLeftButtonPressed || LoupeScroll.ZoomFactor <= 1.01f)
            return;

        _pan = (point.Position, LoupeScroll.HorizontalOffset, LoupeScroll.VerticalOffset);
        LoupeScroll.CapturePointer(e.Pointer);
    }

    /// <summary>Dragging a zoomed photo with the mouse moves it, like in any photo viewer.</summary>
    private void LoupeScroll_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(LoupeScroll).Position;
        _pointerInLoupe = position;
        if (_pan is not { } pan)
            return;

        LoupeScroll.ChangeView(pan.Horizontal - (position.X - pan.Start.X), pan.Vertical - (position.Y - pan.Start.Y), null, disableAnimation: true);
    }

    private void LoupeScroll_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pan is null)
            return;

        _pan = null;
        LoupeScroll.ReleasePointerCapture(e.Pointer);
    }

    // ---------- Shooting details ----------

    private string DetailsLine(PhotoItem item)
    {
        var parts = new List<string> { item.FormatLabel };
        if (item.Record.CapturedAt is { } taken)
            parts.Add($"{taken:dd.MM.yyyy HH:mm:ss}");
        if (_details.TryGetValue(item.Id, out var details) && !string.IsNullOrEmpty(details))
            parts.Add(details);
        parts.Add(PhotoMetadata.FormatSize(item.Record.FileSize));
        return string.Join("  ·  ", parts);
    }

    /// <summary>Reads camera, lens and exposure off the UI thread, once per photo.</summary>
    private async void LoadDetails(PhotoItem item)
    {
        if (_details.ContainsKey(item.Id))
            return;

        _details[item.Id] = null;
        var details = await Task.Run(() => PhotoMetadata.TryReadDetails(item.Path));
        _details[item.Id] = details?.Summary;
        if (InLoupe && _loupeIndex >= 0 && _loupeIndex < _visible.Count && _visible[_loupeIndex] == item)
            LoupeDetails.Text = DetailsLine(item);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindChild<T>(child) is { } nested)
                return nested;
        }

        return null;
    }
}
