using System.Numerics;
using HapticSeat.Telemetry;
using HapticSeat.Telemetry.Il2;

namespace HapticSeat.Tests;

public class Il2DecoderTests
{
    private readonly Il2Decoder _decoder = new();
    private readonly FlightState _state = new();
    private readonly List<FlightEvent> _events = [];

    [Fact]
    public void Indicadores_de_estado()
    {
        var packet = new Il2PacketBuilder()
            .Indicator(0, 2400, 2350)          // RPM de dos motores
            .Indicator(1, 101325, 101000)      // presión de admisión (sin uso)
            .Indicator(4, 1, 1, 1)             // tren desplegado
            .Indicator(5, 0.4f, 0.5f, 0)       // carga en ruedas
            .Indicator(6, 80)                  // IAS m/s
            .Indicator(8, 1, 9.81f * 2, 0.5f)  // aceleraciones
            .Indicator(9, 12, 0.3f)            // buffet
            .Indicator(10, 150)                // AGL
            .Indicator(11, 0.5f)               // flaps
            .Indicator(12, 1)                  // aerofreno
            .Build();

        Assert.True(_decoder.Decode(packet, _state, _events));
        Assert.False(_state.Paused);
        Assert.Equal(2, _state.EngineCount);
        Assert.Equal(2350, _state.EngineRpm[1]);
        Assert.Equal(3, _state.WheelCount);
        Assert.Equal(0.5f, _state.WheelLoad[1]);
        Assert.Equal(80, _state.IasMs);
        Assert.Equal(2, _state.VerticalG, 3);
        Assert.Equal(12, _state.BuffetFrequency);
        Assert.Equal(0.3f, _state.BuffetAmplitude);
        Assert.Equal(150, _state.AglM);
        Assert.Equal(0.5f, _state.Flaps);
        Assert.Equal(1, _state.Airbrake);
        Assert.Empty(_events);
    }

    [Fact]
    public void Paquete_sin_indicadores_es_pausa()
    {
        Assert.True(_decoder.Decode(new Il2PacketBuilder().Build(), _state, _events));
        Assert.True(_state.Paused);
    }

    [Fact]
    public void Disparo_usa_los_datos_del_arma()
    {
        var setup = new Il2PacketBuilder()
            .AircraftName("Bf 109 G-6")
            .GunData(0, new Vector3(1.5f, 0.3f, 0), 0.096f, 800)  // MG 151/20 en el morro
            .GunData(1, new Vector3(0.5f, 0, -2.5f), 0.048f, 850) // ala izquierda
            .Indicator(6, 0) // necesita un indicador para no ser pausa
            .Build();
        _decoder.Decode(setup, _state, _events);
        Assert.Equal("Bf 109 G-6", _state.Aircraft);

        _decoder.Decode(new Il2PacketBuilder().Indicator(6, 100).GunFired(0).GunFired(1).Build(), _state, _events);

        Assert.Equal(2, _events.Count);
        Assert.All(_events, e => Assert.Equal(FlightEventType.GunFired, e.Type));
        Assert.Equal(76.8f, _events[0].Magnitude, 2);
        Assert.Equal(-2.5f, _events[1].Position.Z);
    }

    [Fact]
    public void Impacto_bomba_motor_y_rueda()
    {
        var packet = new Il2PacketBuilder()
            .Indicator(6, 100)
            .EngineData(0, new Vector3(2, 0, 0), 2800)
            .WheelData(1, new Vector3(0.5f, -1.5f, 1.2f))
            .Hit(new Vector3(-3, 0, 1), new Vector3(300, 400, 0))
            .BombRelease(new Vector3(0, -1, 0), 250)
            .Build();

        Assert.True(_decoder.Decode(packet, _state, _events));
        Assert.Equal(2800, _state.EngineMaxRpm[0]);
        Assert.Equal(1.2f, _state.WheelPos[1].Z);
        Assert.Equal(FlightEventType.Hit, _events[0].Type);
        Assert.Equal(500, _events[0].Magnitude, 2);
        Assert.Equal(FlightEventType.BombRelease, _events[1].Type);
        Assert.Equal(250, _events[1].Magnitude);
    }

    [Fact]
    public void Movimiento()
    {
        Assert.True(_decoder.Decode(Il2PacketBuilder.Motion(0.1f, -0.2f, 1, 2, 3), _state, _events));
        Assert.Equal(0.1f, _state.Pitch);
        Assert.Equal(-0.2f, _state.Roll);
        Assert.Equal(1, _state.RollRate);
        Assert.Equal(3, _state.YawRate);
    }

    [Fact]
    public void Ids_desconocidos_se_saltan_y_se_avisan_una_vez()
    {
        var packet = new Il2PacketBuilder()
            .Indicator(99, 1, 2, 3)
            .Indicator(6, 70)
            .Event(77, [1, 2, 3, 4])
            .GunFired(0)
            .Build();

        Assert.True(_decoder.Decode(packet, _state, _events));
        Assert.True(_decoder.Decode(packet, _state, _events));
        Assert.Equal(70, _state.IasMs);
        Assert.Equal(2, _events.Count); // un disparo por paquete
        Assert.Equal(2, _decoder.Notices.Count);
    }

    [Fact]
    public void Paquetes_truncados_o_ajenos_no_lanzan()
    {
        var packet = new Il2PacketBuilder().Indicator(0, 2400, 2400).GunFired(0).Build();
        for (var len = 0; len < packet.Length; len++)
            _decoder.Decode(packet.AsSpan(0, len), new FlightState(), _events);

        Assert.False(_decoder.Decode([1, 2, 3, 4, 5, 6, 7, 8], _state, _events));
    }
}
