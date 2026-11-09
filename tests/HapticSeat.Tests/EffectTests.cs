using System.Numerics;
using HapticSeat.Effects;
using HapticSeat.Telemetry;

namespace HapticSeat.Tests;

public class EffectTests
{
    private const float Dt = 0.01f;
    private readonly float[] _motors = new float[8];

    private static FlightState Flying(float iasMs = 100) => new() { Connected = true, IasMs = iasMs, Accel = new Vector3(0, 9.81f, 0) };

    [Fact]
    public void Sin_telemetria_o_en_pausa_todo_a_cero()
    {
        var engine = EffectEngine.CreateDefault();
        var s = Flying();
        s.EngineCount = 1;
        s.EngineRpm[0] = 2500;

        s.Connected = false;
        engine.Update(s, [], Dt, _motors);
        Assert.All(_motors, v => Assert.Equal(0, v));

        s.Connected = true;
        s.Paused = true;
        engine.Update(s, [], Dt, _motors);
        Assert.All(_motors, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Disparo_golpea_y_se_desvanece()
    {
        var engine = new EffectEngine(new GunEffect());
        var s = Flying();
        FlightEvent[] shot = [new(FlightEventType.GunFired, 80, new Vector3(0, 0, -2.5f))]; // ala izquierda

        engine.Update(s, shot, Dt, _motors);
        Assert.True(_motors[3] > 0.4f, "glúteo izquierdo (arma a la altura del piloto)");
        Assert.True(_motors[3] > _motors[2] * 2, "más a la izquierda que a la derecha");

        for (var i = 0; i < 30; i++) engine.Update(s, [], Dt, _motors); // 300 ms
        Assert.All(_motors, v => Assert.True(v < 0.01f));
    }

    [Fact]
    public void Toma_de_contacto_solo_al_pasar_del_aire_al_suelo()
    {
        var engine = new EffectEngine(new GroundEffect());
        var s = Flying(iasMs: 45);
        s.WheelCount = 2;
        s.WheelPos[0] = new Vector3(0, -1.5f, -1.2f);
        s.WheelPos[1] = new Vector3(0, -1.5f, 1.2f);

        for (var i = 0; i < 60; i++) engine.Update(s, [], Dt, _motors); // 0,6 s en el aire
        Assert.All(_motors, v => Assert.Equal(0, v));

        s.WheelLoad[0] = s.WheelLoad[1] = 0.6f;
        engine.Update(s, [], Dt, _motors);
        Assert.True(_motors[2] > 0.4f && _motors[3] > 0.4f, "golpe en los glúteos");

        for (var i = 0; i < 100; i++) engine.Update(s, [], Dt, _motors); // 1 s rodando
        Assert.True(_motors.Max() < 0.4f, "sin golpes repetidos, solo rodadura");
        Assert.True(_motors.Max() > 0, "sigue la rodadura");
    }

    /// <summary>Dos ruedas en el suelo con velocidad sobre el suelo y en IAS dadas (helicóptero: Y = 0 en los patines).</summary>
    private static FlightState TwoWheels(float iasMs, float groundSpeedMs, float load)
    {
        var s = Flying(iasMs);
        s.GroundSpeedMs = groundSpeedMs;
        s.WheelCount = 2;
        s.WheelPos[0] = new Vector3(0, -1.5f, -1.2f);
        s.WheelPos[1] = new Vector3(0, -1.5f, 1.2f);
        s.WheelLoad[0] = s.WheelLoad[1] = load;
        return s;
    }

    private static FlightState Helicopter(float iasMs, float rotor = 1)
    {
        var s = Flying(iasMs);
        s.Rotorcraft = true;
        s.RotorSpeed = rotor;
        s.BladePassHz = 19;
        return s;
    }

    private float RotorPeak(FlightState s)
    {
        var engine = new EffectEngine(new RotorEffect());
        var peak = 0f;
        for (var i = 0; i < 100; i++)
        {
            engine.Update(s, [], Dt, _motors);
            peak = Math.Max(peak, _motors.Max());
        }
        return peak;
    }

    [Fact]
    public void Rotor_suave_en_estacionario_y_crucero_y_fuerte_en_traslacional_y_alta_velocidad()
    {
        var hover = RotorPeak(Helicopter(0));
        var etl = RotorPeak(Helicopter(10));
        var cruise = RotorPeak(Helicopter(45));
        var fast = RotorPeak(Helicopter(90));
        Assert.InRange(hover, 0.04f, 0.12f);
        Assert.InRange(cruise, 0.04f, 0.12f);
        Assert.True(etl > hover * 2.5f, $"traslacional {etl:0.00} frente a estacionario {hover:0.00}");
        Assert.True(fast > cruise * 3, $"rápido {fast:0.00} frente a crucero {cruise:0.00}");

        var g = Helicopter(45);
        g.Accel = new Vector3(0, 9.81f * 2.5f, 0);
        Assert.True(RotorPeak(g) > cruise * 2);
    }

    [Fact]
    public void Rotor_parado_o_avion_no_vibran()
    {
        Assert.Equal(0, RotorPeak(Helicopter(10, rotor: 0)));
        var plane = Helicopter(10);
        plane.Rotorcraft = false;
        Assert.Equal(0, RotorPeak(plane));
        Assert.True(RotorPeak(Helicopter(10, rotor: 0.5f)) < RotorPeak(Helicopter(10)) * 0.4f, "arrancando, flojo");
    }

    [Fact]
    public void En_helicopteros_el_motor_casi_no_se_nota()
    {
        float EnginePeak(bool rotorcraft)
        {
            var engine = new EffectEngine(new EngineEffect());
            var s = Flying();
            s.Rotorcraft = rotorcraft;
            s.EngineCount = 1;
            s.EngineRpm[0] = s.EngineMaxRpm[0] = 3000;
            var peak = 0f;
            for (var i = 0; i < 100; i++) { engine.Update(s, [], Dt, _motors); peak = Math.Max(peak, _motors.Max()); }
            return peak;
        }
        Assert.True(EnginePeak(true) < EnginePeak(false) * 0.3f);
    }

    [Fact]
    public void Helicoptero_que_se_posa_casi_parado_da_el_golpe()
    {
        var engine = new EffectEngine(new GroundEffect());
        var s = TwoWheels(iasMs: 2, groundSpeedMs: 0.5f, load: 0);
        for (var i = 0; i < 60; i++) engine.Update(s, [], Dt, _motors);

        s.WheelLoad[0] = s.WheelLoad[1] = 0.6f;
        engine.Update(s, [], Dt, _motors);
        Assert.True(_motors.Max() > 0.4f);
    }

    [Fact]
    public void Rueda_que_se_descarga_un_instante_no_da_golpe()
    {
        var engine = new EffectEngine(new GroundEffect());
        var s = TwoWheels(iasMs: 10, groundSpeedMs: 10, load: 0.6f);
        for (var i = 0; i < 50; i++) engine.Update(s, [], Dt, _motors);

        s.WheelLoad[0] = 0; // bache: 0,1 s sin carga
        for (var i = 0; i < 10; i++) engine.Update(s, [], Dt, _motors);
        s.WheelLoad[0] = 0.6f;
        engine.Update(s, [], Dt, _motors);
        Assert.True(_motors.Max() < 0.4f, "solo rodadura");
    }

    [Fact]
    public void Helicoptero_parado_con_el_rotor_no_rueda_aunque_la_IAS_no_sea_cero()
    {
        var engine = new EffectEngine(new GroundEffect());
        var s = TwoWheels(iasMs: 2.2f, groundSpeedMs: 0.05f, load: 0.6f);
        for (var i = 0; i < 20; i++) engine.Update(s, [], Dt, _motors);
        Assert.All(_motors, v => Assert.Equal(0, v));

        s.GroundSpeedMs = float.NaN; // simulador sin velocidad sobre el suelo: se usa la IAS
        for (var i = 0; i < 20; i++) engine.Update(s, [], Dt, _motors);
        Assert.True(_motors.Max() > 0);
    }

    [Fact]
    public void Primer_paquete_con_tren_abajo_no_hace_clonc()
    {
        var engine = new EffectEngine(new GearFlapsEffect());
        var s = Flying();
        s.WheelCount = 3;
        s.GearPosition.AsSpan(0, 3).Fill(1);

        engine.Update(s, [], Dt, _motors);
        Assert.All(_motors, v => Assert.Equal(0, v));
    }

    [Fact]
    public void Motor_vibra_proporcional_a_las_rpm()
    {
        float Peak(float rpm)
        {
            var engine = new EffectEngine(new EngineEffect());
            var s = Flying();
            s.EngineCount = 1;
            s.EngineRpm[0] = rpm;
            s.EngineMaxRpm[0] = 2800;
            var max = 0f;
            for (var i = 0; i < 100; i++)
            {
                engine.Update(s, [], Dt, _motors);
                max = Math.Max(max, _motors.Max());
            }
            return max;
        }

        Assert.Equal(0, Peak(0));
        Assert.True(Peak(2800) > Peak(800) * 1.5f);
    }

    [Fact]
    public void Mezcla_satura_en_uno()
    {
        var engine = new EffectEngine(new GunEffect(), new HitEffect());
        FlightEvent[] boom =
        [
            new(FlightEventType.GunFired, 650, Vector3.Zero),
            new(FlightEventType.Hit, 1e6f, Vector3.Zero),
        ];
        engine.Update(Flying(), boom, Dt, _motors);
        Assert.All(_motors, v => Assert.InRange(v, 0, 1));
        Assert.Equal(1, _motors.Max(), 3);
    }

    [Fact]
    public void Niveles_respetan_el_minimo_de_cada_motor()
    {
        byte[] levels = new byte[8];
        byte[] min = [20, 20, 20, 20, 25, 25, 30, 30];
        EffectEngine.ToLevels([0, 0.001f, 0.01f, 1, 0.5f, 0, 0.01f, 0], levels, min);
        Assert.Equal(0, levels[0]);
        Assert.Equal(0, levels[1]);
        Assert.InRange(levels[2], 20, 25);
        Assert.Equal(255, levels[3]);
        Assert.Equal(140, levels[4]);
        Assert.InRange(levels[6], 30, 35);
    }

    [Theory]
    [InlineData(3, 0, 0, 1)]    // morro -> muslos
    [InlineData(-3, 0, 4, 5)]   // cola -> espalda
    [InlineData(0, 2.5f, 0, 1)] // ala derecha -> lado derecho (pares)
    public void Reparto_espacial(float x, float z, int strong, int other)
    {
        var w = new float[8];
        Zones.Spatial(new Vector3(x, 0, z), 1, w);
        Assert.Equal(1, w.Max(), 3);
        Assert.True(w[strong] >= w[other]);
        if (z > 0) Assert.True(w[0] > w[1]);
        if (x > 0) Assert.True(w[0] > w[4]);
        if (x < 0) Assert.True(w[4] > w[0]);
    }
}
