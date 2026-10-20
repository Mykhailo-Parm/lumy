using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Lumy.Remote;

/// <summary>
/// The phone remote: a small web server on the home network. The phone opens the page from the QR code,
/// keeps a WebSocket open, sends commands and receives what Lumy is showing. Every request must carry the
/// pairing token from the QR code, so other devices on the network cannot drive Lumy or see photos.
/// </summary>
/// <param name="preview">A large image of a photo for the main view.</param>
/// <param name="thumbnail">A small one for grids; falls back to <paramref name="preview"/>.</param>
public sealed class RemoteServer(
    string token,
    Func<long, CancellationToken, Task<RemoteImage?>> preview,
    Func<long, CancellationToken, Task<RemoteImage?>>? thumbnail = null) : IAsyncDisposable
{
    public const int DefaultPort = 8765;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly byte[] _token = Encoding.UTF8.GetBytes(token);
    private WebApplication? _app;
    private RemoteState _state = RemoteState.Empty;
    private RemotePhotoList _photos = RemotePhotoList.Empty;

    /// <summary>Raised on a thread-pool thread for every valid command a phone sends.</summary>
    public event Action<RemoteRequest>? CommandReceived;

    /// <summary>Raised when a phone connects or disconnects, with the number now connected.</summary>
    public event Action<int>? ClientsChanged;

    public string Token { get; } = token;

    public int Port { get; private set; }

    public bool IsRunning => _app is not null;

    public int ConnectedClients => _clients.Count;

    /// <summary>A fresh pairing token: 16 random bytes, URL-safe.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string UrlFor(string host) => $"http://{host}:{Port}/?t={Uri.EscapeDataString(Token)}";

    /// <summary>
    /// Starts listening on <paramref name="preferredPort"/>, or the next free port after it.
    /// <paramref name="localOnly"/> binds to 127.0.0.1 only (used by tests).
    /// </summary>
    public async Task StartAsync(int preferredPort = DefaultPort, bool localOnly = false, CancellationToken cancellationToken = default)
    {
        if (_app is not null)
            return;

        Exception? last = null;
        for (var port = preferredPort; port < preferredPort + 10; port++)
        {
            var app = Build(port, localOnly);
            try
            {
                await app.StartAsync(cancellationToken);
                _app = app;
                Port = port;
                return;
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                // Port taken by another program: try the next one.
                last = e;
                await app.DisposeAsync();
            }
        }

        throw new IOException($"No free port between {preferredPort} and {preferredPort + 9}.", last);
    }

    public async Task StopAsync()
    {
        if (_app is not { } app)
            return;

        _app = null;
        foreach (var client in _clients.Values)
            client.Abort();
        _clients.Clear();
        await app.StopAsync();
        await app.DisposeAsync();
        ClientsChanged?.Invoke(0);
    }

    /// <summary>Sends Lumy's current state to every connected phone.</summary>
    public void Publish(RemoteState state)
    {
        _state = state;
        var message = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        foreach (var client in _clients.Values)
            _ = client.SendAsync(message);
    }

    /// <summary>Replaces the photo list the phone's grid shows; the state's <see cref="RemoteState.ListVersion"/> should match.</summary>
    public void PublishPhotos(RemotePhotoList photos) => _photos = photos;

    public ValueTask DisposeAsync() => new(StopAsync());

    private WebApplication Build(int port, bool localOnly)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (localOnly)
                options.ListenLocalhost(port);
            else
                options.ListenAnyIP(port);
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Use(async (context, next) =>
        {
            if (!IsAuthorized(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("Lumy: отсканируйте QR-код в приложении на компьютере ещё раз.");
                return;
            }

            await next(context);
        });

        app.MapGet("/", ServePage);
        app.MapGet("/preview/{id:long}", (HttpContext context, long id) => ServeImage(context, id, preview));
        app.MapGet("/thumb/{id:long}", (HttpContext context, long id) => ServeImage(context, id, thumbnail ?? preview));
        app.MapGet("/photos", ServePhotos);
        app.Map("/ws", HandleSocket);
        return app;
    }

    private bool IsAuthorized(HttpRequest request)
    {
        var given = Encoding.UTF8.GetBytes(request.Query["t"].ToString());
        return CryptographicOperations.FixedTimeEquals(given, _token);
    }

    private static async Task ServePage(HttpContext context)
    {
        await using var page = typeof(RemoteServer).Assembly.GetManifestResourceStream("Lumy.Remote.remote.html")
            ?? throw new InvalidOperationException("The remote page is missing from the build.");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await page.CopyToAsync(context.Response.Body);
    }

    private async Task ServePhotos(HttpContext context)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(_photos, Json), context.RequestAborted);
    }

    private static async Task ServeImage(HttpContext context, long id, Func<long, CancellationToken, Task<RemoteImage?>> source)
    {
        var image = await source(id, context.RequestAborted);
        if (image is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = image.ContentType;
        context.Response.Headers.CacheControl = "private, max-age=300";
        await context.Response.Body.WriteAsync(image.Bytes, context.RequestAborted);
    }

    private async Task HandleSocket(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        var client = new Client(socket);
        _clients[id] = client;
        ClientsChanged?.Invoke(_clients.Count);
        try
        {
            await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(_state, Json));
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage)
                    continue;

                if (ParseCommand(buffer.AsSpan(0, result.Count)) is { } command)
                    CommandReceived?.Invoke(command);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // The phone went to sleep or left the network; it reconnects on its own.
        }
        finally
        {
            _clients.TryRemove(id, out _);
            ClientsChanged?.Invoke(_clients.Count);
        }
    }

    private static RemoteRequest? ParseCommand(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("cmd", out var cmd) || cmd.ValueKind != JsonValueKind.String
                || !RemoteCommands.TryParse(cmd.GetString(), out var command))
                return null;

            string? arg = null;
            if (root.TryGetProperty("arg", out var value))
                arg = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null,
                };
            return new RemoteRequest(command, arg);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class Client(WebSocket socket)
    {
        private readonly SemaphoreSlim _send = new(1, 1);

        public async Task SendAsync(byte[] message)
        {
            await _send.WaitAsync();
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
            finally
            {
                _send.Release();
            }
        }

        public void Abort() => socket.Abort();
    }
}
