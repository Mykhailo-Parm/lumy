using System.Globalization;
using Lumy.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Lumy.App;

/// <summary>
/// Small things a gallery should remember between runs: recent folders under the arrow of "Открыть папку",
/// the window's size and place, the sort order, and the photo that was selected, plus counts on the filters.
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<string, string> _filterTitles = [];
    private RectInt32? _restoredBounds;
    private bool _restoringSort;

    // ---------- Recent folders ----------

    private async void OpenFolderButton_Click(SplitButton sender, SplitButtonClickEventArgs args) => await PickFolderAsync();

    private void RefreshRecentFolders()
    {
        RecentFoldersMenu.Items.Clear();
        var folders = _settings.RecentFolders.Where(Directory.Exists).ToList();
        if (folders.Count == 0)
        {
            RecentFoldersMenu.Items.Add(new MenuFlyoutItem { Text = "Недавних папок пока нет", IsEnabled = false });
            return;
        }

        foreach (var folder in folders)
        {
            var name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
            var item = new MenuFlyoutItem
            {
                Text = string.IsNullOrEmpty(name) ? folder : name,
                Icon = new FontIcon { Glyph = "\uE8B7" },
            };
            ToolTipService.SetToolTip(item, folder);
            item.Click += (_, _) => _ = OpenFolderAsync(folder);
            RecentFoldersMenu.Items.Add(item);
        }

        RecentFoldersMenu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "Очистить список", Icon = new FontIcon { Glyph = "\uE74D" } };
        clear.Click += (_, _) =>
        {
            _settings.ClearRecentFolders();
            RefreshRecentFolders();
        };
        RecentFoldersMenu.Items.Add(clear);
    }

    // ---------- Filter counts ----------

    /// <summary>"Лайки 12": the filters show how many photos each one holds; null clears the numbers.</summary>
    private void UpdateFilterCounts(int? likes, int? supers, int? rejected, int? unrated)
    {
        foreach (var item in FilterBar.Items)
        {
            var tag = item.Tag as string ?? "";
            if (!_filterTitles.TryGetValue(tag, out var title))
                _filterTitles[tag] = title = item.Text;

            int? count = tag switch
            {
                nameof(TierFilter.All) => likes is null ? null : _all.Count,
                nameof(TierFilter.LikedOrBetter) => likes + supers,
                nameof(TierFilter.SuperLikes) => supers,
                nameof(TierFilter.Unrated) => unrated,
                nameof(TierFilter.Rejected) => rejected,
                _ => null,
            };
            item.Text = count is null ? title : $"{title} {count}";
        }
    }

    // ---------- Sort order ----------

    private void RestoreSort()
    {
        if (_settings.Sort is not { } sort)
            return;

        var index = SortBox.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => i.Tag as string == sort);
        if (index < 0)
            return;

        _restoringSort = true;
        SortBox.SelectedIndex = index;
        _restoringSort = false;
    }

    // ---------- Where you left off ----------

    private void SaveLastPhoto()
    {
        var photo = InLoupe && _loupeIndex >= 0 && _loupeIndex < _visible.Count
            ? _visible[_loupeIndex]
            : Gallery.SelectedItem as PhotoItem;
        if (photo is not null)
            _settings.LastPhoto = photo.Path;
    }

    /// <summary>Reopening the folder Lumy closed on selects the photo that was selected then.</summary>
    private void RestoreLastPhoto(string folder)
    {
        if (_settings.LastPhoto is not { } path
            || !string.Equals(System.IO.Path.GetDirectoryName(path), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return;

        var photo = _visible.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (photo is null)
            return;

        Gallery.SelectedItem = photo;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Gallery.ScrollIntoView(photo, ScrollIntoViewAlignment.Leading));
    }

    private void SaveSession()
    {
        try
        {
            SaveLastPhoto();
            if (_restoredBounds is { } bounds)
            {
                var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
                _settings.WindowPlacement = string.Join(',', bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized ? 1 : 0);
            }
        }
        catch (Exception e)
        {
            CrashLog.Write(e);
        }
    }

    // ---------- Window size and place ----------

    private void RestoreWindowPlacement()
    {
        var restored = false;
        var maximize = false;
        var parts = (_settings.WindowPlacement ?? "").Split(',');
        if (parts.Length == 5
            && parts.Take(4).Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null).ToList() is [int x, int y, int width, int height]
            && width >= 640 && height >= 480)
        {
            // Only if it still lands on a screen: a monitor may have been unplugged since.
            var bounds = new RectInt32(x, y, width, height);
            var area = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.None);
            if (area is not null)
            {
                AppWindow.MoveAndResize(bounds);
                _restoredBounds = bounds;
                restored = true;
                maximize = parts[4] == "1";
            }
        }

        if (!restored)
            AppWindow.Resize(new SizeInt32(1440, 920));
        if (maximize && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();

        AppWindow.Changed += (sender, args) =>
        {
            if ((args.DidPositionChange || args.DidSizeChange)
                && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
                _restoredBounds = new RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        };
        _restoredBounds ??= new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
    }
}
