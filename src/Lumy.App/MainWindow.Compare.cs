using Lumy.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Lumy.App;

public sealed partial class MainWindow
{
    private const int MaxCompare = 9;

    private readonly List<CompareCell> _compareCells = [];
    private CompareCell? _hoveredCell;
    private bool _syncCompare = true;
    private bool _syncingCompare;

    private bool InCompare => CompareView.Visibility == Visibility.Visible;

    /// <summary>
    /// C: compare the photos selected in the gallery, or, with one photo chosen (or in the viewer), every frame of its series.
    /// </summary>
    private void StartCompare()
    {
        List<PhotoItem> photos;
        if (InLoupe)
        {
            // Photos picked in the filmstrip with Ctrl/Shift win; otherwise the open photo's series.
            var picked = FilmstripPicks();
            photos = picked.Count >= 2 ? picked
                : _loupeIndex >= 0 && _loupeIndex < _visible.Count ? SeriesOf(_visible[_loupeIndex]) : [];
        }
        else
        {
            var selected = Gallery.SelectedItems.OfType<PhotoItem>().ToList();
            photos = selected.Count >= 2 ? selected : selected.Count == 1 ? SeriesOf(selected[0]) : [];
        }

        ComparePhotos(photos);
    }

    private void ComparePhotos(List<PhotoItem> photos)
    {
        if (photos.Count < 2)
        {
            ShowToast(InLoupe ? "Выберите 2–9 фото в ленте (Shift или Ctrl + клик) или откройте фото из серии" : "Выделите 2–9 фото (Ctrl + клик) или фото из серии");
            return;
        }

        if (photos.Count > MaxCompare)
        {
            ShowToast($"Сравниваю первые {MaxCompare} из {photos.Count}");
            photos = photos.Take(MaxCompare).ToList();
        }

        CloseLoupe();
        CompareView.Visibility = Visibility.Visible;
        BuildCompare(photos);
    }

    private List<PhotoItem> SeriesOf(PhotoItem photo) =>
        photo.SeriesId == 0
            ? [photo]
            : _all.Where(p => p.SeriesId == photo.SeriesId).OrderBy(p => p.SeriesPosition).ToList();

    private void CloseCompare()
    {
        var focus = _hoveredCell?.Photo ?? (_compareCells.Count == 1 ? _compareCells[0].Photo : null);
        CompareView.Visibility = Visibility.Collapsed;
        CompareGrid.Children.Clear();
        _compareCells.Clear();
        _hoveredCell = null;
        UpdateCounter();

        if (focus is not null && _visible.IndexOf(focus) is var index and >= 0)
        {
            Gallery.SelectedIndex = index;
            Gallery.ScrollIntoView(focus);
        }

        Gallery.Focus(FocusState.Programmatic);
    }

    private bool HandleCompareKey(VirtualKey key, bool ctrl)
    {
        if (ctrl)
        {
            if (key is VirtualKey.Z or VirtualKey.Y)
            {
                UndoOrRedo(redo: key == VirtualKey.Y);
                RefreshCompareBadges();
                return true;
            }

            return false;
        }

        if (key is >= VirtualKey.Number1 and <= VirtualKey.Number9 || key is >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9)
        {
            var n = key >= VirtualKey.NumberPad1 ? key - VirtualKey.NumberPad1 : key - VirtualKey.Number1;
            RemoveFromCompare(n);
            return true;
        }

        switch (key)
        {
            case VirtualKey.F: RateHovered(Tier.Like); return true;
            case VirtualKey.R: RateHovered(Tier.SuperLike); return true;
            case VirtualKey.X: RateHovered(Tier.Rejected); return true;
            case VirtualKey.Number0 or VirtualKey.NumberPad0: RateHovered(Tier.None); return true;
            case VirtualKey.L:
                _syncCompare = !_syncCompare;
                UpdateCompareInfo();
                ShowToast(_syncCompare ? "Зум и сдвиг синхронно" : "Зум и сдвиг по отдельности");
                return true;
            case VirtualKey.Escape: CloseCompare(); return true;
            case VirtualKey.Enter when _compareCells.Count == 1: CloseCompare(); return true;
            case VirtualKey.F11: ToggleFullScreen(); return true;
            default: return false;
        }
    }

    private void RateHovered(Tier tier)
    {
        if (_hoveredCell is not { } cell)
        {
            ShowToast("Наведите мышь на фото, которое хотите оценить");
            return;
        }

        _ratings.Rate([cell.Photo], tier);
        RefreshCompareBadges();
    }

    private void RemoveFromCompare(int index)
    {
        if (index < 0 || index >= _compareCells.Count || _compareCells.Count == 1)
            return;

        if (_hoveredCell == _compareCells[index])
            _hoveredCell = null;
        _compareCells.RemoveAt(index);
        LayoutCompare();
        if (_compareCells.Count == 1)
            ShowToast("Остался один кадр. Enter — к нему в галерее");
    }

    private void BuildCompare(IReadOnlyList<PhotoItem> photos)
    {
        CompareGrid.Children.Clear();
        _compareCells.Clear();
        _hoveredCell = null;
        foreach (var photo in photos)
            _compareCells.Add(new CompareCell(this, photo));
        LayoutCompare();
    }

    private void CompareGrid_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutCompare();

    private void LayoutCompare()
    {
        if (!InCompare || _compareCells.Count == 0)
            return;

        var width = Math.Max(CompareGrid.ActualWidth, 1);
        var height = Math.Max(CompareGrid.ActualHeight, 1);
        var aspect = _compareCells[0].Aspect ?? 1.5;
        var (rows, columns) = CompareLayout.Best(_compareCells.Count, width, height, aspect);

        CompareGrid.Children.Clear();
        CompareGrid.RowDefinitions.Clear();
        CompareGrid.ColumnDefinitions.Clear();
        for (var r = 0; r < rows; r++)
            CompareGrid.RowDefinitions.Add(new RowDefinition());
        for (var c = 0; c < columns; c++)
            CompareGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var scale = CompareGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var decodeWidth = (int)Math.Min(4096, width / columns * scale * 2); // room to zoom 2× before it softens
        for (var i = 0; i < _compareCells.Count; i++)
        {
            var cell = _compareCells[i];
            Grid.SetRow(cell.Root, i / columns);
            Grid.SetColumn(cell.Root, i % columns);
            cell.SetNumber(i + 1);
            CompareGrid.Children.Add(cell.Root);
            _ = cell.LoadAsync(decodeWidth);
        }

        UpdateCompareInfo();
    }

    private void UpdateCompareInfo()
    {
        CompareInfo.Text = $"{_compareCells.Count} фото  ·  синхронный зум {(_syncCompare ? "вкл" : "выкл")}";
        PushRemoteState();
    }

    private void RefreshCompareBadges()
    {
        foreach (var cell in _compareCells)
            cell.UpdateBadge();
        UpdateCounter();
    }

    private void OnCompareViewChanged(CompareCell source)
    {
        if (!_syncCompare || _syncingCompare)
            return;

        _syncingCompare = true;
        foreach (var cell in _compareCells.Where(c => c != source))
            cell.Scroll.ChangeView(source.Scroll.HorizontalOffset, source.Scroll.VerticalOffset, source.Scroll.ZoomFactor, disableAnimation: true);
        _syncingCompare = false;
    }

    /// <summary>One photo in the comparison grid: a zoomable image with its number, name and tier.</summary>
    private sealed class CompareCell
    {
        private readonly MainWindow _owner;
        private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        // Like the viewer: a canvas the size of the cell holds the photo centred, and zooming scales the canvas.
        private readonly Grid _canvas = new() { Background = new SolidColorBrush(Colors.Transparent) };
        private readonly TextBlock _number = new() { FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        private readonly FontIcon _tier = new() { FontSize = 14 };
        private int _decodedWidth;

        public CompareCell(MainWindow owner, PhotoItem photo)
        {
            _owner = owner;
            Photo = photo;
            Scroll = new ScrollViewer
            {
                ZoomMode = ZoomMode.Enabled,
                MinZoomFactor = 1,
                MaxZoomFactor = 16,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Content = _canvas,
            };
            _canvas.Children.Add(_image);
            Scroll.SizeChanged += (_, e) =>
            {
                _canvas.Width = e.NewSize.Width;
                _canvas.Height = e.NewSize.Height;
            };
            Scroll.ViewChanged += (_, _) => _owner.OnCompareViewChanged(this);

            var label = new Border
            {
                Background = new SolidColorBrush(ColorHelper.FromArgb(0x99, 0, 0, 0)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _number, _tier, new TextBlock { Text = photo.FileName, FontSize = 12, Opacity = 0.8 } },
                },
            };

            Root = new Grid { Margin = new Thickness(4), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Colors.Transparent) };
            Root.Children.Add(Scroll);
            Root.Children.Add(label);
            Root.BorderThickness = new Thickness(2);
            Root.PointerEntered += (_, _) => Point();
            Root.PointerExited += (_, _) =>
            {
                if (_owner._hoveredCell == this)
                    _owner._hoveredCell = null;
                Root.BorderBrush = null;
            };
            UpdateBadge();
        }

        public PhotoItem Photo { get; }

        /// <summary>Makes this the photo F, R, X and 0 rate: under the mouse, or picked on the phone.</summary>
        public void Point()
        {
            if (_owner._hoveredCell is { } previous && previous != this)
                previous.Root.BorderBrush = null;
            _owner._hoveredCell = this;
            Root.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        }

        public Grid Root { get; }

        public ScrollViewer Scroll { get; }

        /// <summary>Width / height of the decoded photo, once known.</summary>
        public double? Aspect { get; private set; }

        /// <summary>The photo has the same gap on both sides inside its cell.</summary>
        public bool IsCentred()
        {
            if (_image.ActualWidth <= 0 || Scroll.ActualWidth <= 0)
                return false;

            var origin = _image.TransformToVisual(Scroll).TransformPoint(new Windows.Foundation.Point(0, 0));
            var left = origin.X;
            var right = Scroll.ActualWidth - (origin.X + _image.ActualWidth);
            var top = origin.Y;
            var bottom = Scroll.ActualHeight - (origin.Y + _image.ActualHeight);
            return Math.Abs(left - right) <= 2 && Math.Abs(top - bottom) <= 2;
        }

        public void SetNumber(int n) => _number.Text = n.ToString();

        public void UpdateBadge()
        {
            _tier.Glyph = Photo.Tier == Tier.None ? "\uEB51" : Photo.TierGlyph;
            _tier.Foreground = Photo.TierBrush ?? (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        }

        public async Task LoadAsync(int width)
        {
            if (width <= _decodedWidth)
                return;

            _decodedWidth = width;
            _image.Source ??= Photo.Thumbnail;
            var bitmap = await LoupeImageCache.LoadAsync(Photo.Path, width);
            if (bitmap is null || width != _decodedWidth)
                return;

            _image.Source = bitmap;
            if (Aspect is null && bitmap.PixelHeight > 0)
            {
                Aspect = (double)bitmap.PixelWidth / bitmap.PixelHeight;

                // The grid was laid out for landscape frames; portrait ones may fit better another way.
                if (_owner._compareCells.FirstOrDefault() == this && Math.Abs(Aspect.Value - 1.5) > 0.3)
                    _owner.LayoutCompare();
            }
        }
    }
}
