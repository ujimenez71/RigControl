using System.Net;
using System.Net.Sockets;
using HapticSeat.Telemetry;
using HapticSeat.Telemetry.Il2;

namespace HapticSeat.Tests;

public class Il2SourceTests
{
    /// <summary>Igual que el juego: broadcast a 127.255.255.255, con dos oyentes compartiendo el puerto.</summary>
    [Fact]
    public void Recibe_el_broadcast_de_loopback_compartiendo_puerto()
    {
        const int port = 47811;
        using var first = new Il2Source(port);
        using var second = new Il2Source(port); // como HFS o SimHub escuchando a la vez
        first.Start();
        second.Start();

        using var sender = new UdpClient { EnableBroadcast = true };
        var packet = new Il2PacketBuilder().Indicator(6, 123).GunFired(0).Build();
        var target = new IPEndPoint(IPAddress.Parse("127.255.255.255"), port);

        var s1 = new FlightState();
        var s2 = new FlightState();
        for (var i = 0; i < 50 && (s1.IasMs != 123 || s2.IasMs != 123); i++)
        {
            sender.Send(packet, packet.Length, target);
            Thread.Sleep(20);
            first.Snapshot(s1);
            second.Snapshot(s2);
        }

        Assert.True(s1.Connected);
        Assert.Equal(123, s1.IasMs);
        Assert.Equal(123, s2.IasMs);

        var events = new List<FlightEvent>();
        first.DrainEvents(events);
        Assert.NotEmpty(events);
        events.Clear();
        first.DrainEvents(events);
        Assert.Empty(events);
    }
}
