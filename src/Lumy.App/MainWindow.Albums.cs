using Lumy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Lumy.App;

/// <summary>
/// Albums: picks for a carousel or a story, in the order they will be posted. An album only remembers which
/// photos are in it, so the files stay where they are and one photo can be in many albums.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The album shown in the gallery instead of a folder, if any.</summary>
    private Album? _album;

    // ---------- The Albums menu ----------

    private void AlbumsMenu_Opening(object sender, object e)
    {
        AlbumsMenu.Items.Clear();
        var selected = CurrentPhotos();
        var create = new MenuFlyoutItem
        {
            Text = selected.Count > 0 ? $"Новый альбом из выделенных ({selected.Count})" : "Новый альбом",
            Icon = new FontIcon { Glyph = "\uE710" },
        };
        create.Click += async (_, _) => await NewAlbumAsync(selected);
        AlbumsMenu.Items.Add(create);

        var albums = _database.Albums();
        if (albums.Count > 0)
            AlbumsMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var album in albums)
        {
            var item = new MenuFlyoutItem { Text = $"{album.Name}  ·  {album.Count}", Icon = new FontIcon { Glyph = "\uE8F1" } };
            if (album.Id == _album?.Id)
                item.Icon = new FontIcon { Glyph = "\uE73E" };
            item.Click += async (_, _) => await OpenAlbumAsync(album.Id);
            AlbumsMenu.Items.Add(item);
        }
    }

    /// <summary>"Добавить в альбом" in a photo's right-click menu.</summary>
    private MenuFlyoutSubItem AlbumMenu(List<PhotoItem> photos)
    {
        var menu = new MenuFlyoutSubItem { Text = "Добавить в альбом", Icon = new FontIcon { Glyph = "\uE8F1" } };
        foreach (var album in _database.Albums().Where(a => a.Id != _album?.Id))
        {
            var item = new MenuFlyoutItem { Text = $"{album.Name}  ·  {album.Count}" };
            item.Click += (_, _) => AddToAlbum(album, photos);
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = "Новый альбом…", Icon = new FontIcon { Glyph = "\uE710" } };
        create.Click += async (_, _) => await NewAlbumAsync(photos);
        menu.Items.Add(create);
        return menu;
    }

    // ---------- Making and filling albums ----------

    private async Task NewAlbumAsync(List<PhotoItem> photos)
    {
        var suggested = _folder is null ? "Новый альбом" : System.IO.Path.GetFileName(_folder.TrimEnd('\\', '/'));
        if (await AskTextAsync("Новый альбом", suggested, "Создать") is not { } name)
            return;

        CreateAlbum(name, photos);
    }

    private Album CreateAlbum(string name, List<PhotoItem> photos)
    {
        var album = _database.CreateAlbum(name);
        var added = _database.AddToAlbum(album.Id, photos.Select(p => p.Id));
        ShowToast(added == 0
            ? $"Альбом «{album.Name}» создан. Добавляйте фото правым кликом → «Добавить в альбом»"
            : $"Альбом «{album.Name}»: {PhotoCount(added)}. Открыть его можно в меню «Альбомы»");
        PushRemoteState();
        return album;
    }

    private void AddToAlbum(Album album, List<PhotoItem> photos)
    {
        if (photos.Count == 0)
            return;

        var added = _database.AddToAlbum(album.Id, photos.Select(p => p.Id));
        ShowToast(added == 0
            ? (photos.Count == 1 ? $"Это фото уже в альбоме «{album.Name}»" : $"Эти фото уже в альбоме «{album.Name}»")
            : $"В альбом «{album.Name}» добавлено: {PhotoCount(added)}");
        PushRemoteState();
    }

    private void RemoveFromAlbum(Album album, List<PhotoItem> photos)
    {
        if (photos.Count == 0)
            return;

        _database.RemoveFromAlbum(album.Id, photos.Select(p => p.Id));
        _album = album with { Count = Math.Max(0, album.Count - photos.Count) };
        RemoveFromGallery(photos);
        ShowToast(photos.Count == 1 ? "Фото убрано из альбома, файл на месте" : $"Убрано из альбома: {PhotoCount(photos.Count)}, файлы на месте");
    }

    private static string PhotoCount(int count) => count == 1 ? "1 фото" : $"{count} фото";

    // ---------- Showing an album ----------

    private async Task OpenAlbumAsync(long albumId)
    {
        if (_database.Albums().FirstOrDefault(a => a.Id == albumId) is not { } album)
        {
            ShowToast("Этого альбома больше нет");
            return;
        }

        _loading?.Cancel();
        LeaveScreens();
        SaveLastPhoto();

        // Photos moved or deleted outside Lumy are left out, but stay in the album in case they come back.
        var records = await Task.Run(() => _database.AlbumPhotos(albumId).Where(r => File.Exists(r.Path)).ToList());
        _album = album;
        ShowPhotos(records);
        if (CurrentFilter != TierFilter.All)
            SetFilter(TierFilter.All);
        EmptyState.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = false;
        if (records.Count == 0)
            ShowToast("Альбом пуст. Добавляйте фото правым кликом → «Добавить в альбом»");
        Gallery.Focus(FocusState.Programmatic);
    }

    private async Task CloseAlbumAsync()
    {
        if (_album is null)
            return;

        if (_folder is { } folder && Directory.Exists(folder))
        {
            await OpenFolderAsync(folder);
            return;
        }

        LeaveScreens();
        _album = null;
        ShowPhotos([]);
        FolderTitle.Text = "";
        EmptyState.Visibility = Visibility.Visible;
    }

    private void UpdateAlbumBar()
    {
        AlbumBar.Visibility = _album is null ? Visibility.Collapsed : Visibility.Visible;
        SortBox.IsEnabled = _album is null;
        if (_album is { } album)
        {
            AlbumTitle.Text = album.Name;
            AlbumHint.Text = $"{PhotoCount(_all.Count)}  ·  перетаскивайте фото, чтобы задать порядок карусели  ·  файлы остаются на своих местах";
            FolderTitle.Text = $"Альбом «{album.Name}»";
        }
        else if (_folder is not null)
        {
            FolderTitle.Text = _folder;
        }

        UpdateAlbumReorder();
    }

    /// <summary>Photos can be dragged into order in an album, when every photo is shown.</summary>
    private void UpdateAlbumReorder()
    {
        var reorder = _album is not null && CurrentFilter == TierFilter.All;
        Gallery.CanDragItems = reorder;
        Gallery.CanReorderItems = reorder;
        Gallery.AllowDrop = reorder;
    }

    private void Gallery_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_album is null || args.DropResult == DataPackageOperation.None)
            return;

        SaveAlbumOrder();
    }

    /// <summary>Makes the gallery's order the album's.</summary>
    private void SaveAlbumOrder()
    {
        if (_album is not { } album || CurrentFilter != TierFilter.All || _visible.SequenceEqual(_all))
            return;

        _all = _visible.ToList();
        _database.SetAlbumOrder(album.Id, _all.Select(p => p.Id).ToList());
        PublishRemotePhotos();
    }

    /// <summary>Moves one photo to <paramref name="position"/> in the open album; the phone's up and down arrows.</summary>
    private void MoveInAlbum(PhotoItem photo, int position)
    {
        if (_album is null)
            return;

        if (CurrentFilter != TierFilter.All)
            SetFilter(TierFilter.All);
        var from = _visible.IndexOf(photo);
        var to = Math.Clamp(position, 0, _visible.Count - 1);
        if (from < 0 || from == to)
            return;

        _visible.Move(from, to);
        SaveAlbumOrder();
        Gallery.SelectedItem = photo;
        Gallery.ScrollIntoView(photo);
    }

    // ---------- The album bar ----------

    private async void AlbumExport_Click(object sender, RoutedEventArgs e)
    {
        if (_all.Count == 0)
        {
            ShowToast("В альбоме пока нет фото");
            return;
        }

        await ExportAsync(_all.ToList(), album: true);
    }

    private async void AlbumRename_Click(object sender, RoutedEventArgs e)
    {
        if (_album is not { } album || await AskTextAsync("Переименовать альбом", album.Name, "Сохранить") is not { } name)
            return;

        _database.RenameAlbum(album.Id, name);
        _album = _database.Albums().FirstOrDefault(a => a.Id == album.Id) ?? album with { Name = name };
        UpdateAlbumBar();
        PushRemoteState();
    }

    private async void AlbumDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_album is not { } album)
            return;

        var dialog = new ContentDialog
        {
            Title = $"Удалить альбом «{album.Name}»?",
            Content = "Удалится только подборка. Сами фото останутся на месте, вместе с оценками и правками.",
            PrimaryButtonText = "Удалить альбом",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        _database.DeleteAlbum(album.Id);
        await CloseAlbumAsync();
        ShowToast($"Альбом «{album.Name}» удалён, фото на месте");
    }

    private async void AlbumClose_Click(object sender, RoutedEventArgs e) => await CloseAlbumAsync();

    private async Task<string?> AskTextAsync(string title, string text, string action)
    {
        var box = new TextBox { Text = text, MinWidth = 320 };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = action,
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                dialog.Hide();
                box.Tag = "ok";
            }
        };

        var result = await dialog.ShowAsync();
        var name = box.Text.Trim();
        return (result == ContentDialogResult.Primary || box.Tag is "ok") && name.Length > 0 ? name : null;
    }
}
