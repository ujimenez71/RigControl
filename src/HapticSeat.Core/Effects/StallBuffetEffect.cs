using HapticSeat.Telemetry;

namespace HapticSeat.Effects;

/// <summary>
/// Buffet de pérdida que da IL-2 (frecuencia y amplitud). Rugosidad aleatoria a esa frecuencia, por
/// todo el cojín. La escala de la amplitud está sin calibrar: verla en vuelo con el monitor.
/// </summary>
public sealed class StallBuffetEffect : IEffect
{
    /// <summary>Amplitud de IL-2 que se considera buffet máximo.</summary>
    public float FullScaleAmplitude { get; set; } = 1;

    private Oscillator _osc = new(71);

    public string Id => "buffet";
    public string Name => "Buffet";
    public string Description => "Rugosidad por todo el cojín al acercarse a la pérdida.";
    public float Gain { get; set; } = 1;

    public void Update(FlightState s, ReadOnlySpan<FlightEvent> events, float dt, Span<float> motors)
    {
        if (s.BuffetAmplitude <= 0.001f) return;

        var amount = Math.Clamp(s.BuffetAmplitude / FullScaleAmplitude, 0, 1);
        var hz = Math.Clamp(s.BuffetFrequency, 3, 20);
        var rough = 0.4f + 0.6f * _osc.Noise(hz, dt);
        Zones.Add(motors, Zones.All, amount * rough * Gain);
    }

    public void Reset() { }
}
