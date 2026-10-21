using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Lumy.Core;
using Lumy.Remote;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Lumy.App;

/// <summary>
/// The phone remote: Lumy serves a small page on the home network, the phone opens it from a QR code and
/// sends the same actions as the keyboard. After every change Lumy tells the phone what is on screen.
/// </summary>
public sealed partial class MainWindow
{
    private const uint RemotePreviewSize = 1280;

    private RemoteServer? _remote;
    private bool _remotePushQueued;
    private int _remoteListVersion;
    private string? _remoteMessage;
    private int _remoteMessageId;

    /// <summary>Photo id → record, for the photos of the open folder. Read by the server off the UI thread, so it is swapped, never changed.</summary>
    private IReadOnlyDictionary<long, PhotoRecord> _remoteRecords = new Dictionary<long, PhotoRecord>();

    private TextBlock? _remoteDialogStatus;

    // ---------- Server ----------

    private async Task<bool> StartRemoteAsync(bool showErrors)
    {
        if (_remote is { IsRunning: true })
            return true;

        var token = _settings.RemoteToken;
        if (token is null)
            _settings.RemoteToken = token = RemoteServer.NewToken();

        var server = new RemoteServer(token, RemotePreviewAsync, RemoteThumbnailAsync);
        server.CommandReceived += request => DispatcherQueue.TryEnqueue(() => HandleRemoteCommand(request));
        server.ClientsChanged += count => DispatcherQueue.TryEnqueue(() => UpdateRemoteIndicators(count));
        try
        {
            await server.StartAsync(_settings.RemotePort ?? RemoteServer.DefaultPort);
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (showErrors)
                ShowToast($"Не удалось включить пульт: {e.Message}");
            return false;
        }

        _remote = server;
        _settings.RemoteEnabled = true;
        _settings.RemotePort = server.Port;
        UpdateRemoteIndicators(0);
        PublishRemotePhotos();
        PushRemoteState();
        return true;
    }

    private async Task StopRemoteAsync()
    {
        if (_remote is not { } server)
            return;

        _remote = null;
        _settings.RemoteEnabled = false;
        await server.StopAsync();
        UpdateRemoteIndicators(0);
    }

    private void UpdateRemoteIndicators(int connected)
    {
        RemoteButtonText.Text = _remote is null ? "Телефон" : connected > 0 ? "Телефон подключён" : "Телефон · ждёт";
        if (_remoteDialogStatus is not null)
            _remoteDialogStatus.Text = connected switch
            {
                0 => "Ждёт подключения телефона",
                1 => "Подключён 1 телефон",
                _ => $"Подключено телефонов: {connected}",
            };
    }

    /// <summary>A sharp preview for the phone: the shell thumbnail (the embedded preview for RAW), re-encoded as JPEG.</summary>
    private async Task<RemoteImage?> RemotePreviewAsync(long id, CancellationToken cancellationToken)
    {
        if (!_remoteRecords.TryGetValue(id, out var record))
            return null;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(record.Path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, RemotePreviewSize, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null)
                return null;

            var decoder = await BitmapDecoder.CreateAsync(thumbnail);
            using var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
            encoder.SetSoftwareBitmap(pixels);
            await encoder.FlushAsync();

            using var bytes = new MemoryStream();
            output.Seek(0);
            await output.AsStreamForRead().CopyToAsync(bytes, cancellationToken);
            return new RemoteImage(bytes.ToArray(), "image/jpeg");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            return null;
        }
    }

    /// <summary>A grid tile for the phone: Lumy's own cached thumbnail, made now if it is not there yet.</summary>
    private async Task<RemoteImage?> RemoteThumbnailAsync(long id, CancellationToken cancellationToken)
    {
        if (!_remoteRecords.TryGetValue(id, out var record))
            return null;

        try
        {
            var path = await _thumbnails.GetAsync(record, background: false, cancellationToken);
            return path is null ? null : new RemoteImage(await File.ReadAllBytesAsync(path, cancellationToken), "image/jpeg");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------- State sent to the phone ----------

    /// <summary>Gives the phone's grid the gallery as it is now; called when photos are added, removed, filtered or sorted.</summary>
    private void PublishRemotePhotos()
    {
        _remoteListVersion++;
        _remote?.PublishPhotos(new RemotePhotoList(_remoteListVersion, _visible.Select(ToRemote).ToList()));
        PushRemoteState();
    }

    private static RemotePhoto ToRemote(PhotoItem photo) => new(photo.Id, photo.FileName, photo.Tier);

    /// <summary>Queues one update to the phone; several changes in a row are sent once, after the screen has settled.</summary>
    private void PushRemoteState()
    {
        if (_remote is not { IsRunning: true } || _remotePushQueued)
            return;

        _remotePushQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _remotePushQueued = false;
            _remote?.Publish(CurrentRemoteState());
        });
    }

    private RemoteState CurrentRemoteState()
    {
        int likes = 0, supers = 0, rejected = 0;
        foreach (var p in _all)
        {
            switch (p.Tier)
            {
                case Tier.Like: likes++; break;
                case Tier.SuperLike: supers++; break;
                case Tier.Rejected: rejected++; break;
            }
        }

        // What every screen shares: the folder, filters, toggles and the last message.
        var common = new RemoteState
        {
            Folder = _folder is null ? null : System.IO.Path.GetFileName(_folder.TrimEnd('\\', '/')),
            Folders = _settings.RecentFolders.Select(f => System.IO.Path.GetFileName(f.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : f).ToList(),
            Filter = CurrentFilter.ToString(),
            Sort = CurrentSort.ToString(),
            Counts = new RemoteCounts(_all.Count, likes, supers, _all.Count - likes - supers - rejected, rejected),
            ListVersion = _remoteListVersion,
            AutoAdvance = _settings.AutoAdvance,
            FilmstripVisible = _settings.FilmstripVisible,
            FullScreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen,
            Message = _remoteMessage,
            MessageId = _remoteMessageId,
            Albums = _database.Albums().Select(a => new RemoteAlbum(a.Id, a.Name, a.Count)).ToList(),
            Album = _album is { } album ? new RemoteAlbum(album.Id, album.Name, _all.Count) : null,
        };

        if (InEditor && _editorPhoto is { } edited)
        {
            var index = _visible.IndexOf(edited);
            return Describe(edited, index + 1, _visible.Count, common with { Mode = "editor", Editor = RemoteEditorState() });
        }

        if (InSwipe && _swipe is { } session)
        {
            var counts = common with { Liked = session.Liked, SuperLiked = session.SuperLiked, Rejected = session.Rejected, Total = session.Total };
            return session.IsFinished
                ? counts with { Mode = "swipe-done" }
                : Describe(session.Current!, session.Position, session.Total, counts with { Mode = "swipe" });
        }

        if (InTournament)
        {
            if (TournamentResult.Visibility == Visibility.Visible)
                return common with
                {
                    Mode = "tournament-done",
                    Photos = _tournamentWinners.Select((p, i) => ToRemote(p) with { Label = $"{i + 1}" }).ToList(),
                    Total = _tournamentWinners.Count,
                    Detail = $"{_tournamentAnswers.Count} дуэлей",
                };

            return common with
            {
                Mode = "tournament",
                Photos = _duel is { } duel ? [ToRemote(duel.Left), ToRemote(duel.Right)] : [],
                Position = _duel?.Number ?? 0,
                Total = _tournamentEstimate,
                Detail = TournamentStageText.Text,
            };
        }

        if (InCompare)
            return common with
            {
                Mode = "compare",
                Photos = _compareCells.Select((c, i) => ToRemote(c.Photo) with { Label = $"{i + 1}" }).ToList(),
                Focus = _hoveredCell is { } cell ? _compareCells.IndexOf(cell) : -1,
                Total = _compareCells.Count,
                Overlay = InOverlay ? (_overlayFade ? "fade" : "wipe") : null,
                OverlayValue = OverlaySlider.Value,
            };

        if (InLoupe && _loupeIndex >= 0 && _loupeIndex < _visible.Count)
            return Describe(_visible[_loupeIndex], _loupeIndex + 1, _visible.Count, common with { Mode = "loupe" });

        if (Gallery.SelectedItem is PhotoItem selected)
            return Describe(selected, Gallery.SelectedIndex + 1, _visible.Count, common with { Mode = "gallery" });

        return _visible.Count > 0 || _all.Count > 0
            ? common with { Mode = "gallery", Total = _visible.Count }
            : common;
    }

    private static RemoteState Describe(PhotoItem photo, int position, int total, RemoteState state) => state with
    {
        PhotoId = photo.Id,
        FileName = photo.FileName,
        Position = position,
        Total = total,
        Tier = photo.Tier,
        Detail = photo.SeriesSize > 1 ? $"серия {photo.SeriesPosition}/{photo.SeriesSize}" : null,
    };

    // ---------- Commands from the phone ----------

    private void HandleRemoteCommand(RemoteRequest request)
    {
        try
        {
            // These work the same on every screen.
            switch (request.Command)
            {
                case RemoteCommand.FullScreen: ToggleFullScreen(); return;
                case RemoteCommand.AutoAdvance: ToggleAutoAdvance(); return;
                case RemoteCommand.OpenFolder: RemoteOpenFolder(request); return;
                case RemoteCommand.Tournament: _ = RemoteTournamentAsync(request); return;
                case RemoteCommand.Compare: _ = RemoteCompareAsync(request); return;
                case RemoteCommand.Export: _ = RemoteExportAsync(request); return;
                case RemoteCommand.AlbumOpen: _ = RemoteOpenAlbumAsync(request); return;
                case RemoteCommand.AlbumNew: RemoteNewAlbum(request); return;
                case RemoteCommand.AlbumAdd: RemoteAddToAlbum(request); return;
                case RemoteCommand.AlbumClose: _ = RemoteCloseAlbumAsync(); return;
            }

            if (InEditor)
                HandleRemoteEditor(request);
            else if (InTournament)
                HandleRemoteTournament(request);
            else if (InSwipe)
                HandleRemoteSwipe(request.Command);
            else if (InCompare)
                HandleRemoteCompare(request);
            else
                HandleRemoteBrowse(request);
        }
        catch (Exception e)
        {
            CrashLog.Write(e);
        }
        finally
        {
            PushRemoteState();
        }
    }

    private void HandleRemoteTournament(RemoteRequest request)
    {
        var done = TournamentResult.Visibility == Visibility.Visible;
        switch (request.Command)
        {
            case RemoteCommand.Previous when !done: PickDuel(left: true); break;
            case RemoteCommand.Next when !done: PickDuel(left: false); break;
            case RemoteCommand.Pick when !done && request.Numbers() is [var side]: PickDuel(left: side == 0); break;
            case RemoteCommand.Undo: UndoDuel(); break;
            case RemoteCommand.WinnersSuperLike when done: SuperLikeWinners(); break;
            case RemoteCommand.WinnersShow when done: _ = ShowWinnersAsync(); break;
            case RemoteCommand.WinnersCompare when done: _ = CompareWinnersAsync(); break;
            case RemoteCommand.Goto when done && request.Numbers() is [var id] && _tournamentWinners.FirstOrDefault(w => w.Id == id) is { } winner:
                _ = OpenWinnerAsync(winner);
                break;

            // The phone has already asked "leave the tournament?", so no dialog waits on the computer.
            case RemoteCommand.Back: _ = CloseTournamentAsync(ask: false); break;
        }
    }

    private async Task OpenWinnerAsync(PhotoItem winner)
    {
        await CloseTournamentAsync(ask: false);
        if (!_visible.Contains(winner))
            SetFilter(TierFilter.All);
        if (_visible.IndexOf(winner) is var index and >= 0)
            OpenLoupe(index);
    }

    private void HandleRemoteSwipe(RemoteCommand command)
    {
        switch (command)
        {
            case RemoteCommand.Like: Swipe(SwipeMove.Like); break;
            case RemoteCommand.SuperLike: Swipe(SwipeMove.SuperLike); break;
            case RemoteCommand.Reject: Swipe(SwipeMove.Reject); break;
            case RemoteCommand.RejectSeries: Swipe(SwipeMove.RejectSeries); break;
            case RemoteCommand.Skip or RemoteCommand.Next: Swipe(SwipeMove.Skip); break;
            case RemoteCommand.Undo or RemoteCommand.Previous: SwipeBack(); break;
            case RemoteCommand.Back: CloseSwipe(); break;
        }
    }

    private void HandleRemoteCompare(RemoteRequest request)
    {
        var focus = _hoveredCell;
        switch (request.Command)
        {
            case RemoteCommand.Overlay:
                if (InOverlay)
                    CloseOverlay();
                else if (request.Numbers() is [var a, var b] && a >= 0 && b >= 0 && a < _compareCells.Count && b < _compareCells.Count && a != b)
                    OpenOverlay(_compareCells[(int)Math.Min(a, b)], _compareCells[(int)Math.Max(a, b)]);
                else
                    ToggleOverlay();
                break;
            case RemoteCommand.OverlayMode when InOverlay: SetOverlayFade(request.Arg == "fade"); break;
            case RemoteCommand.OverlayValue when InOverlay && request.Values() is [var value] && !double.IsNaN(value):
                SetOverlayValue(value);
                break;
            case RemoteCommand.OverlaySwap when InOverlay: SwapOverlay(); break;
            case RemoteCommand.Back when InOverlay: CloseOverlay(); break;
            case RemoteCommand.Pick when request.Numbers() is [var i] && i >= 0 && i < _compareCells.Count:
                _compareCells[(int)i].Point();
                break;
            case RemoteCommand.Remove when request.Numbers() is [var i]:
                RemoveFromCompare((int)i);
                break;
            case RemoteCommand.Like: RateHovered(Tier.Like); break;
            case RemoteCommand.SuperLike: RateHovered(Tier.SuperLike); break;
            case RemoteCommand.Reject or RemoteCommand.RejectSeries: RateHovered(Tier.Rejected); break;
            case RemoteCommand.Clear: RateHovered(Tier.None); break;
            case RemoteCommand.Undo or RemoteCommand.Redo:
                UndoOrRedo(redo: request.Command == RemoteCommand.Redo);
                RefreshCompareBadges();
                break;
            case RemoteCommand.Open when focus is not null:
                CloseCompare();
                if (_visible.IndexOf(focus.Photo) is var index and >= 0)
                    OpenLoupe(index);
                break;
            case RemoteCommand.Back: CloseCompare(); break;
            case RemoteCommand.StartSwipe:
                CloseCompare();
                StartSwipe();
                break;
        }
    }

    private void HandleRemoteBrowse(RemoteRequest request)
    {
        switch (request.Command)
        {
            case RemoteCommand.Like: Rate(Tier.Like); break;
            case RemoteCommand.SuperLike: Rate(Tier.SuperLike); break;
            case RemoteCommand.Reject or RemoteCommand.RejectSeries: Rate(Tier.Rejected); break;
            case RemoteCommand.Clear: Rate(Tier.None); break;
            case RemoteCommand.Undo: UndoOrRedo(redo: false); break;
            case RemoteCommand.Redo: UndoOrRedo(redo: true); break;
            case RemoteCommand.Next or RemoteCommand.Skip: StepRemote(+1); break;
            case RemoteCommand.Previous: StepRemote(-1); break;
            case RemoteCommand.NextUnrated: StepRemote(+1, unratedOnly: true); break;
            case RemoteCommand.Open when !InLoupe: OpenLoupe(Math.Max(Gallery.SelectedIndex, 0)); break;
            case RemoteCommand.Back: CloseLoupe(); break;
            case RemoteCommand.Zoom when InLoupe: _ = ToggleZoomAsync(); break;
            case RemoteCommand.View when request.Values() is [var zoom, var x, var y] && !double.IsNaN(zoom + x + y):
                _ = RemoteViewAsync(zoom, x, y);
                break;
            case RemoteCommand.StartSwipe: StartSwipe(); break;
            case RemoteCommand.Filmstrip when InLoupe: ToggleFilmstrip(); break;
            case RemoteCommand.Goto when request.Numbers() is [var id]: RemoteGoto(id); break;
            case RemoteCommand.Filter when Enum.TryParse<TierFilter>(request.Arg, out var filter):
                CloseLoupe();
                SetFilter(filter);
                break;
            case RemoteCommand.Sort:
                var sortIndex = SortBox.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => i.Tag as string == request.Arg);
                if (sortIndex >= 0)
                    SortBox.SelectedIndex = sortIndex;
                break;
            case RemoteCommand.Edit:
                if (request.Numbers() is [var editId] && _all.FirstOrDefault(p => p.Id == editId) is { } toEdit)
                    _ = OpenEditorAsync(toEdit);
                else
                    EditCurrent();
                break;
            case RemoteCommand.AlbumRemove when _album is { } album:
                var removeIds = request.Numbers().ToHashSet();
                var toRemove = removeIds.Count > 0 ? _all.Where(p => removeIds.Contains(p.Id)).ToList() : CurrentPhotos();
                RemoveFromAlbum(album, toRemove);
                break;
            case RemoteCommand.AlbumMove when request.Values() is [var moveId, var position] && _all.FirstOrDefault(p => p.Id == (long)moveId) is { } moved:
                MoveInAlbum(moved, (int)position);
                break;
            case RemoteCommand.Delete:
                var ids = request.Numbers().ToHashSet();
                var photos = ids.Count > 0 ? _all.Where(p => ids.Contains(p.Id)).ToList() : CurrentPhotos();
                _ = DeletePhotosAsync(photos, confirmed: true);
                break;
        }
    }

    /// <summary>A tap in the phone's grid: that photo opens in the viewer, or is selected in the gallery.</summary>
    private void RemoteGoto(long id)
    {
        var index = _visible.ToList().FindIndex(p => p.Id == id);
        if (index < 0)
            return;

        if (InLoupe)
        {
            ShowInLoupe(index);
            return;
        }

        Gallery.SelectedIndex = index;
        Gallery.ScrollIntoView(_visible[index]);
    }

    private void RemoteOpenFolder(RemoteRequest request)
    {
        var folders = _settings.RecentFolders;
        if (request.Numbers() is [var i] && i >= 0 && i < folders.Count && Directory.Exists(folders[(int)i]))
        {
            if (InTournament)
                _ = CloseTournamentAsync(ask: false);
            _ = OpenFolderAsync(folders[(int)i]);
        }
        else
        {
            ShowToast("Эта папка больше не найдена");
        }
    }

    /// <summary>Compare from the phone: the photos it lists, or what C would compare on the computer.</summary>
    private async Task RemoteCompareAsync(RemoteRequest request)
    {
        if (InTournament)
            await CloseTournamentAsync(ask: false);
        if (InSwipe)
            CloseSwipe();

        var ids = request.Numbers();
        if (ids.Count == 0)
        {
            StartCompare();
            return;
        }

        var byId = _all.ToDictionary(p => p.Id);
        ComparePhotos(ids.Where(byId.ContainsKey).Select(id => byId[id]).Distinct().ToList());
    }

    /// <summary>"likes:10", "shown:5" or "12,15,18:3": which photos compete, and how many win.</summary>
    private async Task RemoteTournamentAsync(RemoteRequest request)
    {
        var parts = (request.Arg ?? "").Split(':');
        var winners = parts.Length > 1 && int.TryParse(parts[1], out var n) ? Math.Clamp(n, 1, 50) : DefaultWinners;
        var ids = new RemoteRequest(RemoteCommand.Tournament, parts[0]).Numbers().ToHashSet();
        var pool = parts[0] switch
        {
            "likes" => _all.Where(p => p.Tier >= Tier.Like).ToList(),
            "shown" or "" => _visible.ToList(),
            _ => _all.Where(p => ids.Contains(p.Id)).ToList(),
        };
        if (pool.Count < 2)
        {
            ShowToast(parts[0] == "likes" ? "Для турнира из лайков нужно хотя бы 2 лайка" : "Для турнира нужно хотя бы 2 фото");
            return;
        }

        if (InTournament)
            await CloseTournamentAsync(ask: false);
        if (InSwipe)
            CloseSwipe();
        StartTournament(new Tournament<PhotoItem>(pool, winners, p => p.SeriesId));
    }

    private void StepRemote(int delta, bool unratedOnly = false)
    {
        if (InLoupe)
        {
            StepLoupe(delta, unratedOnly);
            return;
        }

        if (_visible.Count == 0)
            return;

        var i = Gallery.SelectedIndex < 0 ? 0 : Gallery.SelectedIndex + delta;
        while (unratedOnly && i >= 0 && i < _visible.Count && _visible[i].Tier != Tier.None)
            i += delta;
        if (i < 0 || i >= _visible.Count)
            return;

        Gallery.SelectedIndex = i;
        Gallery.ScrollIntoView(_visible[i]);
    }

    // ---------- The pairing dialog ----------

    private async void Remote_Click(object sender, RoutedEventArgs e)
    {
        if (!await StartRemoteAsync(showErrors: true))
            return;

        var content = new StackPanel { Spacing = 12, MaxWidth = 380 };
        var dialog = new ContentDialog
        {
            Title = "Пульт с телефона",
            Content = content,
            PrimaryButtonText = "Новый код",
            SecondaryButtonText = "Выключить пульт",
            CloseButtonText = "Готово",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            // A new code unpairs every phone that had the old link, for when a link was shared by mistake.
            args.Cancel = true;
            var deferral = args.GetDeferral();
            await StopRemoteAsync();
            _settings.RemoteToken = RemoteServer.NewToken();
            if (await StartRemoteAsync(showErrors: true))
                await FillRemoteDialogAsync(content);
            deferral.Complete();
        };
        dialog.SecondaryButtonClick += async (_, _) => await StopRemoteAsync();

        await FillRemoteDialogAsync(content);
        await dialog.ShowAsync();
        _remoteDialogStatus = null;
    }

    private async Task FillRemoteDialogAsync(StackPanel content)
    {
        content.Children.Clear();
        if (_remote is not { } server)
            return;

        var addresses = RemoteNetwork.LanAddresses();
        if (addresses.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Компьютер сейчас не в домашней сети. Подключите его к Wi‑Fi или кабелем к роутеру, к которому подключён телефон.",
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        content.Children.Add(new TextBlock
        {
            Text = "Наведите камеру телефона на код. Телефон должен быть в той же сети Wi‑Fi, что и компьютер.",
            TextWrapping = TextWrapping.Wrap,
        });

        var url = server.UrlFor(addresses[0]);
        content.Children.Add(new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Image { Source = await QrImageAsync(RemoteNetwork.QrPng(url)), Width = 240, Height = 240 },
        });

        content.Children.Add(new TextBlock
        {
            Text = url,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
        });

        if (addresses.Count > 1)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Если не открывается, попробуйте: " + string.Join("  ", addresses.Skip(1).Select(a => $"{a}:{server.Port}")),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }

        _remoteDialogStatus = new TextBlock { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        content.Children.Add(_remoteDialogStatus);
        UpdateRemoteIndicators(server.ConnectedClients);

        content.Children.Add(new TextBlock
        {
            Text = "Если Windows спросит про брандмауэр, разрешите доступ в частных сетях. Пульт работает только внутри домашней сети, а ссылка содержит секретный код, без которого Lumy никого не пустит.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
    }

    private static async Task<BitmapImage> QrImageAsync(byte[] png)
    {
        var bitmap = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
