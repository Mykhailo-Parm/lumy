using System.Net;
using Lumy.Remote;

namespace Lumy.Remote.Tests;

public class RemoteBasicsTests
{
    [Theory]
    [InlineData("next", RemoteCommand.Next)]
    [InlineData("prev", RemoteCommand.Previous)]
    [InlineData("SUPER", RemoteCommand.SuperLike)]
    [InlineData("reject-series", RemoteCommand.RejectSeries)]
    [InlineData("swipe", RemoteCommand.StartSwipe)]
    [InlineData("tournament", RemoteCommand.Tournament)]
    [InlineData("winners-super", RemoteCommand.WinnersSuperLike)]
    [InlineData("auto-advance", RemoteCommand.AutoAdvance)]
    [InlineData("edit-set", RemoteCommand.EditSet)]
    [InlineData("export", RemoteCommand.Export)]
    [InlineData("album", RemoteCommand.AlbumOpen)]
    [InlineData("album-move", RemoteCommand.AlbumMove)]
    [InlineData("overlay-value", RemoteCommand.OverlayValue)]
    public void Parses_command_names(string name, RemoteCommand expected)
    {
        Assert.True(RemoteCommands.TryParse(name, out var command));
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Like ")]
    [InlineData("format-disk")]
    public void Rejects_unknown_commands(string? name) => Assert.False(RemoteCommands.TryParse(name, out _));

    [Theory]
    [InlineData("192.168.0.10", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)]
    public void Knows_home_network_addresses(string address, bool expected) =>
        Assert.Equal(expected, RemoteNetwork.IsPrivate(IPAddress.Parse(address)));

    [Fact]
    public void Tokens_are_random_and_url_safe()
    {
        var a = RemoteServer.NewToken();
        var b = RemoteServer.NewToken();
        Assert.NotEqual(a, b);
        Assert.Equal(22, a.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
    }

    [Fact]
    public void Qr_code_is_a_png()
    {
        var png = RemoteNetwork.QrPng("http://192.168.1.5:8765/?t=abc");
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png.Take(4).ToArray());
    }

    [Fact]
    public void Request_numbers_skip_junk() =>
        Assert.Equal([3L, 17L], new RemoteRequest(RemoteCommand.Compare, "3, x,17,").Numbers());

    [Fact]
    public void Request_values_split_on_colons()
    {
        var values = new RemoteRequest(RemoteCommand.EditCrop, "0.05:-0.1:x").Values();
        Assert.Equal(3, values.Count);
        Assert.Equal(0.05, values[0], 6);
        Assert.Equal(-0.1, values[1], 6);
        Assert.True(double.IsNaN(values[2]));
    }
}
