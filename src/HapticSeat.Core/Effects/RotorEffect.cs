using HapticSeat.Telemetry;

namespace HapticSeat.Effects;

/// <summary>
/// Vibración del rotor de los helicópteros, a la frecuencia de paso de las palas. Suave en estacionario
/// y en crucero; más fuerte al pasar por la sustentación traslacional (~20-55 km/h, al despegar y al tomar
/// tierra), a velocidad alta y al cargar G. Crece con el rotor al arrancar.
/// </summary>
public sealed class RotorEffect : IEffect
{
    private const float DefaultBladePassHz = 15;
    private const float Base = 0.08f;
    private const float TranslationalPeak = 0.25f, TranslationalMs = 10, TranslationalWidthMs = 4;
    private const float HighSpeedMax = 0.3f, HighSpeedFromMs = 65, HighSpeedRangeMs = 30;
    private const float GMax = 0.2f, GRange = 1.5f;

    private Oscillator _blades = new(61);
    private Oscillator _rough = new(67);

    public string Id => "rotor";
    public string Name => "Rotor";
    public string Description => "Helicópteros: vibración de las palas, más fuerte en la sustentación traslacional, a velocidad alta y con G.";
    public float Gain { get; set; } = 1;

    public void Update(FlightState s, ReadOnlySpan<FlightEvent> events, float dt, Span<float> motors)
    {
        var spin = Math.Clamp(s.RotorSpeed, 0, 1);
        if (!s.Rotorcraft || spin < 0.05f) return;

        var ias = s.IasMs;
        var etl = (ias - TranslationalMs) / TranslationalWidthMs;
        var level = Base
            + TranslationalPeak * MathF.Exp(-etl * etl)
            + HighSpeedMax * Math.Clamp((ias - HighSpeedFromMs) / HighSpeedRangeMs, 0, 1)
            + GMax * Math.Clamp(MathF.Abs(s.VerticalG - 1) / GRange, 0, 1);
        level *= spin * spin; // rotor arrancando: poca energía todavía

        var hz = (s.BladePassHz > 0 ? s.BladePassHz : DefaultBladePassHz) * spin;
        var mod = (0.6f + 0.4f * _blades.Sine(hz, dt)) * (0.85f + 0.15f * _rough.Noise(hz / 2, dt));
        var amount = level * mod * Gain;
        for (var m = 0; m < motors.Length; m++) motors[m] += Zones.SeatAndBack[m] * amount;
    }

    public void Reset() { }
}
