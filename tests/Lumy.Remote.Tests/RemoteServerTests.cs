using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lumy.Core;
using Lumy.Remote;

namespace Lumy.Remote.Tests;

public sealed class RemoteServerTests : IAsyncLifetime
{
    private const string Token = "secret-token";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xD9];

    private readonly RemoteServer _server = new(Token, (id, _) =>
        Task.FromResult(id == 7 ? new RemoteImage(Jpeg, "image/jpeg") : null));

    private readonly HttpClient _http = new();

    // Each test class instance gets its own port so tests can run side by side.
    private static int _nextPort = 18765;

    public async Task InitializeAsync()
    {
        await _server.StartAsync(Interlocked.Add(ref _nextPort, 10), localOnly: true);
        _http.BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/?t=wrong")]
    [InlineData("/preview/7")]
    [InlineData("/ws?t=secret")]
    public async Task Requests_without_the_token_are_refused(string path)
    {
        var response = await _http.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Serves_the_remote_page_with_the_token()
    {
        var response = await _http.GetAsync($"/?t={Token}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Lumy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Serves_previews_and_404s_unknown_photos()
    {
        var found = await _http.GetAsync($"/preview/7?t={Token}");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal("image/jpeg", found.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Jpeg, await found.Content.ReadAsByteArrayAsync());

        var missing = await _http.GetAsync($"/preview/8?t={Token}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Socket_receives_current_state_then_updates()
    {
        _server.Publish(new RemoteState { Mode = "loupe", PhotoId = 7, FileName = "a.jpg", Tier = Tier.SuperLike, Position = 2, Total = 9 });
        using var socket = await ConnectAsync();

        var first = await ReceiveAsync(socket);
        Assert.Equal("loupe", first.GetProperty("mode").GetString());
        Assert.Equal(7, first.GetProperty("photoId").GetInt64());
        Assert.Equal("superLike", first.GetProperty("tier").GetString());

        _server.Publish(new RemoteState { Mode = "swipe", Tier = Tier.Rejected, Liked = 3 });
        var second = await ReceiveAsync(socket);
        Assert.Equal("swipe", second.GetProperty("mode").GetString());
        Assert.Equal("rejected", second.GetProperty("tier").GetString());
        Assert.Equal(3, second.GetProperty("liked").GetInt32());
    }

    [Fact]
    public async Task Socket_commands_raise_CommandReceived_and_junk_is_ignored()
    {
        var received = new List<RemoteRequest>();
        var gotTwo = new TaskCompletionSource();
        _server.CommandReceived += c =>
        {
            lock (received)
            {
                received.Add(c);
                if (received.Count == 3)
                    gotTwo.TrySetResult();
            }
        };

        using var socket = await ConnectAsync();
        await ReceiveAsync(socket);
        await SendAsync(socket, """{"cmd":"like"}""");
        await SendAsync(socket, "not json");
        await SendAsync(socket, """{"cmd":"format-disk"}""");
        await SendAsync(socket, """{"cmd":"swipe"}""");

        await SendAsync(socket, """{"cmd":"goto","arg":42}""");

        await gotTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([new RemoteRequest(RemoteCommand.Like), new RemoteRequest(RemoteCommand.StartSwipe), new RemoteRequest(RemoteCommand.Goto, "42")], received);
    }

    [Fact]
    public async Task Serves_the_photo_list_and_thumbnails()
    {
        _server.PublishPhotos(new RemotePhotoList(3, [new RemotePhoto(7, "a.jpg", Tier.Like), new RemotePhoto(8, "b.jpg", Tier.None)]));
        var list = JsonDocument.Parse(await _http.GetStringAsync($"/photos?t={Token}")).RootElement;
        Assert.Equal(3, list.GetProperty("version").GetInt32());
        Assert.Equal(2, list.GetProperty("photos").GetArrayLength());
        Assert.Equal("like", list.GetProperty("photos")[0].GetProperty("tier").GetString());

        var thumb = await _http.GetAsync($"/thumb/7?t={Token}");
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/photos")).StatusCode);
    }

    [Fact]
    public async Task Counts_connected_phones()
    {
        var changed = new TaskCompletionSource<int>();
        _server.ClientsChanged += n => changed.TrySetResult(n);
        using var socket = await ConnectAsync();
        Assert.Equal(1, await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, _server.ConnectedClients);
    }

    [Fact]
    public async Task Moves_to_the_next_port_when_one_is_taken()
    {
        await using var second = new RemoteServer(Token, (_, _) => Task.FromResult<RemoteImage?>(null));
        await second.StartAsync(_server.Port, localOnly: true);
        Assert.NotEqual(_server.Port, second.Port);
        Assert.InRange(second.Port, _server.Port + 1, _server.Port + 9);
    }

    [Fact]
    public void Url_carries_the_token()
    {
        Assert.Equal($"http://192.168.1.5:{_server.Port}/?t={Token}", _server.UrlFor("192.168.1.5"));
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/ws?t={Token}"), CancellationToken.None);
        return socket;
    }

    private static Task SendAsync(ClientWebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket)
    {
        var buffer = new byte[8192];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }
}
