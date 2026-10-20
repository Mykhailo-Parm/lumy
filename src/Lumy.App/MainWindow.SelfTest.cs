using Lumy.Core;
using Lumy.Remote;
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

            // The scroll viewer applies a zoom on its next layout pass, so give it a moment before reading it.
            await ToggleZoomAsync();
            await Task.Delay(500);
            Check(LoupeScroll.ZoomFactor > 1.2f, $"Z zooms to 100% (zoom {LoupeScroll.ZoomFactor:0.00})");
            await ToggleZoomAsync();
            await Task.Delay(500);
            Check(LoupeScroll.ZoomFactor <= 1.01f, $"Z again fits the photo (zoom {LoupeScroll.ZoomFactor:0.00})");

            Check(BuildPhotoMenu(CurrentPhotos()).Items.Count >= 10, "right-click menu has its actions");
            Check(_visible[_loupeIndex].IsCurrent && _visible.Count(p => p.IsCurrent) == 1, "filmstrip frames the open photo");

            if (_visible.Count >= 3)
            {
                var open = _visible[_loupeIndex];
                var first = Math.Min(_loupeIndex, _visible.Count - 3);
                Filmstrip.SelectRange(new ItemIndexRange(first, 3));
                await Task.Delay(300);
                Check(FilmstripPicks().Count == 3 && _visible[_loupeIndex] == open, "shift-picking in the filmstrip keeps the open photo");
                StartCompare();
                await Task.Delay(1500);
                Check(InCompare && _compareCells.Count == 3, "compare opens with the filmstrip picks");
                CloseCompare();
                if (!InLoupe)
                    OpenLoupe(_visible.IndexOf(open));
                await Task.Delay(500);
            }

            CloseLoupe();
            Check(!InLoupe, "viewer closes");
            Check(_all.All(p => !p.IsPicked && !p.IsCurrent), "closing the viewer clears the filmstrip marks");

            if (_visible.Count >= 3)
            {
                Gallery.SelectedItems.Clear();
                Gallery.SelectRange(new ItemIndexRange(0, 3));
                StartCompare();
                await Task.Delay(1500);
                Check(InCompare && _compareCells.Count >= 2, "compare opens with the selection");
                Check(_compareCells.All(c => c.IsCentred()), "compare photos are centred in their cells");
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

            if (_visible.Count >= 12)
            {
                StartTournament(new Tournament<PhotoItem>(_visible.ToList(), 5, p => p.SeriesId));
                var duels = 0;
                while (TournamentResult.Visibility != Visibility.Visible && duels < 2000)
                {
                    await Task.Delay(20);
                    if (_duelAnswer is not null)
                    {
                        PickDuel(left: duels % 3 != 0);
                        duels++;
                    }
                }

                Check(InTournament && _tournamentWinners.Count == 5, $"tournament finds 5 winners in {duels} duels");
                UndoDuel();
                await Task.Delay(500);
                Check(TournamentDuel.Visibility == Visibility.Visible && _duelAnswer is not null, "undo goes back to the last duel");
                PickDuel(left: true);
                await Task.Delay(500);
                Check(TournamentResult.Visibility == Visibility.Visible && _tournamentWinners.Count == 5, "tournament finishes again after undo");
                await CloseTournamentAsync(ask: false);
                Check(!InTournament, "tournament closes");
            }

            await RunRemoteChecksAsync(Check);
            await RunEditorChecksAsync(Check);
            await RunAlbumChecksAsync(Check);
            await RunOverlayChecksAsync(Check);

            Check(_settings.RecentFolders.FirstOrDefault() == folder, "folder is first in recent folders");
            Check(FilterBar.Items[0].Text.EndsWith(_all.Count.ToString(), StringComparison.Ordinal), $"filters show counts ({FilterBar.Items[0].Text})");

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

    /// <summary>Drives Lumy the way the phone does, through the same commands, and reads back what the phone would show.</summary>
    private async Task RunRemoteChecksAsync(Action<bool, string> check)
    {
        void Send(RemoteCommand command, string? arg = null) => HandleRemoteCommand(new RemoteRequest(command, arg));
        static string Text(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Send(RemoteCommand.Filter, nameof(TierFilter.Unrated));
        await Task.Delay(300);
        check(CurrentFilter == TierFilter.Unrated && CurrentRemoteState().Filter == "Unrated", "phone: filter switches");
        Send(RemoteCommand.Filter, nameof(TierFilter.All));
        await Task.Delay(300);

        Send(RemoteCommand.Goto, Text(_visible[4].Id));
        Send(RemoteCommand.Open);
        await Task.Delay(800);
        check(InLoupe && _loupeIndex == 4 && CurrentRemoteState() is { Mode: "loupe" } loupe && loupe.PhotoId == _visible[4].Id, "phone: grid tap opens that photo");
        Send(RemoteCommand.Back);

        Send(RemoteCommand.Compare, string.Join(',', _visible.Take(3).Select(p => Text(p.Id))));
        await Task.Delay(1000);
        check(InCompare && CurrentRemoteState() is { Mode: "compare", Photos.Count: 3 }, "phone: compares the photos it picked");
        Send(RemoteCommand.Pick, "1");
        Send(RemoteCommand.SuperLike);
        check(_compareCells.Count == 3 && _compareCells[1].Photo.Tier == Tier.SuperLike && CurrentRemoteState().Focus == 1, "phone: rates the photo it points at in compare");
        Send(RemoteCommand.Undo);
        Send(RemoteCommand.Back);
        await Task.Delay(300);

        Send(RemoteCommand.Tournament, "shown:3");
        await Task.Delay(500);
        var duels = 0;
        var sawDuel = false;
        while (InTournament && TournamentResult.Visibility != Visibility.Visible && duels < 2000)
        {
            if (_duelAnswer is not null)
            {
                sawDuel |= CurrentRemoteState() is { Mode: "tournament", Photos.Count: 2 };
                Send(RemoteCommand.Pick, Text(duels % 2));
                duels++;
            }

            await Task.Delay(20);
        }

        check(sawDuel, "phone: sees both photos of a duel");
        check(CurrentRemoteState() is { Mode: "tournament-done", Photos.Count: 3 }, $"phone: runs a tournament to its top 3 ({duels} duels)");
        Send(RemoteCommand.Back);
        await Task.Delay(300);
        check(!InTournament, "phone: leaves the tournament");

        var before = _settings.AutoAdvance;
        Send(RemoteCommand.AutoAdvance);
        check(CurrentRemoteState().AutoAdvance != before, "phone: toggles auto-advance");
        Send(RemoteCommand.AutoAdvance);
    }

    private void SendRemote(RemoteCommand command, string? arg = null) => HandleRemoteCommand(new RemoteRequest(command, arg));

    private static string NewTestFolder(string name) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lumy-self-test-{name}-{Guid.NewGuid():N}");

    private double EditorCropRatio()
    {
        if (_editorSource is not { } source)
            return 0;
        var (_, _, width, height) = CropMath.ToPixels(_editorEdit.Crop, source.FullWidth, source.FullHeight);
        return (double)width / height;
    }

    /// <summary>The editor and the export: a 4:5 crop with brighter exposure, saved, and written as a JPEG of that shape.</summary>
    private async Task RunEditorChecksAsync(Action<bool, string> check)
    {
        SetFilter(TierFilter.All);
        await Task.Delay(300);
        Gallery.SelectedItems.Clear();
        Gallery.SelectedIndex = 0;
        var photo = _visible[0];
        EditCurrent();
        await Task.Delay(2500);
        check(InEditor && _editorSource is not null && EditorImage.Source is Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap, "editor opens with a preview");

        SetEditorAspect(CropAspect.Portrait);
        SetEditorSlider("exposure", 0.5);
        await Task.Delay(800);
        check(Math.Abs(EditorCropRatio() - 0.8) < 0.01, $"4:5 crop is 4:5 in pixels ({EditorCropRatio():0.000})");
        check(EditorFrame.Visibility == Visibility.Visible && EditorFrame.ActualWidth > 0, "crop frame is drawn over the photo");
        NudgeEditorCrop(0, 0, 0.6);
        check(Math.Abs(EditorCropRatio() - 0.8) < 0.01, "scaling the frame keeps 4:5");
        await FinishEditorAsync();
        check(!InEditor && photo.Edit is { Aspect: CropAspect.Portrait, Exposure: 0.5 }, "editor saves the edit");
        check(_database.GetEdits([photo.Id]).TryGetValue(photo.Id, out var stored) && stored.Aspect == CropAspect.Portrait, "edit is stored in the library");
        check(BuildPhotoMenu([photo]).Items.OfType<Microsoft.UI.Xaml.Controls.MenuFlyoutItem>().Any(i => i.Text == "Скопировать правки"), "right-click menu offers copying the edit");

        var folder = NewTestFolder("export");
        var written = await RunExportAsync([photo], new ExportOptions(folder, ExportSize.Instagram, 90, Originals: false, Numbered: false, OpenFolder: false));
        var file = Directory.Exists(folder) ? Directory.GetFiles(folder).FirstOrDefault() : null;
        var exported = file is null ? null : await PhotoDecoder.DecodeAsync(file, 0);
        check(written == 1 && exported is not null, "export writes a JPEG");
        if (exported is not null)
            check(Math.Abs((double)exported.Width / exported.Height - 0.8) < 0.01 && exported.Width <= ExportPlan.InstagramWidth,
                $"exported JPEG is 4:5 and at most 1080 wide ({exported.Width} × {exported.Height})");

        var again = await RunExportAsync([photo], new ExportOptions(folder, ExportSize.Instagram, 90, Originals: false, Numbered: false, OpenFolder: false));
        check(again == 1 && Directory.GetFiles(folder).Length == 2, "a second export never overwrites the first");

        // The same from the phone.
        SendRemote(RemoteCommand.Edit, photo.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Task.Delay(2500);
        check(CurrentRemoteState() is { Mode: "editor", Editor.Aspect: "Portrait" }, "phone: opens the editor and sees the crop shape");
        SendRemote(RemoteCommand.EditSet, "contrast:30");
        check(_editorEdit.Contrast == 30 && CurrentRemoteState().Editor?.Values["contrast"] == 30, "phone: moves a slider");
        SendRemote(RemoteCommand.EditAspect, "Square");
        check(Math.Abs(EditorCropRatio() - 1) < 0.01, "phone: switches to 1:1");
        var width = _editorEdit.Crop.Width;
        SendRemote(RemoteCommand.EditCrop, "0:0:0.5");
        SendRemote(RemoteCommand.EditCrop, "0.1:0:1");
        check(Math.Abs(_editorEdit.Crop.Width - width / 2) < 0.01 && Math.Abs(EditorCropRatio() - 1) < 0.02, "phone: scales and moves the frame");
        SendRemote(RemoteCommand.EditCancel);
        await Task.Delay(500);
        check(!InEditor && photo.Edit is { Contrast: 0, Aspect: CropAspect.Portrait }, "phone: cancel leaves the saved edit as it was");

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Albums: made from a selection, shown in their own order, reordered, exported as linked originals.</summary>
    private async Task RunAlbumChecksAsync(Action<bool, string> check)
    {
        var folderCount = _all.Count;
        var picks = _visible.Take(3).ToList();
        var album = CreateAlbum("Self-test", picks);
        check(_database.Albums().Any(a => a.Id == album.Id && a.Count == 3), "album is made from three photos");
        check(CurrentRemoteState().Albums.Any(a => a.Id == album.Id), "phone: sees the album");

        await OpenAlbumAsync(album.Id);
        await Task.Delay(1500);
        check(_album?.Id == album.Id && _all.Count == 3 && AlbumBar.Visibility == Visibility.Visible, "album opens in the gallery");
        check(Gallery.CanReorderItems && !SortBox.IsEnabled, "album photos can be dragged into order");

        MoveInAlbum(_visible[0], 2);
        check(_database.AlbumPhotos(album.Id).Select(r => r.Id).SequenceEqual(new[] { picks[1].Id, picks[2].Id, picks[0].Id }), "new order is saved");
        SendRemote(RemoteCommand.AlbumMove, $"{picks[0].Id}:0");
        check(_database.AlbumPhotos(album.Id)[0].Id == picks[0].Id, "phone: moves a photo in the album");

        var folder = NewTestFolder("album");
        var written = await RunExportAsync(_all.ToList(), new ExportOptions(folder, ExportSize.Full, 90, Originals: true, Numbered: true, OpenFolder: false));
        var files = Directory.Exists(folder) ? Directory.GetFiles(folder).Select(f => System.IO.Path.GetFileName(f)).Order().ToList() : new List<string>();
        check(written == 3 && files.Count == 3 && files[0].StartsWith("01_", StringComparison.Ordinal), $"album exports its originals in order ({string.Join(", ", files)})");

        SendRemote(RemoteCommand.AlbumRemove, picks[1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        check(_all.Count == 2 && _database.AlbumPhotos(album.Id).Count == 2 && File.Exists(picks[1].Path), "phone: takes a photo out of the album, the file stays");

        SendRemote(RemoteCommand.AlbumClose);
        await Task.Delay(3000);
        check(_album is null && _all.Count == folderCount && AlbumBar.Visibility == Visibility.Collapsed, "closing the album goes back to the folder");

        SendRemote(RemoteCommand.AlbumOpen, album.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Task.Delay(1500);
        check(CurrentRemoteState().Album is { Count: 2 }, "phone: opens the album");
        SendRemote(RemoteCommand.AlbumClose);
        await Task.Delay(3000);

        _database.DeleteAlbum(album.Id);
        check(_album is null && !_database.Albums().Any(a => a.Id == album.Id) && File.Exists(picks[0].Path), "album is deleted, photos stay");
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Overlay: two photos on top of each other, wiped and faded, from the keyboard and the phone.</summary>
    private async Task RunOverlayChecksAsync(Action<bool, string> check)
    {
        ComparePhotos(_visible.Take(3).ToList());
        await Task.Delay(1500);
        ToggleOverlay();
        await Task.Delay(1500);
        check(InOverlay && OverlayTop.Source is not null && OverlayBottom.Source is not null && CompareGrid.Visibility == Visibility.Collapsed, "overlay puts two photos on top of each other");

        SetOverlayFade(false);
        SetOverlayValue(0.3);
        await Task.Delay(300);
        check(OverlayTop.Clip is not null && OverlaySplit.Visibility == Visibility.Visible, "wipe clips the top photo");
        SetOverlayFade(true);
        await Task.Delay(300);
        check(OverlayTop.Clip is null && Math.Abs(OverlayTop.Opacity - 0.3) < 0.01, "fade makes the top photo see-through");

        var right = _overlayRight;
        right?.Point();
        RateHovered(Tier.Like);
        check(right?.Photo.Tier == Tier.Like, "rating in the overlay rates the pointed photo");
        UndoOrRedo(redo: false);

        SendRemote(RemoteCommand.OverlayMode, "wipe");
        SendRemote(RemoteCommand.OverlayValue, "0.7");
        check(CurrentRemoteState() is { Overlay: "wipe" } state && Math.Abs(state.OverlayValue - 0.7) < 0.01, "phone: switches to the wipe and moves it");
        var left = _overlayLeft;
        SendRemote(RemoteCommand.OverlaySwap);
        check(_overlayRight == left && _overlayLeft == right, "phone: swaps the photos");
        SendRemote(RemoteCommand.Back);
        await Task.Delay(500);
        check(InCompare && !InOverlay && CompareGrid.Visibility == Visibility.Visible, "phone: back leaves the overlay for the grid");
        SendRemote(RemoteCommand.Overlay, "0,2");
        check(InOverlay && _overlayLeft == _compareCells[0] && _overlayRight == _compareCells[2], "phone: overlays the two photos it picked");
        SendRemote(RemoteCommand.Back);
        SendRemote(RemoteCommand.Back);
        await Task.Delay(500);
        check(!InCompare && !InOverlay, "phone: leaves the comparison");
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
