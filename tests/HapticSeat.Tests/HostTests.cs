using System.Numerics;
using HapticSeat.Effects;
using HapticSeat.Telemetry;

namespace HapticSeat.Tests;

public class HostTests
{
    private const float Dt = 0.01f;

    [Theory]
    [InlineData(0.5, 0)]    // espera inicial
    [InlineData(1.2, 5)]    // pulso 1
    [InlineData(2.2, 0)]    // pausa
    [InlineData(5.6, 20)]   // pulso 4
    [InlineData(25.0, 0)]   // terminada
    public void Rampa_nivel_que_suena(double t, int level) => Assert.Equal(level, HapticHost.RampLevelPlaying(t));

    [Theory]
    [InlineData(0.5, 0)]    // aún no empezaba
    [InlineData(1.2, 5)]
    [InlineData(2.2, 5)]    // en la pausa cuenta el pulso anterior
    [InlineData(5.6, 20)]
    [InlineData(40.0, 80)]  // tope
    public void Rampa_nivel_notado(double t, int level) => Assert.Equal(level, HapticHost.RampLevelFelt(t));

    [Fact]
    public void Efecto_desactivado_no_suena_salvo_en_solo()
    {
        var engine = new EffectEngine(new GunEffect(), new HitEffect());
        var s = new FlightState { Connected = true };
        FlightEvent[] shot = [new(FlightEventType.GunFired, 80, Vector3.Zero)];
        var motors = new float[8];

        engine.SetEnabled(0, false);
        engine.Update(s, shot, Dt, motors);
        Assert.All(motors, v => Assert.Equal(0, v));

        engine.Solo = 0; // botón Probar: suena aunque esté desactivado
        engine.Update(s, shot, Dt, motors);
        Assert.True(motors.Max() > 0.3f);
    }

    [Fact]
    public void Solo_silencia_los_demas()
    {
        var engine = EffectEngine.CreateDefault();
        var s = new FlightState { Connected = true, IasMs = 100 };
        s.EngineCount = 1;
        s.EngineRpm[0] = 2500;
        var motors = new float[8];

        engine.Solo = engine.IndexOf("guns"); // sin disparos: nada, aunque el motor gire
        for (var i = 0; i < 20; i++) engine.Update(s, [], Dt, motors);
        Assert.All(motors, v => Assert.Equal(0, v));
    }

    /// <summary>Cada demo del botón Probar tiene que hacer sonar su efecto.</summary>
    [Fact]
    public void Todas_las_demos_suenan()
    {
        var engine = EffectEngine.CreateDefault();
        foreach (var effect in engine.Effects)
        {
            engine.ResetAll();
            engine.Solo = engine.IndexOf(effect.Id);
            var demo = new EffectDemo(effect.Id);
            var events = new List<FlightEvent>();
            var motors = new float[8];
            var peak = 0f;
            while (!demo.Finished)
            {
                events.Clear();
                var s = demo.Step(Dt, events);
                engine.Update(s, events.ToArray(), Dt, motors);
                peak = Math.Max(peak, motors.Max());
            }
            Assert.True(peak > 0.1f, $"La demo de «{effect.Name}» no suena (pico {peak:0.00})");
        }
    }
}
