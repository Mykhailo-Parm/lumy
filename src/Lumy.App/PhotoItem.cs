using System.ComponentModel;
using System.Runtime.CompilerServices;
using Lumy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Lumy.App;

/// <summary>A photo in the gallery: its library record, its current tier, and a thumbnail loaded while it is on screen.</summary>
public sealed class PhotoItem(PhotoRecord record) : IRatedPhoto, INotifyPropertyChanged
{
    private Tier _tier = record.Tier;
    private ImageSource? _thumbnail;
    private int _thumbnailRequest;
    private int _thumbnailUsers;
    private int _seriesId;
    private int _seriesSize = 1;
    private int _seriesPosition = 1;

    public event PropertyChangedEventHandler? PropertyChanged;

    public PhotoRecord Record { get; } = record;

    public long Id => Record.Id;

    public string Path => Record.Path;

    public string? RawPath => Record.RawPath;

    public string FileName => Record.FileName;

    /// <summary>"JPG + CR3" for a RAW+JPEG pair, otherwise the extension.</summary>
    public string FormatLabel
    {
        get
        {
            var main = System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
            return RawPath is { } raw && raw != Path ? $"{main} + {System.IO.Path.GetExtension(raw).TrimStart('.').ToUpperInvariant()}" : main;
        }
    }

    public Tier Tier
    {
        get => _tier;
        set
        {
            if (_tier == value)
                return;
            _tier = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TierGlyph));
            OnPropertyChanged(nameof(TierBrush));
            OnPropertyChanged(nameof(TierBadgeVisibility));
            OnPropertyChanged(nameof(ImageOpacity));
        }
    }

    public string TierGlyph => GlyphFor(Tier);

    public Brush? TierBrush => BrushFor(Tier);

    public Visibility TierBadgeVisibility => Tier == Tier.None ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Rejected photos stay visible but fade back so the keepers stand out.</summary>
    public double ImageOpacity => Tier == Tier.Rejected ? 0.35 : 1.0;

    /// <summary>Perceptual hash of the thumbnail; filled in the background after a folder opens.</summary>
    public ulong? Hash { get; set; } = record.Hash;

    /// <summary>Which burst of near-identical frames this photo belongs to; 0 when it stands alone.</summary>
    public int SeriesId => _seriesId;

    public int SeriesSize => _seriesSize;

    public int SeriesPosition => _seriesPosition;

    public string SeriesBadgeText => $"{_seriesPosition}/{_seriesSize}";

    public Visibility SeriesBadgeVisibility => _seriesSize > 1 ? Visibility.Visible : Visibility.Collapsed;

    public void SetSeries(int id, int position, int size)
    {
        if (_seriesId == id && _seriesPosition == position && _seriesSize == size)
            return;
        _seriesId = id;
        _seriesPosition = position;
        _seriesSize = size;
        OnPropertyChanged(nameof(SeriesId));
        OnPropertyChanged(nameof(SeriesBadgeText));
        OnPropertyChanged(nameof(SeriesBadgeVisibility));
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public static string GlyphFor(Tier tier) => tier switch
    {
        Tier.Like => "\uEB52",      // HeartFill
        Tier.SuperLike => "\uE735", // FavoriteStarFill
        Tier.Rejected => "\uE711",  // Cancel
        _ => "",
    };

    public static Brush? BrushFor(Tier tier) => tier switch
    {
        Tier.Like => (Brush)Application.Current.Resources["LikeBrush"],
        Tier.SuperLike => (Brush)Application.Current.Resources["SuperLikeBrush"],
        Tier.Rejected => (Brush)Application.Current.Resources["RejectBrush"],
        _ => null,
    };

    /// <summary>
    /// A list started showing this photo: loads its thumbnail if no other list already has. The gallery and the
    /// viewer's filmstrip share one bitmap, so each counts its uses and the last one out frees it.
    /// </summary>
    public async void AcquireThumbnail(ThumbnailService thumbnails)
    {
        if (++_thumbnailUsers > 1 || _thumbnail is not null)
            return;

        var request = ++_thumbnailRequest;
        var bitmap = await LoadCachedThumbnailAsync(thumbnails, Record);
        if (request == _thumbnailRequest && _thumbnailUsers > 0 && bitmap is not null)
            Thumbnail = bitmap;
    }

    /// <summary>A list stopped showing this photo; frees the bitmap once nothing shows it, so a 3000-photo folder never holds 3000 bitmaps.</summary>
    public void ReleaseThumbnail()
    {
        if (_thumbnailUsers == 0 || --_thumbnailUsers > 0)
            return;

        _thumbnailRequest++;
        Thumbnail = null;
    }

    /// <summary>The thumbnail as a bitmap of its own, for a placeholder while the full image decodes.</summary>
    public static async Task<BitmapImage?> LoadCachedThumbnailAsync(ThumbnailService thumbnails, PhotoRecord record)
    {
        try
        {
            var path = await thumbnails.GetAsync(record, background: false);
            if (path is null)
                return null;

            var bitmap = new BitmapImage();
            using var stream = File.OpenRead(path).AsRandomAccessStream();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception)
        {
            // A broken cache file or a photo that vanished keeps the placeholder.
            return null;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
