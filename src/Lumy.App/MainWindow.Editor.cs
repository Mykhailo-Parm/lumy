using System.Runtime.InteropServices.WindowsRuntime;
using Lumy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;

namespace Lumy.App;

/// <summary>
/// The editor (E): a crop frame in Instagram's shapes and Lightroom-style sliders, previewed on a screen-sized copy
/// with the same code the export uses. Nothing is written into the photo; the edit is saved in the library and,
/// for RAW files, as Camera Raw settings in the .xmp sidecar.
/// </summary>
public sealed partial class MainWindow
{
    private const int EditorPreviewEdge = 2000;

    private static readonly (CropAspect Aspect, string Label)[] EditorAspectChoices =
    [
        (CropAspect.Original, "Оригинал"),
        (CropAspect.Portrait, "4:5 лента"),
        (CropAspect.Square, "1:1"),
        (CropAspect.Landscape, "1.91:1"),
        (CropAspect.Story, "9:16 сторис"),
        (CropAspect.Free, "Свободно"),
    ];

    private static readonly (string Name, string Label, double Min, double Max, double Step)[] EditorSliderChoices =
    [
        ("exposure", "Экспозиция", -3, 3, 0.05),
        ("contrast", "Контраст", -100, 100, 1),
        ("highlights", "Света", -100, 100, 1),
        ("shadows", "Тени", -100, 100, 1),
        ("temperature", "Температура", -100, 100, 1),
        ("tint", "Оттенок", -100, 100, 1),
        ("saturation", "Насыщенность", -100, 100, 1),
        ("vibrance", "Сочность", -100, 100, 1),
    ];

    private readonly Dictionary<string, (Slider Slider, TextBlock Value)> _editorSliders = [];
    private PhotoItem? _editorPhoto;
    private DecodedPhoto? _editorSource;
    private PhotoEdit _editorEdit = PhotoEdit.None;
    private PhotoEdit _editorStart = PhotoEdit.None;
    private WriteableBitmap? _editorBitmap;
    private bool _editorRendering;
    private bool _editorDirty;
    private bool _editorBefore;
    private bool _editorSyncing;
    private int _editorSession;
    private CropHandle _editorDrag;
    private Point _editorDragStart;
    private CropRect _editorDragRect = CropRect.Full;
    private PhotoEdit? _copiedEdit;

    private bool InEditor => EditorView.Visibility == Visibility.Visible;

    // ---------- Opening and closing ----------

    private void LoupeEdit_Click(object sender, RoutedEventArgs e) => EditCurrent();

    /// <summary>E: edits the open photo, or the one selected in the gallery.</summary>
    private void EditCurrent()
    {
        if (CurrentPhotos().FirstOrDefault() is { } photo)
            _ = OpenEditorAsync(photo);
        else
            ShowToast("Выберите фото, которое хотите отредактировать");
    }

    private async Task OpenEditorAsync(PhotoItem photo)
    {
        SetUpEditorControls();
        var session = ++_editorSession;
        _editorPhoto = photo;
        _editorSource = null;
        _editorStart = photo.Edit ?? PhotoEdit.None;
        _editorEdit = _editorStart;
        _editorBefore = false;
        EditorBeforeButton.IsChecked = false;
        EditorBeforeBadge.Visibility = Visibility.Collapsed;
        EditorPasteButton.Visibility = _copiedEdit is null ? Visibility.Collapsed : Visibility.Visible;
        EditorName.Text = photo.FileName;
        EditorView.Visibility = Visibility.Visible;
        EditorLoading.IsActive = true;
        EditorImage.Source = photo.Thumbnail ?? await PhotoItem.LoadCachedThumbnailAsync(_thumbnails, photo.Record);
        SyncEditorControls();
        PushRemoteState();

        var scale = Content.XamlRoot?.RasterizationScale ?? 1.0;
        var decoded = await Task.Run(() => PhotoDecoder.DecodeAsync(photo.Path, (int)Math.Min(EditorPreviewEdge * scale, 4096)));
        if (session != _editorSession || !InEditor)
            return;

        EditorLoading.IsActive = false;
        if (decoded is null)
        {
            ShowToast("Не удалось открыть фото для редактирования");
            CloseEditor();
            return;
        }

        _editorSource = decoded;
        _editorBitmap = new WriteableBitmap(decoded.Width, decoded.Height);

        // An old edit made for a different shape of photo, or none: start from the whole frame.
        if (_editorEdit.Aspect != CropAspect.Free && _editorEdit.Crop.IsFull)
            _editorEdit = _editorEdit with { Crop = CropMath.Fit(_editorEdit.Aspect, decoded.FullWidth, decoded.FullHeight) };
        SyncEditorControls();
        RenderEditor();
    }

    private void CloseEditor()
    {
        _editorSession++;
        EditorView.Visibility = Visibility.Collapsed;
        EditorImage.Source = null;
        _editorBitmap = null;
        _editorSource = null;
        _editorPhoto = null;
        _editorDrag = CropHandle.None;
        if (InLoupe)
            LoupeScroll.Focus(FocusState.Programmatic);
        else
            Gallery.Focus(FocusState.Programmatic);
        PushRemoteState();
    }

    /// <summary>Saves the edit to the library, and to the RAW's sidecar for Lightroom.</summary>
    private async Task SaveEditorAsync()
    {
        if (_editorPhoto is not { } photo)
            return;

        var edit = _editorEdit.Normalized();
        await SaveEditAsync(photo, edit, _editorSource?.Orientation);
        _editorStart = edit;
    }

    private async Task SaveEditAsync(PhotoItem photo, PhotoEdit edit, int? orientation = null)
    {
        photo.Edit = edit;
        _database.SetEdit(photo.Id, edit);
        if (photo.RawPath is { } raw && _settings.WriteXmpSidecars)
        {
            try
            {
                var turn = orientation ?? await PhotoDecoder.ReadOrientationAsync(photo.Path);
                await Task.Run(() => XmpSidecar.SetDevelop(raw, edit, turn));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
            {
                ShowToast($"Правки сохранены в Lumy, но не в {System.IO.Path.GetFileName(XmpSidecar.PathFor(raw))}: {e.Message}");
            }
        }
    }

    private async void EditorDone_Click(object sender, RoutedEventArgs e) => await FinishEditorAsync();

    private async Task FinishEditorAsync()
    {
        await SaveEditorAsync();
        var photo = _editorPhoto;
        CloseEditor();
        if (photo is not null)
            ShowToast(photo.Edit is null ? "Правки сняты" : $"Сохранено: {photo.EditSummary}");
    }

    private async void EditorCancel_Click(object sender, RoutedEventArgs e) => await CancelEditorAsync(ask: true);

    private async Task CancelEditorAsync(bool ask)
    {
        if (ask && _editorEdit.Normalized() != _editorStart.Normalized())
        {
            var dialog = new ContentDialog
            {
                Title = "Выйти без сохранения?",
                Content = "Изменения в кадре и цвете пропадут.",
                PrimaryButtonText = "Выйти",
                SecondaryButtonText = "Сохранить",
                CloseButtonText = "Остаться",
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = Content.XamlRoot,
            };
            switch (await dialog.ShowAsync())
            {
                case ContentDialogResult.Secondary:
                    await FinishEditorAsync();
                    return;
                case ContentDialogResult.None:
                    return;
            }
        }

        CloseEditor();
    }

    private async void EditorExport_Click(object sender, RoutedEventArgs e)
    {
        await SaveEditorAsync();
        if (_editorPhoto is { } photo)
            await ExportAsync([photo]);
    }

    private bool HandleEditorKey(VirtualKey key, bool ctrl)
    {
        switch (key)
        {
            case VirtualKey.Escape: _ = CancelEditorAsync(ask: true); return true;
            case VirtualKey.Enter: _ = FinishEditorAsync(); return true;
            case VirtualKey.E when ctrl: EditorExport_Click(this, new RoutedEventArgs()); return true;
            case (VirtualKey)220: SetEditorBefore(!_editorBefore); return true; // backslash, as in Lightroom
            case VirtualKey.F11: ToggleFullScreen(); return true;
            default: return false;
        }
    }

    // ---------- Controls ----------

    private void SetUpEditorControls()
    {
        if (_editorSliders.Count > 0)
            return;

        EditorAspects.ItemsSource = EditorAspectChoices.Select(a => a.Label).ToList();
        foreach (var (name, label, min, max, step) in EditorSliderChoices)
        {
            var value = new TextBlock { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], HorizontalAlignment = HorizontalAlignment.Right };
            var header = new Grid();
            header.Children.Add(new TextBlock { Text = label });
            header.Children.Add(value);
            var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, SmallChange = step, LargeChange = step * 10, Header = header };
            slider.ValueChanged += (_, e) =>
            {
                value.Text = FormatSlider(name, e.NewValue);
                if (_editorSyncing)
                    return;
                _editorEdit = _editorEdit.With(name, e.NewValue);
                RenderEditor();
                PushRemoteState();
            };
            slider.DoubleTapped += (_, _) => slider.Value = 0;
            _editorSliders[name] = (slider, value);
            EditorSliders.Children.Add(slider);
        }
    }

    private static string FormatSlider(string name, double value) =>
        name == "exposure"
            ? value.ToString("+0.00;-0.00;0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
            : value.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Puts the edit's values into the sliders and the shape buttons without feeding back.</summary>
    private void SyncEditorControls()
    {
        _editorSyncing = true;
        try
        {
            var e = _editorEdit;
            Set("exposure", e.Exposure);
            Set("contrast", e.Contrast);
            Set("highlights", e.Highlights);
            Set("shadows", e.Shadows);
            Set("temperature", e.Temperature);
            Set("tint", e.Tint);
            Set("saturation", e.Saturation);
            Set("vibrance", e.Vibrance);
            EditorAspects.SelectedIndex = Array.FindIndex(EditorAspectChoices, a => a.Aspect == e.Aspect);
        }
        finally
        {
            _editorSyncing = false;
        }

        UpdateCropOverlay();

        void Set(string name, double value)
        {
            if (_editorSliders.TryGetValue(name, out var control))
            {
                control.Slider.Value = value;
                control.Value.Text = FormatSlider(name, value);
            }
        }
    }

    private void EditorAspects_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_editorSyncing || EditorAspects.SelectedIndex < 0)
            return;
        SetEditorAspect(EditorAspectChoices[EditorAspects.SelectedIndex].Aspect);
    }

    private void SetEditorAspect(CropAspect aspect)
    {
        var (width, height) = EditorFullSize();
        _editorEdit = _editorEdit with { Aspect = aspect, Crop = CropMath.ChangeAspect(_editorEdit.Crop, aspect, width, height) };
        SyncEditorControls();
        PushRemoteState();
    }

    private void EditorReset_Click(object sender, RoutedEventArgs e) => ResetEditor();

    private void ResetEditor()
    {
        _editorEdit = PhotoEdit.None;
        SyncEditorControls();
        RenderEditor();
        PushRemoteState();
    }

    private void EditorBefore_Click(object sender, RoutedEventArgs e) => SetEditorBefore(EditorBeforeButton.IsChecked == true);

    private void SetEditorBefore(bool before)
    {
        _editorBefore = before;
        EditorBeforeButton.IsChecked = before;
        EditorBeforeBadge.Visibility = before ? Visibility.Visible : Visibility.Collapsed;
        RenderEditor();
    }

    private void EditorPaste_Click(object sender, RoutedEventArgs e)
    {
        if (_copiedEdit is not { } copied)
            return;

        _editorEdit = WithColour(_editorEdit, copied);
        SyncEditorControls();
        RenderEditor();
    }

    /// <summary>The sliders of <paramref name="source"/> on <paramref name="target"/>'s crop: a look carries over, a frame does not.</summary>
    private static PhotoEdit WithColour(PhotoEdit target, PhotoEdit source) => target with
    {
        Exposure = source.Exposure,
        Contrast = source.Contrast,
        Highlights = source.Highlights,
        Shadows = source.Shadows,
        Temperature = source.Temperature,
        Tint = source.Tint,
        Saturation = source.Saturation,
        Vibrance = source.Vibrance,
    };

    private (int Width, int Height) EditorFullSize() =>
        _editorSource is { } source ? (source.FullWidth, source.FullHeight) : (3, 2);

    // ---------- Preview ----------

    /// <summary>Re-renders the preview off the UI thread; changes that arrive meanwhile are folded into one more pass.</summary>
    private async void RenderEditor()
    {
        if (_editorSource is not { } source || _editorBitmap is not { } bitmap)
            return;

        if (_editorRendering)
        {
            _editorDirty = true;
            return;
        }

        _editorRendering = true;
        try
        {
            do
            {
                _editorDirty = false;
                var edit = _editorBefore ? PhotoEdit.None : _editorEdit;
                var session = _editorSession;
                var pixels = await Task.Run(() =>
                {
                    var copy = (byte[])source.Pixels.Clone();
                    ImageAdjust.Apply(copy, source.Width, source.Height, source.Width * 4, edit);
                    return copy;
                });
                if (session != _editorSession)
                    return;

                using (var stream = bitmap.PixelBuffer.AsStream())
                    await stream.WriteAsync(pixels);
                bitmap.Invalidate();
                if (EditorImage.Source != bitmap)
                    EditorImage.Source = bitmap;
            }
            while (_editorDirty);
        }
        catch (Exception e)
        {
            CrashLog.Write(e);
        }
        finally
        {
            _editorRendering = false;
        }
    }

    // ---------- Crop frame ----------

    private void EditorStage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCropOverlay();

    private void EditorImage_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCropOverlay();

    /// <summary>Where the photo is drawn inside the overlay canvas.</summary>
    private Rect EditorImageRect()
    {
        if (EditorImage.ActualWidth <= 0 || EditorImage.ActualHeight <= 0)
            return Rect.Empty;

        var origin = EditorImage.TransformToVisual(EditorOverlay).TransformPoint(new Point(0, 0));
        return new Rect(origin.X, origin.Y, EditorImage.ActualWidth, EditorImage.ActualHeight);
    }

    private void UpdateCropOverlay()
    {
        var image = EditorImageRect();
        var visible = !image.IsEmpty && _editorSource is not null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in EditorOverlay.Children)
            element.Visibility = visible;
        UpdateCropInfo();
        if (visible == Visibility.Collapsed)
            return;

        var c = _editorEdit.Crop;
        var frame = new Rect(image.X + c.X * image.Width, image.Y + c.Y * image.Height, c.Width * image.Width, c.Height * image.Height);
        EditorShadeOuter.Rect = image;
        EditorShadeInner.Rect = frame;
        Place(EditorFrame, frame.X, frame.Y, frame.Width, frame.Height);

        void Third(Microsoft.UI.Xaml.Shapes.Line line, double x1, double y1, double x2, double y2) =>
            (line.X1, line.Y1, line.X2, line.Y2) = (x1, y1, x2, y2);
        Third(EditorThird1, frame.X + frame.Width / 3, frame.Y, frame.X + frame.Width / 3, frame.Bottom);
        Third(EditorThird2, frame.X + frame.Width * 2 / 3, frame.Y, frame.X + frame.Width * 2 / 3, frame.Bottom);
        Third(EditorThird3, frame.X, frame.Y + frame.Height / 3, frame.Right, frame.Y + frame.Height / 3);
        Third(EditorThird4, frame.X, frame.Y + frame.Height * 2 / 3, frame.Right, frame.Y + frame.Height * 2 / 3);

        Corner(EditorHandleTL, frame.X, frame.Y);
        Corner(EditorHandleTR, frame.Right, frame.Y);
        Corner(EditorHandleBL, frame.X, frame.Bottom);
        Corner(EditorHandleBR, frame.Right, frame.Bottom);

        static void Place(FrameworkElement element, double x, double y, double width, double height)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            element.Width = Math.Max(0, width);
            element.Height = Math.Max(0, height);
        }

        static void Corner(FrameworkElement handle, double x, double y)
        {
            Canvas.SetLeft(handle, x - handle.Width / 2);
            Canvas.SetTop(handle, y - handle.Height / 2);
        }
    }

    private void UpdateCropInfo()
    {
        if (_editorSource is not { } source)
        {
            EditorCropInfo.Text = "";
            return;
        }

        var (_, _, width, height) = CropMath.ToPixels(_editorEdit.Crop, source.FullWidth, source.FullHeight);
        var (outWidth, outHeight) = ExportPlan.TargetSize(_settings.ExportSize, width, height);
        var warning = ExportPlan.InstagramWarning(width, height);
        EditorCropInfo.Text = $"Кадр {width} × {height}, экспорт {outWidth} × {outHeight}" + (warning is null ? "" : $". Фото {warning}");
    }

    private Point EditorPoint(PointerRoutedEventArgs e, Rect image)
    {
        var p = e.GetCurrentPoint(EditorOverlay).Position;
        return new Point((p.X - image.X) / image.Width, (p.Y - image.Y) / image.Height);
    }

    private void EditorStage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var image = EditorImageRect();
        if (image.IsEmpty || _editorSource is null || _editorBefore)
            return;

        var p = EditorPoint(e, image);
        var handle = CropMath.HitTest(_editorEdit.Crop, p.X, p.Y, 16 / image.Width, 16 / image.Height);
        if (handle == CropHandle.None)
            return;

        _editorDrag = handle;
        _editorDragStart = p;
        _editorDragRect = _editorEdit.Crop;
        EditorStage.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void EditorStage_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var image = EditorImageRect();
        if (image.IsEmpty)
            return;

        if (_editorDrag == CropHandle.None)
            return;

        var p = EditorPoint(e, image);
        var (width, height) = EditorFullSize();
        var crop = _editorDrag == CropHandle.Move
            ? CropMath.Move(_editorDragRect, p.X - _editorDragStart.X, p.Y - _editorDragStart.Y)
            : CropMath.Resize(_editorDragRect, _editorDrag, p.X, p.Y, _editorEdit.Aspect, width, height);
        _editorEdit = _editorEdit with { Crop = crop };
        UpdateCropOverlay();
        e.Handled = true;
    }

    private void EditorStage_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_editorDrag == CropHandle.None)
            return;

        _editorDrag = CropHandle.None;
        EditorStage.ReleasePointerCapture(e.Pointer);
        PushRemoteState();
    }

    private void EditorStage_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _editorDrag = CropHandle.None;

    // ---------- Copying a look to other photos ----------

    private void CopyEdit(PhotoItem photo)
    {
        _copiedEdit = photo.Edit ?? PhotoEdit.None;
        ShowToast("Правки скопированы. Правый клик по другим фото → «Вставить цвет»");
    }

    /// <summary>Gives the photos the copied sliders; each keeps its own crop.</summary>
    private async Task PasteColourAsync(List<PhotoItem> photos)
    {
        if (_copiedEdit is not { } copied || photos.Count == 0)
            return;

        foreach (var photo in photos)
            await SaveEditAsync(photo, WithColour(photo.Edit ?? PhotoEdit.None, copied));
        ShowToast(photos.Count == 1 ? "Цвет вставлен" : $"Цвет вставлен в {photos.Count} фото");
    }

    private async Task ClearEditsAsync(List<PhotoItem> photos)
    {
        var edited = photos.Where(p => p.Edit is not null).ToList();
        foreach (var photo in edited)
            await SaveEditAsync(photo, PhotoEdit.None);
        ShowToast(edited.Count == 1 ? "Правки сброшены" : $"Правки сброшены у {edited.Count} фото");
    }
}
