using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Lumy.Core;
using Lumy.Remote;
using Microsoft.UI.Dispatching;
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

    /// <summary>Photo id → file, for the photos of the open folder. Read by the server off the UI thread, so it is swapped, never changed.</summary>
    private IReadOnlyDictionary<long, string> _remotePaths = new Dictionary<long, string>();

    private TextBlock? _remoteDialogStatus;

    // ---------- Server ----------

    private async Task<bool> StartRemoteAsync(bool showErrors)
    {
        if (_remote is { IsRunning: true })
            return true;

        var token = _settings.RemoteToken;
        if (token is null)
            _settings.RemoteToken = token = RemoteServer.NewToken();

        var server = new RemoteServer(token, RemotePreviewAsync);
        server.CommandReceived += command => DispatcherQueue.TryEnqueue(() => HandleRemoteCommand(command));
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
        if (!_remotePaths.TryGetValue(id, out var path))
            return null;

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
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

    // ---------- State sent to the phone ----------

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
        if (InSwipe && _swipe is { } session)
        {
            var counts = new RemoteState { Liked = session.Liked, SuperLiked = session.SuperLiked, Rejected = session.Rejected, Total = session.Total };
            return session.IsFinished
                ? counts with { Mode = "swipe-done" }
                : Describe(session.Current!, session.Position, session.Total, counts with { Mode = "swipe" });
        }

        if (InTournament)
            return new RemoteState { Mode = "tournament", Position = _duel?.Number ?? 0, Total = _tournamentEstimate };

        if (InCompare)
            return new RemoteState { Mode = "compare", Total = _compareCells.Count };

        if (InLoupe && _loupeIndex >= 0 && _loupeIndex < _visible.Count)
            return Describe(_visible[_loupeIndex], _loupeIndex + 1, _visible.Count, new RemoteState { Mode = "loupe" });

        if (Gallery.SelectedItem is PhotoItem selected)
            return Describe(selected, Gallery.SelectedIndex + 1, _visible.Count, new RemoteState { Mode = "gallery" });

        return _visible.Count > 0
            ? new RemoteState { Mode = "gallery", Total = _visible.Count }
            : RemoteState.Empty;
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

    private void HandleRemoteCommand(RemoteCommand command)
    {
        if (InTournament)
            HandleRemoteTournament(command);
        else if (InSwipe)
            HandleRemoteSwipe(command);
        else if (InCompare)
            HandleRemoteCompare(command);
        else
            HandleRemoteBrowse(command);
        PushRemoteState();
    }

    private void HandleRemoteTournament(RemoteCommand command)
    {
        switch (command)
        {
            case RemoteCommand.Previous: PickDuel(left: true); break;
            case RemoteCommand.Next: PickDuel(left: false); break;
            case RemoteCommand.Undo: UndoDuel(); break;
            case RemoteCommand.Back: _ = CloseTournamentAsync(ask: true); break;
        }
    }

    private void HandleRemoteSwipe(RemoteCommand command)
    {
        switch (command)
        {
            case RemoteCommand.Like: Swipe(SwipeMove.Like); break;
            case RemoteCommand.SuperLike: Swipe(SwipeMove.SuperLike); break;
            case RemoteCommand.Reject: Swipe(SwipeMove.Reject); break;
            case RemoteCommand.RejectSeries: Swipe(SwipeMove.RejectSeries); break;
            case RemoteCommand.Skip: Swipe(SwipeMove.Skip); break;
            case RemoteCommand.Undo: SwipeBack(); break;
            case RemoteCommand.Back: CloseSwipe(); break;
        }
    }

    private void HandleRemoteCompare(RemoteCommand command)
    {
        // Comparing is about pointing at one of several photos, which a phone cannot do; it can only leave.
        if (command is RemoteCommand.Back)
            CloseCompare();
        else if (command is RemoteCommand.Undo)
        {
            UndoOrRedo(redo: false);
            RefreshCompareBadges();
        }
        else if (command is RemoteCommand.StartSwipe)
        {
            CloseCompare();
            StartSwipe();
        }
    }

    private void HandleRemoteBrowse(RemoteCommand command)
    {
        switch (command)
        {
            case RemoteCommand.Like: Rate(Tier.Like); break;
            case RemoteCommand.SuperLike: Rate(Tier.SuperLike); break;
            case RemoteCommand.Reject or RemoteCommand.RejectSeries: Rate(Tier.Rejected); break;
            case RemoteCommand.Clear: Rate(Tier.None); break;
            case RemoteCommand.Undo: UndoOrRedo(redo: false); break;
            case RemoteCommand.Next or RemoteCommand.Skip: StepRemote(+1); break;
            case RemoteCommand.Previous: StepRemote(-1); break;
            case RemoteCommand.NextUnrated: StepRemote(+1, unratedOnly: true); break;
            case RemoteCommand.Open when !InLoupe: OpenLoupe(Math.Max(Gallery.SelectedIndex, 0)); break;
            case RemoteCommand.Back: CloseLoupe(); break;
            case RemoteCommand.Zoom when InLoupe: _ = ToggleZoomAsync(); break;
            case RemoteCommand.StartSwipe: StartSwipe(); break;
        }
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
