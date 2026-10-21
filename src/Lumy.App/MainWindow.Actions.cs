using System.Diagnostics;
using Lumy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Win32;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Lumy.App;

/// <summary>
/// What a right click offers, as in Explorer: copy, copy path, show in folder, open in another program,
/// rate, compare, move to the Recycle Bin. The same actions have keys (Ctrl+C, Ctrl+Shift+C, Delete).
/// </summary>
public sealed partial class MainWindow
{
    private static readonly Lazy<string?> PhotoshopPath = new(FindPhotoshop);

    /// <summary>The photos an action applies to: the open photo in the viewer, otherwise the gallery selection.</summary>
    private List<PhotoItem> CurrentPhotos()
    {
        if (InLoupe)
            return _loupeIndex >= 0 && _loupeIndex < _visible.Count ? [_visible[_loupeIndex]] : [];
        return Gallery.SelectedItems.OfType<PhotoItem>().ToList();
    }

    // ---------- Where the menu opens ----------

    private void Gallery_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as PhotoItem
            ?? (e.OriginalSource is DependencyObject container ? Gallery.ItemFromContainer(container) as PhotoItem : null);
        if (item is null)
            return;

        // Like Explorer: right-clicking outside the selection selects just that photo.
        if (!Gallery.SelectedItems.Contains(item))
            Gallery.SelectedItem = item;

        ShowPhotoMenu(Gallery.SelectedItems.OfType<PhotoItem>().ToList(), Gallery, e);
    }

    private void LoupeScroll_ContextRequested(UIElement sender, ContextRequestedEventArgs e) =>
        ShowPhotoMenu(CurrentPhotos(), LoupeScroll, e);

    private void Filmstrip_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not PhotoItem item)
            return;

        var picked = FilmstripPicks();
        ShowPhotoMenu(picked.Contains(item) ? picked : [item], Filmstrip, e);
    }

    private void LoupeMore_Click(object sender, RoutedEventArgs e) =>
        BuildPhotoMenu(CurrentPhotos()).ShowAt((FrameworkElement)sender);

    private void ShowPhotoMenu(List<PhotoItem> photos, UIElement target, ContextRequestedEventArgs e)
    {
        if (photos.Count == 0)
            return;

        var menu = BuildPhotoMenu(photos);
        if (e.TryGetPosition(target, out var point))
            menu.ShowAt(target, point);
        else
            menu.ShowAt(e.OriginalSource as FrameworkElement ?? (FrameworkElement)target);
        e.Handled = true;
    }

    private MenuFlyout BuildPhotoMenu(List<PhotoItem> photos)
    {
        var menu = new MenuFlyout();
        if (photos.Count == 0)
            return menu;

        var one = photos.Count == 1;
        void Add(string text, string glyph, string? keys, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            if (keys is not null)
                item.KeyboardAcceleratorTextOverride = keys;
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (!InLoupe && one)
            Add("Открыть", "\uE8A7", "Enter", () => OpenLoupe(_visible.IndexOf(photos[0])));
        Add(one ? "Копировать" : $"Копировать ({photos.Count})", "\uE8C8", "Ctrl+C", () => _ = CopyPhotosAsync(photos, pathsOnly: false));
        Add(one ? "Копировать путь" : "Копировать пути", "\uE71B", "Ctrl+Shift+C", () => _ = CopyPhotosAsync(photos, pathsOnly: true));
        menu.Items.Add(new MenuFlyoutSeparator());

        Add("Показать в проводнике", "\uEC50", null, () => ShowInExplorer(photos[0]));
        if (one)
        {
            Add("Открыть в программе по умолчанию", "\uE8E5", null, () => OpenWithDefault(photos[0]));
            if (PhotoshopPath.Value is { } photoshop)
                Add(photos[0].RawPath is not null ? "Открыть RAW в Photoshop" : "Открыть в Photoshop", "\uE70F", null, () => Launch(photoshop, Quote(photos[0].RawPath ?? photos[0].Path)));
            Add("Открыть с помощью…", "\uE7AC", null, () => Launch("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + photos[0].Path));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Лайк", "\uEB52", "F", () => RatePhotos(photos, Tier.Like));
        Add("Суперлайк", "\uE735", "R", () => RatePhotos(photos, Tier.SuperLike));
        Add("Отказ", "\uE8E0", "X", () => RatePhotos(photos, Tier.Rejected));
        Add("Снять оценку", "\uE894", "0", () => RatePhotos(photos, Tier.None));
        if (photos.Count >= 2)
            Add($"Сравнить ({Math.Min(photos.Count, MaxCompare)})", "\uE8A9", "C", () => ComparePhotos(photos));

        menu.Items.Add(new MenuFlyoutSeparator());
        if (one)
            Add("Кроп и цвет", "\uE70F", "E", () => _ = OpenEditorAsync(photos[0]));
        if (one && photos[0].Edit is not null)
            Add("Скопировать правки", "\uE8C8", null, () => CopyEdit(photos[0]));
        if (_copiedEdit is not null)
            Add(one ? "Вставить цвет" : $"Вставить цвет ({photos.Count})", "\uE77F", null, () => _ = PasteColourAsync(photos));
        if (photos.Any(p => p.Edit is not null))
            Add(one ? "Сбросить правки" : "Сбросить правки у выделенных", "\uE7A7", null, () => _ = ClearEditsAsync(photos));
        Add(one ? "Экспорт…" : $"Экспорт ({photos.Count})…", "\uEDE1", "Ctrl+E", () => _ = ExportAsync(photos));
        menu.Items.Add(AlbumMenu(photos));
        if (_album is { } album)
            Add("Убрать из альбома", "\uE738", null, () => RemoveFromAlbum(album, photos));

        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Удалить в корзину", "\uE74D", "Delete", () => _ = DeletePhotosAsync(photos));
        return menu;
    }

    private void RatePhotos(List<PhotoItem> photos, Tier tier)
    {
        tier = Toggled(photos, tier);
        _ratings.Rate(photos, tier);
        UpdateCounter();
        if (InLoupe)
        {
            Flash(tier);
            UpdateLoupeInfo();
        }
    }

    // ---------- Clipboard ----------

    /// <summary>
    /// Ctrl+C: puts the files on the clipboard the way Explorer does, so they paste into a folder, Telegram or
    /// an email; a single photo also goes as a picture for Photoshop or a chat. Ctrl+Shift+C copies the paths.
    /// </summary>
    private async Task CopyPhotosAsync(List<PhotoItem> photos, bool pathsOnly)
    {
        if (photos.Count == 0)
            return;

        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            if (pathsOnly)
            {
                package.SetText(string.Join(Environment.NewLine, photos.Select(p => p.Path)));
            }
            else
            {
                var files = new List<IStorageItem>();
                foreach (var photo in photos)
                    files.Add(await StorageFile.GetFileFromPathAsync(photo.Path));
                package.SetStorageItems(files);
                if (files.Count == 1 && !PhotoFormats.IsRaw(photos[0].Path))
                    package.SetBitmap(RandomAccessStreamReference.CreateFromFile((StorageFile)files[0]));
            }

            Clipboard.SetContent(package);
            try
            {
                Clipboard.Flush(); // keep it on the clipboard after Lumy closes
            }
            catch (Exception)
            {
            }

            ShowToast(pathsOnly
                ? (photos.Count == 1 ? "Путь скопирован" : $"Пути скопированы: {photos.Count}")
                : (photos.Count == 1 ? "Фото скопировано" : $"Скопировано фото: {photos.Count}"));
        }
        catch (Exception e)
        {
            ShowToast($"Не удалось скопировать: {e.Message}");
        }
    }

    // ---------- Other programs ----------

    private void ShowInExplorer(PhotoItem photo) => Launch("explorer.exe", $"/select,{Quote(photo.Path)}");

    private void OpenWithDefault(PhotoItem photo)
    {
        try
        {
            Process.Start(new ProcessStartInfo(photo.Path) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            ShowToast($"Не удалось открыть: {e.Message}");
        }
    }

    private void Launch(string program, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            ShowToast($"Не удалось запустить {System.IO.Path.GetFileNameWithoutExtension(program)}: {e.Message}");
        }
    }

    private static string Quote(string path) => $"\"{path}\"";

    /// <summary>Photoshop registers itself under App Paths; null when it is not installed.</summary>
    private static string? FindPhotoshop()
    {
        try
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Photoshop.exe");
                if (key?.GetValue(null) is string path && File.Exists(path.Trim('"')))
                    return path.Trim('"');
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    // ---------- Recycle Bin ----------

    /// <summary>
    /// Delete: moves the photos to the Recycle Bin after asking, together with their RAW pair and .xmp sidecar,
    /// so nothing is left half-deleted. They can be restored from the Recycle Bin.
    /// </summary>
    /// <param name="confirmed">Already confirmed on the phone, so no dialog waits on the computer.</param>
    private async Task DeletePhotosAsync(List<PhotoItem> photos, bool confirmed = false)
    {
        if (photos.Count == 0)
            return;

        var withRaw = photos.Any(p => p.RawPath is { } raw && raw != p.Path);
        var dialog = new ContentDialog
        {
            Title = photos.Count == 1 ? "Удалить фото в корзину?" : $"Удалить {photos.Count} фото в корзину?",
            Content = (photos.Count == 1 ? photos[0].FileName : $"{photos.Count} файлов")
                + (withRaw ? " вместе с RAW-файлами и .xmp." : ".")
                + " Их можно будет вернуть из корзины Windows.",
            PrimaryButtonText = "В корзину",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (!confirmed && await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var deleted = await Task.Run(() => photos.Where(MoveToRecycleBin).ToList());
        RemoveFromGallery(deleted);
        ShowToast(deleted.Count == photos.Count
            ? (deleted.Count == 1 ? "Фото в корзине" : $"В корзине: {deleted.Count} фото")
            : $"Удалено {deleted.Count} из {photos.Count}: остальные заняты другой программой или защищены");
    }

    /// <summary>Takes photos out of the gallery and the viewer, which then shows the next one.</summary>
    private void RemoveFromGallery(IReadOnlyCollection<PhotoItem> photos)
    {
        var removed = photos.ToHashSet();
        var keepIndex = _loupeIndex;
        _all = _all.Where(p => !removed.Contains(p)).ToList();
        foreach (var photo in removed)
            _visible.Remove(photo);
        UpdateCounter();
        UpdateAlbumBar();
        PublishRemotePhotos();

        if (InLoupe)
        {
            if (_visible.Count == 0)
                CloseLoupe();
            else
                ShowInLoupe(Math.Min(keepIndex, _visible.Count - 1));
        }

        EmptyState.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool MoveToRecycleBin(PhotoItem photo)
    {
        try
        {
            var files = new List<string> { photo.Path };
            if (photo.RawPath is { } raw && raw != photo.Path)
                files.Add(raw);
            if (photo.RawPath is { } rawFile)
                files.Add(XmpSidecar.PathFor(rawFile));

            foreach (var file in files.Where(File.Exists))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    file, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
