using System.Globalization;
using Lumy.Core;
using Lumy.Remote;
using Microsoft.UI.Xaml;

namespace Lumy.App;

/// <summary>The phone remote for the editor, the export and albums: everything the mouse does there, by tap.</summary>
public sealed partial class MainWindow
{
    private RemoteEditor RemoteEditorState()
    {
        var e = _editorEdit;
        var crop = e.Crop;
        return new RemoteEditor(
            e.Aspect.ToString(),
            [crop.X, crop.Y, crop.Width, crop.Height],
            new Dictionary<string, double>
            {
                ["exposure"] = e.Exposure,
                ["contrast"] = e.Contrast,
                ["highlights"] = e.Highlights,
                ["shadows"] = e.Shadows,
                ["temperature"] = e.Temperature,
                ["tint"] = e.Tint,
                ["saturation"] = e.Saturation,
                ["vibrance"] = e.Vibrance,
            },
            _editorBefore,
            _copiedEdit is not null,
            EditorCropInfo.Text);
    }

    private void HandleRemoteEditor(RemoteRequest request)
    {
        switch (request.Command)
        {
            case RemoteCommand.EditSet when request.Arg?.Split(':') is [var name, var text]
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value):
                SetEditorSlider(name, value);
                break;
            case RemoteCommand.EditAspect when Enum.TryParse<CropAspect>(request.Arg, ignoreCase: true, out var aspect):
                SetEditorAspect(aspect);
                break;
            case RemoteCommand.EditCrop when request.Values() is [var dx, var dy, var scale]:
                NudgeEditorCrop(double.IsNaN(dx) ? 0 : dx, double.IsNaN(dy) ? 0 : dy, double.IsNaN(scale) ? 1 : scale);
                break;
            case RemoteCommand.EditReset: ResetEditor(); break;
            case RemoteCommand.EditBefore: SetEditorBefore(!_editorBefore); break;
            case RemoteCommand.EditPaste: EditorPaste_Click(this, new RoutedEventArgs()); break;
            case RemoteCommand.EditSave or RemoteCommand.Open: _ = FinishEditorAsync(); break;

            // The phone has already asked "leave without saving?".
            case RemoteCommand.EditCancel or RemoteCommand.Back: _ = CancelEditorAsync(ask: false); break;
            case RemoteCommand.Next: _ = StepEditorAsync(+1); break;
            case RemoteCommand.Previous: _ = StepEditorAsync(-1); break;
            case RemoteCommand.FullScreen: ToggleFullScreen(); break;
        }
    }

    private void SetEditorSlider(string name, double value)
    {
        _editorEdit = _editorEdit.With(name, value);
        SyncEditorControls();
        RenderEditor();
    }

    /// <summary>The phone's arrows and +/−: move the frame by a fraction of the photo, or scale it around its centre.</summary>
    private void NudgeEditorCrop(double dx, double dy, double scale)
    {
        if (_editorSource is null)
            return;

        var crop = _editorEdit.Crop;
        if (scale > 0 && Math.Abs(scale - 1) > 1e-6)
            crop = CropMath.Scale(crop, scale);
        crop = CropMath.Move(crop, dx, dy);
        _editorEdit = _editorEdit with { Crop = crop };
        UpdateCropOverlay();
    }

    /// <summary>Saves this photo's edit and opens the next one, to go through a carousel in one sitting.</summary>
    private async Task StepEditorAsync(int delta)
    {
        if (_editorPhoto is not { } photo)
            return;

        var index = _visible.IndexOf(photo) + delta;
        if (index < 0 || index >= _visible.Count)
        {
            ShowToast(delta > 0 ? "Это последнее фото" : "Это первое фото");
            return;
        }

        await SaveEditorAsync();
        var next = _visible[index];
        if (InLoupe)
        {
            _loupeIndex = index;
            ShowInLoupe(index);
        }
        else
        {
            Gallery.SelectedIndex = index;
        }

        await OpenEditorAsync(next);
    }

    /// <summary>The photos a phone command means: the comparison, the tournament winners, or the current photo or selection.</summary>
    private List<PhotoItem> RemoteTargets()
    {
        if (InEditor && _editorPhoto is { } edited)
            return [edited];
        if (InCompare)
            return _compareCells.Select(c => c.Photo).ToList();
        if (InTournament && TournamentResult.Visibility == Visibility.Visible)
            return _tournamentWinners.ToList();
        return CurrentPhotos();
    }

    /// <summary>"album", "likes", "supers", an id list, or nothing for what is on screen; with the last export settings.</summary>
    private async Task RemoteExportAsync(RemoteRequest request)
    {
        if (_exporting)
        {
            ShowToast("Экспорт уже идёт");
            return;
        }

        if (InEditor)
            await SaveEditorAsync();

        var ids = request.Numbers().ToHashSet();
        var album = request.Arg == "album" && _album is not null;
        var photos = request.Arg switch
        {
            "album" => album ? _all.ToList() : new List<PhotoItem>(),
            "likes" => _all.Where(p => p.Tier >= Tier.Like).ToList(),
            "supers" => _all.Where(p => p.Tier == Tier.SuperLike).ToList(),
            _ when ids.Count > 0 => _all.Where(p => ids.Contains(p.Id)).ToList(),
            _ => RemoteTargets(),
        };
        if (photos.Count == 0)
        {
            ShowToast(request.Arg switch
            {
                "album" => "Сначала откройте альбом",
                "likes" => "Лайков пока нет",
                "supers" => "Суперлайков пока нет",
                _ => "Нечего экспортировать",
            });
            return;
        }

        await RunExportAsync(photos, SavedExportOptions(album));
    }

    private async Task RemoteOpenAlbumAsync(RemoteRequest request)
    {
        if (request.Numbers() is not [var id])
            return;

        if (InTournament)
            await CloseTournamentAsync(ask: false);
        await OpenAlbumAsync(id);
    }

    private async Task RemoteCloseAlbumAsync()
    {
        if (InTournament)
            await CloseTournamentAsync(ask: false);
        await CloseAlbumAsync();
    }

    /// <summary>arg: the name, optionally followed by "|" and the photo ids picked on the phone.</summary>
    private void RemoteNewAlbum(RemoteRequest request)
    {
        var (name, photos) = SplitAlbumArg(request.Arg, '|');
        if (string.IsNullOrWhiteSpace(name))
            name = _folder is null ? "Новый альбом" : System.IO.Path.GetFileName(_folder.TrimEnd('\\', '/'));
        CreateAlbum(name.Trim(), photos ?? RemoteTargets());
    }

    /// <summary>arg: the album's id, optionally followed by ":" and the photo ids picked on the phone.</summary>
    private void RemoteAddToAlbum(RemoteRequest request)
    {
        var (head, photos) = SplitAlbumArg(request.Arg, ':');
        if (long.TryParse(head, System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            && _database.Albums().FirstOrDefault(a => a.Id == id) is { } album)
            AddToAlbum(album, photos ?? RemoteTargets());
        else
            ShowToast("Этого альбома больше нет");
    }

    /// <summary>"head", or "head" + separator + a comma-separated id list; the photos are null when no list is given.</summary>
    private (string Head, List<PhotoItem>? Photos) SplitAlbumArg(string? arg, char separator)
    {
        arg ??= "";
        var at = arg.LastIndexOf(separator);
        if (at < 0)
            return (arg, null);

        var ids = new RemoteRequest(RemoteCommand.Compare, arg[(at + 1)..]).Numbers().ToHashSet();
        if (ids.Count == 0)
            return (arg, null);

        return (arg[..at], _all.Where(p => ids.Contains(p.Id)).ToList());
    }
}
