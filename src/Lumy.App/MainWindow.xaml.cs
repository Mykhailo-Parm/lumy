using System.Collections.ObjectModel;
using Lumy.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace Lumy.App;

public sealed partial class MainWindow : Window
{
    private readonly LibraryDatabase _database;
    private readonly AppSettings _settings;
    private readonly RatingService _ratings;
    private readonly LoupeImageCache _loupeCache;
    private readonly ThumbnailService _thumbnails = new(ThumbnailService.DefaultFolder);
    private readonly ThumbnailTracker _galleryThumbnails;
    private readonly ThumbnailTracker _filmstripThumbnails;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(380) };

    /// <summary>Every photo in the open folder, in the chosen sort order.</summary>
    private List<PhotoItem> _all = [];

    /// <summary>The photos the current filter shows. Rated photos stay until the filter is chosen again, so nothing jumps while culling.</summary>
    private readonly ObservableCollection<PhotoItem> _visible = [];

    private string? _folder;
    private CancellationTokenSource? _loading;
    private int _loupeIndex = -1;
    private int _loupeRequest;
    private bool _zoomedIn;
    private Point _pointerInLoupe;

    public MainWindow(string? folder, bool selfTest = false)
    {
        InitializeComponent();
        _galleryThumbnails = new ThumbnailTracker(_thumbnails);
        _filmstripThumbnails = new ThumbnailTracker(_thumbnails);

        _database = new LibraryDatabase(LibraryDatabase.DefaultPath);
        _settings = new AppSettings(_database);
        _ratings = new RatingService(_database) { WriteXmpSidecars = _settings.WriteXmpSidecars };
        _ratings.SidecarWriteFailed += (path, _) =>
            DispatcherQueue.TryEnqueue(() => ShowToast($"Не удалось записать XMP рядом с {System.IO.Path.GetFileName(path)}. Оценка сохранена в Lumy."));

        var screenWidth = DisplayArea.Primary?.OuterBounds.Width ?? 2560;
        _loupeCache = new LoupeImageCache(Math.Clamp(screenWidth, 1920, 3840));

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1440, 920));
        Closed += (_, _) =>
        {
            _ = _remote?.StopAsync();
            _database.Dispose();
        };

        Gallery.ItemsSource = _visible;
        SetUpViewer();
        SizeSlider.Value = _settings.ThumbnailSize;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Opacity = 0; };
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); FlashBadge.Opacity = 0; };
        Gallery.SelectionChanged += (_, _) => PushRemoteState();
        if (_settings.RemoteEnabled)
            _ = StartRemoteAsync(showErrors: false);

        if (selfTest && folder is not null)
        {
            _ = RunSelfTestAsync(folder);
            return;
        }

        var start = folder ?? _settings.LastFolder;
        if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
            _ = OpenFolderAsync(start);
    }

    private bool InLoupe => Loupe.Visibility == Visibility.Visible;

    private TierFilter CurrentFilter =>
        Enum.TryParse<TierFilter>(FilterBar.SelectedItem?.Tag as string, out var f) ? f : TierFilter.All;

    private PhotoSort CurrentSort =>
        Enum.TryParse<PhotoSort>((SortBox.SelectedItem as ComboBoxItem)?.Tag as string, out var s) ? s : PhotoSort.CaptureTime;

    // ---------- Opening a folder ----------

    private async void OpenFolder_Click(object sender, RoutedEventArgs e) => await PickFolderAsync();

    private async Task PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var picked = await picker.PickSingleFolderAsync();
        if (picked is not null)
            await OpenFolderAsync(picked.Path);
    }

    private async Task OpenFolderAsync(string folder)
    {
        _loading?.Cancel();
        var cancellation = _loading = new CancellationTokenSource();

        CloseLoupe();
        if (InSwipe)
            CloseSwipe();
        if (InCompare)
            CloseCompare();
        EmptyState.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = true;
        FolderTitle.Text = folder;

        try
        {
            var sort = CurrentSort;
            var records = await Task.Run(() => FolderLoader.Load(_database, folder, sort, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;

            _folder = folder;
            _settings.LastFolder = folder;
            _loupeCache.Clear();
            _details.Clear();
            _galleryThumbnails.ReleaseAll();
            _filmstripThumbnails.ReleaseAll();
            _all = records.Select(r => new PhotoItem(r)).ToList();
            _remotePaths = _all.ToDictionary(p => p.Id, p => p.Path);
            ApplyFilter();
            _ = DetectSeriesAsync(_all);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"Не удалось открыть папку: {ex.Message}");
        }
        finally
        {
            if (cancellation == _loading)
                LoadingRing.IsActive = false;
            EmptyState.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Link;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var items = await e.DataView.GetStorageItemsAsync();
        var folder = items.OfType<StorageFolder>().FirstOrDefault()?.Path
            ?? System.IO.Path.GetDirectoryName(items.OfType<StorageFile>().FirstOrDefault()?.Path);
        if (folder is not null)
            await OpenFolderAsync(folder);
    }

    // ---------- Filtering, sorting, counts ----------

    private void ApplyFilter()
    {
        var filter = CurrentFilter;
        _visible.Clear();
        foreach (var item in _all.Where(p => filter.Matches(p.Tier)))
            _visible.Add(item);

        UpdateCounter();
        ApplyThumbnailSize();
        if (_visible.Count > 0)
            Gallery.SelectedIndex = 0;
    }

    private void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => ApplyFilter();

    private void SetFilter(TierFilter filter) =>
        FilterBar.SelectedItem = FilterBar.Items.FirstOrDefault(i => (string)i.Tag == filter.ToString());

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_all.Count == 0)
            return;

        var byId = _all.ToDictionary(p => p.Id);
        _all = _all.Select(p => p.Record with { Tier = p.Tier }).Sort(CurrentSort).Select(r => byId[r.Id]).ToList();
        ApplyFilter();
    }

    private void UpdateCounter()
    {
        PushRemoteState();
        if (_all.Count == 0)
        {
            CounterText.Text = "";
            return;
        }

        int likes = 0, supers = 0, rejected = 0;
        foreach (var p in _all)
        {
            switch (p.Tier)
            {
                case Tier.Like: likes++; break;
                case Tier.SuperLike: supers++; break;
                case Tier.Rejected: rejected++; break;
            }
        }

        var unrated = _all.Count - likes - supers - rejected;
        CounterText.Text = $"Отобрано {likes + supers} из {_all.Count}  ·  ★ {supers}  ·  ♥ {likes}  ·  ✕ {rejected}  ·  без оценки {unrated}";
    }

    // ---------- Gallery ----------

    private void Gallery_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        _galleryThumbnails.OnContainerContentChanging(args);
    }

    private void Gallery_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PhotoItem item)
            OpenLoupe(_visible.IndexOf(item));
    }

    private void Gallery_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!IsDown(VirtualKey.Control))
            return;

        var delta = e.GetCurrentPoint(Gallery).Properties.MouseWheelDelta;
        SizeSlider.Value = Math.Clamp(SizeSlider.Value + Math.Sign(delta) * 20, SizeSlider.Minimum, SizeSlider.Maximum);
        e.Handled = true;
    }

    private void SizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        ApplyThumbnailSize();

        // Also raised while the window is still being built, before settings exist.
        if (_settings is not null)
            _settings.ThumbnailSize = e.NewValue;
    }

    private void ApplyThumbnailSize()
    {
        if (Gallery?.ItemsPanelRoot is ItemsWrapGrid grid)
        {
            grid.ItemWidth = SizeSlider.Value;
            grid.ItemHeight = SizeSlider.Value;
        }
    }

    // ---------- Full-screen viewer ----------

    private void OpenLoupe(int index)
    {
        if (index < 0 || index >= _visible.Count)
            return;

        Loupe.Visibility = Visibility.Visible;
        ShowInLoupe(index);
    }

    private void CloseLoupe()
    {
        if (!InLoupe)
            return;

        Loupe.Visibility = Visibility.Collapsed;
        LoupeImage.Source = null;
        if (_loupeIndex >= 0 && _loupeIndex < _visible.Count)
        {
            Gallery.SelectedIndex = _loupeIndex;
            Gallery.ScrollIntoView(_visible[_loupeIndex]);
        }

        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        Gallery.Focus(FocusState.Programmatic);
        PushRemoteState();
    }

    private async void ShowInLoupe(int index)
    {
        if (index < 0 || index >= _visible.Count)
            return;

        _loupeIndex = index;
        var item = _visible[index];
        var request = ++_loupeRequest;

        ResetZoom();
        UpdateLoupeInfo();
        SyncFilmstrip(index);
        LoadDetails(item);

        var pending = _loupeCache.GetAsync(item);
        _loupeCache.Prefetch(_visible, index);

        // Usually the neighbour was prefetched and is ready. If not, show the thumbnail at once and swap in the sharp image.
        if (!pending.IsCompleted)
        {
            var placeholder = item.Thumbnail ?? await PhotoItem.LoadCachedThumbnailAsync(_thumbnails, item.Record);
            if (request == _loupeRequest && !pending.IsCompleted)
                LoupeImage.Source = placeholder;
        }

        var bitmap = await pending;
        if (request == _loupeRequest && bitmap is not null)
            LoupeImage.Source = bitmap;
    }

    private void UpdateLoupeInfo()
    {
        if (_loupeIndex < 0 || _loupeIndex >= _visible.Count)
            return;

        PushRemoteState();

        var item = _visible[_loupeIndex];
        LoupeName.Text = item.FileName;
        LoupeDetails.Text = DetailsLine(item);
        LoupePosition.Text = item.SeriesSize > 1
            ? $"серия {item.SeriesPosition}/{item.SeriesSize}  ·  {_loupeIndex + 1} / {_visible.Count}"
            : $"{_loupeIndex + 1} / {_visible.Count}";
        UpdateRatingButtons(item.Tier);
    }

    private void StepLoupe(int delta, bool unratedOnly = false)
    {
        var i = _loupeIndex + delta;
        while (unratedOnly && i >= 0 && i < _visible.Count && _visible[i].Tier != Tier.None)
            i += delta;

        if (i >= 0 && i < _visible.Count)
            ShowInLoupe(i);
        else if (unratedOnly)
            ShowToast("Дальше фото без оценки нет");
    }

    private void LoupeScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // At zoom 1 the canvas exactly fills the space between the bars and the photo sits centred in it.
        LoupeCanvas.Width = e.NewSize.Width;
        LoupeCanvas.Height = e.NewSize.Height;
        ResetZoom();
    }

    private void LoupeScroll_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _pointerInLoupe = e.GetPosition(LoupeScroll);
        _ = ToggleZoomAsync();
        e.Handled = true;
    }

    private void ResetZoom()
    {
        _zoomedIn = false;
        LoupeScroll.ChangeView(0, 0, 1f, disableAnimation: true);
    }

    /// <summary>Z: jump to 100% (one image pixel per screen pixel) around the mouse, to check focus and eyes; Z again to fit.</summary>
    private async Task ToggleZoomAsync()
    {
        if (_zoomedIn)
        {
            ResetZoom();
            if (_loupeIndex >= 0 && _loupeIndex < _visible.Count)
                LoupeImage.Source = await _loupeCache.GetAsync(_visible[_loupeIndex]) ?? LoupeImage.Source;
            return;
        }

        if (_loupeIndex < 0 || _loupeIndex >= _visible.Count)
            return;

        var request = _loupeRequest;
        var full = await LoupeImageCache.LoadFullResolutionAsync(_visible[_loupeIndex]);
        if (full is null || request != _loupeRequest || full.PixelWidth == 0 || full.PixelHeight == 0)
            return;

        var view = new Size(LoupeScroll.ActualWidth, LoupeScroll.ActualHeight);
        var fit = Math.Min(view.Width / full.PixelWidth, view.Height / full.PixelHeight);
        var scale = LoupeScroll.XamlRoot?.RasterizationScale ?? 1.0;
        var zoom = (float)Math.Clamp(1 / (fit * scale), 1, LoupeScroll.MaxZoomFactor);

        LoupeImage.Source = full;
        _zoomedIn = true;
        var p = _pointerInLoupe == default ? new Point(view.Width / 2, view.Height / 2) : _pointerInLoupe;
        LoupeScroll.ChangeView(p.X * (zoom - 1), p.Y * (zoom - 1), zoom, disableAnimation: true);
    }

    private void ToggleFullScreen()
    {
        var full = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        AppWindow.SetPresenter(full ? AppWindowPresenterKind.Default : AppWindowPresenterKind.FullScreen);
    }

    // ---------- Rating ----------

    private void Rate(Tier tier)
    {
        var targets = new List<PhotoItem>();
        if (!InLoupe)
            targets.AddRange(Gallery.SelectedItems.OfType<PhotoItem>());
        else if (_loupeIndex >= 0 && _loupeIndex < _visible.Count)
            targets.Add(_visible[_loupeIndex]);
        if (targets.Count == 0)
            return;

        _ratings.Rate(targets, tier);
        UpdateCounter();

        // Clearing a rating stays on the photo: it is a correction, not a decision.
        var advance = _settings.AutoAdvance && tier != Tier.None;
        if (InLoupe)
        {
            Flash(tier);
            UpdateLoupeInfo();
            if (advance && _loupeIndex < _visible.Count - 1)
                StepLoupe(+1);
        }
        else if (advance && targets.Count == 1 && Gallery.SelectedIndex < _visible.Count - 1)
        {
            Gallery.SelectedIndex++;
            Gallery.ScrollIntoView(Gallery.SelectedItem);
        }
    }

    private void UndoOrRedo(bool redo)
    {
        var touched = redo ? _ratings.Redo() : _ratings.Undo();
        if (touched.Count == 0)
            return;

        UpdateCounter();
        var index = _visible.IndexOf((PhotoItem)touched[0]);
        if (index < 0)
            return;

        if (InLoupe)
            ShowInLoupe(index);
        else
        {
            Gallery.SelectedIndex = index;
            Gallery.ScrollIntoView(_visible[index]);
        }
    }

    private void Flash(Tier tier)
    {
        FlashIcon.Glyph = tier == Tier.None ? "\uEB51" : PhotoItem.GlyphFor(tier);
        FlashIcon.Foreground = PhotoItem.BrushFor(tier) ?? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        FlashBadge.Opacity = 1;
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    // ---------- Keyboard ----------

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        if (InSwipe)
            e.Handled = HandleSwipeKey(e.Key, ctrl, shift);
        else if (InCompare)
            e.Handled = HandleCompareKey(e.Key, ctrl);
        else
            e.Handled = ctrl ? HandleCtrlKey(e.Key, shift) : HandleKey(e.Key, shift);
    }

    private bool HandleCtrlKey(VirtualKey key, bool shift)
    {
        switch (key)
        {
            case VirtualKey.O: _ = PickFolderAsync(); return true;
            case VirtualKey.C: _ = CopyPhotosAsync(CurrentPhotos(), pathsOnly: shift); return true;
            case VirtualKey.Z: UndoOrRedo(redo: shift); return true;
            case VirtualKey.Y: UndoOrRedo(redo: true); return true;
            case VirtualKey.Number0: SetFilter(TierFilter.All); return true;
            case VirtualKey.Number1: SetFilter(TierFilter.LikedOrBetter); return true;
            case VirtualKey.Number2: SetFilter(TierFilter.SuperLikes); return true;
            case VirtualKey.Number3: SetFilter(TierFilter.Unrated); return true;
            case VirtualKey.Number4: SetFilter(TierFilter.Rejected); return true;
            default: return false;
        }
    }

    private bool HandleKey(VirtualKey key, bool shift)
    {
        // Letter keys are matched by position, so they work the same on the Russian layout (F = А, R = К, X = Ч).
        switch (key)
        {
            case VirtualKey.Number1 or VirtualKey.NumberPad1 or VirtualKey.F: Rate(Tier.Like); return true;
            case VirtualKey.Number2 or VirtualKey.NumberPad2 or VirtualKey.R: Rate(Tier.SuperLike); return true;
            case VirtualKey.X: Rate(Tier.Rejected); return true;
            case VirtualKey.Number0 or VirtualKey.NumberPad0: Rate(Tier.None); return true;
            case (VirtualKey)192: ToggleAutoAdvance(); return true; // ` (tilde key)
            case (VirtualKey)191 when shift: ShowHelp(); return true; // ?
            case VirtualKey.F1: ShowHelp(); return true;
            case VirtualKey.F11: ToggleFullScreen(); return true;
            case VirtualKey.S: StartSwipe(); return true;
            case VirtualKey.C: StartCompare(); return true;
            case VirtualKey.Delete: _ = DeletePhotosAsync(CurrentPhotos()); return true;
        }

        if (InLoupe)
        {
            switch (key)
            {
                case VirtualKey.Left or VirtualKey.A: StepLoupe(-1); return true;
                case VirtualKey.Right or VirtualKey.D: StepLoupe(+1, unratedOnly: shift); return true;
                case VirtualKey.Home: ShowInLoupe(0); return true;
                case VirtualKey.End: ShowInLoupe(_visible.Count - 1); return true;
                case VirtualKey.Z: _ = ToggleZoomAsync(); return true;
                case VirtualKey.T: ToggleFilmstrip(); return true;
                case VirtualKey.Escape or VirtualKey.Enter: CloseLoupe(); return true;
                default: return false;
            }
        }

        if (key == VirtualKey.Enter && Gallery.SelectedIndex >= 0)
        {
            OpenLoupe(Gallery.SelectedIndex);
            return true;
        }

        return false;
    }

    private void ToggleAutoAdvance()
    {
        _settings.AutoAdvance = !_settings.AutoAdvance;
        ShowToast(_settings.AutoAdvance ? "Автопереход после оценки: включён" : "Автопереход после оценки: выключен");
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // ---------- Help and toasts ----------

    private void Help_Click(object sender, RoutedEventArgs e) => ShowHelp();

    private async void ShowHelp()
    {
        (string Keys, string Action)[] rows =
        [
            ("← →  или  A D", "Предыдущее / следующее фото"),
            ("Shift + →", "Следующее фото без оценки"),
            ("1  или  F", "Лайк"),
            ("2  или  R", "Суперлайк"),
            ("X", "Отказ"),
            ("0", "Снять оценку"),
            ("Ctrl+Z / Ctrl+Y", "Отменить / вернуть"),
            ("Ctrl + 0 / 1 / 2 / 3 / 4", "Все / Лайки / Суперлайки / Без оценки / Отклонённые"),
            ("Enter / Esc", "Открыть на весь экран / назад в галерею"),
            ("Z  или двойной клик", "Масштаб 100% и обратно"),
            ("Колесо мыши", "Следующее / предыдущее фото; над лентой листает ленту"),
            ("Ctrl + колесо, перетаскивание", "Зум и сдвиг увеличенного фото"),
            ("T", "Показать или скрыть ленту в просмотре"),
            ("Правый клик", "Копировать, показать в проводнике, открыть в другой программе"),
            ("Ctrl+C / Ctrl+Shift+C", "Копировать фото / путь к файлу"),
            ("Delete", "Удалить в корзину (вместе с RAW и .xmp)"),
            ("S", "Режим свайпа по фото без оценки: → лайк, ↑ суперлайк, ← отказ, ↓ потом, Backspace назад"),
            ("Shift + ← (в свайпе)", "Отклонить всю серию похожих кадров"),
            ("C", "Сравнить выделенные фото или всю серию текущего фото"),
            ("1–9 (в сравнении)", "Убрать фото из сравнения; F / R / X оценивают фото под мышью"),
            ("F11", "Полноэкранный режим"),
            ("`", "Автопереход после оценки вкл/выкл"),
            ("Ctrl+O", "Открыть папку"),
            ("Ctrl + колесо", "Размер миниатюр"),
            ("Кнопка «Телефон»", "Пульт: листать и оценивать с телефона по QR-коду"),
        ];

        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = rows[i].Keys, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Foreground = (Brush)Application.Current.Resources["SuperLikeBrush"] };
            var action = new TextBlock { Text = rows[i].Action, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keys);
            grid.Children.Add(action);
        }

        var dialog = new ContentDialog
        {
            Title = "Клавиши",
            Content = new ScrollViewer { Content = grid },
            CloseButtonText = "Закрыть",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Opacity = 1;
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
