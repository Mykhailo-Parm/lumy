using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lumy.App;

/// <summary>
/// Remembers which photo each tile of a list shows, so a photo's thumbnail is released exactly once when its
/// tile scrolls away or is reused, even with two lists (gallery and filmstrip) showing the same photos.
/// </summary>
internal sealed class ThumbnailTracker(ThumbnailService thumbnails)
{
    private readonly Dictionary<DependencyObject, PhotoItem> _shown = [];

    public void OnContainerContentChanging(ContainerContentChangingEventArgs args)
    {
        if (_shown.Remove(args.ItemContainer, out var previous))
            previous.ReleaseThumbnail();

        if (!args.InRecycleQueue && args.Item is PhotoItem item)
        {
            _shown[args.ItemContainer] = item;
            item.AcquireThumbnail(thumbnails);
        }
    }

    /// <summary>Forgets every tile, for when the list is about to show a different folder.</summary>
    public void ReleaseAll()
    {
        foreach (var item in _shown.Values)
            item.ReleaseThumbnail();
        _shown.Clear();
    }
}
