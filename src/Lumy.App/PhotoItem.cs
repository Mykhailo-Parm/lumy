using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lumy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Lumy.App;

/// <summary>A photo in the gallery: its library record, its current tier, and a thumbnail loaded while it is on screen.</summary>
public sealed class PhotoItem(PhotoRecord record) : IRatedPhoto, INotifyPropertyChanged
{
    private Tier _tier = record.Tier;
    private ImageSource? _thumbnail;
    private int _thumbnailRequest;

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
    /// Loads the thumbnail from the Windows thumbnail cache, which already holds the embedded preview
    /// for JPEG and RAW files, so the gallery never decodes full images.
    /// </summary>
    public async Task LoadThumbnailAsync(uint size)
    {
        if (_thumbnail is not null)
            return;

        var request = ++_thumbnailRequest;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || request != _thumbnailRequest)
                return;

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            if (request == _thumbnailRequest)
                Thumbnail = bitmap;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            // The file was moved or cannot be read; the tile keeps its placeholder.
        }
    }

    /// <summary>Drops the thumbnail when its tile scrolls away, so a 3000-photo folder does not hold 3000 bitmaps.</summary>
    public void ReleaseThumbnail()
    {
        _thumbnailRequest++;
        Thumbnail = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
