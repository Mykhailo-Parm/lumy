using Lumy.Core;
using Windows.Graphics.Imaging;

namespace Lumy.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _seriesWork;

    /// <summary>
    /// Groups the open folder into series. Capture times give a first grouping at once. Then, in the background,
    /// every photo gets its cached thumbnail (so scrolling later is instant) and a hash of it, saved for next time,
    /// which splits bursts that changed scene.
    /// </summary>
    private async Task DetectSeriesAsync(IReadOnlyList<PhotoItem> items)
    {
        _seriesWork?.Cancel();
        var cancellation = _seriesWork = new CancellationTokenSource();
        AssignSeries(items);

        try
        {
            var work = items.Select(i => (i.Record, NeedsHash: i.Hash is null)).ToList();
            var hashes = await Task.Run(() => WarmUpAsync(work, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || hashes.Count == 0)
                return;

            var byId = items.ToDictionary(i => i.Id);
            foreach (var (id, hash) in hashes)
                byId[id].Hash = hash;
            await Task.Run(() => _database.SetHashes(hashes));
            AssignSeries(items);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            // Series are a convenience; failing to build them must not take the app down.
            CrashLog.Write(e);
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

    private async Task<List<(long Id, ulong Hash)>> WarmUpAsync(List<(PhotoRecord Record, bool NeedsHash)> work, CancellationToken cancellationToken)
    {
        var results = new List<(long, ulong)>();
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken }, async (entry, token) =>
        {
            var thumbnail = await _thumbnails.GetAsync(entry.Record, background: true, token);
            if (entry.NeedsHash && thumbnail is not null && await HashAsync(thumbnail) is { } hash)
            {
                lock (results)
                    results.Add((entry.Record.Id, hash));
            }
        });
        return results;
    }

    /// <summary>Shrinks the cached thumbnail to 9×8 pixels and hashes it.</summary>
    private static async Task<ulong?> HashAsync(string thumbnailPath)
    {
        try
        {
            using var stream = File.OpenRead(thumbnailPath).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform
            {
                ScaledWidth = PerceptualHash.Width,
                ScaledHeight = PerceptualHash.Height,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            return PerceptualHash.FromBgra(pixels.DetachPixelData());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
