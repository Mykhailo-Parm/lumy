using System.Runtime.InteropServices;
using Lumy.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Lumy.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _seriesWork;

    /// <summary>
    /// Groups the open folder into series. Capture times give a first grouping at once; thumbnail hashes,
    /// computed in the background and saved for next time, then split bursts that changed scene.
    /// </summary>
    private async Task DetectSeriesAsync(IReadOnlyList<PhotoItem> items)
    {
        _seriesWork?.Cancel();
        var cancellation = _seriesWork = new CancellationTokenSource();
        AssignSeries(items);

        var missing = items.Where(i => i.Hash is null).ToList();
        if (missing.Count == 0)
            return;

        try
        {
            var hashes = await Task.Run(() => HashAllAsync(missing, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;

            foreach (var (item, hash) in hashes)
                item.Hash = hash;
            await Task.Run(() => _database.SetHashes(hashes.Select(h => (h.Item.Id, h.Hash))));
            AssignSeries(items);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void AssignSeries(IReadOnlyList<PhotoItem> items)
    {
        var byId = items.ToDictionary(i => i.Id);
        var series = SeriesDetector.Group(items.Select(i => new SeriesInput(i.Id, i.Record.CapturedAt, i.Hash)));
        var next = 1;
        foreach (var group in series)
        {
            var id = group.Count > 1 ? next++ : 0;
            for (var i = 0; i < group.Count; i++)
                byId[group[i]].SetSeries(id, i + 1, group.Count);
        }
    }

    private static async Task<List<(PhotoItem Item, ulong Hash)>> HashAllAsync(IReadOnlyList<PhotoItem> items, CancellationToken cancellationToken)
    {
        var results = new List<(PhotoItem, ulong)>();
        using var gate = new SemaphoreSlim(4);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (await HashAsync(item.Path) is { } hash)
                {
                    lock (results)
                        results.Add((item, hash));
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>Shrinks the Windows thumbnail to 9×8 pixels and hashes it.</summary>
    private static async Task<ulong?> HashAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 96, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null)
                return null;

            var decoder = await BitmapDecoder.CreateAsync(thumbnail);
            var transform = new BitmapTransform
            {
                ScaledWidth = PerceptualHash.Width,
                ScaledHeight = PerceptualHash.Height,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            return PerceptualHash.FromBgra(pixels.DetachPixelData());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or ArgumentException)
        {
            return null;
        }
    }
}
