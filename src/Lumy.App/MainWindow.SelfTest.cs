using Lumy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace Lumy.App;

/// <summary>
/// "Lumy.exe --self-test D:\Photos": opens the folder, goes through every view the way a person would, writes
/// the outcome to %LOCALAPPDATA%\Lumy\self-test.txt and exits with 0 only if every check passed and nothing was
/// logged as a crash. CI runs it on Windows, where the app can actually start.
/// </summary>
public sealed partial class MainWindow
{
    private async Task RunSelfTestAsync(string folder)
    {
        var lines = new List<string>();
        var failures = 0;
        void Check(bool passed, string what)
        {
            lines.Add($"{(passed ? "ok  " : "FAIL")}  {what}");
            if (!passed)
                failures++;
        }

        try
        {
            await Task.Delay(500);
            await OpenFolderAsync(folder);
            Check(_all.Count > 0, $"folder opened: {_all.Count} photos");

            await Task.Delay(4000);
            Check(_visible.Any(p => p.Thumbnail is not null), "gallery thumbnails loaded");

            OpenLoupe(0);
            await Task.Delay(1500);
            Check(InLoupe && LoupeImage.Source is not null, "viewer shows the first photo");
            Check(IsCentred(), "photo is centred horizontally");

            for (var i = 0; i < 12; i++)
            {
                StepLoupe(+1);
                await Task.Delay(100);
            }

            Check(_loupeIndex == Math.Min(12, _visible.Count - 1), "arrow keys / wheel step through photos");
            Check(Filmstrip.SelectedIndex == _loupeIndex, "filmstrip follows the open photo");

            ToggleFilmstrip();
            await Task.Delay(300);
            Check(FilmstripPanel.Visibility == Visibility.Collapsed, "filmstrip hides");
            ToggleFilmstrip();
            await Task.Delay(300);
            Check(FilmstripPanel.Visibility == Visibility.Visible, "filmstrip shows again");

            var photo = _visible[_loupeIndex];
            ToggleLoupeTier(Tier.SuperLike);
            Check(photo.Tier == Tier.SuperLike, "superlike button rates");
            UndoOrRedo(redo: false);
            Check(photo.Tier == Tier.None, "undo clears it");
            ShowInLoupe(_visible.IndexOf(photo));
            await Task.Delay(800);

            await ToggleZoomAsync();
            await Task.Delay(500);
            await ToggleZoomAsync();
            Check(LoupeScroll.ZoomFactor <= 1.01f, "zoom to 100% and back");

            Check(BuildPhotoMenu(CurrentPhotos()).Items.Count >= 10, "right-click menu has its actions");
            CloseLoupe();
            Check(!InLoupe, "viewer closes");

            if (_visible.Count >= 3)
            {
                Gallery.SelectedItems.Clear();
                Gallery.SelectRange(new ItemIndexRange(0, 3));
                StartCompare();
                await Task.Delay(1500);
                Check(InCompare && _compareCells.Count >= 2, "compare opens with the selection");
                CloseCompare();
            }

            StartSwipe();
            await Task.Delay(800);
            Check(InSwipe, "swipe mode opens");
            Swipe(SwipeMove.Like);
            await Task.Delay(400);
            Swipe(SwipeMove.Reject);
            await Task.Delay(400);
            SwipeBack();
            await Task.Delay(400);
            CloseSwipe();
            Check(!InSwipe, "swipe mode closes");

            await Task.Delay(3000);
            Check(Directory.Exists(ThumbnailService.DefaultFolder)
                && Directory.EnumerateFiles(ThumbnailService.DefaultFolder, "*.jpg", SearchOption.AllDirectories).Any(), "thumbnail cache is written");
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }

        Check(CrashLog.Count == 0, $"errors logged while running: {CrashLog.Count}");
        var report = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(CrashLog.Path)!, "self-test.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(report)!);
        await File.WriteAllLinesAsync(report, lines);
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    /// <summary>The photo has the same gap on its left and right inside the viewer.</summary>
    private bool IsCentred()
    {
        if (LoupeImage.ActualWidth <= 0)
            return false;

        var origin = LoupeImage.TransformToVisual(LoupeScroll).TransformPoint(new Point(0, 0));
        var left = origin.X;
        var right = LoupeScroll.ActualWidth - (origin.X + LoupeImage.ActualWidth);
        return Math.Abs(left - right) <= 2;
    }
}
