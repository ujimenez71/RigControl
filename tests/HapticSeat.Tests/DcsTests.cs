using System.Net;
using System.Net.Sockets;
using System.Text;
using HapticSeat.Telemetry;
using HapticSeat.Telemetry.Dcs;

namespace HapticSeat.Tests;

public class DcsTests
{
    /// <summary>Paquete como el del script; cambiar solo lo que interese en cada prueba.</summary>
    private static byte[] Packet(string name = "AH-64D_BLK_II", double time = 10, string ias = "50.000", string agl = "100.000", string g = "1.000",
        string flaps = "0.000", string brake = "0.000", string gear = "1.000", string rodN = "0.000", string rodL = "0.000", string rodR = "0.000",
        string rpmL = "90.000", string rpmR = "80.000", string shells = "1200", string bombs = "0", string rockets = "38", string failures = "0", string stall = "0") =>
        Encoding.UTF8.GetBytes(string.Join(';', "HS1", time.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture), name, ias, agl, g, flaps, brake, gear,
            rodN, rodL, rodR, rpmL, rpmR, shells, bombs, rockets, failures, stall));

    private readonly DcsDecoder _decoder = new();
    private readonly FlightState _state = new();
    private readonly List<FlightEvent> _events = [];

    private void Feed(byte[] packet) => Assert.True(_decoder.Decode(packet, _state, _events));

    [Fact]
    public void Interpreta_los_campos()
    {
        Feed(Packet(ias: "61.500", agl: "12.500", g: "1.500", flaps: "0.500", brake: "0.250", gear: "0.750", rodL: "0.100"));
        Assert.Equal("AH-64D_BLK_II", _state.Aircraft);
        Assert.Equal(10, _decoder.ModelTime);
        Assert.Equal(61.5f, _state.IasMs, 0.001f);
        Assert.Equal(12.5f, _state.AglM, 0.001f);
        Assert.Equal(1.5f, _state.VerticalG, 0.001f);
        Assert.Equal(0.5f, _state.Flaps, 0.001f);
        Assert.Equal(0.25f, _state.Airbrake, 0.001f);
        Assert.Equal(0.75f, _state.GearPosition[2], 0.001f);
        Assert.Equal(0.1f, _state.WheelLoad[1], 0.001f);
        Assert.Equal(0, _state.WheelLoad[0]);
    }

    [Fact]
    public void Las_rpm_llegan_en_porcentaje_y_se_distinguen_uno_o_dos_motores()
    {
        Feed(Packet(rpmL: "50.000", rpmR: "100.000"));
        Assert.Equal(2, _state.EngineCount);
        Assert.Equal(0.5f, _state.EngineRpm[0] / _state.EngineMaxRpm[0], 0.001f);
        Assert.True(_state.EnginePos[0].Z < 0 && _state.EnginePos[1].Z > 0);

        Feed(Packet(rpmL: "70.000", rpmR: "0.000"));
        Assert.Equal(1, _state.EngineCount);
        Assert.Equal(0, _state.EngineRpm[1]);
    }

    [Fact]
    public void Campos_vacios_o_raros_valen_cero_sin_fallar()
    {
        Feed(Packet(ias: "", g: "nan", shells: "", rockets: "", failures: ""));
        Assert.Equal(0, _state.IasMs);
        Assert.Equal(0, _state.VerticalG);
        Assert.Empty(_events);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HS1;1;2")]
    [InlineData("XX1;0;a;0;0;0;0;0;0;0;0;0;0;0;0;0;0;0;0")]
    [InlineData("HS1;0;a;0;0;0;0;0;0;0;0;0;0;0;0;0;0;0;0;campo de más")]
    public void Paquete_ajeno_se_rechaza(string text) =>
        Assert.False(_decoder.Decode(Encoding.UTF8.GetBytes(text), _state, _events));

    [Fact]
    public void El_primer_dato_es_referencia_y_bajar_municion_son_disparos()
    {
        Feed(Packet(shells: "1200"));
        Assert.Empty(_events);

        Feed(Packet(shells: "1198"));
        Assert.Equal(2, _events.Count);
        Assert.All(_events, e => Assert.Equal(FlightEventType.GunFired, e.Type));
    }

    [Fact]
    public void Rearmar_no_dispara_y_una_gran_bajada_tiene_tope()
    {
        Feed(Packet(shells: "10"));
        Feed(Packet(shells: "1200"));
        Assert.Empty(_events);

        Feed(Packet(shells: "100"));
        Assert.Equal(4, _events.Count);
    }

    [Fact]
    public void Cada_arma_da_su_evento()
    {
        Feed(Packet(bombs: "2", rockets: "38"));
        Feed(Packet(bombs: "1", rockets: "37"));
        Assert.Contains(_events, e => e.Type == FlightEventType.BombRelease);
        Assert.Contains(_events, e => e.Type == FlightEventType.RocketLaunch);
    }

    [Fact]
    public void Aumentar_las_averias_es_un_impacto_y_repararlas_no()
    {
        Feed(Packet(failures: "0"));
        Feed(Packet(failures: "3"));
        Assert.Single(_events);
        Assert.Equal(FlightEventType.Hit, _events[0].Type);

        _events.Clear();
        Feed(Packet(failures: "0"));
        Assert.Empty(_events);
    }

    [Fact]
    public void Cambiar_de_avion_no_cuenta_como_disparos()
    {
        Feed(Packet(name: "A-10C_2", shells: "1150"));
        Feed(Packet(name: "AH-64D_BLK_II", shells: "300"));
        Assert.Empty(_events);
        Assert.Equal("AH-64D_BLK_II", _state.Aircraft);
    }

    [Fact]
    public void Aviso_de_perdida_da_buffet()
    {
        Feed(Packet(stall: "1"));
        Assert.True(_state.BuffetAmplitude > 0);
        Feed(Packet(stall: "0"));
        Assert.Equal(0, _state.BuffetAmplitude);
    }

    // ------------------------------------------------------ por UDP

    private static void Send(UdpClient sender, int port, byte[] packet) => sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));

    private static bool WaitFor(DcsSource source, FlightState s, Func<FlightState, bool> condition, Action? repeat = null)
    {
        for (var i = 0; i < 100; i++)
        {
            repeat?.Invoke();
            Thread.Sleep(20);
            source.Snapshot(s);
            if (condition(s)) return true;
        }
        return false;
    }

    [Fact]
    public void Por_UDP_activo_mientras_avanza_el_tiempo_y_en_pausa_si_se_detiene()
    {
        const int port = 47812;
        using var source = new DcsSource(port);
        source.Start();
        using var sender = new UdpClient();
        var s = new FlightState();

        source.Snapshot(s);
        Assert.False(s.Connected);

        var time = 10.0;
        Assert.True(WaitFor(source, s, x => x.Active, () => Send(sender, port, Packet(time: time += 0.02))), "no llega a activo");
        Assert.Equal("DCS", s.Source);

        // ESC: siguen llegando paquetes, pero el tiempo de la misión no avanza
        Assert.True(WaitFor(source, s, x => x.Connected && x.Paused, () => Send(sender, port, Packet(time: time))), "no detecta la pausa");

        // Sin paquetes: pausa enseguida
        Assert.True(WaitFor(source, s, x => x.Active, () => Send(sender, port, Packet(time: time += 0.02))), "no se reanuda");
        Thread.Sleep(600);
        source.Snapshot(s);
        Assert.True(s.Paused);
    }
}
